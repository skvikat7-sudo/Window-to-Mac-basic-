using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.System;
using ProjectCrystal.Native.WindowManagement;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed class ControlCenterWindow : GlassWindowBase
{
    private readonly CoreAudioService _audio;
    private readonly DisplayBrightnessService _display;
    private readonly NetworkRadioService _networkSvc;
    private readonly MediaSessionService _mediaSvc;
    private readonly WindowManagerService _windowMgr;
    private readonly Action _onOpenSettings;
    private readonly Action _onOpenExpose;

    private NetworkStatusInfo _net = new(false, false, false, false, "Checking...", "Ethernet", "-", 0, 0);
    private BluetoothStatusInfo _bt = new(false, false, "Unavailable", 0);
    private BrightnessStatusInfo _bright = new(false, "Checking", 0, 100, 100, "Display");
    private MediaSessionInfo _media = new(false, "", "No Media Playing", "", "", false, false, false, false, null);
    private IReadOnlyList<AudioDeviceInfo> _audioDevices = Array.Empty<AudioDeviceInfo>();
    private float _volume = 0.5f;
    private bool _muted;
    private bool _darkMode;
    private float _mediaPulseSec;
    private float _mediaPulseAccum;
    private string _statusMessage = "";

    private readonly List<(string Id, RectangleF Rect, Action<MouseEventArgs> OnAction)> _controls = new();
    private string _hoverId = "";
    private string _draggingSlider = "";
    private RectangleF _volTrackRect;
    private RectangleF _brightTrackRect;
    private bool _showDevicePicker;

    protected override PanelMotionPreset MotionPreset => PanelMotionPreset.MacPopover;

    public event Action<SnapTarget>? SnapRequested;

    public ControlCenterWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        CoreAudioService audio,
        DisplayBrightnessService display,
        NetworkRadioService networkSvc,
        MediaSessionService mediaSvc,
        WindowManagerService windowMgr,
        Action onOpenSettings,
        Action onOpenExpose)
        : base(gpu, configMgr, backdrop, noActivate: false, topMost: true)
    {
        _audio = audio;
        _display = display;
        _networkSvc = networkSvc;
        _mediaSvc = mediaSvc;
        _windowMgr = windowMgr;
        _onOpenSettings = onOpenSettings;
        _onOpenExpose = onOpenExpose;

        SetBounds(0, 0, 368, 520);

        Deactivate += (_, __) => { _showDevicePicker = false; HideAnimated(-10f); };
        MouseDown += OnCcMouseDown;
        MouseMove += OnCcMouseMove;
        MouseUp += (_, __) => { _draggingSlider = ""; MarkDirty(); };
        MouseLeave += (_, __) => { _hoverId = ""; MarkDirty(); };
    }

    public void ToggleAt(Rectangle monitorBounds, int menuBarHeight)
    {
        if (Visible && PanelMotion.IsOpenOrOpening)
        {
            HideAnimated(-10f);
            return;
        }
        int x = monitorBounds.Right - Width - 12;
        int y = monitorBounds.Y + menuBarHeight + 10;
        SetBounds(x, y, Width, Height);
        _ = RefreshStateAsync();
        ShowAnimated(-12f);
    }

    public async Task RefreshStateAsync()
    {
        var netTask = _networkSvc.GetNetworkStatusAsync();
        var btTask = _networkSvc.GetBluetoothStatusAsync();
        var mediaTask = _mediaSvc.GetCurrentAsync();
        var brightTask = Task.Run(() => _display.GetBrightnessStatus());
        var audioTask = Task.Run(() => (_audio.GetMasterVolumeState(), _audio.GetOutputDevices()));
        await Task.WhenAll(netTask, btTask, mediaTask, brightTask, audioTask);

        _net = netTask.Result;
        _bt = btTask.Result;
        _media = mediaTask.Result;
        _bright = brightTask.Result;
        ((_volume, _muted, _), _audioDevices) = audioTask.Result;
        _darkMode = DisplayBrightnessService.IsWindowsDarkMode();
        MarkDirty();
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        if (Visible && _media.HasSession && _media.IsPlaying && !ShouldReduceMotion())
        {
            _mediaPulseSec += dtSeconds;
            _mediaPulseAccum += dtSeconds;
            if (_mediaPulseAccum >= 0.055f)
            {
                _mediaPulseAccum = 0f;
                return true;
            }
        }
        return false;
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        int audioH = _showDevicePicker ? 158 : 92;
        int mediaY = 214 + audioH + 10;
        return new[]
        {
            new GlassShapeItem
            {
                X = 4,
                Y = 4,
                Width = Width - 8,
                Height = Height - 8,
                CornerRadius = 22,
                SquirclePower = 3.2f,
                HoverGlow = IsMouseInside ? 0.14f : 0.04f,
                BevelScale = 1.0f
            },
            // Embedded Liquid-Glass spherical Play/Pause lens orb (inspired by liquid-glass-music-player)
            new GlassShapeItem
            {
                X = 18 + 332 - 82,
                Y = mediaY + 22,
                Width = 38,
                Height = 38,
                CornerRadius = 19,
                SquirclePower = 2.0f,
                HoverGlow = _hoverId == "mediaPlay" ? 0.52f : 0.24f,
                BevelScale = 1.15f
            }
        };
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        _controls.Clear();
        var pal = CrystalUIHelpers.GetPalette(IsDarkModeActive(), Config.Appearance.AccentColorHex);

        // Top Grid: Left Connectivity Card (16, 16, 162, 114) & Right Quick Toggles (190, 16, 162, 114)
        DrawConnectivityCard(d2d, pal, 18, 18, 160, 112);
        DrawQuickTogglesCard(d2d, pal, 190, 18, 160, 112);

        // Brightness Section (18, 142, 332, 62)
        DrawBrightnessCard(d2d, pal, 18, 142, 332, 62);

        // Sound & Output Switcher Section (18, 214, 332, 92 / 158)
        int audioH = _showDevicePicker ? 158 : 92;
        DrawAudioCard(d2d, pal, 18, 214, 332, audioH);

        // Media Playback Section
        int mediaY = 214 + audioH + 10;
        DrawMediaCard(d2d, pal, 18, mediaY, 332, 86);

        // Window Tiling Section
        int snapY = mediaY + 96;
        DrawSnapCard(d2d, pal, 18, snapY, 332, 82);

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _statusMessage, 20, Height - 26, Width - 40, 18, 10.5f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Center);
        }
    }

    private void DrawConnectivityCard(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, float h)
    {
        CrystalUIHelpers.DrawHairlineCard(d2d, x, y, w, h, 16, pal);

        var netRect = new RectangleF(x + 8, y + 8, w - 16, 44);
        bool netActive = _net.HasWifiHardware ? _net.IsWifiEnabled : _net.IsConnected;
        if (_hoverId == "net")
            CrystalUIHelpers.DrawRoundedRect(d2d, netRect.X, netRect.Y, netRect.Width, netRect.Height, 10, pal.ControlHover);

        using (var circle = d2d.CreateSolidColorBrush(netActive ? pal.Accent : pal.ControlTrack))
        {
            if (netActive)
            {
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, netRect.X + 18, netRect.Y + 22, 20f, 20f,
                    new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.34f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, netRect.X + 18, netRect.Y + 22, 14f, 14f,
                    new Color4(Math.Min(1f, pal.Accent.R + 0.16f), Math.Min(1f, pal.Accent.G + 0.16f), Math.Min(1f, pal.Accent.B + 0.16f), 1f),
                    new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.90f, 1f), -3f, -4f);
            }
            else
            {
                d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(netRect.X + 18, netRect.Y + 22), 14, 14), circle);
            }
        }

        if (_net.HasWifiHardware)
            CrystalUIHelpers.DrawWifiIcon(d2d, netRect.X + 18, netRect.Y + 22, 15, new Color4(1f, 1f, 1f, 0.96f), _net.IsWifiConnected);
        else
            CrystalUIHelpers.DrawEthernetIcon(d2d, netRect.X + 18, netRect.Y + 22, 15, new Color4(1f, 1f, 1f, 0.96f), _net.IsConnected);

        string netTitle = _net.HasWifiHardware ? "Wi-Fi" : "Ethernet";
        string netSub = _net.IsConnected ? $"{_net.LinkSpeedMbps} Mbps" : "Offline";
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, netTitle, netRect.X + 38, netRect.Y + 5, netRect.Width - 42, 18, 12f, pal.PrimaryText, FontWeight.SemiBold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, netSub, netRect.X + 38, netRect.Y + 22, netRect.Width - 42, 16, 10.5f, pal.SecondaryText);
        _controls.Add(("net", netRect, async _ =>
        {
            if (_net.HasWifiHardware) await _networkSvc.SetWifiEnabledAsync(!_net.IsWifiEnabled);
            else _statusMessage = $"Ethernet IP: {_net.IpAddress}";
            await RefreshStateAsync();
        }));

        var btRect = new RectangleF(x + 8, y + 58, w - 16, 44);
        if (_hoverId == "bt")
            CrystalUIHelpers.DrawRoundedRect(d2d, btRect.X, btRect.Y, btRect.Width, btRect.Height, 10, pal.ControlHover);

        bool btActive = _bt.IsSupported && _bt.IsEnabled;
        if (btActive)
        {
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, btRect.X + 18, btRect.Y + 22, 20f, 20f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.34f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, btRect.X + 18, btRect.Y + 22, 14f, 14f,
                new Color4(Math.Min(1f, pal.Accent.R + 0.16f), Math.Min(1f, pal.Accent.G + 0.16f), Math.Min(1f, pal.Accent.B + 0.16f), 1f),
                new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.90f, 1f), -3f, -4f);
        }
        else
        {
            using var circle = d2d.CreateSolidColorBrush(pal.ControlTrack);
            d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(btRect.X + 18, btRect.Y + 22), 14, 14), circle);
        }
        CrystalUIHelpers.DrawBluetoothIcon(d2d, btRect.X + 18, btRect.Y + 22, 14, new Color4(1f, 1f, 1f, 0.94f), _bt.IsEnabled);

        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Bluetooth", btRect.X + 38, btRect.Y + 5, btRect.Width - 42, 18, 12f, pal.PrimaryText, FontWeight.SemiBold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _bt.IsSupported ? (_bt.IsEnabled ? "On" : "Off") : "No Hardware", btRect.X + 38, btRect.Y + 22, btRect.Width - 42, 16, 10.5f, pal.SecondaryText);
        _controls.Add(("bt", btRect, async _ =>
        {
            if (_bt.IsSupported) await _networkSvc.SetBluetoothEnabledAsync(!_bt.IsEnabled);
            else _statusMessage = "No Bluetooth radio detected";
            await RefreshStateAsync();
        }));
    }

    private void DrawQuickTogglesCard(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, float h)
    {
        float bw = (w - 8f) * 0.5f;
        float bh = (h - 8f) * 0.5f;

        // Dark/Light Mode
        var rTheme = new RectangleF(x, y, bw, bh);
        CrystalUIHelpers.DrawHairlineCard(d2d, rTheme.X, rTheme.Y, rTheme.Width, rTheme.Height, 14, pal, _hoverId == "theme");
        if (_darkMode)
        {
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, rTheme.X + 1, rTheme.Y + 1, rTheme.Width - 2, rTheme.Height - 2, 13,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.28f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.10f));
        }
        CrystalUIHelpers.DrawMoonIcon(d2d, rTheme.X + bw * 0.5f, rTheme.Y + 19, 15, _darkMode ? pal.Accent : pal.PrimaryText);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _darkMode ? "Dark" : "Light", rTheme.X, rTheme.Y + 32, bw, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        _controls.Add(("theme", rTheme, _ =>
        {
            bool nextDark = !_darkMode;
            DisplayBrightnessService.SetWindowsDarkMode(nextDark);
            _darkMode = nextDark;
            ConfigMgr.Update(c => c.Appearance.Theme = nextDark ? ThemeAppearance.Dark : ThemeAppearance.Light);
            MarkDirty();
        }));

        // Do Not Disturb
        bool dnd = Config.Notifications.DoNotDisturb;
        var rDnd = new RectangleF(x + bw + 8, y, bw, bh);
        CrystalUIHelpers.DrawHairlineCard(d2d, rDnd.X, rDnd.Y, rDnd.Width, rDnd.Height, 14, pal, _hoverId == "dnd");
        if (dnd)
        {
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, rDnd.X + 1, rDnd.Y + 1, rDnd.Width - 2, rDnd.Height - 2, 13,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.28f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.10f));
        }
        CrystalUIHelpers.DrawBellIcon(d2d, rDnd.X + bw * 0.5f, rDnd.Y + 19, 14.5f, dnd ? pal.Accent : pal.PrimaryText);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, dnd ? "Silent" : "Alerts", rDnd.X, rDnd.Y + 32, bw, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        _controls.Add(("dnd", rDnd, _ =>
        {
            ConfigMgr.Update(c => c.Notifications.DoNotDisturb = !c.Notifications.DoNotDisturb);
            MarkDirty();
        }));

        // Exposé
        var rExp = new RectangleF(x, y + bh + 8, bw, bh);
        CrystalUIHelpers.DrawHairlineCard(d2d, rExp.X, rExp.Y, rExp.Width, rExp.Height, 14, pal, _hoverId == "expose");
        CrystalUIHelpers.DrawExposeGridIcon(d2d, rExp.X + bw * 0.5f, rExp.Y + 19, 15, pal.PrimaryText);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Exposé", rExp.X, rExp.Y + 32, bw, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        _controls.Add(("expose", rExp, _ => { HideAnimated(-8f); _onOpenExpose(); }));

        // Glass Lab
        var rLab = new RectangleF(x + bw + 8, y + bh + 8, bw, bh);
        CrystalUIHelpers.DrawHairlineCard(d2d, rLab.X, rLab.Y, rLab.Width, rLab.Height, 14, pal, _hoverId == "lab");
        CrystalUIHelpers.DrawCrystalEmblem(d2d, rLab.X + bw * 0.5f, rLab.Y + 19, 15, pal.Accent);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Glass Lab", rLab.X, rLab.Y + 32, bw, 16, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        _controls.Add(("lab", rLab, _ => { HideAnimated(-8f); _onOpenSettings(); }));
    }

    private void DrawBrightnessCard(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, float h)
    {
        CrystalUIHelpers.DrawHairlineCard(d2d, x, y, w, h, 15, pal);
        string label = _bright.IsSupported ? $"Display  •  {_bright.Current}%" : $"Display  •  {_bright.Method}";
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, label, x + 14, y + 8, w - 28, 16, 11f, pal.SecondaryText, FontWeight.SemiBold);

        CrystalUIHelpers.DrawSunIcon(d2d, x + 22, y + 40, 15, _bright.IsSupported ? pal.PrimaryText : pal.SubtleText);
        _brightTrackRect = new RectangleF(x + 42, y + 30, w - 58, 20);
        float val01 = _bright.IsSupported && _bright.Max > _bright.Min
            ? (_bright.Current - _bright.Min) / (float)(_bright.Max - _bright.Min)
            : 1f;
        CrystalUIHelpers.DrawGlassSlider(d2d, _brightTrackRect.X, y + 40, _brightTrackRect.Width, val01, pal, 5.5f, _draggingSlider == "bright");
        _controls.Add(("brightSlider", _brightTrackRect, e =>
        {
            if (!_bright.IsSupported) { _statusMessage = "Hardware DDC/CI brightness not supported on this display link"; return; }
            ApplyBrightnessFromMouse(e.X);
        }));
    }

    private void DrawAudioCard(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, float h)
    {
        CrystalUIHelpers.DrawHairlineCard(d2d, x, y, w, h, 15, pal);

        string defName = _audioDevices.FirstOrDefault(d => d.IsDefault)?.Name ?? "Audio Output";
        var devBtn = new RectangleF(x + 12, y + 8, w - 24, 20);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, defName, x + 14, y + 8, w - 92, 18, 11.5f, pal.SecondaryText, FontWeight.SemiBold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _showDevicePicker ? "Hide" : "Output", x + w - 72, y + 8, 58, 18, 11f, pal.Accent, FontWeight.SemiBold, TextAlignment.Trailing);
        _controls.Add(("audioDevices", devBtn, _ => { _showDevicePicker = !_showDevicePicker; Height = _showDevicePicker ? 586 : 520; MarkDirty(); }));

        var muteBtn = new RectangleF(x + 10, y + 36, 26, 26);
        CrystalUIHelpers.DrawVolumeIcon(d2d, x + 22, y + 50, 15, _muted ? pal.Danger : pal.PrimaryText, _volume, _muted);
        _controls.Add(("mute", muteBtn, me =>
        {
            _audio.ToggleMute();
            (_volume, _muted, _) = _audio.GetMasterVolumeState();
            MarkDirty();
        }));

        _volTrackRect = new RectangleF(x + 42, y + 40, w - 94, 20);
        CrystalUIHelpers.DrawGlassSlider(d2d, _volTrackRect.X, y + 50, _volTrackRect.Width, _muted ? 0f : _volume, pal, 5.5f, _draggingSlider == "vol");
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _muted ? "Mute" : $"{(int)(_volume * 100)}%", x + w - 48, y + 40, 36, 20, 11f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Trailing);
        _controls.Add(("volSlider", _volTrackRect, e => ApplyVolumeFromMouse(e.X)));

        if (_showDevicePicker)
        {
            float dy = y + 78f;
            foreach (var dev in _audioDevices.Take(3))
            {
                var dr = new RectangleF(x + 12, dy, w - 24, 24);
                if (dev.IsDefault)
                    CrystalUIHelpers.DrawRoundedRect(d2d, dr.X, dr.Y, dr.Width, dr.Height, 7, pal.AccentSoft);
                else if (_hoverId == $"dev:{dev.Id}")
                    CrystalUIHelpers.DrawRoundedRect(d2d, dr.X, dr.Y, dr.Width, dr.Height, 7, pal.ControlHover);

                CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, (dev.IsDefault ? "• " : "  ") + dev.Name, dr.X + 8, dr.Y, dr.Width - 16, dr.Height, 11f, pal.PrimaryText);
                string devId = dev.Id;
                _controls.Add(($"dev:{devId}", dr, async _ =>
                {
                    _audio.SetDefaultOutputDevice(devId);
                    await RefreshStateAsync();
                }));
                dy += 26f;
            }
        }
    }

    private void DrawMediaCard(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, float h)
    {
        CrystalUIHelpers.DrawHairlineCard(d2d, x, y, w, h, 16, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "NOW PLAYING", x + 14, y + 10, 96, 14, 9.5f, pal.SubtleText, FontWeight.Bold);
        CrystalUIHelpers.DrawAcousticWaveform(d2d, x + 98, y + 17, _mediaPulseSec, pal.Accent, _media.HasSession && _media.IsPlaying, 7);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _media.HasSession ? _media.Title : "Nothing Playing", x + 14, y + 29, w - 144, 20, 13f, pal.PrimaryText, FontWeight.SemiBold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _media.HasSession ? _media.Artist : "No active media session", x + 14, y + 51, w - 144, 18, 11f, pal.SecondaryText);

        var prevR = new RectangleF(x + w - 118, y + 27, 28, 28);
        var playR = new RectangleF(x + w - 82, y + 22, 38, 38);
        var nextR = new RectangleF(x + w - 36, y + 27, 28, 28);
        CrystalUIHelpers.DrawGlassOrbButton(d2d, prevR.X + 14, prevR.Y + 14, 14f, pal.Accent, false, _hoverId == "mediaPrev");
        CrystalUIHelpers.DrawGlassOrbButton(d2d, playR.X + 19, playR.Y + 19, 19f, pal.Accent, true, _hoverId == "mediaPlay");
        CrystalUIHelpers.DrawGlassOrbButton(d2d, nextR.X + 14, nextR.Y + 14, 14f, pal.Accent, false, _hoverId == "mediaNext");

        CrystalUIHelpers.DrawSkipIcon(d2d, prevR.X + 14, prevR.Y + 14, 13, pal.PrimaryText, false);
        if (_media.IsPlaying) CrystalUIHelpers.DrawPauseIcon(d2d, playR.X + 19, playR.Y + 19, 15, new Color4(1f, 1f, 1f, 1f));
        else CrystalUIHelpers.DrawPlayIcon(d2d, playR.X + 19, playR.Y + 19, 15, new Color4(1f, 1f, 1f, 1f));
        CrystalUIHelpers.DrawSkipIcon(d2d, nextR.X + 14, nextR.Y + 14, 13, pal.PrimaryText, true);

        _controls.Add(("mediaPrev", prevR, async _ => { await _mediaSvc.PreviousAsync(); await RefreshStateAsync(); }));
        _controls.Add(("mediaPlay", playR, async _ => { await _mediaSvc.TogglePlayPauseAsync(); await RefreshStateAsync(); }));
        _controls.Add(("mediaNext", nextR, async _ => { await _mediaSvc.NextAsync(); await RefreshStateAsync(); }));
    }

    private void DrawSnapCard(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, float h)
    {
        CrystalUIHelpers.DrawHairlineCard(d2d, x, y, w, h, 15, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "WINDOW LAYOUT", x + 14, y + 8, 160, 14, 9.5f, pal.SubtleText, FontWeight.Bold);

        var items = new (string Label, int Glyph, Action Act)[]
        {
            ("Left", 0, () => { if (SnapRequested != null) SnapRequested(SnapTarget.LeftHalf); else _windowMgr.SnapWindow(0, SnapTarget.LeftHalf, 12, 12); }),
            ("Right", 1, () => { if (SnapRequested != null) SnapRequested(SnapTarget.RightHalf); else _windowMgr.SnapWindow(0, SnapTarget.RightHalf, 12, 12); }),
            ("Full", 2, () => { if (SnapRequested != null) SnapRequested(SnapTarget.Maximize); else _windowMgr.SnapWindow(0, SnapTarget.Maximize, 12, 12); }),
            ("Center", 3, () => { if (SnapRequested != null) SnapRequested(SnapTarget.CenterFocus); else _windowMgr.SnapWindow(0, SnapTarget.CenterFocus, 12, 12); })
        };

        float bx = x + 12f;
        float bw = (w - 24f - 18f) / 4f;
        for (int i = 0; i < items.Length; i++)
        {
            var r = new RectangleF(bx + i * (bw + 6f), y + 28f, bw, 42f);
            string id = $"snap:{i}";
            CrystalUIHelpers.DrawRoundedRect(d2d, r.X, r.Y, r.Width, r.Height, 10, _hoverId == id ? pal.ControlHover : pal.ControlSurface, pal.HairlineStroke, 0.7f);
            CrystalUIHelpers.DrawSnapTileGlyph(d2d, r.X + r.Width * 0.5f, r.Y + 15f, 16f, pal.PrimaryText, items[i].Glyph);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, items[i].Label, r.X, r.Y + 24f, r.Width, 16, 10f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Center);
            var act = items[i].Act;
            _controls.Add((id, r, _ => act()));
        }
    }

    private void OnCcMouseDown(object? sender, MouseEventArgs e)
    {
        if (_volTrackRect.Contains(e.X, e.Y)) { _draggingSlider = "vol"; ApplyVolumeFromMouse(e.X); return; }
        if (_brightTrackRect.Contains(e.X, e.Y) && _bright.IsSupported) { _draggingSlider = "bright"; ApplyBrightnessFromMouse(e.X); return; }

        foreach (var c in _controls)
        {
            if (c.Rect.Contains(e.X, e.Y)) { c.OnAction(e); break; }
        }
    }

    private void OnCcMouseMove(object? sender, MouseEventArgs e)
    {
        if (_draggingSlider == "vol") { ApplyVolumeFromMouse(e.X); return; }
        if (_draggingSlider == "bright") { ApplyBrightnessFromMouse(e.X); return; }

        string hit = "";
        foreach (var c in _controls)
        {
            if (c.Rect.Contains(e.X, e.Y)) { hit = c.Id; break; }
        }
        if (hit != _hoverId) { _hoverId = hit; MarkDirty(); }
    }

    private void ApplyVolumeFromMouse(int mouseX)
    {
        float t = Math.Clamp((mouseX - _volTrackRect.X) / _volTrackRect.Width, 0f, 1f);
        _audio.SetMasterVolumeScalar(t);
        _volume = t;
        _muted = t <= 0.005f;
        MarkDirty();
    }

    private void ApplyBrightnessFromMouse(int mouseX)
    {
        float t = Math.Clamp((mouseX - _brightTrackRect.X) / _brightTrackRect.Width, 0f, 1f);
        int val = (int)Math.Round(_bright.Min + t * (_bright.Max - _bright.Min));
        _display.SetBrightness(val);
        _bright = _bright with { Current = val };
        MarkDirty();
    }
}
