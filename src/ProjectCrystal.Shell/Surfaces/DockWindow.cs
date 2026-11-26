using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Apps;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.Interop;
using ProjectCrystal.Native.WindowManagement;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed record DockItemView(
    string Id,
    string Name,
    string Target,
    string Arguments,
    bool IsPinned,
    bool IsRunning,
    bool IsForeground,
    bool IsSeparator,
    bool IsSpecialStack,
    bool IsSpecialExpose,
    bool IsSpecialSettings,
    nint ActiveWindowHandle,
    bool IsSpecialDesktop = false,
    bool IsMinimized = false,
    int WindowCount = 1,
    IReadOnlyList<nint>? WindowHandles = null
);

public sealed class DockWindow : GlassWindowBase
{
    private readonly Rectangle _monitorBounds;
    private readonly IconTextureRegistry _icons;
    private readonly InstalledAppsService _appsService;
    private readonly WindowManagerService _windowMgr;
    private readonly Func<bool> _isDockMenuOpen;
    private readonly Func<Rectangle, bool> _isFullscreenActive;
    private readonly Action<Point, DockItemView> _onShowItemContextMenu;
    private readonly Action<Point, DockStackEntry> _onShowStackPopup;
    private readonly Action _onOpenExpose;
    private readonly Action _onOpenSettings;
    private readonly Action? _onShowDesktop;

