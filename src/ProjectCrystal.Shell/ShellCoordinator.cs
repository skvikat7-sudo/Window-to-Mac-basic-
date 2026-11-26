using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Apps;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.Interop;
using ProjectCrystal.Native.System;
using ProjectCrystal.Native.WindowManagement;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using ProjectCrystal.Shell.Surfaces;
using Vortice.DirectWrite;
using Color4 = Vortice.Mathematics.Color4;

namespace ProjectCrystal.Shell;

internal sealed class HotkeyMessageWindow : NativeWindow, IDisposable
{
    public event Action<int>? HotkeyPressed;
    public HotkeyMessageWindow() => CreateHandle(new CreateParams());
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32Interop.WM_HOTKEY)
        {
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }
    public void Dispose() => DestroyHandle();
}

public sealed class ShellCoordinator : IDisposable
{
    private readonly ConfigManager _configMgr;
    private readonly RecoveryGuard _recoveryGuard;
    private readonly bool _safeMode;

    private readonly GpuDeviceContext _gpu;
    private readonly HardwareProfile _hardware;
    private readonly WgcDesktopCaptureService _backdrop;
    private readonly InstalledAppsService _appsSvc = new();
    private readonly CoreAudioService _audioSvc = new();
    private readonly DisplayBrightnessService _displaySvc = new();
    private readonly NetworkRadioService _networkSvc = new();
    private readonly MediaSessionService _mediaSvc = new();
    private readonly NotificationService _notifSvc = new();
    private readonly SystemMetricsService _metricsSvc = new();
    private readonly WeatherService _weatherSvc = new();
    private readonly WindowManagerService _windowMgr = new();
    private readonly ShellIntegrationService _shellSvc;
    private readonly IconTextureRegistry _icons;

    private readonly List<MenuBarWindow> _menuBars = new();
    private readonly List<DockWindow> _docks = new();
    private ControlCenterWindow? _controlCenter;
    private NotificationCenterWindow? _notificationCenter;
    private LauncherWindow? _launcher;
    private DesktopWidgetsHost? _widgetsHost;
    private ExposeOverlayWindow? _expose;
    private SettingsGlassLabWindow? _settingsLab;
    private ContextMenuWindow? _contextMenu;
    private sealed record PendingGenieLaunch(
        string AppName,
        string ExeName,
        Rectangle IconRect,
        HashSet<nint> ExistingHwnds,
        DateTime ExpiresUtc);

    private GenieOverlayWindow? _genieOverlay;
    private readonly WindowLifecycleMonitor _lifecycleMonitor = new();
    private readonly Dictionary<nint, DateTime> _suppressedLifecycleHwnds = new();
    private readonly Dictionary<nint, WindowInfo> _trackedWindows = new();
    private readonly List<PendingGenieLaunch> _pendingGenieLaunches = new();
    private readonly Control _uiMarshaler = new();

    private readonly HotkeyMessageWindow _hotkeyWin = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _renderTimer;
    private readonly System.Windows.Forms.Timer _edgeSensorTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _weatherTimer;
    private readonly System.Windows.Forms.Timer _launchWatchTimer;

    private bool _disposed;

    public ShellCoordinator(ConfigManager configMgr, RecoveryGuard recoveryGuard, bool safeMode)
    {
        _configMgr = configMgr;
        _recoveryGuard = recoveryGuard;
        _safeMode = safeMode;

        _gpu = new GpuDeviceContext();
        var primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        _hardware = HardwareInspector.Inspect(_gpu.AdapterName, _gpu.AdapterMemoryMb, primary.Bounds.Width, primary.Bounds.Height, 60);

        if (_configMgr.Current.Performance.Quality == QualityPreset.Auto)
            _configMgr.Update(c => c.Performance.Quality = _hardware.RecommendedQuality, false);

        nint hMon = Win32Interop.MonitorFromPoint(new Win32Interop.POINT { X = primary.Bounds.X + 20, Y = primary.Bounds.Y + 20 }, 2);
        _backdrop = new WgcDesktopCaptureService(_gpu.D3DDevice, _gpu.D3DContext, hMon, _gpu.DeviceLock);
        _backdrop.SetMaxCaptureFps(_configMgr.Current.Performance.CaptureRateFps);

        _shellSvc = new ShellIntegrationService(_recoveryGuard);
        _icons = new IconTextureRegistry(_appsSvc);

        _trayIcon = BuildTrayIcon();

        int targetFps = Math.Clamp(_configMgr.Current.Performance.TargetFps, 24, 90);
        _renderTimer = new System.Windows.Forms.Timer { Interval = Math.Max(15, 1000 / targetFps) };
        _renderTimer.Tick += (_, __) => TickRenderLoop();

        _edgeSensorTimer = new System.Windows.Forms.Timer { Interval = 28 };
        _edgeSensorTimer.Tick += (_, __) => PollGlobalScreenEdges();

        _statusTimer = new System.Windows.Forms.Timer { Interval = Math.Clamp(_configMgr.Current.Performance.MetricsPollIntervalMs, 500, 5000) };
        _statusTimer.Tick += async (_, __) => await PollSystemStatusAsync();

        _weatherTimer = new System.Windows.Forms.Timer { Interval = 15 * 60 * 1000 };
        _weatherTimer.Tick += async (_, __) => { if (_widgetsHost != null) await _widgetsHost.RefreshWeatherAsync(); };

        _launchWatchTimer = new System.Windows.Forms.Timer { Interval = 75 };
        _launchWatchTimer.Tick += (_, __) => PollPendingGenieLaunches();

        _configMgr.ConfigChanged += OnConfigChanged;
        _hotkeyWin.HotkeyPressed += OnHotkeyPressed;
        _ = _uiMarshaler.Handle;

        _lifecycleMonitor.WindowMinimizeStarted += hwnd =>
        {
            if (!_uiMarshaler.IsDisposed)
                _uiMarshaler.BeginInvoke(() => OnExternalMinimizeStarted(hwnd));
        };
        _lifecycleMonitor.WindowMinimizeEnded += hwnd =>
        {
            if (!_uiMarshaler.IsDisposed)
                _uiMarshaler.BeginInvoke(() => OnExternalMinimizeEnded(hwnd));
        };
        _lifecycleMonitor.WindowShown += hwnd =>
        {
            if (!_uiMarshaler.IsDisposed)
                _uiMarshaler.BeginInvoke(() => OnExternalWindowShown(hwnd));
        };
        _lifecycleMonitor.WindowDestroyed += hwnd =>
        {
            if (!_uiMarshaler.IsDisposed)
                _uiMarshaler.BeginInvoke(() => OnExternalWindowDestroyed(hwnd));
        };
    }

    public async Task InitializeAsync()
    {
        if (_configMgr.Current.Appearance.FollowWindowsAccent)
        {
            var acc = DisplayBrightnessService.GetWindowsAccentColor();
            _configMgr.Update(c => c.Appearance.AccentColorHex = $"#{acc.R:X2}{acc.G:X2}{acc.B:X2}");
        }

        // Fast-path startup: index apps & sync real Windows Taskbar pins (~150ms) and bring up shell immediately
        await _appsSvc.RefreshInstalledAppsAsync();
        _icons.WarmIcons(_appsSvc.GetCachedApps().Select(a => a.IconKey), 48);
        var syncedPins = WindowsTaskbarPinsService.RepairOrPopulatePinnedApps(_configMgr.Current.Dock.PinnedApps, _appsSvc);
        _configMgr.Update(c => c.Dock.PinnedApps = syncedPins, saveNow: true);

        CreateAndShowSurfaces();
        RegisterGlobalHotkeys();
        if (!_safeMode)
            _lifecycleMonitor.Start();

        if (!_safeMode)
        {
            _shellSvc.ApplyShellMode(_configMgr.Current.ShellMode, _hardware);
        }
        else
        {
            // In safe mode: still apply taskbar hiding so the Windows taskbar doesn't cover the shell,
            // but skip the live WGC capture (too risky for a true crash-loop scenario).
            // We do NOT call RestoreTaskbarIfBackedUp() here because that would make the Windows
            // taskbar reappear on top of Project Crystal's menu bar.
            _shellSvc.ApplyShellMode(_configMgr.Current.ShellMode, _hardware);
            CrystalLogger.Info("Shell", "Safe mode: taskbar hide applied; live WGC capture skipped for recovery.");
        }

        _renderTimer.Start();
        _edgeSensorTimer.Start();
        _statusTimer.Start();
        _weatherTimer.Start();

        // Warm heavy backdrop, WGC capture, SMTC, notifications, and weather asynchronously without blocking shell startup
        _ = Task.Run(async () =>
        {
            var wp = await WallpaperService.LoadWallpaperOrFallbackAsync(1920, 1080);
            _gpu.UpdateFallbackBackdrop(wp.BgraPixels, wp.Width, wp.Height);
        });

        if (!_safeMode && _configMgr.Current.Glass.CaptureBackend == CaptureBackendMode.LiveDesktopWgc)
            await _backdrop.StartAsync();

        await _mediaSvc.InitializeAsync();
        await _notifSvc.InitializeAsync();

        await PollSystemStatusAsync();
        if (_widgetsHost != null)
        {
            await _widgetsHost.RefreshWeatherAsync();
            await _widgetsHost.RefreshMediaAsync();
        }

        CrystalLogger.Info("Shell", $"Project Crystal v2 started (SafeMode={_safeMode}, Hardware={_hardware.OsCaption}, GPU={_hardware.GpuName}).");
    }

