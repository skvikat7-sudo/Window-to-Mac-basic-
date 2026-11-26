using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ProjectCrystal.Native.System;

public sealed class NotificationService
{
    private UserNotificationListener? _listener;
    private readonly List<NotificationItem> _items = new();
    private readonly HashSet<uint> _locallyDismissed = new();

    public bool IsSupportedAndAllowed { get; private set; }
    public string StatusNote { get; private set; } = "Not initialized";
    public event Action? NotificationsChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            IsSupportedAndAllowed = status == UserNotificationListenerAccessStatus.Allowed;
            StatusNote = $"Windows Notification Listener: {status}";
            if (IsSupportedAndAllowed)
            {
                try { _listener.NotificationChanged += OnPlatformNotificationChanged; }
                catch { /* Unpackaged Win32 apps fall back to periodic polling in ShellCoordinator */ }
                await RefreshAsync();
            }
            CrystalLogger.Info("Notifications", $"{StatusNote} ({_items.Count} active)");
        }
        catch (Exception ex)
        {
            IsSupportedAndAllowed = false;
            StatusNote = $"Notification listener unavailable: {ex.GetType().Name} (0x{ex.HResult:X8}) {ex.Message}";
            CrystalLogger.Warn("Notifications", StatusNote);
        }
    }

    private void OnPlatformNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!IsSupportedAndAllowed || _listener is null) return;
        try
        {
            var platformNotifs = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var list = new List<NotificationItem>();

            foreach (var n in platformNotifs)
            {
                if (_locallyDismissed.Contains(n.Id)) continue;
                string appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "Windows";
                string appId = n.AppInfo?.AppUserModelId ?? "windows";

                string title = "";
                string body = "";
                try
                {
                    var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding != null)
                    {
                        var textElements = binding.GetTextElements();
                        title = textElements.ElementAtOrDefault(0)?.Text ?? "";
                        body = string.Join(" — ", textElements.Skip(1).Select(t => t.Text).Where(s => !string.IsNullOrWhiteSpace(s)));
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                    title = appName;

                list.Add(new NotificationItem(
                    Id: $"win-{n.Id}",
                    PlatformId: n.Id,
                    AppName: appName,
                    AppId: appId,
                    Title: title,
                    Body: body,
                    Timestamp: n.CreationTime));
            }

            list.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            lock (_items)
            {
                _items.Clear();
                _items.AddRange(list);
            }
            NotificationsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("Notifications", $"RefreshAsync failed: {ex.Message}");
        }
    }

    public IReadOnlyList<NotificationItem> GetNotifications(int maxCount = 60)
    {
        lock (_items) return _items.Take(maxCount).ToArray();
    }

    public void Dismiss(uint platformId)
    {
        _locallyDismissed.Add(platformId);
        try { _listener?.RemoveNotification(platformId); } catch { }
        lock (_items) { _items.RemoveAll(x => x.PlatformId == platformId); }
        NotificationsChanged?.Invoke();
    }

    public void ClearAll()
    {
        uint[] ids;
        lock (_items) { ids = _items.Select(x => x.PlatformId).ToArray(); _items.Clear(); }
        foreach (uint id in ids)
        {
            _locallyDismissed.Add(id);
            try { _listener?.RemoveNotification(id); } catch { }
        }
        NotificationsChanged?.Invoke();
    }
}
