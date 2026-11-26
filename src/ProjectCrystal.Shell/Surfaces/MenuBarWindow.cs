using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.Interop;
using ProjectCrystal.Native.System;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed class MenuBarWindow : GlassWindowBase
{
    private readonly Rectangle _monitorBounds;
    private readonly Func<bool> _isAnyPopupOpen;
    private readonly Func<Rectangle, bool> _isFullscreenActive;
    private readonly Action<Point> _onOpenCrystalMenu;
    private readonly Action<Point, WindowInfo?> _onOpenAppMenu;
    private readonly Action _onOpenLauncher;
    private readonly Action _onOpenExpose;
    private readonly Action _onOpenSettings;
    private readonly Action _onToggleControlCenter;
    private readonly Action _onToggleNotificationCenter;
    private readonly Action _onToggleMediaPlayPause;
    private readonly Action<float> _onAdjustVolumeDelta;
    private readonly Action _onToggleMute;
    private readonly Action? _onShowDesktop;

    private string _activeAppName = "Desktop";
    private WindowInfo? _activeWindow;
    private NetworkStatusInfo _network = new(false, false, false, false, "Offline", "Offline", "-", 0, 0);
    private BluetoothStatusInfo _bluetooth = new(false, false, "Unavailable", 0);
    private BatteryStatusInfo _battery = new(false, true, 100, -1, "AC Desktop");
    private MediaSessionInfo _media = new(false, "", "", "", "", false, false, false, false, null);
    private float _volumeScalar = 0.5f;
    private bool _isMuted;
    private int _notificationCount;

    private readonly List<(string Id, RectangleF Rect, Action OnClick)> _hitZones = new();
    private string _hoverId = "";
    private SpringFloat _hoverPillX = new(0f);
    private SpringFloat _hoverPillW = new(0f);
    private SpringFloat _hoverPillAlpha = new(0f);

    private SpringFloat _revealSlide;
    private bool _wantVisible;
    private float _mediaPulseSec;
    private float _mediaPulseAccum;
    private DateTime _lastActiveMouseUtc = DateTime.UtcNow;

    public MenuBarWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        Rectangle monitorBounds,
        Func<bool> isAnyPopupOpen,
        Func<Rectangle, bool> isFullscreenActive,
        Action<Point> onOpenCrystalMenu,
        Action<Point, WindowInfo?> onOpenAppMenu,
        Action onOpenLauncher,
        Action onOpenExpose,
        Action onOpenSettings,
        Action onToggleControlCenter,
        Action onToggleNotificationCenter,
        Action onToggleMediaPlayPause,
        Action<float> onAdjustVolumeDelta,
        Action onToggleMute,
        Action? onShowDesktop = null)
        : base(gpu, configMgr, backdrop, noActivate: true, topMost: true)
    {
        _monitorBounds = monitorBounds;
        _isAnyPopupOpen = isAnyPopupOpen;
        _isFullscreenActive = isFullscreenActive;
        _onOpenCrystalMenu = onOpenCrystalMenu;
        _onOpenAppMenu = onOpenAppMenu;
        _onOpenLauncher = onOpenLauncher;
        _onOpenExpose = onOpenExpose;
        _onOpenSettings = onOpenSettings;
        _onToggleControlCenter = onToggleControlCenter;
        _onToggleNotificationCenter = onToggleNotificationCenter;
        _onToggleMediaPlayPause = onToggleMediaPlayPause;
        _onAdjustVolumeDelta = onAdjustVolumeDelta;
        _onToggleMute = onToggleMute;
        _onShowDesktop = onShowDesktop;

        bool autoHide = Config.MenuBar.AutoHide;
        _wantVisible = !autoHide;
        _revealSlide = new SpringFloat(autoHide ? -(Config.MenuBar.Height + 16f) : 0f);
        UpdateGeometry();

        MouseMove += (_, e) =>
        {
            _lastActiveMouseUtc = DateTime.UtcNow;
            string hit = HitTestZone(e.Location);
            if (hit != _hoverId) { _hoverId = hit; MarkDirty(); }
        };
        MouseLeave += (_, __) => { _hoverId = ""; MarkDirty(); };
        MouseClick += (_, e) =>
        {
            foreach (var z in _hitZones)
            {
                if (z.Rect.Contains(e.X, e.Y))
                {
                    z.OnClick();
                    break;
                }
            }
        };
        MouseWheel += (_, e) =>
        {
            var volZone = _hitZones.FirstOrDefault(z => z.Id == "volume");
            if (volZone.Rect.Contains(e.X, e.Y))
                _onAdjustVolumeDelta(e.Delta > 0 ? 0.05f : -0.05f);
        };
    }

    public void UpdateGeometry()
    {
        int barH = Config.MenuBar.Height;
        SetBounds(_monitorBounds.X, _monitorBounds.Y, _monitorBounds.Width, barH + 14);
        MarkDirty();
    }

    public void UpdateLiveStatus(
        WindowInfo? fgWin,
        NetworkStatusInfo net,
        BluetoothStatusInfo bt,
        BatteryStatusInfo bat,
        MediaSessionInfo media,
        float volumeScalar,
        bool isMuted,
        int notifCount)
    {
        _activeWindow = fgWin;
        _activeAppName = fgWin?.ProcessName ?? "Crystal Desktop";
        _network = net;
        _bluetooth = bt;
        _battery = bat;
        _media = media;
        _volumeScalar = volumeScalar;
        _isMuted = isMuted;
        _notificationCount = notifCount;
        MarkDirty();
    }

    /// <summary>
    /// Called continuously by ShellCoordinator's global pointer edge sensor even when MenuBar is hidden,
    /// ensuring ZERO overlap with application title bars while hidden.
    /// </summary>
    public void PollEdgeTrigger(Point cursorScreen)
    {
        if (!Config.MenuBar.Enabled)
        {
            if (Visible) Hide();
            return;
        }

        if (!Config.MenuBar.AutoHide)
        {
            _wantVisible = true;
            if (!Visible) { Show(); _revealSlide.Target = 0f; MarkDirty(); }
            return;
        }

        bool inMonitorX = cursorScreen.X >= _monitorBounds.Left && cursorScreen.X < _monitorBounds.Right;
        int sens = Math.Clamp(Config.MenuBar.RevealSensitivityPx, 4, 20);
        bool atTopEdge = inMonitorX && cursorScreen.Y >= _monitorBounds.Top && cursorScreen.Y <= _monitorBounds.Top + sens;
        bool insideBar = Visible && Bounds.Contains(cursorScreen);
        bool popupLocked = _isAnyPopupOpen();

        if (atTopEdge)
        {
            // Do not accidentally reveal over fullscreen games/videos unless user lingers right on top rim
            if (!_wantVisible && _isFullscreenActive(_monitorBounds) && cursorScreen.Y > _monitorBounds.Top + 3)
                return;

            _wantVisible = true;
            _lastActiveMouseUtc = DateTime.UtcNow;
            if (!Visible)
            {
                _revealSlide.Value = -(Config.MenuBar.Height + 14f);
                _revealSlide.Target = 0f;
                Show();
                Win32Interop.SetWindowPos(Handle, Win32Interop.HWND_TOPMOST, Left, Top, Width, Height,
                    Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
                BringToFront();
                MarkDirty();
                TickAndRenderIfNeeded();
            }
        }
        else if (insideBar || popupLocked)
        {
            _lastActiveMouseUtc = DateTime.UtcNow;
            _wantVisible = true;
        }
        else if (_wantVisible)
        {
            if ((DateTime.UtcNow - _lastActiveMouseUtc).TotalMilliseconds >= Config.MenuBar.AutoHideDelayMs)
            {
                _wantVisible = false;
                MarkDirty();
            }
        }
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        bool moving = false;
        bool instant = ShouldReduceMotion();
        float speed = Config.Glass.AnimationSpeed;

        float hiddenY = -(Config.MenuBar.Height + 14f);
        _revealSlide.Target = _wantVisible ? 0f : hiddenY;
        moving |= _revealSlide.Step(dtSeconds, 440f * speed, 34f, instant);

        if (!_wantVisible && Config.MenuBar.AutoHide && _revealSlide.Value <= hiddenY + 1.5f && Visible)
        {
            // Fully hide HWND so ZERO pixels overlap application title bars or close/min/max controls!
            Hide();
            return false;
        }

        var hov = _hitZones.FirstOrDefault(z => z.Id == _hoverId);
        if (!string.IsNullOrEmpty(hov.Id))
        {
            if (_hoverPillAlpha.Value < 0.05f)
            {
                _hoverPillX.Value = hov.Rect.X;
                _hoverPillW.Value = hov.Rect.Width;
            }
            _hoverPillX.Target = hov.Rect.X;
            _hoverPillW.Target = hov.Rect.Width;
            _hoverPillAlpha.Target = 1f;
        }
        else
        {
            _hoverPillAlpha.Target = 0f;
        }

        moving |= _hoverPillX.Step(dtSeconds, 540f, 34f, instant);
        moving |= _hoverPillW.Step(dtSeconds, 540f, 34f, instant);
        moving |= _hoverPillAlpha.Step(dtSeconds, 480f, 32f, instant);

        if (Visible && _media.HasSession && _media.IsPlaying && !instant)
        {
            _mediaPulseSec += dtSeconds;
            _mediaPulseAccum += dtSeconds;
            if (_mediaPulseAccum >= 0.055f)
            {
                _mediaPulseAccum = 0f;
                moving = true;
            }
        }
        return moving;
    }

    protected override bool HitTestTransparent(Point localPoint)
    {
        if (!_wantVisible && Config.MenuBar.AutoHide) return true;
        float yOff = _revealSlide.Value;
        float barH = Config.MenuBar.Height;
        var barRect = new RectangleF(10f, 4f + yOff, Width - 20f, barH);
        return !barRect.Contains(localPoint.X, localPoint.Y);
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        float yOff = _revealSlide.Value;
        float barH = Config.MenuBar.Height;
        return new[]
        {
            new GlassShapeItem
            {
                X = 10f,
                Y = 4f + yOff,
                Width = Width - 20f,
                Height = barH,
                CornerRadius = barH * 0.44f,
                SquirclePower = 3.0f,
                HoverGlow = IsMouseInside ? 0.16f : 0.04f,
                BevelScale = 0.68f
            }
        };
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        _hitZones.Clear();
        float yOff = _revealSlide.Value;
        float barH = Config.MenuBar.Height;
        float barTop = 4f + yOff;
        if (barTop + barH < 1f) return;

        var pal = CrystalUIHelpers.GetPalette(IsDarkModeActive(), Config.Appearance.AccentColorHex);

        // Animated gliding pill highlight under hovered menu item with top specular highlight
        if (_hoverPillAlpha.Value > 0.02f && _hoverPillW.Value > 4f)
        {
            float a = _hoverPillAlpha.Value;
            var pillCol = new Color4(pal.ControlHover.R, pal.ControlHover.G, pal.ControlHover.B, pal.ControlHover.A * a);
            var pillEdge = new Color4(1f, 1f, 1f, 0.24f * a);
            CrystalUIHelpers.DrawRoundedRect(d2d, _hoverPillX.Value, barTop + 3f, _hoverPillW.Value, barH - 6f, 7f, pillCol, pillEdge, 0.7f);
        }

        // Left Cluster: Illuminated Crystal emblem pill + hairline separator + active app crest + navigation pills
        float lx = 18f;
        var crystalRect = new RectangleF(lx, barTop + 3f, 34f, barH - 6f);
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, crystalRect.X + 17f, barTop + barH * 0.5f, 13f, 10f,
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.22f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
        CrystalUIHelpers.DrawCrystalEmblem(d2d, crystalRect.X + 17f, barTop + barH * 0.5f, 16f, pal.PrimaryText);
        _hitZones.Add(("crystal", crystalRect, () => _onOpenCrystalMenu(new Point(Left + (int)crystalRect.X, Top + (int)(barTop + barH + 6)))));
        lx += 36f;

        using (var sepB = d2d.CreateSolidColorBrush(pal.Divider))
            d2d.DrawLine(new System.Numerics.Vector2(lx + 2f, barTop + 7f), new System.Numerics.Vector2(lx + 2f, barTop + barH - 7f), sepB, 1f);
        lx += 8f;

        if (Config.MenuBar.ShowActiveApp)
        {
            float appW = Math.Clamp(_activeAppName.Length * 8.2f + 26f, 82f, 200f);
            var appRect = new RectangleF(lx, barTop + 3f, appW, barH - 6f);
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, appRect.X + 9f, barTop + barH * 0.5f, 5.5f, 5.5f,
                pal.Accent, new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _activeAppName, appRect.X + 16f, barTop, appW - 22f, barH, 12.5f, pal.PrimaryText, FontWeight.Bold);
            _hitZones.Add(("activeApp", appRect, () => _onOpenAppMenu(new Point(Left + (int)appRect.X, Top + (int)(barTop + barH + 6)), _activeWindow)));
            lx += appW + 6f;
        }

        lx = DrawMenuTextButton(d2d, pal, "apps", "Apps", lx, barTop, barH, _onOpenLauncher);
        lx = DrawMenuTextButton(d2d, pal, "windows", "Exposé", lx, barTop, barH, _onOpenExpose);
        lx = DrawMenuTextButton(d2d, pal, "lab", "Glass Lab", lx, barTop, barH, _onOpenSettings);

        // Right Cluster
        float rx = Width - 20f;

        if (_onShowDesktop != null)
        {
            rx -= 30f;
            var deskRect = new RectangleF(rx, barTop + 3f, 28f, barH - 6f);
            CrystalUIHelpers.DrawRoundedRect(d2d, deskRect.X, deskRect.Y, deskRect.Width, deskRect.Height, 7f, pal.ControlSurface, pal.HairlineStroke, 0.6f);
            CrystalUIHelpers.DrawDesktopHomeIcon(d2d, deskRect.X + 14f, barTop + barH * 0.5f, 15f, pal.PrimaryText);
            _hitZones.Add(("desktop", deskRect, _onShowDesktop));
            rx -= 5f;
        }

        string timeFmt = Config.MenuBar.Use24HourClock
            ? (Config.MenuBar.ShowSeconds ? "ddd dd MMM  HH:mm:ss" : "ddd dd MMM  HH:mm")
            : (Config.MenuBar.ShowSeconds ? "ddd dd MMM  h:mm:ss tt" : "ddd dd MMM  h:mm tt");
        string clockText = DateTime.Now.ToString(timeFmt);
        float clockW = Config.MenuBar.ShowSeconds ? 182f : 162f;
        rx -= clockW;
        var clockRect = new RectangleF(rx, barTop + 3f, clockW, barH - 6f);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, clockText, clockRect.X + 6f, barTop, clockW - 12f, barH, 12f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        _hitZones.Add(("clock", clockRect, _onToggleNotificationCenter));
        rx -= 4f;

        // Notification Bell with numeric badge pill
        rx -= 30f;
        var bellRect = new RectangleF(rx, barTop + 3f, 30f, barH - 6f);
        CrystalUIHelpers.DrawBellIcon(d2d, bellRect.X + 14f, barTop + barH * 0.5f, 14f, pal.PrimaryText);
        if (_notificationCount > 0 && !Config.Notifications.DoNotDisturb)
        {
            string cntTxt = _notificationCount > 9 ? "9+" : _notificationCount.ToString();
            CrystalUIHelpers.DrawRoundedRect(d2d, bellRect.Right - 14f, barTop + 4f, 13f, 12f, 6f, pal.Accent);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, cntTxt, bellRect.Right - 14f, barTop + 4f, 13f, 12f, 8.5f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
        }
        _hitZones.Add(("bell", bellRect, _onToggleNotificationCenter));
        rx -= 4f;

        // Control Center Icon
        rx -= 28f;
        var ccRect = new RectangleF(rx, barTop + 3f, 28f, barH - 6f);
        CrystalUIHelpers.DrawControlCenterIcon(d2d, ccRect.X + 14f, barTop + barH * 0.5f, 15f, pal.PrimaryText);
        _hitZones.Add(("cc", ccRect, _onToggleControlCenter));
        rx -= 4f;

        // Search
        rx -= 26f;
        var searchRect = new RectangleF(rx, barTop + 3f, 26f, barH - 6f);
        CrystalUIHelpers.DrawSearchIcon(d2d, searchRect.X + 13f, barTop + barH * 0.5f, 13.5f, pal.PrimaryText);
        _hitZones.Add(("search", searchRect, _onOpenLauncher));
        rx -= 6f;

        // Battery
        if (Config.MenuBar.ShowBattery && _battery.HasBattery)
        {
            rx -= 56f;
            var batRect = new RectangleF(rx, barTop + 3f, 54f, barH - 6f);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, $"{_battery.ChargePercent}%", batRect.X, barTop, 28f, barH, 11f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Trailing);
            CrystalUIHelpers.DrawBatteryIcon(d2d, batRect.X + 41f, barTop + barH * 0.5f, 17f, pal.PrimaryText, _battery.ChargePercent, true);
            _hitZones.Add(("bat", batRect, _onToggleControlCenter));
            rx -= 4f;
        }

        // Volume
        if (Config.MenuBar.ShowVolume)
        {
            rx -= 46f;
            var volRect = new RectangleF(rx, barTop + 3f, 44f, barH - 6f);
            CrystalUIHelpers.DrawVolumeIcon(d2d, volRect.X + 13f, barTop + barH * 0.5f, 13.5f, _isMuted ? pal.SubtleText : pal.PrimaryText, _volumeScalar, _isMuted);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _isMuted ? "0%" : $"{(int)(_volumeScalar * 100)}%", volRect.X + 20f, barTop, 24f, barH, 10.5f, pal.SecondaryText);
            _hitZones.Add(("volume", volRect, _onToggleMute));
            rx -= 4f;
        }

        // Bluetooth
        if (Config.MenuBar.ShowBluetooth && _bluetooth.IsSupported)
        {
            rx -= 26f;
            var btRect = new RectangleF(rx, barTop + 3f, 26f, barH - 6f);
            CrystalUIHelpers.DrawBluetoothIcon(d2d, btRect.X + 13f, barTop + barH * 0.5f, 13.5f, _bluetooth.IsEnabled ? pal.Accent : pal.SubtleText, _bluetooth.IsEnabled);
            _hitZones.Add(("bt", btRect, _onToggleControlCenter));
            rx -= 4f;
        }

        // Network
        if (Config.MenuBar.ShowNetwork)
        {
            rx -= 28f;
            var netRect = new RectangleF(rx, barTop + 3f, 28f, barH - 6f);
            if (_network.HasWifiHardware)
                CrystalUIHelpers.DrawWifiIcon(d2d, netRect.X + 14f, barTop + barH * 0.5f, 14f, pal.PrimaryText, _network.IsWifiConnected);
            else
                CrystalUIHelpers.DrawEthernetIcon(d2d, netRect.X + 14f, barTop + barH * 0.5f, 14f, _network.IsConnected ? pal.PrimaryText : pal.Danger, _network.IsConnected);
            _hitZones.Add(("net", netRect, _onToggleControlCenter));
            rx -= 6f;
        }

        // Media Pill (LiquidGlassDesign acoustic waveform island!)
        if (Config.MenuBar.ShowMediaPill && _media.HasSession && rx - lx > 180f)
        {
            float extraEq = _media.IsPlaying ? 26f : 0f;
            float pillW = Math.Clamp(_media.Title.Length * 6.8f + 44f + extraEq, 118f, 224f);
            rx -= pillW;
            var mediaRect = new RectangleF(rx, barTop + 4f, pillW, barH - 8f);
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, mediaRect.X, mediaRect.Y, mediaRect.Width, mediaRect.Height, (barH - 8f) * 0.5f,
                new Color4(pal.ControlSurface.R, pal.ControlSurface.G, pal.ControlSurface.B, pal.ControlSurface.A * 1.15f),
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, _media.IsPlaying ? 0.22f : 0.06f));
            CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, mediaRect.X, mediaRect.Y, mediaRect.Width, mediaRect.Height, (barH - 8f) * 0.5f,
                new Color4(1f, 1f, 1f, 0.44f), new Color4(1f, 1f, 1f, 0.08f), 0.85f);
            if (_media.IsPlaying)
            {
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, mediaRect.X + 20f, mediaRect.Y + mediaRect.Height * 0.5f, 16f, 10f,
                    new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.34f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
                CrystalUIHelpers.DrawAcousticWaveform(d2d, mediaRect.X + 10f, mediaRect.Y + mediaRect.Height * 0.5f, _mediaPulseSec, pal.Accent, true, 5);
                CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _media.Title, mediaRect.X + 34f, mediaRect.Y, pillW - 42f, mediaRect.Height, 11.5f, pal.PrimaryText, FontWeight.SemiBold);
            }
            else
            {
                CrystalUIHelpers.DrawPlayIcon(d2d, mediaRect.X + 14f, mediaRect.Y + mediaRect.Height * 0.5f, 11f, pal.Accent);
                CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _media.Title, mediaRect.X + 25f, mediaRect.Y, pillW - 34f, mediaRect.Height, 11.5f, pal.PrimaryText, FontWeight.Medium);
            }
            _hitZones.Add(("media", mediaRect, _onToggleMediaPlayPause));
        }
    }

    private float DrawMenuTextButton(ID2D1DeviceContext d2d, UiPalette pal, string id, string label, float x, float barTop, float barH, Action onClick)
    {
        float w = label.Length * 7.2f + 18f;
        var r = new RectangleF(x, barTop + 3f, w, barH - 6f);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, label, r.X, barTop, r.Width, barH, 12.5f, _hoverId == id ? pal.PrimaryText : pal.SecondaryText, FontWeight.Medium, TextAlignment.Center);
        _hitZones.Add((id, r, onClick));
        return x + w + 2f;
    }

    private string HitTestZone(Point pt)
    {
        foreach (var z in _hitZones)
        {
            if (z.Rect.Contains(pt.X, pt.Y)) return z.Id;
        }
        return "";
    }
}