    private void CreateAndShowSurfaces()
    {
        _genieOverlay ??= new GenieOverlayWindow(_gpu, _configMgr);
        _contextMenu = new ContextMenuWindow(_gpu, _configMgr, _backdrop);

        _controlCenter = new ControlCenterWindow(
            _gpu, _configMgr, _backdrop,
            _audioSvc, _displaySvc, _networkSvc, _mediaSvc, _windowMgr,
            () => OpenGlassLab(),
            OpenExpose);
        _controlCenter.SnapRequested += t => SnapWindowWithMorph(0, t);

        _notificationCenter = new NotificationCenterWindow(_gpu, _configMgr, _backdrop, _notifSvc);

        _launcher = new LauncherWindow(
            _gpu, _configMgr, _backdrop,
            _appsSvc, _icons, _windowMgr,
            () => OpenGlassLab(),
            OpenExpose);
        _launcher.LaunchAppRequested += app =>
        {
            Rectangle iconRect = ResolveDockIconRect(0, app.LaunchTarget, app.Name);
            LaunchAppWithGenie(app.LaunchTarget, app.Arguments, app.Name, iconRect);
        };
        _launcher.RestoreWindowRequested += hwnd => RestoreWindowWithGenie(hwnd);

        _expose = new ExposeOverlayWindow(_gpu, _configMgr, _backdrop, _windowMgr, _icons);
        _expose.CloseWindowRequested += hwnd => CloseWindowWithGenie(hwnd);
        _expose.RestoreWindowRequested += hwnd => RestoreWindowWithGenie(hwnd);

        _settingsLab = new SettingsGlassLabWindow(
            _gpu, _configMgr, _backdrop,
            _hardware, _displaySvc, _shellSvc, _notifSvc, _windowMgr,
            () => RebuildSurfacesFromConfig(peekDock: true),
            SyncTaskbarPinsFromWindows,
            SafeExitApplication,
            OpenLauncher,
            OpenExpose);

        _widgetsHost = new DesktopWidgetsHost(
            _gpu, _configMgr, _backdrop,
            _metricsSvc, _weatherSvc, _mediaSvc,
            ShowWidgetContextMenu);

        RebuildSurfacesFromConfig(peekDock: true);
    }

    private void SyncTaskbarPinsFromWindows()
    {
        var realPins = WindowsTaskbarPinsService.ReadRealWindowsTaskbarPins(_appsSvc);
        _configMgr.Update(c => c.Dock.PinnedApps = realPins, saveNow: true);
        var openWins = _windowMgr.GetOpenWindows();
        foreach (var d in _docks) d.RebuildItems(openWins);
    }

    private bool IsAnyMenuBarPopupOpen() =>
        (_contextMenu?.Visible ?? false)
        || (_controlCenter?.Visible ?? false)
        || (_notificationCenter?.Visible ?? false);

    private bool IsDockPopupOpen() => _contextMenu?.Visible ?? false;

    private void RebuildSurfacesFromConfig(bool peekDock = false)
    {
        var cfg = _configMgr.Current;

        _shellSvc.UnregisterAppBar();
        foreach (var mb in _menuBars) { mb.Close(); mb.Dispose(); }
        _menuBars.Clear();

        if (cfg.MenuBar.Enabled)
        {
            var screens = cfg.MenuBar.MonitorMode == MenuBarMonitorMode.AllMonitors
                ? Screen.AllScreens
                : new[] { Screen.PrimaryScreen ?? Screen.AllScreens[0] };

            foreach (var scr in screens)
            {
                var bounds = scr.Bounds;
                var mb = new MenuBarWindow(
                    _gpu, _configMgr, _backdrop, bounds,
                    IsAnyMenuBarPopupOpen,
                    _windowMgr.IsFullscreenApplicationActive,
                    ShowCrystalSystemMenu,
                    ShowActiveAppMenu,
                    OpenLauncher,
                    OpenExpose,
                    () => OpenGlassLab(),
                    () => OpenControlCenter(bounds),
                    () => OpenNotificationCenter(bounds),
                    () => _ = _mediaSvc.TogglePlayPauseAsync(),
                    delta => { var (v, _, _) = _audioSvc.GetMasterVolumeState(); _audioSvc.SetMasterVolumeScalar(v + delta); _ = PollSystemStatusAsync(); },
                    () => { _audioSvc.ToggleMute(); _ = PollSystemStatusAsync(); },
                    ToggleShowDesktop);

                if (!cfg.MenuBar.AutoHide)
                    mb.Show();

                _menuBars.Add(mb);

                // Only reserve permanent desktop work-area when Auto-Hide is OFF
                if (scr.Primary && !cfg.MenuBar.AutoHide && cfg.WindowManagement.ReserveDesktopWorkArea)
                    _shellSvc.RegisterTopMenuAppBar(mb.Handle, bounds.Width, cfg.MenuBar.Height + 6);
            }
        }

        foreach (var d in _docks) { d.Close(); d.Dispose(); }
        _docks.Clear();
        if (cfg.Dock.Enabled)
        {
            var openWins = _windowMgr.GetOpenWindows();
            foreach (var scr in Screen.AllScreens)
            {
                var dock = new DockWindow(
                    _gpu, _configMgr, _backdrop, scr.Bounds,
                    _icons, _appsSvc, _windowMgr,
                    IsDockPopupOpen,
                    _windowMgr.IsFullscreenApplicationActive,
                    ShowDockItemContextMenu,
                    ShowDockStackPopup,
                    OpenExpose,
                    () => OpenGlassLab(),
                    ToggleShowDesktop);
                dock.GenieLaunchRequested += (item, iconRect) => LaunchAppWithGenie(item.Target, item.Arguments, item.Name, iconRect);
                dock.GenieMinimizeRequested += (hwnd, iconRect) => MinimizeWindowWithGenie(hwnd, iconRect);
                dock.GenieRestoreRequested += (hwnd, iconRect) => RestoreWindowWithGenie(hwnd, iconRect);
                dock.RebuildItems(openWins);
                if (!cfg.Dock.AutoHide)
                    dock.Show();
                else if (peekDock)
                    dock.PeekTemporarily(1800);
                _docks.Add(dock);
            }
        }

        if (!_safeMode)
            _widgetsHost?.SyncWidgetsWithConfig();

        ApplyCaptureAffinityAll();
    }

    private void PollGlobalScreenEdges()
    {
        if (!Win32Interop.GetCursorPos(out var pt)) return;
        var cursor = new Point(pt.X, pt.Y);
        foreach (var mb in _menuBars) mb.PollEdgeTrigger(cursor);
        foreach (var d in _docks) d.PollEdgeTrigger(cursor);
    }

    private void ApplyCaptureAffinityAll()
    {
        foreach (var mb in _menuBars) mb.ApplyCaptureExclusionAffinity();
        foreach (var d in _docks) d.ApplyCaptureExclusionAffinity();
        _controlCenter?.ApplyCaptureExclusionAffinity();
        _notificationCenter?.ApplyCaptureExclusionAffinity();
        _launcher?.ApplyCaptureExclusionAffinity();
        _expose?.ApplyCaptureExclusionAffinity();
        _settingsLab?.ApplyCaptureExclusionAffinity();
        _contextMenu?.ApplyCaptureExclusionAffinity();
        _widgetsHost?.ApplyCaptureExclusion();
    }

    private void OnConfigChanged(CrystalConfig cfg)
    {
        _renderTimer.Interval = Math.Max(15, 1000 / Math.Clamp(cfg.Performance.TargetFps, 24, 90));
        _statusTimer.Interval = Math.Clamp(cfg.Performance.MetricsPollIntervalMs, 500, 5000);
        _backdrop.SetMaxCaptureFps(cfg.Performance.CaptureRateFps);

        foreach (var mb in _menuBars) mb.MarkDirty();
        foreach (var d in _docks) d.MarkDirty();
        _controlCenter?.MarkDirty();
        _notificationCenter?.MarkDirty();
        _launcher?.MarkDirty();
        _settingsLab?.MarkDirty();
        _widgetsHost?.MarkAllDirty();
    }

    private void TickRenderLoop()
    {
        bool anyRendered = false;
        foreach (var mb in _menuBars) anyRendered |= mb.TickAndRenderIfNeeded(commitDComp: false);
        foreach (var d in _docks) anyRendered |= d.TickAndRenderIfNeeded(commitDComp: false);
        if (_controlCenter != null) anyRendered |= _controlCenter.TickAndRenderIfNeeded(commitDComp: false);
        if (_notificationCenter != null) anyRendered |= _notificationCenter.TickAndRenderIfNeeded(commitDComp: false);
        if (_launcher != null) anyRendered |= _launcher.TickAndRenderIfNeeded(commitDComp: false);
        if (_expose != null) anyRendered |= _expose.TickAndRenderIfNeeded(commitDComp: false);
        if (_settingsLab != null) anyRendered |= _settingsLab.TickAndRenderIfNeeded(commitDComp: false);
        if (_contextMenu != null) anyRendered |= _contextMenu.TickAndRenderIfNeeded(commitDComp: false);
        if (_widgetsHost != null) anyRendered |= _widgetsHost.TickAll(commitDComp: false);
        if (_genieOverlay != null) anyRendered |= _genieOverlay.TickAndRenderIfNeeded(commitDComp: false);

        if (anyRendered)
        {
            lock (_gpu.DeviceLock)
                _gpu.DCompDevice.Commit();
        }
    }