    private readonly List<DockItemView> _items = new();
    private readonly Dictionary<string, SpringFloat> _scaleSprings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpringFloat> _bounceSprings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DockItemView Item, RectangleF HitRect, float CenterX, float CenterY, float Size)> _renderedSlots = new();

    private SpringFloat _autoHideSlide;
    private SpringFloat _tooltipAlpha = new(0f);
    private SpringFloat _hoverPillX = new(0f);
    private SpringFloat _hoverPillY = new(0f);
    private SpringFloat _hoverPillSize = new(48f);
    private SpringFloat _hoverPillAlpha = new(0f);
    private SpringFloat _capsuleGlow = new(0.06f);
    private float _activePulseSec;
    private bool _wantVisible;
    private DateTime _lastActiveMouseUtc = DateTime.UtcNow;
    private DateTime _peekUntilUtc = DateTime.MinValue;
    private int _hoverSlotIdx = -1;
    private string _lastTooltipText = "";
    private float _lastTooltipX;
    private float _lastTooltipY;
    private int _draggedPinnedIndex = -1;
    private bool _isDragging;
    private Point _mouseDownPoint;
    private RectangleF _dockCapsuleRect;

    public event Action<DockItemView, Rectangle>? GenieLaunchRequested;
    public event Action<nint, Rectangle>? GenieMinimizeRequested;
    public event Action<nint, Rectangle>? GenieRestoreRequested;

    public DockWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        Rectangle monitorBounds,
        IconTextureRegistry icons,
        InstalledAppsService appsService,
        WindowManagerService windowMgr,
        Func<bool> isDockMenuOpen,
        Func<Rectangle, bool> isFullscreenActive,
        Action<Point, DockItemView> onShowItemContextMenu,
        Action<Point, DockStackEntry> onShowStackPopup,
        Action onOpenExpose,
        Action onOpenSettings,
        Action? onShowDesktop = null)
        : base(gpu, configMgr, backdrop, noActivate: true, topMost: true)
    {
        _monitorBounds = monitorBounds;
        _icons = icons;
        _appsService = appsService;
        _windowMgr = windowMgr;
        _isDockMenuOpen = isDockMenuOpen;
        _isFullscreenActive = isFullscreenActive;
        _onShowItemContextMenu = onShowItemContextMenu;
        _onShowStackPopup = onShowStackPopup;
        _onOpenExpose = onOpenExpose;
        _onOpenSettings = onOpenSettings;
        _onShowDesktop = onShowDesktop;

        bool autoHide = Config.Dock.AutoHide;
        _wantVisible = !autoHide;
        _autoHideSlide = new SpringFloat(autoHide ? 120f : 0f);

        UpdateDockBounds();

        MouseDown += OnDockMouseDown;
        MouseMove += OnDockMouseMove;
        MouseUp += OnDockMouseUp;
        MouseLeave += (_, __) => { _hoverSlotIdx = -1; MarkDirty(); };
    }

    public void UpdateDockBounds()
    {
        var pos = Config.Dock.Position;
        if (pos == DockPosition.Bottom)
        {
            int hostW = Math.Min(_monitorBounds.Width - 12, 1880);
            int hostH = 160;
            int x = _monitorBounds.X + (_monitorBounds.Width - hostW) / 2;
            int y = _monitorBounds.Bottom - hostH;
            SetBounds(x, y, hostW, hostH);
        }
        else if (pos == DockPosition.Left)
        {
            int hostW = 160;
            int hostH = Math.Min(_monitorBounds.Height - 90, 960);
            int y = _monitorBounds.Y + 48 + ((_monitorBounds.Height - 48 - hostH) / 2);
            SetBounds(_monitorBounds.X, y, hostW, hostH);
        }
        else
        {
            int hostW = 160;
            int hostH = Math.Min(_monitorBounds.Height - 90, 960);
            int y = _monitorBounds.Y + 48 + ((_monitorBounds.Height - 48 - hostH) / 2);
            SetBounds(_monitorBounds.Right - hostW, y, hostW, hostH);
        }
        ComputeLayoutAndSlots();
        MarkDirty();
    }

    public void RebuildItems(IReadOnlyList<WindowInfo> openWindows)
    {
        var next = new List<DockItemView>();
        var matchedHandles = new HashSet<nint>();

        // 1. Pinned apps (deduplicated; marks ALL matching open window handles so zero duplicates appear!)
        var seenPinTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in Config.Dock.PinnedApps)
        {
            if (pin.Target.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase))
                continue;
            string normPin = WindowsTaskbarPinsService.NormalizeKey(pin.Target);
            if (!seenPinTargets.Add(normPin))
                continue;

            var matchingWindows = FindAllMatchingWindows(pin, openWindows, matchedHandles);
            foreach (var mw in matchingWindows) matchedHandles.Add(mw.Handle);

            var primaryWin = matchingWindows.FirstOrDefault(w => w.IsForeground)
                ?? matchingWindows.FirstOrDefault(w => !w.IsMinimized)
                ?? matchingWindows.FirstOrDefault();
            _icons.PreloadIcon(pin.Target);

            next.Add(new DockItemView(
                Id: $"pin:{pin.Id}",
                Name: pin.Name,
                Target: pin.Target,
                Arguments: pin.Arguments,
                IsPinned: true,
                IsRunning: matchingWindows.Count > 0,
                IsForeground: matchingWindows.Any(w => w.IsForeground),
                IsSeparator: false,
                IsSpecialStack: false,
                IsSpecialExpose: false,
                IsSpecialSettings: false,
                ActiveWindowHandle: primaryWin?.Handle ?? 0,
                IsSpecialDesktop: false,
                IsMinimized: matchingWindows.Count > 0 && matchingWindows.All(w => w.IsMinimized),
                WindowCount: Math.Max(1, matchingWindows.Count),
                WindowHandles: matchingWindows.Count > 0 ? matchingWindows.Select(w => w.Handle).ToArray() : null));
        }

        // 2. Unpinned running applications (grouped cleanly, Foreground first, includes minimized & Electron/AppX/elevated windows)
        if (Config.Dock.ShowRecentApps)
        {
            var unpinnedGroups = openWindows
                .Where(w => !matchedHandles.Contains(w.Handle)
                         && !w.ExecutablePath.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase)
                         && !w.ProcessName.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase))
                .GroupBy(w =>
                {
                    if (string.IsNullOrWhiteSpace(w.ExecutablePath))
                        return $"proc:{w.ProcessName}";
                    if (Path.GetFileNameWithoutExtension(w.ExecutablePath).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                        return $"uwp:{w.Title}";
                    return $"exe:{WindowsTaskbarPinsService.NormalizeKey(w.ExecutablePath)}";
                }, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Any(w => w.IsForeground))
                .ThenBy(g => g.All(w => w.IsMinimized))
                .Take(12)
                .ToList();

            if (unpinnedGroups.Count > 0)
            {
                if (Config.Dock.ShowSeparators)
                    next.Add(new DockItemView("sep-run", "", "", "", false, false, false, true, false, false, false, 0));

                foreach (var grp in unpinnedGroups)
                {
                    var grpList = grp.ToList();
                    var rep = grpList.FirstOrDefault(w => w.IsForeground)
                        ?? grpList.FirstOrDefault(w => !w.IsMinimized)
                        ?? grpList.First();
                    bool isFrameHost = !string.IsNullOrEmpty(rep.ExecutablePath)
                        && Path.GetFileNameWithoutExtension(rep.ExecutablePath).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
                    string displayName = isFrameHost && !string.IsNullOrWhiteSpace(rep.Title) ? rep.Title : rep.ProcessName;
                    _icons.PreloadIcon(rep.ExecutablePath);
                    next.Add(new DockItemView(
                        Id: $"win:{grp.Key.ToLowerInvariant()}",
                        Name: displayName,
                        Target: rep.ExecutablePath,
                        Arguments: "",
                        IsPinned: false,
                        IsRunning: true,
                        IsForeground: grpList.Any(w => w.IsForeground),
                        IsSeparator: false,
                        IsSpecialStack: false,
                        IsSpecialExpose: false,
                        IsSpecialSettings: false,
                        ActiveWindowHandle: rep.Handle,
                        IsSpecialDesktop: false,
                        IsMinimized: grpList.All(w => w.IsMinimized),
                        WindowCount: grpList.Count,
                        WindowHandles: grpList.Select(w => w.Handle).ToArray()));
                }
            }
        }

        // 3. Stacks + Exposé + Crystal Lab
        if (Config.Dock.ShowSeparators)
            next.Add(new DockItemView("sep-sys", "", "", "", false, false, false, true, false, false, false, 0));

        foreach (var stack in Config.Dock.Stacks.Where(s => s.Enabled))
        {
            next.Add(new DockItemView($"stack:{stack.Id}", stack.Name, Environment.ExpandEnvironmentVariables(stack.FolderPath), "", false, false, false, false, true, false, false, 0));
        }

        next.Add(new DockItemView("special:desktop", "Desktop", "", "", false, false, false, false, false, false, false, 0, IsSpecialDesktop: true));
        next.Add(new DockItemView("special:expose", "Exposé", "", "", false, false, false, false, false, true, false, 0));
        next.Add(new DockItemView("special:settings", "Glass Lab", "", "", false, false, false, false, false, false, true, 0));

        bool changed = false;
        lock (_items)
        {
            if (_items.Count != next.Count)
            {
                changed = true;
            }
            else
            {
                for (int i = 0; i < next.Count; i++)
                {
                    var a = _items[i];
                    var b = next[i];
                    if (a.Id != b.Id || a.IsRunning != b.IsRunning || a.IsForeground != b.IsForeground
                        || a.IsMinimized != b.IsMinimized || a.WindowCount != b.WindowCount
                        || a.ActiveWindowHandle != b.ActiveWindowHandle || a.Name != b.Name)
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (changed)
            {
                _items.Clear();
                _items.AddRange(next);
            }
        }
        if (changed)
        {
            ComputeLayoutAndSlots();
            MarkDirty();
        }
    }

    public IReadOnlyList<DockItemView> GetCurrentItems()
    {
        lock (_items) return _items.ToArray();
    }

    private static List<WindowInfo> FindAllMatchingWindows(PinnedAppEntry pin, IReadOnlyList<WindowInfo> windows, HashSet<nint> alreadyUsed)
    {
        var matches = new List<WindowInfo>();
        string pinExe = Path.GetFileNameWithoutExtension(pin.Target);
        string pinName = pin.Name;

        foreach (var w in windows)
        {
            if (alreadyUsed.Contains(w.Handle)) continue;
            string winExe = Path.GetFileNameWithoutExtension(w.ExecutablePath);

            bool exeMatch = !pin.IsUwp
                && !string.IsNullOrEmpty(pinExe)
                && !string.IsNullOrEmpty(winExe)
                && string.Equals(pinExe, winExe, StringComparison.OrdinalIgnoreCase);

            bool titleOrUwpMatch = pin.IsUwp
                && (w.Title.Contains(pinName, StringComparison.OrdinalIgnoreCase)
                    || pinName.Contains(w.ProcessName, StringComparison.OrdinalIgnoreCase));

            bool nameMatch = !string.IsNullOrEmpty(pinName)
                && (string.Equals(w.ProcessName, pinName, StringComparison.OrdinalIgnoreCase)
                    || (pinName.Contains("Explorer", StringComparison.OrdinalIgnoreCase) && winExe.Equals("explorer", StringComparison.OrdinalIgnoreCase)));

            if (exeMatch || titleOrUwpMatch || nameMatch)
                matches.Add(w);
        }
        return matches;
    }

    public void PeekTemporarily(int durationMs = 1800)
    {
        if (!Config.Dock.Enabled) return;
        _peekUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Clamp(durationMs, 400, 6000));
        _lastActiveMouseUtc = _peekUntilUtc;
        _wantVisible = true;
        RevealNow();
    }

    private void RevealNow()
    {
        if (!Visible)
        {
            RebuildItems(_windowMgr.GetOpenWindows());
            _autoHideSlide.Value = 110f;
            _autoHideSlide.Target = 0f;
            Show();
            Win32Interop.SetWindowPos(Handle, Win32Interop.HWND_TOPMOST, Left, Top, Width, Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
            BringToFront();
            ComputeLayoutAndSlots();
            MarkDirty();
            TickAndRenderIfNeeded();
        }
        else
        {
            Win32Interop.SetWindowPos(Handle, Win32Interop.HWND_TOPMOST, Left, Top, Width, Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE);
        }
    }

    public void PollEdgeTrigger(Point cursorScreen)
    {
        if (!Config.Dock.Enabled)
        {
            if (Visible) Hide();
            return;
        }

        if (!Config.Dock.AutoHide)
        {
            _wantVisible = true;
            if (!Visible) { _autoHideSlide.Target = 0f; RevealNow(); }
            return;
        }

        if (DateTime.UtcNow < _peekUntilUtc)
        {
            _wantVisible = true;
            _lastActiveMouseUtc = DateTime.UtcNow;
            if (!Visible) RevealNow();
            return;
        }

        int sens = Math.Clamp(Config.Dock.RevealSensitivityPx, 5, 24);
        bool inMonitor = _monitorBounds.Contains(cursorScreen);

        // Ensure Top Bar and Left/Right Dock never fight over top-corner pixels (reserve top 42px for Top Bar)
        bool atEdge = Config.Dock.Position switch
        {
            DockPosition.Bottom => cursorScreen.X >= _monitorBounds.Left && cursorScreen.X < _monitorBounds.Right
                                   && cursorScreen.Y >= _monitorBounds.Bottom - sens && cursorScreen.Y <= _monitorBounds.Bottom,
            DockPosition.Left => cursorScreen.Y >= _monitorBounds.Top + 44 && cursorScreen.Y < _monitorBounds.Bottom
                                 && cursorScreen.X >= _monitorBounds.Left && cursorScreen.X <= _monitorBounds.Left + sens,
            DockPosition.Right => cursorScreen.Y >= _monitorBounds.Top + 44 && cursorScreen.Y < _monitorBounds.Bottom
                                  && cursorScreen.X >= _monitorBounds.Right - sens && cursorScreen.X <= _monitorBounds.Right,
            _ => false
        };

        var paddedCapsule = RectangleF.Inflate(_dockCapsuleRect, 14f, 14f);
        bool insideCapsule = Visible && Bounds.Contains(cursorScreen) && paddedCapsule.Contains(cursorScreen.X - Left, cursorScreen.Y - Top);
        bool nearEdgeBand = Visible && _wantVisible && Config.Dock.Position switch
        {
            DockPosition.Bottom => cursorScreen.X >= Left && cursorScreen.X <= Right && cursorScreen.Y >= _monitorBounds.Bottom - 36 && cursorScreen.Y <= _monitorBounds.Bottom,
            DockPosition.Left => cursorScreen.Y >= Top && cursorScreen.Y <= Bottom && cursorScreen.X >= _monitorBounds.Left && cursorScreen.X <= _monitorBounds.Left + 36,
            DockPosition.Right => cursorScreen.Y >= Top && cursorScreen.Y <= Bottom && cursorScreen.X >= _monitorBounds.Right - 36 && cursorScreen.X <= _monitorBounds.Right,
            _ => false
        };
        bool lockedOpen = _isDragging || _isDockMenuOpen();

        if (atEdge)
        {
            if (!_wantVisible && _isFullscreenActive(_monitorBounds))
            {
                // Require cursor within outer 4px rim over genuine exclusive fullscreen games/videos
                bool exactRim = Config.Dock.Position switch
                {
                    DockPosition.Bottom => cursorScreen.Y >= _monitorBounds.Bottom - 4,
                    DockPosition.Left => cursorScreen.X <= _monitorBounds.Left + 3,
                    DockPosition.Right => cursorScreen.X >= _monitorBounds.Right - 4,
                    _ => false
                };
                if (!exactRim) return;
            }

            _wantVisible = true;
            _lastActiveMouseUtc = DateTime.UtcNow;
            if (!Visible)
                RevealNow();
        }
        else if (insideCapsule || nearEdgeBand || lockedOpen || (inMonitor && IsMouseInside && _hoverSlotIdx >= 0))
        {
            _lastActiveMouseUtc = DateTime.UtcNow;
            _wantVisible = true;
        }
        else if (_wantVisible)
        {
            if ((DateTime.UtcNow - _lastActiveMouseUtc).TotalMilliseconds >= Config.Dock.AutoHideDelayMs)
            {
                _wantVisible = false;
                MarkDirty();
            }
        }
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        bool moving = false;
        float speed = Config.Dock.AnimationSpeed;
        bool instant = ShouldReduceMotion();

        _autoHideSlide.Target = _wantVisible ? 0f : 120f;
        moving |= _autoHideSlide.Step(dtSeconds, 420f * speed, 30f, instant);

        if (!_wantVisible && Config.Dock.AutoHide && _autoHideSlide.Value >= 116f && Visible)
        {
            Hide();
            return false;
        }

        bool hoverActive = IsMouseInside && _dockCapsuleRect.Contains(MouseLocal.X, MouseLocal.Y);
        int newHoverIdx = -1;
        if (IsMouseInside)
        {
            for (int i = 0; i < _renderedSlots.Count; i++)
            {
                if (!_renderedSlots[i].Item.IsSeparator && _renderedSlots[i].HitRect.Contains(MouseLocal.X, MouseLocal.Y))
                {
                    newHoverIdx = i;
                    break;
                }
            }
        }
        if (newHoverIdx != _hoverSlotIdx)
            _hoverSlotIdx = newHoverIdx;

        float maxScale = Config.Dock.MagnificationScale;
        float spread = Config.Dock.MagnificationSpread;
        bool vertical = Config.Dock.Position != DockPosition.Bottom;

        for (int i = 0; i < _renderedSlots.Count; i++)
        {
            var slot = _renderedSlots[i];
            if (slot.Item.IsSeparator) continue;

            float targetScale = 1.0f;
            if (hoverActive && maxScale > 1.01f)
            {
                float dist = vertical
                    ? Math.Abs(MouseLocal.Y - slot.CenterY)
                    : Math.Abs(MouseLocal.X - slot.CenterX);
                if (dist < spread)
                {
                    float norm = dist / spread;
                    float bell = MathF.Cos(norm * MathF.PI * 0.5f);
                    bell *= bell;
                    targetScale = 1.0f + (maxScale - 1.0f) * bell;
                }
            }

            if (!_scaleSprings.TryGetValue(slot.Item.Id, out var sp))
                sp = new SpringFloat(1.0f);
            sp.Target = targetScale;
            moving |= sp.Step(dtSeconds, 460f * speed, 29f, instant);
            _scaleSprings[slot.Item.Id] = sp;

            if (_bounceSprings.TryGetValue(slot.Item.Id, out var bp))
            {
                moving |= bp.Step(dtSeconds, 360f * speed, 11f, instant);
                _bounceSprings[slot.Item.Id] = bp;
            }
        }

        _tooltipAlpha.Target = (_hoverSlotIdx >= 0 && _hoverSlotIdx < _renderedSlots.Count) ? 1f : 0f;
        moving |= _tooltipAlpha.Step(dtSeconds, 520f, 34f, instant);

        if (_hoverSlotIdx >= 0 && _hoverSlotIdx < _renderedSlots.Count)
        {
            var hs = _renderedSlots[_hoverSlotIdx];
            if (_hoverPillAlpha.Value < 0.05f)
            {
                _hoverPillX.Value = hs.CenterX;
                _hoverPillY.Value = hs.CenterY;
                _hoverPillSize.Value = hs.Size;
            }
            _hoverPillX.Target = hs.CenterX;
            _hoverPillY.Target = hs.CenterY;
            _hoverPillSize.Target = hs.Size;
            _hoverPillAlpha.Target = 1f;
        }
        else
        {
            _hoverPillAlpha.Target = 0f;
        }
        moving |= _hoverPillX.Step(dtSeconds, 520f * speed, 34f, instant);
        moving |= _hoverPillY.Step(dtSeconds, 520f * speed, 34f, instant);
        moving |= _hoverPillSize.Step(dtSeconds, 480f * speed, 32f, instant);
        moving |= _hoverPillAlpha.Step(dtSeconds, 440f * speed, 30f, instant);

        _capsuleGlow.Target = IsMouseInside ? 0.28f : 0.07f;
        moving |= _capsuleGlow.Step(dtSeconds, 320f, 26f, instant);

        if (Visible && !instant && _items.Any(i => i.IsForeground))
        {
            _activePulseSec += dtSeconds;
        }

        return moving;
    }

    protected override bool HitTestTransparent(Point localPoint)
    {
        if (!_wantVisible && Config.Dock.AutoHide) return true;
        return !_dockCapsuleRect.Contains(localPoint.X, localPoint.Y);
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        ComputeLayoutAndSlots();
        return new[]
        {
            new GlassShapeItem
            {
                X = _dockCapsuleRect.X,
                Y = _dockCapsuleRect.Y,
                Width = _dockCapsuleRect.Width,
                Height = _dockCapsuleRect.Height,
                CornerRadius = Math.Min(23f, Math.Min(_dockCapsuleRect.Width, _dockCapsuleRect.Height) * 0.34f),
                SquirclePower = 3.2f,
                HoverGlow = _capsuleGlow.Value,
                BevelScale = 1.0f
            }
        };
    }

    private void ComputeLayoutAndSlots()
    {
        _renderedSlots.Clear();
        DockItemView[] snapshot;
        lock (_items) snapshot = _items.ToArray();

        float baseSize = Config.Dock.IconSize;
        float gap = Math.Max(8f, baseSize * 0.18f);
        float pad = 13f;
        bool vertical = Config.Dock.Position != DockPosition.Bottom;

        float totalSpan = pad * 2;
        var scaledSizes = new float[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].IsSeparator)
                scaledSizes[i] = 9f;
            else
            {
                float sc = _scaleSprings.TryGetValue(snapshot[i].Id, out var sp) ? sp.Value : 1.0f;
                scaledSizes[i] = baseSize * sc;
            }
            totalSpan += scaledSizes[i];
            if (i < snapshot.Length - 1) totalSpan += gap;
        }

        float slide = _autoHideSlide.Value;
        float capsuleThickness = baseSize + 22f;

        if (!vertical)
        {
            float cxStart = (Width - totalSpan) * 0.5f;
            float cyBottom = Height - 10f + slide;
            _dockCapsuleRect = new RectangleF(cxStart, cyBottom - capsuleThickness, totalSpan, capsuleThickness);

            float cur = cxStart + pad;
            for (int i = 0; i < snapshot.Length; i++)
            {
                float sz = scaledSizes[i];
                float centerX = cur + sz * 0.5f;
                float bounce = _bounceSprings.TryGetValue(snapshot[i].Id, out var bp) ? MathF.Abs(bp.Value) : 0f;
                float lift = (sz - baseSize) * 0.60f + bounce;
                float centerY = cyBottom - 11f - baseSize * 0.5f - lift;
                var hit = new RectangleF(cur - gap * 0.4f, centerY - sz * 0.65f, sz + gap * 0.8f, sz + 22f);
                _renderedSlots.Add((snapshot[i], hit, centerX, centerY, sz));
                cur += sz + gap;
            }
        }
        else
        {
            bool isLeft = Config.Dock.Position == DockPosition.Left;
            float cyStart = (Height - totalSpan) * 0.5f;
            float capX = isLeft ? (10f - slide) : (Width - 10f - capsuleThickness + slide);
            _dockCapsuleRect = new RectangleF(capX, cyStart, capsuleThickness, totalSpan);

            float cur = cyStart + pad;
            for (int i = 0; i < snapshot.Length; i++)
            {
                float sz = scaledSizes[i];
                float centerY = cur + sz * 0.5f;
                float bounce = _bounceSprings.TryGetValue(snapshot[i].Id, out var bp) ? MathF.Abs(bp.Value) : 0f;
                float lift = (sz - baseSize) * 0.60f + bounce;
                float centerX = isLeft
                    ? (capX + capsuleThickness * 0.5f + lift)
                    : (capX + capsuleThickness * 0.5f - lift);
                var hit = new RectangleF(centerX - sz * 0.65f, cur - gap * 0.4f, sz + 22f, sz + gap * 0.8f);
                _renderedSlots.Add((snapshot[i], hit, centerX, centerY, sz));
                cur += sz + gap;
            }
        }
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        var pal = CrystalUIHelpers.GetPalette(IsDarkModeActive(), Config.Appearance.AccentColorHex);
        bool vertical = Config.Dock.Position != DockPosition.Bottom;

        if (_hoverPillAlpha.Value > 0.02f)
        {
            float ps = _hoverPillSize.Value + 12f;
            float alpha = _hoverPillAlpha.Value;
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, _hoverPillX.Value, _hoverPillY.Value, ps * 0.72f, ps * 0.62f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.28f * alpha),
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, _hoverPillX.Value - ps * 0.5f, _hoverPillY.Value - ps * 0.5f, ps, ps, ps * 0.26f,
                new Color4(1f, 1f, 1f, (IsDarkModeActive() ? 0.16f : 0.30f) * alpha),
                new Color4(1f, 1f, 1f, 0.03f * alpha));
            CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, _hoverPillX.Value - ps * 0.5f, _hoverPillY.Value - ps * 0.5f, ps, ps, ps * 0.26f,
                new Color4(1f, 1f, 1f, 0.42f * alpha), new Color4(1f, 1f, 1f, 0.06f * alpha), 0.9f);
        }

        for (int i = 0; i < _renderedSlots.Count; i++)
        {
            var slot = _renderedSlots[i];
            if (slot.Item.IsSeparator)
            {
                using var div = d2d.CreateSolidColorBrush(pal.Divider);
                if (!vertical)
                    d2d.DrawLine(new System.Numerics.Vector2(slot.CenterX, _dockCapsuleRect.Y + 14f), new System.Numerics.Vector2(slot.CenterX, _dockCapsuleRect.Bottom - 14f), div, 1.2f);
                else
                    d2d.DrawLine(new System.Numerics.Vector2(_dockCapsuleRect.X + 14f, slot.CenterY), new System.Numerics.Vector2(_dockCapsuleRect.Right - 14f, slot.CenterY), div, 1.2f);
                continue;
            }

            float half = slot.Size * 0.5f;
            var iconRect = new RawRectF(slot.CenterX - half, slot.CenterY - half, slot.CenterX + half, slot.CenterY + half);

            if (slot.Item.IsSpecialDesktop)
            {
                CrystalUIHelpers.DrawLuxuryDesktopIcon(d2d, iconRect.Left, iconRect.Top, slot.Size);
            }
            else if (slot.Item.IsSpecialExpose)
            {
                CrystalUIHelpers.DrawLuxuryExposeIcon(d2d, iconRect.Left, iconRect.Top, slot.Size);
            }
            else if (slot.Item.IsSpecialSettings)
            {
                CrystalUIHelpers.DrawLuxuryGlassLabIcon(d2d, iconRect.Left, iconRect.Top, slot.Size);
            }
            else if (slot.Item.IsSpecialStack)
            {
                CrystalUIHelpers.DrawLuxuryFolderStackIcon(d2d, iconRect.Left, iconRect.Top, slot.Size);
            }
            else
            {
                var iconObj = _icons.GetIconNonBlocking(d2d, slot.Item.Target, () =>
                {
                    if (!IsDisposed && IsHandleCreated) BeginInvoke(MarkDirty);
                }, slot.Item.ActiveWindowHandle, slot.Item.Name);
                if (iconObj != null)
                {
                    CrystalUIHelpers.DrawBitmapRect(d2d, iconObj.Bitmap, iconRect.Left, iconRect.Top, slot.Size, slot.Size, 1f, iconObj.HasOpaqueCorners);
                }
                else
                {
                    CrystalUIHelpers.DrawLuxurySquirclePlate(
                        d2d, iconRect.Left, iconRect.Top, slot.Size,
                        new Color4(pal.Accent.R * 0.85f, pal.Accent.G * 0.85f, pal.Accent.B, 0.92f),
                        new Color4(pal.Accent.R * 0.45f, pal.Accent.G * 0.45f, pal.Accent.B * 0.7f, 0.94f),
                        new Color4(0.08f, 0.12f, 0.24f, 0.98f));
                    string letter = string.IsNullOrEmpty(slot.Item.Name) ? "A" : slot.Item.Name[..1].ToUpperInvariant();
                    CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, letter, iconRect.Left, iconRect.Top, slot.Size, slot.Size, slot.Size * 0.42f, new Color4(1f, 1f, 1f, 0.98f), FontWeight.Bold, TextAlignment.Center);
                }
            }

            if (Config.Dock.ShowRunningIndicators && slot.Item.IsRunning)
            {
                float pulse = slot.Item.IsForeground ? (0.85f + 0.15f * MathF.Sin(_activePulseSec * 3.4f)) : 1f;
                float pillLen = slot.Item.IsForeground ? 12f : (slot.Item.IsMinimized ? 7.5f : 6f);
                float pillThick = 3.2f;
                if (slot.Item.IsMinimized)
                {
                    // Luminous hollow capsule ring indicates minimized app ready for 1-click restore
                    Color4 minCol = new(pal.PrimaryText.R, pal.PrimaryText.G, pal.PrimaryText.B, 0.72f);
                    if (!vertical)
                        CrystalUIHelpers.DrawRoundedRect(d2d, slot.CenterX - pillLen * 0.5f, _dockCapsuleRect.Bottom - 6.8f, pillLen, pillThick, 1.6f, default, minCol, 1.15f);
                    else
                    {
                        float dx = Config.Dock.Position == DockPosition.Left ? _dockCapsuleRect.X + 4.0f : _dockCapsuleRect.Right - 7.0f;
                        CrystalUIHelpers.DrawRoundedRect(d2d, dx, slot.CenterY - pillLen * 0.5f, pillThick, pillLen, 1.6f, default, minCol, 1.15f);
                    }
                }
                else
                {
                    Color4 dotCol = slot.Item.IsForeground
                        ? new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, pulse)
                        : new Color4(pal.PrimaryText.R, pal.PrimaryText.G, pal.PrimaryText.B, 0.88f);
                    if (slot.Item.IsForeground)
                    {
                        Color4 aura = new(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.44f * pulse);
                        if (!vertical)
                            CrystalUIHelpers.FillEllipseRadialGradient(d2d, slot.CenterX, _dockCapsuleRect.Bottom - 5.2f, pillLen * 1.1f, 5.5f, aura, new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
                        else
                        {
                            float dx = Config.Dock.Position == DockPosition.Left ? _dockCapsuleRect.X + 5.5f : _dockCapsuleRect.Right - 5.5f;
                            CrystalUIHelpers.FillEllipseRadialGradient(d2d, dx, slot.CenterY, 5.5f, pillLen * 1.1f, aura, new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
                        }
                    }

                    if (!vertical)
                    {
                        CrystalUIHelpers.DrawRoundedRect(d2d, slot.CenterX - pillLen * 0.5f, _dockCapsuleRect.Bottom - 6.8f, pillLen, pillThick, 1.6f, dotCol);
                        if (slot.Item.WindowCount > 1)
                        {
                            using var dotB = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.78f));
                            d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(slot.CenterX + pillLen * 0.5f + 4.2f, _dockCapsuleRect.Bottom - 5.2f), 1.5f, 1.5f), dotB);
                        }
                    }
                    else
                    {
                        float dx = Config.Dock.Position == DockPosition.Left ? _dockCapsuleRect.X + 4.0f : _dockCapsuleRect.Right - 7.0f;
                        CrystalUIHelpers.DrawRoundedRect(d2d, dx, slot.CenterY - pillLen * 0.5f, pillThick, pillLen, 1.6f, dotCol);
                    }
                }
            }
        }

        // Smooth animated tooltip pill (shows Minimized / Multi-window hint automatically)
        if (_hoverSlotIdx >= 0 && _hoverSlotIdx < _renderedSlots.Count)
        {
            var hov = _renderedSlots[_hoverSlotIdx];
            if (!string.IsNullOrEmpty(hov.Item.Name))
            {
                string suffix = hov.Item.IsRunning
                    ? (hov.Item.IsMinimized ? " • Minimized" : (hov.Item.WindowCount > 1 ? $" ({hov.Item.WindowCount})" : ""))
                    : "";
                _lastTooltipText = hov.Item.Name + suffix;
                _lastTooltipX = hov.CenterX;
                _lastTooltipY = hov.CenterY;
            }
        }

        if (_tooltipAlpha.Value > 0.04f && !string.IsNullOrEmpty(_lastTooltipText))
        {
            float a = _tooltipAlpha.Value;
            float tipW = Math.Clamp(_lastTooltipText.Length * 7.6f + 28f, 74f, 238f);
            float tipH = 29f;
            float tx = !vertical
                ? Math.Clamp(_lastTooltipX - tipW * 0.5f, 6f, Width - tipW - 6f)
                : (Config.Dock.Position == DockPosition.Left ? _dockCapsuleRect.Right + 10f : _dockCapsuleRect.Left - tipW - 10f);
            float ty = !vertical
                ? (_dockCapsuleRect.Y - tipH - 12f)
                : Math.Clamp(_lastTooltipY - tipH * 0.5f, 6f, Height - tipH - 6f);
            Color4 pillTop = IsDarkModeActive() ? new Color4(0.11f, 0.14f, 0.20f, 0.94f * a) : new Color4(1f, 1f, 1f, 0.96f * a);
            Color4 pillBot = IsDarkModeActive() ? new Color4(0.05f, 0.07f, 0.11f, 0.94f * a) : new Color4(0.90f, 0.93f, 0.98f, 0.96f * a);
            Color4 txtCol = new(pal.PrimaryText.R, pal.PrimaryText.G, pal.PrimaryText.B, pal.PrimaryText.A * a);
            CrystalUIHelpers.DrawRoundedRect(d2d, tx, ty + 2.2f, tipW, tipH, 10f, new Color4(0f, 0f, 0f, 0.30f * a));
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, tx, ty, tipW, tipH, 10f, pillTop, pillBot);
            CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, tx, ty, tipW, tipH, 10f,
                new Color4(1f, 1f, 1f, 0.48f * a), new Color4(1f, 1f, 1f, 0.10f * a), 0.9f);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _lastTooltipText, tx + 8f, ty, tipW - 16f, tipH, 11.5f, txtCol, FontWeight.SemiBold, TextAlignment.Center, ParagraphAlignment.Center, false);
        }
    }

    private void TriggerBounce(string itemId)
    {
        if (ShouldReduceMotion()) return;
        var sp = new SpringFloat(0f) { Velocity = 240f, Target = 0f };
        _bounceSprings[itemId] = sp;
        MarkDirty();
    }

    private void OnDockMouseDown(object? sender, MouseEventArgs e)
    {
        _mouseDownPoint = e.Location;
        _isDragging = false;
        int idx = FindSlotAt(e.Location);
        if (idx < 0) return;
        var slot = _renderedSlots[idx];

        if (e.Button == MouseButtons.Right)
        {
            _onShowItemContextMenu(PointToScreen(e.Location), slot.Item);
            return;
        }

        if (e.Button == MouseButtons.Left && slot.Item.IsPinned)
        {
            _draggedPinnedIndex = Config.Dock.PinnedApps.FindIndex(p => $"pin:{p.Id}" == slot.Item.Id);
        }
    }

    private void OnDockMouseMove(object? sender, MouseEventArgs e)
    {
        _lastActiveMouseUtc = DateTime.UtcNow;
        if (e.Button == MouseButtons.Left && _draggedPinnedIndex >= 0)
        {
            if (Math.Abs(e.X - _mouseDownPoint.X) > 14 || Math.Abs(e.Y - _mouseDownPoint.Y) > 14)
            {
                _isDragging = true;
                int targetSlot = FindSlotAt(e.Location);
                if (targetSlot >= 0 && _renderedSlots[targetSlot].Item.IsPinned)
                {
                    int targetPinIdx = Config.Dock.PinnedApps.FindIndex(p => $"pin:{p.Id}" == _renderedSlots[targetSlot].Item.Id);
                    if (targetPinIdx >= 0 && targetPinIdx != _draggedPinnedIndex)
                    {
                        ConfigMgr.Update(cfg =>
                        {
                            var moved = cfg.Dock.PinnedApps[_draggedPinnedIndex];
                            cfg.Dock.PinnedApps.RemoveAt(_draggedPinnedIndex);
                            cfg.Dock.PinnedApps.Insert(targetPinIdx, moved);
                        });
                        _draggedPinnedIndex = targetPinIdx;
                    }
                }
            }
        }
        MarkDirty();
    }

    private void OnDockMouseUp(object? sender, MouseEventArgs e)
    {
        bool wasDrag = _isDragging;
        _draggedPinnedIndex = -1;
        _isDragging = false;

        if (wasDrag || e.Button != MouseButtons.Left) return;

        int idx = FindSlotAt(e.Location);
        if (idx < 0) return;
        var item = _renderedSlots[idx].Item;
        var iconScreenRect = GetSlotScreenRect(idx);
        TriggerBounce(item.Id);

        if (item.IsSpecialDesktop)
        {
            if (_onShowDesktop != null) _onShowDesktop();
            else { _windowMgr.ToggleShowDesktop(); RebuildItems(_windowMgr.GetOpenWindows()); }
        }
        else if (item.IsSpecialExpose)
        {
            _onOpenExpose();
        }
        else if (item.IsSpecialSettings)
        {
            _onOpenSettings();
        }
        else if (item.IsSpecialStack)
        {
            var stack = Config.Dock.Stacks.FirstOrDefault(s => $"stack:{s.Id}" == item.Id);
            if (stack != null)
                _onShowStackPopup(PointToScreen(new Point((int)_renderedSlots[idx].CenterX, (int)_dockCapsuleRect.Y)), stack);
        }
        else if (item.IsRunning && item.ActiveWindowHandle != 0)
        {
            var handles = item.WindowHandles;
            nint target = item.ActiveWindowHandle;
            if (item.IsForeground && !item.IsMinimized)
            {
                if (handles != null && handles.Count > 1)
                {
                    int curIdx = -1;
                    for (int hIdx = 0; hIdx < handles.Count; hIdx++)
                    {
                        if (handles[hIdx] == target) { curIdx = hIdx; break; }
                    }
                    nint nextHandle = handles[(curIdx + 1) % handles.Count];
                    if (Win32Interop.IsIconic(nextHandle) && GenieRestoreRequested != null)
                        GenieRestoreRequested.Invoke(nextHandle, iconScreenRect);
                    else
                        _windowMgr.FocusWindow(nextHandle);
                }
                else
                {
                    if (GenieMinimizeRequested != null)
                        GenieMinimizeRequested.Invoke(target, iconScreenRect);
                    else
                        _windowMgr.MinimizeWindow(target);
                }
            }
            else
            {
                if (Win32Interop.IsIconic(target) && GenieRestoreRequested != null)
                    GenieRestoreRequested.Invoke(target, iconScreenRect);
                else
                    _windowMgr.FocusWindow(target);
            }
            RebuildItems(_windowMgr.GetOpenWindows());
        }
        else
        {
            if (GenieLaunchRequested != null)
                GenieLaunchRequested.Invoke(item, iconScreenRect);
            else
                _appsService.LaunchApp(item.Target, item.Arguments);
        }
    }

    public Rectangle GetIconScreenRectForWindow(nint hwnd, string? processPath = null, string? title = null)
    {
        for (int i = 0; i < _renderedSlots.Count; i++)
        {
            var slot = _renderedSlots[i].Item;
            if (slot.ActiveWindowHandle == hwnd || (slot.WindowHandles != null && slot.WindowHandles.Contains(hwnd)))
                return GetSlotScreenRect(i);
        }

        string exeName = string.IsNullOrWhiteSpace(processPath) ? "" : Path.GetFileNameWithoutExtension(processPath);
        if (!string.IsNullOrWhiteSpace(exeName))
        {
            for (int i = 0; i < _renderedSlots.Count; i++)
            {
                var slot = _renderedSlots[i].Item;
                if (slot.IsSeparator || slot.IsSpecialDesktop || slot.IsSpecialExpose || slot.IsSpecialSettings || slot.IsSpecialStack)
                    continue;
                string slotExe = string.IsNullOrWhiteSpace(slot.Target) ? "" : Path.GetFileNameWithoutExtension(slot.Target);
                if ((!string.IsNullOrWhiteSpace(slotExe) && slotExe.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    || slot.Name.Contains(exeName, StringComparison.OrdinalIgnoreCase)
                    || exeName.Contains(slot.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return GetSlotScreenRect(i);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            for (int i = 0; i < _renderedSlots.Count; i++)
            {
                var slot = _renderedSlots[i].Item;
                if (!slot.IsSeparator && title.Contains(slot.Name, StringComparison.OrdinalIgnoreCase))
                    return GetSlotScreenRect(i);
            }
        }

        // Fallback to center of revealed dock capsule
        int iconSz = Math.Max(40, (int)Config.Dock.IconSize);
        if (_dockCapsuleRect.Width > 10 && _dockCapsuleRect.Height > 10)
        {
            int cx = Config.Dock.Position == DockPosition.Left
                ? _monitorBounds.Left + 21
                : (Config.Dock.Position == DockPosition.Right
                    ? _monitorBounds.Right - 21 - iconSz
                    : Left + (int)(_dockCapsuleRect.X + _dockCapsuleRect.Width * 0.5f - iconSz * 0.5f));
            int cy = Config.Dock.Position == DockPosition.Bottom
                ? _monitorBounds.Bottom - 21 - iconSz
                : Top + (int)(_dockCapsuleRect.Y + _dockCapsuleRect.Height * 0.5f - iconSz * 0.5f);
            return new Rectangle(cx, cy, iconSz, iconSz);
        }
        return Config.Dock.Position switch
        {
            DockPosition.Left => new Rectangle(_monitorBounds.Left + 21, _monitorBounds.Top + _monitorBounds.Height / 2 - iconSz / 2, iconSz, iconSz),
            DockPosition.Right => new Rectangle(_monitorBounds.Right - 21 - iconSz, _monitorBounds.Top + _monitorBounds.Height / 2 - iconSz / 2, iconSz, iconSz),
            _ => new Rectangle(_monitorBounds.Left + _monitorBounds.Width / 2 - iconSz / 2, _monitorBounds.Bottom - 21 - iconSz, iconSz, iconSz)
        };
    }

    private Rectangle GetSlotScreenRect(int idx)
    {
        if (idx < 0 || idx >= _renderedSlots.Count)
            return GetIconScreenRectForWindow(0);

        var slot = _renderedSlots[idx];
        int sz = Math.Max(36, (int)MathF.Round(Config.Dock.IconSize));
        int sx, sy;
        if (Config.Dock.Position == DockPosition.Left)
        {
            sx = _monitorBounds.Left + 21;
            sy = Math.Clamp(Top + (int)MathF.Round(slot.CenterY - sz * 0.5f), _monitorBounds.Top + 12, _monitorBounds.Bottom - sz - 12);
        }
        else if (Config.Dock.Position == DockPosition.Right)
        {
            sx = _monitorBounds.Right - 21 - sz;
            sy = Math.Clamp(Top + (int)MathF.Round(slot.CenterY - sz * 0.5f), _monitorBounds.Top + 12, _monitorBounds.Bottom - sz - 12);
        }
        else
        {
            sx = Math.Clamp(Left + (int)MathF.Round(slot.CenterX - sz * 0.5f), _monitorBounds.Left + 12, _monitorBounds.Right - sz - 12);
            sy = _monitorBounds.Bottom - 21 - sz;
        }
        return new Rectangle(sx, sy, sz, sz);
    }

    private int FindSlotAt(Point pt)
    {
        for (int i = 0; i < _renderedSlots.Count; i++)
        {
            if (!_renderedSlots[i].Item.IsSeparator && _renderedSlots[i].HitRect.Contains(pt.X, pt.Y))
                return i;
        }
        return -1;
    }
}