    private async Task PollSystemStatusAsync()
    {
        try
        {
            if (!_safeMode)
                _shellSvc.EnsureTaskbarRemainsHiddenIfNeeded(_configMgr.Current.ShellMode);

            var openWins = _windowMgr.GetOpenWindows();
            var fgWin = _windowMgr.GetForegroundExternalWindow();

            _trackedWindows.Clear();
            foreach (var w in openWins)
                _trackedWindows[w.Handle] = w;

            if (!_safeMode)
            {
                var snapTargets = openWins
                    .Where(w => !w.IsMinimized && w.Width >= 100 && w.Height >= 80)
                    .OrderByDescending(w => w.IsForeground)
                    .Take(3)
                    .Where(w => WindowSnapshotService.GetCached(w.Handle, TimeSpan.FromMilliseconds(1500)) == null)
                    .Select(w => (w.Handle, w.IsForeground))
                    .ToList();

                if (snapTargets.Count > 0)
                {
                    _ = Task.Run(() =>
                    {
                        foreach (var (capHwnd, isFg) in snapTargets)
                            WindowSnapshotService.CaptureWindow(capHwnd, allowScreenFallback: isFg, wgcFallback: _backdrop);
                    });
                }
            }
            var net = await _networkSvc.GetNetworkStatusAsync();
            var bt = await _networkSvc.GetBluetoothStatusAsync();
            var bat = PowerBatteryService.GetBatteryStatus();
            var media = await _mediaSvc.GetCurrentAsync();
            var (vol, muted, _) = _audioSvc.GetMasterVolumeState();
            if (_notifSvc.IsSupportedAndAllowed)
                await _notifSvc.RefreshAsync();
            int notifCount = _notifSvc.GetNotifications().Count;

            foreach (var mb in _menuBars)
                mb.UpdateLiveStatus(fgWin, net, bt, bat, media, vol, muted, notifCount);

            foreach (var d in _docks)
                d.RebuildItems(openWins);

            _widgetsHost?.UpdateMetrics();
            if (_widgetsHost != null) await _widgetsHost.RefreshMediaAsync();
        }
        catch (Exception ex)
        {
            CrystalLogger.Debug("StatusPoll", $"Poll skipped: {ex.Message}");
        }
    }

    private void ToggleShowDesktop()
    {
        _controlCenter?.HideAnimated(-8f);
        _notificationCenter?.HideAnimated(-8f);
        _launcher?.HideAnimated(-8f);
        _expose?.HideAnimated(-8f);
        _settingsLab?.HideAnimated(-8f);
        _contextMenu?.Hide();

        _windowMgr.ToggleShowDesktop();
        var openWins = _windowMgr.GetOpenWindows();
        foreach (var d in _docks) d.RebuildItems(openWins);
        foreach (var mb in _menuBars) mb.MarkDirty();
    }

    private void DismissTransientPopups(Form? keep = null)
    {
        if (!ReferenceEquals(keep, _contextMenu) && (_contextMenu?.Visible ?? false)) _contextMenu!.Hide();
        if (!ReferenceEquals(keep, _controlCenter) && (_controlCenter?.Visible ?? false)) _controlCenter!.Hide();
        if (!ReferenceEquals(keep, _notificationCenter) && (_notificationCenter?.Visible ?? false)) _notificationCenter!.Hide();
        if (!ReferenceEquals(keep, _launcher) && (_launcher?.Visible ?? false)) _launcher!.Hide();
        if (!ReferenceEquals(keep, _expose) && (_expose?.Visible ?? false)) _expose!.Hide();
    }

    private void OpenGlassLab(string tab = "Glass Lab")
    {
        DismissTransientPopups(_settingsLab);
        _settingsLab?.ShowAndFocus(tab);
    }

    private void OpenControlCenter(Rectangle monitorBounds)
    {
        DismissTransientPopups(_controlCenter);
        _controlCenter?.ToggleAt(monitorBounds, _configMgr.Current.MenuBar.Height);
    }

    private void OpenNotificationCenter(Rectangle monitorBounds)
    {
        DismissTransientPopups(_notificationCenter);
        _notificationCenter?.ToggleAt(monitorBounds, _configMgr.Current.MenuBar.Height);
    }

    private void OpenLauncher()
    {
        DismissTransientPopups(_launcher);
        var scr = Screen.FromPoint(Cursor.Position).Bounds;
        _launcher?.ToggleCentered(scr);
    }

    private void OpenExpose()
    {
        DismissTransientPopups(_expose);
        var scr = Screen.FromPoint(Cursor.Position).Bounds;
        _expose?.ToggleForMonitor(scr);
    }

    private void ShowCrystalSystemMenu(Point pt)
    {
        DismissTransientPopups(_contextMenu);
        _contextMenu?.ShowMenu(pt, new List<CrystalMenuEntry>
        {
            new("Glass Appearance Lab & Settings", () => { DismissTransientPopups(_settingsLab); _settingsLab?.ShowAndFocus("Glass Lab"); }, ShortcutHint: "Lab"),
            new("Show Desktop Homescreen", ToggleShowDesktop),
            new("Application Launcher", OpenLauncher, ShortcutHint: "Alt+Space"),
            new("Exposé Window Overview", OpenExpose, ShortcutHint: "Ctrl+Alt+↑"),
            new("", IsSeparator: true),
            new("Toggle Dark / Light Mode", () =>
            {
                bool dark = !DisplayBrightnessService.IsWindowsDarkMode();
                DisplayBrightnessService.SetWindowsDarkMode(dark);
                _configMgr.Update(c => c.Appearance.Theme = dark ? ThemeAppearance.Dark : ThemeAppearance.Light, true);
            }),
            new(_shellSvc.IsOriginalTaskbarPeeked ? "Hide Original Windows Taskbar" : "Peek Original Windows Taskbar",
                () => _shellSvc.TogglePeekOriginalTaskbar(_configMgr.Current.ShellMode)),
            new("Sync Dock Pins from Windows", SyncTaskbarPinsFromWindows),
            new("", IsSeparator: true),
            new("Safe Exit & Restore Windows Desktop", SafeExitApplication)
        }, 272);
    }

    private void ShowActiveAppMenu(Point pt, WindowInfo? win)
    {
        string title = win?.ProcessName ?? "Desktop";
        _contextMenu?.ShowMenu(pt, new List<CrystalMenuEntry>
        {
            new($"Active: {title}", Enabled: false),
            new("", IsSeparator: true),
            new("Snap Left Half", () => SnapWindowWithMorph(win?.Handle ?? 0, SnapTarget.LeftHalf), ShortcutHint: "Ctrl+Alt+←"),
            new("Snap Right Half", () => SnapWindowWithMorph(win?.Handle ?? 0, SnapTarget.RightHalf), ShortcutHint: "Ctrl+Alt+→"),
            new("Maximize Window", () => SnapWindowWithMorph(win?.Handle ?? 0, SnapTarget.Maximize), ShortcutHint: "Ctrl+Alt+M"),
            new("Center Focus", () => SnapWindowWithMorph(win?.Handle ?? 0, SnapTarget.CenterFocus)),
            new("", IsSeparator: true),
            new("Minimize", () => { if (win != null) MinimizeWindowWithGenie(win.Handle); }, Enabled: win != null),
            new("Close Window", () => { if (win != null) CloseWindowWithGenie(win.Handle); }, Enabled: win != null)
        }, 238);
    }

    private void ShowDockItemContextMenu(Point pt, DockItemView item)
    {
        if (item.IsSpecialDesktop || item.IsSpecialExpose || item.IsSpecialSettings || item.IsSpecialStack)
        {
            _contextMenu?.ShowMenu(pt, new List<CrystalMenuEntry>
            {
                new(item.IsSpecialDesktop ? "Show Desktop Homescreen" : $"Open {item.Name}", () =>
                {
                    if (item.IsSpecialDesktop) ToggleShowDesktop();
                    else if (item.IsSpecialExpose) OpenExpose();
                    else if (item.IsSpecialSettings) _settingsLab?.ShowAndFocus("Dock");
                }),
                new("Dock Settings...", () => _settingsLab?.ShowAndFocus("Dock"))
            });
            return;
        }

        var entries = new List<CrystalMenuEntry>();
        var allOpen = _windowMgr.GetOpenWindows();
        if (item.IsRunning && item.WindowHandles != null && item.WindowHandles.Count > 0)
        {
            foreach (nint h in item.WindowHandles.Take(8))
            {
                var winObj = allOpen.FirstOrDefault(w => w.Handle == h);
                string title = winObj != null
                    ? (winObj.IsMinimized ? $"Restore: {winObj.Title}" : $"Focus: {winObj.Title}")
                    : $"Focus {item.Name}";
                nint capH = h;
                entries.Add(new(title, () =>
                {
                    if (winObj?.IsMinimized == true)
                        RestoreWindowWithGenie(capH);
                    else
                        _windowMgr.FocusWindow(capH);
                    var latest = _windowMgr.GetOpenWindows();
                    foreach (var d in _docks) d.RebuildItems(latest);
                }));
            }
        }
        else
        {
            entries.Add(new(item.IsRunning ? $"Restore / Focus {item.Name}" : $"Launch {item.Name}", () =>
            {
                if (item.IsRunning && item.ActiveWindowHandle != 0) _windowMgr.FocusWindow(item.ActiveWindowHandle);
                else _appsSvc.LaunchApp(item.Target, item.Arguments);
            }));
        }
        entries.Add(new("Launch New Instance", () => _appsSvc.LaunchApp(item.Target, item.Arguments)));
        entries.Add(new("", IsSeparator: true));

        if (item.IsPinned)
        {
            entries.Add(new("Unpin from Dock", () =>
            {
                _configMgr.Update(c => c.Dock.PinnedApps.RemoveAll(p => $"pin:{p.Id}" == item.Id), true);
                var wins = _windowMgr.GetOpenWindows();
                foreach (var d in _docks) d.RebuildItems(wins);
            }));
        }
        else
        {
            entries.Add(new("Pin to Dock", () =>
            {
                _configMgr.Update(c => c.Dock.PinnedApps.Add(new PinnedAppEntry { Id = Guid.NewGuid().ToString("N")[..8], Name = item.Name, Target = item.Target, Arguments = item.Arguments }), true);
                var wins = _windowMgr.GetOpenWindows();
                foreach (var d in _docks) d.RebuildItems(wins);
            }));
        }

        if (item.IsRunning && item.ActiveWindowHandle != 0)
        {
            entries.Add(new("", IsSeparator: true));
            entries.Add(new("Snap Left", () => SnapWindowWithMorph(item.ActiveWindowHandle, SnapTarget.LeftHalf)));
            entries.Add(new("Snap Right", () => SnapWindowWithMorph(item.ActiveWindowHandle, SnapTarget.RightHalf)));
            entries.Add(new("Minimize", () => MinimizeWindowWithGenie(item.ActiveWindowHandle)));
            entries.Add(new("Close Window", () => CloseWindowWithGenie(item.ActiveWindowHandle)));
        }

        _contextMenu?.ShowMenu(pt, entries, 224);
    }

    private void ShowDockStackPopup(Point pt, DockStackEntry stack)
    {
        string path = Environment.ExpandEnvironmentVariables(stack.FolderPath);
        var entries = new List<CrystalMenuEntry>
        {
            new($"Open '{stack.Name}' in Explorer", () => Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })),
            new("", IsSeparator: true)
        };

        if (Directory.Exists(path))
        {
            var files = new DirectoryInfo(path).GetFileSystemInfos()
                .OrderByDescending(f => f.LastWriteTime)
                .Take(9);
            foreach (var f in files)
            {
                string full = f.FullName;
                entries.Add(new(f.Name, () => Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true })));
            }
        }
        _contextMenu?.ShowMenu(new Point(pt.X - 110, pt.Y - entries.Count * 32 - 16), entries, 250);
    }

    private void ShowWidgetContextMenu(Point pt, WidgetPlacement placement)
    {
        _contextMenu?.ShowMenu(pt, new List<CrystalMenuEntry>
        {
            new($"Hide '{placement.Title}'", () =>
            {
                _configMgr.Update(c =>
                {
                    var w = c.Widgets.Items.FirstOrDefault(i => i.Id == placement.Id);
                    if (w != null) w.Enabled = false;
                }, true);
                _widgetsHost?.SyncWidgetsWithConfig();
            }),
            new("Configure Widgets...", () => _settingsLab?.ShowAndFocus("Widgets"))
        });
    }

    private void RegisterGlobalHotkeys()
    {
        if (!_configMgr.Current.WindowManagement.EnableHotkeys) return;
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_NOREPEAT = 0x4000;

        Win32Interop.RegisterHotKey(_hotkeyWin.Handle, 1, MOD_ALT | MOD_NOREPEAT, (uint)Keys.Space);
        Win32Interop.RegisterHotKey(_hotkeyWin.Handle, 2, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.Up);
        Win32Interop.RegisterHotKey(_hotkeyWin.Handle, 3, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.C);
        Win32Interop.RegisterHotKey(_hotkeyWin.Handle, 4, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.N);
        Win32Interop.RegisterHotKey(_hotkeyWin.Handle, 5, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.Left);
        Win32Interop.RegisterHotKey(_hotkeyWin.Handle, 6, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.Right);
        Win32Interop.RegisterHotKey(_hotkeyWin.Handle, 7, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.M);
    }

    private void OnHotkeyPressed(int id)
    {
        var scr = Screen.FromPoint(Cursor.Position).Bounds;
        switch (id)
        {
            case 1: OpenLauncher(); break;
            case 2: OpenExpose(); break;
            case 3: OpenControlCenter(scr); break;
            case 4: OpenNotificationCenter(scr); break;
            case 5: SnapWindowWithMorph(0, SnapTarget.LeftHalf); break;
            case 6: SnapWindowWithMorph(0, SnapTarget.RightHalf); break;
            case 7: SnapWindowWithMorph(0, SnapTarget.Maximize); break;
        }
    }

    private void SuppressLifecycleEvents(nint hwnd, int ms = 1100)
    {
        if (hwnd != 0)
            _suppressedLifecycleHwnds[hwnd] = DateTime.UtcNow.AddMilliseconds(ms);
    }

    private bool IsLifecycleSuppressed(nint hwnd)
    {
        if (_suppressedLifecycleHwnds.TryGetValue(hwnd, out var until))
        {
            if (DateTime.UtcNow < until) return true;
            _suppressedLifecycleHwnds.Remove(hwnd);
        }
        return false;
    }

    private Rectangle ResolveDockIconRect(nint hwnd, string? processPath = null, string? title = null)
    {
        if (_docks.Count > 0)
            return _docks[0].GetIconScreenRectForWindow(hwnd, processPath, title);

        var bounds = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        return new Rectangle(bounds.Left + bounds.Width / 2 - 26, bounds.Bottom - 72, 52, 52);
    }

    private void PeekDockForGenie()
    {
        if (!_configMgr.Current.Dock.AutoHide) return;
        foreach (var d in _docks) d.PeekTemporarily(850);
    }

    private void LaunchAppWithGenie(string target, string? arguments, string appName, Rectangle iconRect)
    {
        string exeName = string.IsNullOrWhiteSpace(target) ? appName : Path.GetFileNameWithoutExtension(target);
        var existing = new HashSet<nint>(_windowMgr.GetOpenWindows().Select(w => w.Handle));
        _pendingGenieLaunches.RemoveAll(p => DateTime.UtcNow > p.ExpiresUtc);
        _pendingGenieLaunches.Add(new PendingGenieLaunch(appName, exeName, iconRect, existing, DateTime.UtcNow.AddSeconds(4.5)));
        if (!_launchWatchTimer.Enabled)
            _launchWatchTimer.Start();
        PeekDockForGenie();
        _appsSvc.LaunchApp(target, arguments ?? "");
    }

    private void PollPendingGenieLaunches()
    {
        _pendingGenieLaunches.RemoveAll(p => DateTime.UtcNow > p.ExpiresUtc);
        if (_pendingGenieLaunches.Count == 0 || _genieOverlay == null || _safeMode)
        {
            _launchWatchTimer.Stop();
            return;
        }

        var openWins = _windowMgr.GetOpenWindows();
        foreach (var win in openWins)
        {
            if (win.IsMinimized || win.Width < 120 || win.Height < 100) continue;
            if (TryMatchAndTriggerPendingLaunch(win))
                break;
        }

        if (_pendingGenieLaunches.Count == 0)
            _launchWatchTimer.Stop();
    }

    private bool TryMatchAndTriggerPendingLaunch(WindowInfo win)
    {
        if (_pendingGenieLaunches.Count == 0 || _genieOverlay == null) return false;
        string procFriendly = win.ProcessName ?? "";
        string rawExe = string.IsNullOrWhiteSpace(win.ExecutablePath) ? "" : Path.GetFileNameWithoutExtension(win.ExecutablePath);

        int matchIdx = _pendingGenieLaunches.FindIndex(p =>
            !p.ExistingHwnds.Contains(win.Handle) &&
            ((!string.IsNullOrWhiteSpace(rawExe) && (p.ExeName.Equals(rawExe, StringComparison.OrdinalIgnoreCase) || p.ExeName.Contains(rawExe, StringComparison.OrdinalIgnoreCase) || rawExe.Contains(p.ExeName, StringComparison.OrdinalIgnoreCase)))
             || (!string.IsNullOrWhiteSpace(procFriendly) && (p.AppName.Contains(procFriendly, StringComparison.OrdinalIgnoreCase) || procFriendly.Contains(p.AppName, StringComparison.OrdinalIgnoreCase)))
             || (!string.IsNullOrWhiteSpace(win.Title) && win.Title.Contains(p.AppName, StringComparison.OrdinalIgnoreCase))));

        // Fallback: if user just clicked a single app < 3s ago and a brand new top-level foreground window appeared, bind it cleanly
        if (matchIdx < 0 && _pendingGenieLaunches.Count == 1 && win.IsForeground && !_pendingGenieLaunches[0].ExistingHwnds.Contains(win.Handle))
            matchIdx = 0;

        if (matchIdx < 0) return false;

        var launch = _pendingGenieLaunches[matchIdx];
        _pendingGenieLaunches.RemoveAt(matchIdx);
        SuppressLifecycleEvents(win.Handle, 1500);
        StartLaunchEmergeSequence(win.Handle, launch.IconRect);
        return true;
    }

    private void StartLaunchEmergeSequence(nint hwnd, Rectangle iconRect)
    {
        if (_genieOverlay == null || hwnd == 0) return;
        PeekDockForGenie();
        Win32Interop.SetWindowCloaked(hwnd, true);
        _ = Task.Delay(95).ContinueWith(_ =>
        {
            if (_uiMarshaler.IsDisposed) return;
            _uiMarshaler.BeginInvoke(() =>
            {
                if (!Win32Interop.IsWindow(hwnd)) return;
                var snap = WindowSnapshotService.CaptureWindow(hwnd, allowScreenFallback: false);
                if (snap == null)
                {
                    // Some GPU/Chromium apps don't paint while cloaked: uncloak briefly, grab WGC/screen snapshot, then Genie Emerge
                    Win32Interop.SetWindowCloaked(hwnd, false);
                    _ = Task.Delay(60).ContinueWith(__ =>
                    {
                        if (_uiMarshaler.IsDisposed) return;
                        _uiMarshaler.BeginInvoke(() =>
                        {
                            var postSnap = WindowSnapshotService.CaptureWindow(hwnd, allowScreenFallback: true, wgcFallback: _backdrop);
                            if (postSnap != null)
                            {
                                Win32Interop.SetWindowCloaked(hwnd, true);
                                _genieOverlay.PlayGenieEmerge(postSnap, iconRect, _configMgr.Current.Dock.Position, () =>
                                {
                                    Win32Interop.SetWindowCloaked(hwnd, false);
                                    Win32Interop.SetForegroundWindow(hwnd);
                                });
                            }
                            else
                            {
                                Win32Interop.SetForegroundWindow(hwnd);
                            }
                        });
                    });
                    return;
                }

                _genieOverlay.PlayGenieEmerge(snap, iconRect, _configMgr.Current.Dock.Position, () =>
                {
                    Win32Interop.SetWindowCloaked(hwnd, false);
                    Win32Interop.SetForegroundWindow(hwnd);
                });
            });
        });
    }

    private void MinimizeWindowWithGenie(nint hwnd, Rectangle? explicitIconRect = null)
    {
        if (hwnd == 0) return;
        SuppressLifecycleEvents(hwnd);

        var snap = WindowSnapshotService.CaptureWindow(hwnd, allowScreenFallback: true, wgcFallback: _backdrop)
                   ?? WindowSnapshotService.GetCached(hwnd, TimeSpan.FromSeconds(10));
        var winInfo = _windowMgr.GetOpenWindows().FirstOrDefault(w => w.Handle == hwnd)
                      ?? (_trackedWindows.TryGetValue(hwnd, out var tracked) ? tracked : null);
        Rectangle iconRect = explicitIconRect ?? ResolveDockIconRect(hwnd, winInfo?.ExecutablePath ?? winInfo?.ProcessName, winInfo?.Title);

        _windowMgr.MinimizeWindow(hwnd);
        if (snap != null && _genieOverlay != null && !_safeMode)
        {
            PeekDockForGenie();
            _genieOverlay.PlayGenieCollapse(snap, iconRect, _configMgr.Current.Dock.Position);
        }
    }

    private void RestoreWindowWithGenie(nint hwnd, Rectangle? explicitIconRect = null)
    {
        if (hwnd == 0) return;
        SuppressLifecycleEvents(hwnd, 1500);

        var winInfo = _windowMgr.GetOpenWindows().FirstOrDefault(w => w.Handle == hwnd)
                      ?? (_trackedWindows.TryGetValue(hwnd, out var tracked) ? tracked : null);
        Rectangle iconRect = explicitIconRect ?? ResolveDockIconRect(hwnd, winInfo?.ExecutablePath ?? winInfo?.ProcessName, winInfo?.Title);

        if (_genieOverlay == null || _safeMode)
        {
            _windowMgr.FocusWindow(hwnd);
            return;
        }

        PeekDockForGenie();
        Win32Interop.SetWindowCloaked(hwnd, true);
        Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);

        _ = Task.Delay(50).ContinueWith(_ =>
        {
            if (_uiMarshaler.IsDisposed) return;
            _uiMarshaler.BeginInvoke(() =>
            {
                var snap = WindowSnapshotService.CaptureWindow(hwnd, allowScreenFallback: false)
                           ?? WindowSnapshotService.GetCached(hwnd, TimeSpan.FromMinutes(15));
                if (snap != null)
                {
                    if (WindowSnapshotService.TryGetRenderBounds(hwnd, out var liveBounds) && snap.ScreenBounds != liveBounds)
                        snap = snap with { ScreenBounds = liveBounds };

                    _genieOverlay.PlayGenieEmerge(snap, iconRect, _configMgr.Current.Dock.Position, () =>
                    {
                        Win32Interop.SetWindowCloaked(hwnd, false);
                        Win32Interop.SetForegroundWindow(hwnd);
                    });
                }
                else
                {
                    Win32Interop.SetWindowCloaked(hwnd, false);
                    _windowMgr.FocusWindow(hwnd);
                }
            });
        });
    }

    private void CloseWindowWithGenie(nint hwnd, Rectangle? explicitIconRect = null)
    {
        if (hwnd == 0) return;
        SuppressLifecycleEvents(hwnd, 1500);

        var snap = WindowSnapshotService.CaptureWindow(hwnd, allowScreenFallback: true, wgcFallback: _backdrop)
                   ?? WindowSnapshotService.GetCached(hwnd, TimeSpan.FromSeconds(10));
        var winInfo = _windowMgr.GetOpenWindows().FirstOrDefault(w => w.Handle == hwnd)
                      ?? (_trackedWindows.TryGetValue(hwnd, out var tracked) ? tracked : null);
        Rectangle iconRect = explicitIconRect ?? ResolveDockIconRect(hwnd, winInfo?.ExecutablePath ?? winInfo?.ProcessName, winInfo?.Title);

        if (snap != null && _genieOverlay != null && !_safeMode)
        {
            PeekDockForGenie();
            Win32Interop.SetWindowCloaked(hwnd, true);
            _windowMgr.CloseWindow(hwnd);
            _genieOverlay.PlayGenieCollapse(snap, iconRect, _configMgr.Current.Dock.Position, () =>
            {
                if (Win32Interop.IsWindow(hwnd))
                    Win32Interop.SetWindowCloaked(hwnd, false);
                WindowSnapshotService.Invalidate(hwnd);
            });
        }
        else
        {
            _windowMgr.CloseWindow(hwnd);
        }
    }

    private void SnapWindowWithMorph(nint hwnd, SnapTarget target)
    {
        nint targetHwnd = hwnd != 0 ? hwnd : _windowMgr.GetForegroundExternalWindow()?.Handle ?? 0;
        if (targetHwnd == 0) return;

        var beforeSnap = (_genieOverlay != null && !_safeMode)
            ? WindowSnapshotService.CaptureWindow(targetHwnd, allowScreenFallback: true, wgcFallback: _backdrop)
            : null;

        if (beforeSnap != null)
            Win32Interop.SetWindowCloaked(targetHwnd, true);

        bool snapped = _windowMgr.SnapWindow(targetHwnd, target, 12, 12);
        if (snapped && beforeSnap != null && WindowSnapshotService.TryGetRenderBounds(targetHwnd, out var newRect) && _genieOverlay != null)
        {
            _genieOverlay.PlayWindowMorph(beforeSnap, newRect, () =>
            {
                Win32Interop.SetWindowCloaked(targetHwnd, false);
                Win32Interop.SetForegroundWindow(targetHwnd);
            });
        }
        else if (beforeSnap != null)
        {
            Win32Interop.SetWindowCloaked(targetHwnd, false);
        }
    }

    private void OnExternalMinimizeStarted(nint hwnd)
    {
        if (IsLifecycleSuppressed(hwnd) || _genieOverlay == null || _safeMode) return;
        var winInfo = _windowMgr.GetOpenWindows().FirstOrDefault(w => w.Handle == hwnd)
                      ?? (_trackedWindows.TryGetValue(hwnd, out var tracked) ? tracked : null);
        if (winInfo == null) return;

        var snap = WindowSnapshotService.GetCached(hwnd, TimeSpan.FromSeconds(12))
                   ?? WindowSnapshotService.CaptureWindow(hwnd, allowScreenFallback: true, wgcFallback: _backdrop);
        if (snap == null) return;

        PeekDockForGenie();
        Rectangle iconRect = ResolveDockIconRect(hwnd, winInfo.ExecutablePath ?? winInfo.ProcessName, winInfo.Title);
        _genieOverlay.PlayGenieCollapse(snap, iconRect, _configMgr.Current.Dock.Position);
    }

    private void OnExternalMinimizeEnded(nint hwnd)
    {
        if (IsLifecycleSuppressed(hwnd) || _genieOverlay == null || _safeMode) return;
        var winInfo = _windowMgr.GetOpenWindows().FirstOrDefault(w => w.Handle == hwnd)
                      ?? (_trackedWindows.TryGetValue(hwnd, out var tracked) ? tracked : null);
        if (winInfo == null) return;

        var snap = WindowSnapshotService.GetCached(hwnd, TimeSpan.FromMinutes(15))
                   ?? WindowSnapshotService.CaptureWindow(hwnd, allowScreenFallback: false);
        if (snap == null) return;

        if (WindowSnapshotService.TryGetRenderBounds(hwnd, out var liveBounds))
            snap = snap with { ScreenBounds = liveBounds };

        PeekDockForGenie();
        SuppressLifecycleEvents(hwnd, 1000);
        Win32Interop.SetWindowCloaked(hwnd, true);
        Rectangle iconRect = ResolveDockIconRect(hwnd, winInfo.ExecutablePath ?? winInfo.ProcessName, winInfo.Title);
        _genieOverlay.PlayGenieEmerge(snap, iconRect, _configMgr.Current.Dock.Position, () =>
        {
            Win32Interop.SetWindowCloaked(hwnd, false);
            Win32Interop.SetForegroundWindow(hwnd);
        });
    }

    private void OnExternalWindowShown(nint hwnd)
    {
        if (_pendingGenieLaunches.Count == 0 || _genieOverlay == null || _safeMode) return;
        _pendingGenieLaunches.RemoveAll(p => DateTime.UtcNow > p.ExpiresUtc);
        if (_pendingGenieLaunches.Count == 0) return;

        if (!Win32Interop.IsWindowVisible(hwnd) || Win32Interop.IsIconic(hwnd)) return;
        int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
        if ((exStyle & Win32Interop.WS_EX_TOOLWINDOW) != 0) return;
        if (!WindowSnapshotService.TryGetRenderBounds(hwnd, out var rect) || rect.Width < 120 || rect.Height < 100) return;

        var winInfo = _windowMgr.GetOpenWindows().FirstOrDefault(w => w.Handle == hwnd);
        if (winInfo != null)
            TryMatchAndTriggerPendingLaunch(winInfo);
    }

    private void OnExternalWindowDestroyed(nint hwnd)
    {
        if (IsLifecycleSuppressed(hwnd) || _genieOverlay == null || _safeMode) return;
        if (!_trackedWindows.TryGetValue(hwnd, out var winInfo)) return;
        _trackedWindows.Remove(hwnd);

        var snap = WindowSnapshotService.GetCached(hwnd, TimeSpan.FromSeconds(15));
        WindowSnapshotService.Invalidate(hwnd);
        if (snap == null) return;

        PeekDockForGenie();
        Rectangle iconRect = ResolveDockIconRect(hwnd, winInfo.ExecutablePath ?? winInfo.ProcessName, winInfo.Title);
        _genieOverlay.PlayGenieCollapse(snap, iconRect, _configMgr.Current.Dock.Position);
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Glass Lab & Settings", null, (_, __) => _settingsLab?.ShowAndFocus("Glass Lab"));
        menu.Items.Add("Open App Launcher (Alt+Space)", null, (_, __) => OpenLauncher());
        menu.Items.Add("Open Exposé (Ctrl+Alt+Up)", null, (_, __) => OpenExpose());
        menu.Items.Add("Peek / Hide Original Windows Taskbar", null, (_, __) => _shellSvc.TogglePeekOriginalTaskbar(_configMgr.Current.ShellMode));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Safe Exit & Restore Windows Desktop", null, (_, __) => SafeExitApplication());

        var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Project Crystal — Liquid Glass Desktop",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, __) => _settingsLab?.ShowAndFocus("Glass Lab");
        return icon;
    }

    public void SafeExitApplication()
    {
        CrystalLogger.Info("Shell", "Safe Exit requested; restoring Windows taskbar & work area.");
        _shellSvc.UnregisterAppBar();
        _shellSvc.RestoreTaskbarIfBackedUp();
        _configMgr.SaveImmediate();
        _recoveryGuard.EndSessionCleanly();
        Application.Exit();
    }

    public static async Task RunVisualVerificationAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var gpu = new GpuDeviceContext();
        var cfgMgr = new ConfigManager(Path.Combine(outDir, "_cfg"));
        ConfigManager.ApplyMaterialPreset(cfgMgr.Current.Glass, GlassMaterialPreset.CrystalClear);
        var appsSvc = new InstalledAppsService();
        await appsSvc.RefreshInstalledAppsAsync();
        cfgMgr.Current.Dock.PinnedApps = WindowsTaskbarPinsService.ReadRealWindowsTaskbarPins(appsSvc);
        using var icons = new IconTextureRegistry(appsSvc);

        const int bw = 1920, bh = 1080;
        byte[] bgra = BuildOpticalVerificationBackdrop(bw, bh);
        gpu.UpdateFallbackBackdrop(bgra, bw, bh);
        cfgMgr.Current.Glass.CaptureBackend = CaptureBackendMode.WallpaperComposited;

        // 1. Optical Refraction Proof over Crisp Text & Grid
        for (int frame = 0; frame < 2; frame++)
        {
            byte[] movedBgra = BuildOpticalVerificationBackdrop(bw, bh, frame * 36);
            gpu.UpdateFallbackBackdrop(movedBgra, bw, bh);

            using var surf = new LiquidGlassSurface(gpu, 0, 960, 260);
            var shapes = new[]
            {
                new GlassShapeItem { X = 120, Y = 40, Width = 720, Height = 160, CornerRadius = 36, SquirclePower = 3.2f, HoverGlow = 0.22f, BevelScale = 1.15f }
            };
            surf.RenderFrame(null, cfgMgr.Current, new Point(480, 410), new PointF(420, 120), shapes, 1.0f + frame * 0.5f, true, d2d =>
            {
                var pal = CrystalUIHelpers.GetPalette(true, cfgMgr.Current.Appearance.AccentColorHex);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, $"CRYSTAL GLOSSY LIQUID GLASS — FRAME {frame + 1}", 140, 56, 680, 28, 12.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
            });
            SaveBgraPng(surf.CaptureOffscreenBgra(), 960, 260, Path.Combine(outDir, $"refraction_proof_frame{frame + 1}.png"));
        }

        // 2. Render Dock (with REAL synced Windows pinned apps + Bespoke Luxury Special Squircle Icons!)
        gpu.UpdateFallbackBackdrop(bgra, bw, bh);
        using (var dockSurf = new LiquidGlassSurface(gpu, 0, 960, 126))
        {
            var apps = cfgMgr.Current.Dock.PinnedApps;
            float baseSz = 48f;
            float gap = 14f;
            float pad = 16f;
            int specialCount = 4; // Stacks, Desktop, Exposé, Glass Lab
            float totalW = pad * 2f + apps.Count * baseSz + (apps.Count - 1) * gap + 20f + specialCount * baseSz + (specialCount - 1) * gap + 14f;
            float startX = (960f - totalW) * 0.5f;
            var dockShapes = new[] { new GlassShapeItem { X = startX, Y = 30, Width = totalW, Height = 74, CornerRadius = 23, SquirclePower = 3.2f, HoverGlow = 0.24f, BevelScale = 1.0f } };
            dockSurf.RenderFrame(null, cfgMgr.Current, new Point(480, 940), new PointF(480, 68), dockShapes, 0.5f, true, d2d =>
            {
                var pal = CrystalUIHelpers.GetPalette(true, cfgMgr.Current.Appearance.AccentColorHex);
                float x = startX + pad;
                for (int i = 0; i < apps.Count; i++)
                {
                    float sz = (i == 2) ? 58f : (i == 1 || i == 3 ? 52f : baseSz);
                    float y = 67f - sz * 0.5f - (sz - baseSz) * 0.5f;
                    var iconObj = icons.GetIcon(d2d, apps[i].Target);
                    if (iconObj != null) CrystalUIHelpers.DrawBitmapRect(d2d, iconObj.Bitmap, x, y, sz, sz, 1f, iconObj.HasOpaqueCorners);
                    else CrystalUIHelpers.DrawRoundedRect(d2d, x, y, sz, sz, 12, pal.AccentSoft);
                    x += sz + gap;
                }
                using (var div = d2d.CreateSolidColorBrush(pal.Divider))
                    d2d.DrawLine(new System.Numerics.Vector2(x + 2f, 44f), new System.Numerics.Vector2(x + 2f, 90f), div, 1.2f);
                x += 18f;
                CrystalUIHelpers.DrawLuxuryFolderStackIcon(d2d, x, 67f - baseSz * 0.5f, baseSz); x += baseSz + gap;
                CrystalUIHelpers.DrawLuxuryDesktopIcon(d2d, x, 67f - baseSz * 0.5f, baseSz); x += baseSz + gap;
                CrystalUIHelpers.DrawLuxuryExposeIcon(d2d, x, 67f - baseSz * 0.5f, baseSz); x += baseSz + gap;
                CrystalUIHelpers.DrawLuxuryGlassLabIcon(d2d, x, 67f - baseSz * 0.5f, baseSz);
            });
            SaveBgraPng(dockSurf.CaptureOffscreenBgra(), 960, 126, Path.Combine(outDir, "surface_dock.png"));
        }

        // 3. Render Top Menu Bar
        using (var mbSurf = new LiquidGlassSurface(gpu, 0, 1280, 48))
        {
            var mbShapes = new[] { new GlassShapeItem { X = 10, Y = 4, Width = 1260, Height = 32, CornerRadius = 14, SquirclePower = 3.0f, HoverGlow = 0.14f, BevelScale = 0.68f } };
            mbSurf.RenderFrame(null, cfgMgr.Current, new Point(320, 4), new PointF(640, 20), mbShapes, 0.5f, true, d2d =>
            {
                var pal = CrystalUIHelpers.GetPalette(true, cfgMgr.Current.Appearance.AccentColorHex);
                CrystalUIHelpers.DrawCrystalEmblem(d2d, 30, 20, 16, pal.PrimaryText);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Google Chrome    Apps    Exposé    Glass Lab", 52, 4, 420, 32, 12.5f, pal.PrimaryText, FontWeight.SemiBold);
                CrystalUIHelpers.DrawEthernetIcon(d2d, 976, 20, 14, pal.PrimaryText, true);
                CrystalUIHelpers.DrawVolumeIcon(d2d, 1010, 20, 13.5f, pal.PrimaryText, 0.7f, false);
                CrystalUIHelpers.DrawSearchIcon(d2d, 1044, 20, 13.5f, pal.PrimaryText);
                CrystalUIHelpers.DrawControlCenterIcon(d2d, 1074, 20, 15, pal.PrimaryText);
                CrystalUIHelpers.DrawBellIcon(d2d, 1102, 20, 14, pal.PrimaryText);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Wed 25 Nov  12:55 PM", 1120, 4, 146, 32, 12f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Trailing);
            });
            SaveBgraPng(mbSurf.CaptureOffscreenBgra(), 1280, 48, Path.Combine(outDir, "surface_menubar.png"));
        }

        // 4. Render Redesigned Clean Control Center (single cohesive glass shell + tactile gradient controls)
        using (var ccSurf = new LiquidGlassSurface(gpu, 0, 368, 520))
        {
            var ccShapes = new[]
            {
                new GlassShapeItem { X = 4, Y = 4, Width = 360, Height = 512, CornerRadius = 22, SquirclePower = 3.2f, HoverGlow = 0.08f, BevelScale = 1.0f },
                new GlassShapeItem { X = 268, Y = 338, Width = 38, Height = 38, CornerRadius = 19, SquirclePower = 2.0f, HoverGlow = 0.38f, BevelScale = 1.15f }
            };
            ccSurf.RenderFrame(null, cfgMgr.Current, new Point(1500, 48), new PointF(184, 180), ccShapes, 0.8f, true, d2d =>
            {
                var pal = CrystalUIHelpers.GetPalette(true, cfgMgr.Current.Appearance.AccentColorHex);
                CrystalUIHelpers.DrawHairlineCard(d2d, 18, 18, 160, 112, 16, pal);
                CrystalUIHelpers.DrawHairlineCard(d2d, 190, 18, 160, 112, 16, pal);
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, 44, 48, 18, 18, new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.35f), default);
                CrystalUIHelpers.DrawEthernetIcon(d2d, 44, 48, 15, pal.Accent, true);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Ethernet", 64, 31, 104, 18, 12f, pal.PrimaryText, FontWeight.SemiBold);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "1000 Mbps", 64, 48, 104, 16, 10.5f, pal.SecondaryText);
                CrystalUIHelpers.DrawBluetoothIcon(d2d, 44, 98, 14, pal.SubtleText, false);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Bluetooth", 64, 81, 104, 18, 12f, pal.PrimaryText, FontWeight.SemiBold);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "No Hardware", 64, 98, 104, 16, 10.5f, pal.SecondaryText);

                CrystalUIHelpers.DrawMoonIcon(d2d, 228, 42, 15, pal.Accent);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Dark", 190, 54, 76, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
                CrystalUIHelpers.DrawBellIcon(d2d, 312, 42, 14.5f, pal.PrimaryText);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Alerts", 274, 54, 76, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
                CrystalUIHelpers.DrawExposeGridIcon(d2d, 228, 96, 15, pal.PrimaryText);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Exposé", 190, 108, 76, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
                CrystalUIHelpers.DrawCrystalEmblem(d2d, 312, 96, 15, pal.Accent);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Glass Lab", 274, 108, 76, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);

                CrystalUIHelpers.DrawHairlineCard(d2d, 18, 142, 332, 62, 15, pal);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Display  •  80%", 32, 150, 200, 16, 11f, pal.SecondaryText, FontWeight.SemiBold);
                CrystalUIHelpers.DrawSunIcon(d2d, 40, 182, 15, pal.PrimaryText);
                CrystalUIHelpers.DrawGlassSlider(d2d, 60, 182, 274, 0.80f, pal);

                CrystalUIHelpers.DrawHairlineCard(d2d, 18, 214, 332, 92, 15, pal);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Realtek High Definition Audio", 32, 222, 240, 18, 11.5f, pal.SecondaryText, FontWeight.SemiBold);
                CrystalUIHelpers.DrawVolumeIcon(d2d, 40, 264, 15, pal.PrimaryText, 0.68f, false);
                CrystalUIHelpers.DrawGlassSlider(d2d, 60, 264, 238, 0.68f, pal);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "68%", 302, 254, 36, 20, 11f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Trailing);

                CrystalUIHelpers.DrawHairlineCard(d2d, 18, 316, 332, 86, 16, pal);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "NOW PLAYING", 32, 326, 90, 14, 9.5f, pal.SubtleText, FontWeight.Bold);
                CrystalUIHelpers.DrawAcousticWaveform(d2d, 116, 333, 0.9f, pal.Accent, true, 7);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Project Crystal Audio", 32, 344, 184, 20, 13f, pal.PrimaryText, FontWeight.SemiBold);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "Live Desktop Environment", 32, 366, 184, 18, 11f, pal.SecondaryText);
                CrystalUIHelpers.DrawGlassOrbButton(d2d, 246, 358, 14f, pal.Accent, false);
                CrystalUIHelpers.DrawGlassOrbButton(d2d, 287, 358, 19f, pal.Accent, true);
                CrystalUIHelpers.DrawGlassOrbButton(d2d, 328, 358, 14f, pal.Accent, false);
                CrystalUIHelpers.DrawSkipIcon(d2d, 246, 358, 13, pal.PrimaryText, false);
                CrystalUIHelpers.DrawPlayIcon(d2d, 287, 358, 15, new Color4(1f, 1f, 1f, 1f));
                CrystalUIHelpers.DrawSkipIcon(d2d, 328, 358, 13, pal.PrimaryText, true);

                CrystalUIHelpers.DrawHairlineCard(d2d, 18, 412, 332, 82, 15, pal);
                CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, "WINDOW LAYOUT", 32, 420, 160, 14, 9.5f, pal.SubtleText, FontWeight.Bold);
                string[] labels = { "Left", "Right", "Full", "Center" };
                for (int i = 0; i < 4; i++)
                {
                    float rx = 30f + i * 79f;
                    CrystalUIHelpers.DrawRoundedRect(d2d, rx, 440, 73, 42, 10, pal.ControlSurface, pal.HairlineStroke, 0.7f);
                    CrystalUIHelpers.DrawSnapTileGlyph(d2d, rx + 36.5f, 455, 16, pal.PrimaryText, i);
                    CrystalUIHelpers.DrawTextCrisp(d2d, gpu.DWriteFactory, labels[i], rx, 464, 73, 16, 10f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Center);
                }
            });
            SaveBgraPng(ccSurf.CaptureOffscreenBgra(), 368, 520, Path.Combine(outDir, "surface_control_center.png"));
        }

        // 5. Render Spotlight Command Palette Launcher (840x598)
        using (var lnSurf = new LiquidGlassSurface(gpu, 0, 840, 598))
        {
            var allApps = appsSvc.GetCachedApps();
            var filtered = allApps.Take(12).ToList();
            var catList = new List<(string, RectangleF)>();
            var itemList = new List<(AppEntry, RectangleF, RectangleF)>();
            var actList = new List<(RectangleF, Action)>();
            lnSurf.RenderFrame(null, cfgMgr.Current, new Point(540, 140), new PointF(420, 220),
                LauncherWindow.BuildLauncherShapes(840, 598, true, true), 0.7f, true, d2d =>
                {
                    LauncherWindow.RenderLauncherContent(
                        d2d, gpu.DWriteFactory, 840, 598, true, cfgMgr.Current, icons,
                        allApps, filtered, Array.Empty<WindowInfo>(),
                        "", "All", 0, "= 1,024", "", 0.4f,
                        18f, 176f, 1f, 18f, 44f, true,
                        catList, itemList, actList);
                });
            SaveBgraPng(lnSurf.CaptureOffscreenBgra(), 840, 598, Path.Combine(outDir, "surface_launcher.png"));
        }

        // 6. Render Aurelia-Style Crystal Studio / Glass Lab (1052x708)
        using (var labSurf = new LiquidGlassSurface(gpu, 0, 1052, 708))
        {
            var rg = new RecoveryGuard(Path.Combine(outDir, "_cfg"));
            var hw = HardwareInspector.Inspect(gpu.AdapterName, gpu.AdapterMemoryMb, 1920, 1080, 60);
            using var labWin = new SettingsGlassLabWindow(
                gpu, cfgMgr, null, hw, new DisplayBrightnessService(), new ShellIntegrationService(rg),
                new NotificationService(), new WindowManagerService(),
                () => { }, () => { }, () => { }, () => { }, () => { });
            labSurf.RenderFrame(null, cfgMgr.Current, new Point(400, 160), new PointF(526, 220),
                SettingsGlassLabWindow.BuildStudioGlassShapes(1052, 708, "Glass Lab", 0.75f), 0.75f, true, d2d =>
                {
                    labWin.DrawStudioPreview(d2d, 0.75f);
                });
            SaveBgraPng(labSurf.CaptureOffscreenBgra(), 1052, 708, Path.Combine(outDir, "surface_glasslab.png"));
        }

        // 7. Render Luxury Desktop Widgets Strip (Clock, Weather, Hardware Telemetry, Media)
        using (var wgSurf = new LiquidGlassSurface(gpu, 0, 1180, 210))
        {
            var wgShapes = new[]
            {
                new GlassShapeItem { X = 14, Y = 14, Width = 280, Height = 128, CornerRadius = 20, SquirclePower = 3.2f, HoverGlow = 0.14f },
                new GlassShapeItem { X = 308, Y = 14, Width = 280, Height = 144, CornerRadius = 20, SquirclePower = 3.2f, HoverGlow = 0.14f },
                new GlassShapeItem { X = 602, Y = 14, Width = 280, Height = 184, CornerRadius = 20, SquirclePower = 3.2f, HoverGlow = 0.14f },
                new GlassShapeItem { X = 896, Y = 14, Width = 270, Height = 124, CornerRadius = 20, SquirclePower = 3.2f, HoverGlow = 0.14f }
            };
            wgSurf.RenderFrame(null, cfgMgr.Current, new Point(360, 240), new PointF(400, 100), wgShapes, 1.2f, true, d2d =>
            {
                var pal = CrystalUIHelpers.GetPalette(true, cfgMgr.Current.Appearance.AccentColorHex);
                void TranslateAndDraw(float ox, float oy, Action draw)
                {
                    d2d.Transform = System.Numerics.Matrix3x2.CreateTranslation(ox, oy);
                    draw();
                    d2d.Transform = System.Numerics.Matrix3x2.Identity;
                }
                TranslateAndDraw(14, 14, () => DesktopWidgetWindow.DrawClockWidget(d2d, gpu.DWriteFactory, pal, cfgMgr.Current, 280, 128));
                TranslateAndDraw(308, 14, () => DesktopWidgetWindow.DrawWeatherWidget(d2d, gpu.DWriteFactory, pal, cfgMgr.Current,
                    new WeatherInfo(true, "Cupertino", 22.4, 58, 12, "Partly Cloudy", 2, 0, DateTimeOffset.Now, "Open-Meteo"), 280, 144, null));
                var metrics = new SystemMetricsSnapshot(32.5, 9.4, 16.0, 58.8, 14200, 3100, new[] { new DriveUsageInfo("C:", "System", 184, 512, 36.0) }, 142, TimeSpan.FromHours(4));
                var cpuHist = Enumerable.Range(0, 20).Select(i => 0.25f + 0.45f * MathF.Abs(MathF.Sin(i * 0.4f))).ToArray();
                TranslateAndDraw(602, 14, () => DesktopWidgetWindow.DrawTelemetryWidget(d2d, gpu.DWriteFactory, pal, metrics, cpuHist, 280, 184));
                var media = new MediaSessionInfo(true, "Spotify", "Midnight City (Studio Mix)", "M83 — Hurry Up, We're Dreaming", "", true, true, true, true, null);
                TranslateAndDraw(896, 14, () => DesktopWidgetWindow.DrawMediaWidget(d2d, gpu.DWriteFactory, pal, media, 270, 124, 1.2f, null, null, null));
            });
            SaveBgraPng(wgSurf.CaptureOffscreenBgra(), 1180, 210, Path.Combine(outDir, "surface_widgets.png"));
        }

        // 8. Render DeskMinder + Liquid-Glass Switch Notification Center (384x650)
        using (var ntSurf = new LiquidGlassSurface(gpu, 0, 384, 650))
        {
            var ntShapes = new[] { new GlassShapeItem { X = 4, Y = 4, Width = 376, Height = 642, CornerRadius = 22, SquirclePower = 3.2f, HoverGlow = 0.14f, BevelScale = 1.0f } };
            var sampleTasks = new[]
            {
                new DeskMinderTask { Id = "1", Title = "Deep focus sprint", TimeTag = "25m", Completed = false },
                new DeskMinderTask { Id = "2", Title = "Check Glass Lab shaders", TimeTag = "Done", Completed = true },
                new DeskMinderTask { Id = "3", Title = "Review window spaces", TimeTag = "15m", Completed = false }
            };
            var sampleNotifs = new[]
            {
                new NotificationItem("n1", 1, "Visual Studio Code", "vscode", "Build Succeeded", "ProjectCrystal solution compiled with zero warnings.", DateTimeOffset.Now)
            };
            var ntZones = new List<(string, RectangleF, Action)>();
            ntSurf.RenderFrame(null, cfgMgr.Current, new Point(1500, 48), new PointF(192, 240), ntShapes, 0.8f, true, d2d =>
            {
                NotificationCenterWindow.RenderNotificationCenterContent(
                    d2d, gpu.DWriteFactory, 384, 650, true, cfgMgr.Current,
                    sampleNotifs, true, "Windows Notification Listener Active",
                    0.52f, 4.2f, 900f, 742f, true,
                    sampleTasks, "", ntZones);
            });
            SaveBgraPng(ntSurf.CaptureOffscreenBgra(), 384, 650, Path.Combine(outDir, "surface_notifications.png"));
        }

        // 9. Render Genie Effect Proof Frames (curling window funnel into exact bottom Dock icon)
        var sampleWinRect = new Rectangle(380, 120, 760, 560);
        var sampleIconRect = new Rectangle(920, 995, 56, 56);
        byte[] sampleWinBgra = BuildSampleAppWindowBgra(sampleWinRect.Width, sampleWinRect.Height);
        var sampleSnap = new WindowSnapshot(0, sampleWinBgra, sampleWinRect.Width, sampleWinRect.Height, sampleWinRect, DateTime.UtcNow);
        float[] genieProgress = { 0.15f, 0.38f, 0.62f, 0.84f };
        for (int gi = 0; gi < genieProgress.Length; gi++)
        {
            byte[] frameBgra = GenieOverlayWindow.RenderOffscreenSampleBgra(gpu, bw, bh, sampleSnap, sampleIconRect, DockPosition.Bottom, genieProgress[gi]);
            SaveBgraPng(frameBgra, bw, bh, Path.Combine(outDir, $"genie_proof_{gi + 1}.png"));
        }
    }

    private static byte[] BuildSampleAppWindowBgra(int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 24, 28, 38));
            using var barBrush = new SolidBrush(Color.FromArgb(255, 38, 44, 60));
            g.FillRectangle(barBrush, 0, 0, w, 46);
            using var red = new SolidBrush(Color.FromArgb(255, 255, 95, 86));
            using var yellow = new SolidBrush(Color.FromArgb(255, 255, 189, 46));
            using var green = new SolidBrush(Color.FromArgb(255, 39, 201, 63));
            g.FillEllipse(red, 18, 15, 14, 14);
            g.FillEllipse(yellow, 40, 15, 14, 14);
            g.FillEllipse(green, 62, 15, 14, 14);
            using var cardBrush = new SolidBrush(Color.FromArgb(255, 48, 86, 168));
            g.FillRectangle(cardBrush, 28, 72, w - 56, 140);
            using var font = new Font("Segoe UI", 22f, System.Drawing.FontStyle.Bold);
            using var white = new SolidBrush(Color.White);
            g.DrawString("GENIE WINDOW SNAPSHOT", font, white, 48, 122);
            using var gridPen = new Pen(Color.FromArgb(140, 110, 180, 255), 2f);
            for (int y = 240; y < h - 30; y += 44) g.DrawLine(gridPen, 28, y, w - 28, y);
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] bytes = new byte[w * h * 4];
        Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);
        return bytes;
    }

    private static byte[] BuildOpticalVerificationBackdrop(int w, int h, int shiftX = 0)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Point(0, 0), new Point(w, h),
                Color.FromArgb(255, 16, 38, 86), Color.FromArgb(255, 134, 34, 108));
            g.FillRectangle(grad, 0, 0, w, h);

            using var gridPen = new Pen(Color.FromArgb(145, 255, 255, 255), 2f);
            for (int x = (shiftX % 48); x < w; x += 48) g.DrawLine(gridPen, x, 0, x, h);
            for (int y = 0; y < h; y += 48) g.DrawLine(gridPen, 0, y, w, y);

            using var accentBrush = new SolidBrush(Color.FromArgb(230, 56, 224, 188));
            g.FillEllipse(accentBrush, 520 + shiftX, 390, 170, 170);
            using var coralBrush = new SolidBrush(Color.FromArgb(230, 255, 112, 68));
            g.FillRectangle(coralBrush, 1020 + shiftX, 430, 210, 120);

            using var font = new Font("Segoe UI", 28f, System.Drawing.FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.White);
            for (int row = 0; row < 12; row++)
            {
                g.DrawString($"CRYSTAL OPTICS REFRACTION TEST • ROW {row:00} • SHARP TEXT", font, textBrush, 140 + shiftX, 80 + row * 82);
            }
        }

        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] bytes = new byte[w * h * 4];
        Marshal.Copy(bd.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(bd);
        return bytes;
    }

    private static void SaveBgraPng(byte[] bgra, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
        bmp.UnlockBits(bd);
        bmp.Save(path, ImageFormat.Png);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderTimer.Stop();
        _edgeSensorTimer.Stop();
        _statusTimer.Stop();
        _weatherTimer.Stop();
        _launchWatchTimer.Stop();
        _renderTimer.Dispose();
        _edgeSensorTimer.Dispose();
        _statusTimer.Dispose();
        _weatherTimer.Dispose();
        _launchWatchTimer.Dispose();

        for (int i = 1; i <= 7; i++)
            Win32Interop.UnregisterHotKey(_hotkeyWin.Handle, i);
        _hotkeyWin.Dispose();

        _shellSvc.UnregisterAppBar();
        _shellSvc.RestoreTaskbarIfBackedUp();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _lifecycleMonitor.Dispose();
        _genieOverlay?.Close();
        _genieOverlay?.Dispose();
        _uiMarshaler.Dispose();

        foreach (var mb in _menuBars) { mb.Close(); mb.Dispose(); }
        foreach (var d in _docks) { d.Close(); d.Dispose(); }
        _controlCenter?.Close(); _controlCenter?.Dispose();
        _notificationCenter?.Close(); _notificationCenter?.Dispose();
        _launcher?.Close(); _launcher?.Dispose();
        _expose?.Close(); _expose?.Dispose();
        _settingsLab?.Close(); _settingsLab?.Dispose();
        _contextMenu?.Close(); _contextMenu?.Dispose();
        _widgetsHost?.Dispose();

        _backdrop.Dispose();
        _icons.Dispose();
        _gpu.Dispose();
    }
}
