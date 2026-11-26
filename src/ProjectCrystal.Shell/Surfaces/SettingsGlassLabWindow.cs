using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.System;
using ProjectCrystal.Native.WindowManagement;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed class SettingsGlassLabWindow : GlassWindowBase
{
    private readonly HardwareProfile _hardware;
    private readonly DisplayBrightnessService _displaySvc;
    private readonly ShellIntegrationService _shellSvc;
    private readonly NotificationService _notifSvc;
    private readonly IDesktopBackdropSource? _backdrop;
    private readonly WindowManagerService _windowMgr;
    private readonly Action _onRestartShellSurfaces;
    private readonly Action _onSyncTaskbarPins;
    private readonly Action _onSafeExit;
    private readonly Action _onOpenLauncher;
    private readonly Action _onOpenExpose;

    private string _activeTab = "Glass Lab";
    private SpringFloat _activeTabY = new(82f);
    private readonly Dictionary<string, SpringFloat> _toggleSprings = new(StringComparer.OrdinalIgnoreCase);

    protected override PanelMotionPreset MotionPreset => PanelMotionPreset.MacWindowPop;

    private static readonly string[] Tabs =
    {
        "Glass Lab", "Appearance", "Dock", "Menu Bar", "Widgets",
        "Notifications", "Window Mgmt", "Performance", "Shell & Startup",
        "Accessibility", "Diagnostics"
    };

    private readonly List<(string Id, RectangleF Rect, Action<MouseEventArgs> OnAction)> _controls = new();
    private readonly List<(string Id, RectangleF TrackRect, float Min, float Max, Func<float> Getter, Action<float> Setter)> _sliders = new();
    private string _draggingSliderId = "";
    private bool _draggingWindow;
    private Point _dragStartMouse;
    private Point _dragStartWin;
    private string _statusToast = "";
    private float _benchTime;
    private float _benchRedrawAccum;

    public SettingsGlassLabWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        HardwareProfile hardware,
        DisplayBrightnessService displaySvc,
        ShellIntegrationService shellSvc,
        NotificationService notifSvc,
        WindowManagerService windowMgr,
        Action onRestartShellSurfaces,
        Action onSyncTaskbarPins,
        Action onSafeExit,
        Action onOpenLauncher,
        Action onOpenExpose)
        : base(gpu, configMgr, backdrop, noActivate: false, topMost: true, useShellOwner: false)
    {
        _hardware = hardware;
        _displaySvc = displaySvc;
        _shellSvc = shellSvc;
        _notifSvc = notifSvc;
        _backdrop = backdrop;
        _windowMgr = windowMgr;
        _onRestartShellSurfaces = onRestartShellSurfaces;
        _onSyncTaskbarPins = onSyncTaskbarPins;
        _onSafeExit = onSafeExit;
        _onOpenLauncher = onOpenLauncher;
        _onOpenExpose = onOpenExpose;

        SetBounds(160, 70, 1052, 708);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HideAnimated(-8f); };

        MouseDown += OnWinMouseDown;
        MouseMove += OnWinMouseMove;
        MouseUp += (_, __) => { _draggingWindow = false; _draggingSliderId = ""; MarkDirty(); };
    }

    public void ShowAndFocus(string? tab = null)
    {
        if (!string.IsNullOrEmpty(tab) && Tabs.Contains(tab))
            _activeTab = tab;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        if (!Visible)
        {
            SetBounds(screen.X + (screen.Width - Width) / 2, screen.Y + (screen.Height - Height) / 2, Width, Height);
            ShowAnimated(-10f);
        }
        else
        {
            SetBounds(
                Math.Clamp(Left, screen.X + 8, Math.Max(screen.X + 8, screen.Right - Width - 8)),
                Math.Clamp(Top, screen.Y + 8, Math.Max(screen.Y + 8, screen.Bottom - Height - 8)),
                Width, Height);
            Activate();
        }
        ProjectCrystal.Native.Interop.Win32Interop.SetWindowPos(
            Handle,
            ProjectCrystal.Native.Interop.Win32Interop.HWND_TOPMOST,
            Left, Top, Width, Height,
            ProjectCrystal.Native.Interop.Win32Interop.SWP_NOMOVE | ProjectCrystal.Native.Interop.Win32Interop.SWP_NOSIZE | ProjectCrystal.Native.Interop.Win32Interop.SWP_SHOWWINDOW);
        BringToFront();
        Activate();
        MarkDirty();
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        bool moving = false;
        bool instant = ShouldReduceMotion();
        int tabIdx = Array.IndexOf(Tabs, _activeTab);
        _activeTabY.Target = 84f + Math.Max(0, tabIdx) * 38f;
        moving |= _activeTabY.Step(dtSeconds, 540f, 34f, instant);

        foreach (var key in _toggleSprings.Keys.ToArray())
        {
            var sp = _toggleSprings[key];
            moving |= sp.Step(dtSeconds, 540f, 32f, instant);
            _toggleSprings[key] = sp;
        }

        if (_activeTab == "Glass Lab" && Visible && !instant && (IsMouseInside || !string.IsNullOrEmpty(_draggingSliderId)))
        {
            _benchTime += dtSeconds * Math.Max(0.25f, Config.Glass.AnimationSpeed);
            _benchRedrawAccum += dtSeconds;
            if (_benchRedrawAccum >= 0.042f)
            {
                _benchRedrawAccum = 0f;
                moving = true;
            }
        }
        return moving;
    }

    public void DrawStudioPreview(ID2D1DeviceContext d2d, float benchTime = 0.7f)
    {
        _benchTime = benchTime;
        DrawForeground2D(d2d);
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        return BuildStudioGlassShapes(Width, Height, _activeTab, _benchTime);
    }

    public static IReadOnlyList<GlassShapeItem> BuildStudioGlassShapes(int width, int height, string activeTab, float benchTime)
    {
        var list = new List<GlassShapeItem>
        {
            new() { X = 4, Y = 4, Width = width - 8, Height = height - 8, CornerRadius = 24, SquirclePower = 3.2f, HoverGlow = 0.06f, BevelScale = 1.0f }
        };

        if (activeTab == "Glass Lab")
        {
            // Floating refractive prism lens + orbiting Liquid-Motion droplet bubble over specimen chamber
            float cx = 244f;
            float benchCenter = cx + 210f;
            float lensX = benchCenter - 102f + MathF.Sin(benchTime * 1.1f) * 76f;
            list.Add(new GlassShapeItem
            {
                X = lensX,
                Y = 166f,
                Width = 204f,
                Height = 96f,
                CornerRadius = 24f,
                SquirclePower = 3.2f,
                HoverGlow = 0.34f,
                BevelScale = 1.25f
            });

            // Secondary circular Liquid-Motion spherical refraction lens (inspired by liquid-motion-design)
            float dropCx = benchCenter + MathF.Cos(benchTime * 1.35f) * 118f;
            float dropCy = 214f + MathF.Sin(benchTime * 1.85f) * 28f;
            list.Add(new GlassShapeItem
            {
                X = dropCx - 29f,
                Y = dropCy - 29f,
                Width = 58f,
                Height = 58f,
                CornerRadius = 29f,
                SquirclePower = 2.0f,
                HoverGlow = 0.45f,
                BevelScale = 1.35f
            });
        }

        return list;
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        _controls.Clear();
        _sliders.Clear();
        var pal = CrystalUIHelpers.GetPalette(IsDarkModeActive(), Config.Appearance.AccentColorHex);

        // 1. Sculpted Obsidian Studio Sidebar (pink-menu inspired + WWDC25 Traffic Capsule)
        CrystalUIHelpers.DrawHairlineCard(d2d, 14, 14, 214, Height - 28, 18, pal);

        // WWDC25 Floating Glass Traffic Lights Pill (Close / Minimize / Center)
        CrystalUIHelpers.DrawTrafficLightsCapsule(d2d, 22f, 22f, showThirdDot: true);
        _controls.Add(("winClose", new RectangleF(22, 22, 24, 26), _ => HideAnimated(-8f)));
        _controls.Add(("winMin", new RectangleF(46, 22, 22, 26), _ => HideAnimated(-8f)));
        _controls.Add(("winReset", new RectangleF(68, 22, 26, 26), _ => { ConfigMgr.Update(c => ConfigManager.ApplyMaterialPreset(c.Glass, GlassMaterialPreset.CrystalClear), true); _statusToast = "Reset material preset to Crystal Clear."; }));

        CrystalUIHelpers.DrawCrystalEmblem(d2d, 106f, 35f, 16f, pal.Accent);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Crystal", 119f, 23f, 100f, 24f, 13.5f, pal.PrimaryText, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "OPTICS ENGINE v2.4", 26f, 54f, 184f, 16f, 9f, pal.SubtleText, FontWeight.Bold);

        // Sliding Active Tab Pill with left accent spine
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, 22, _activeTabY.Value, 198, 34, 11,
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.28f),
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.12f));
        CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, 22, _activeTabY.Value, 198, 34, 11,
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.75f), new Color4(1f, 1f, 1f, 0.12f), 0.95f);
        CrystalUIHelpers.DrawRoundedRect(d2d, 25, _activeTabY.Value + 8, 3f, 18, 1.5f, pal.Accent);

        float ty = 84f;
        for (int i = 0; i < Tabs.Length; i++)
        {
            string tab = Tabs[i];
            var r = new RectangleF(22, ty, 198, 34);
            bool active = tab == _activeTab;
            DrawTabGlyph(d2d, tab, r.X + 20f, r.Y + 17f, active ? pal.Accent : pal.SubtleText, pal);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, tab, r.X + 36, r.Y, r.Width - 78, r.Height, 12.2f,
                active ? pal.PrimaryText : pal.SecondaryText, active ? FontWeight.SemiBold : FontWeight.Medium);

            string? tabBadge = tab switch
            {
                "Glass Lab" => "5",
                "Widgets" => $"{Config.Widgets.Items.Count(w => w.Enabled)}",
                "Performance" => $"{Config.Performance.TargetFps}Hz",
                _ => null
            };
            if (!string.IsNullOrEmpty(tabBadge))
            {
                CrystalUIHelpers.DrawRoundedRect(d2d, r.Right - 40f, r.Y + 8f, 32f, 18f, 9f,
                    active ? pal.AccentSoft : new Color4(1f, 1f, 1f, 0.08f));
                CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, tabBadge, r.Right - 40f, r.Y + 8f, 32f, 18f, 9f,
                    active ? pal.Accent : pal.SubtleText, FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
            }

            string captured = tab;
            _controls.Add(($"tab:{captured}", r, _ => { _activeTab = captured; _statusToast = ""; MarkDirty(); }));
            ty += 38f;
        }

        // Bottom Engine Telemetry Badge inside Sidebar
        float sideFootY = Height - 114f;
        CrystalUIHelpers.DrawHairlineCard(d2d, 24, sideFootY, 194, 86, 13, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "PIPELINE STATUS", 34, sideFootY + 8, 160, 14, 8.8f, pal.Accent, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, Config.Glass.Preset.ToString(), 34, sideFootY + 24, 174, 18, 12f, pal.PrimaryText, FontWeight.Bold);
        string backendShort = Config.Glass.CaptureBackend == CaptureBackendMode.LiveDesktopWgc ? "WGC Live Refraction" : "Wallpaper Composited";
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, backendShort, 34, sideFootY + 44, 174, 16, 10f, pal.SecondaryText);
        float iorApprox = 1.0f + Config.Glass.RefractionIntensity * 0.36f;
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, $"IOR {iorApprox:0.00}  •  {(int)(Config.Glass.Transparency * 100)}% Clarity", 34, sideFootY + 62, 174, 16, 10f, pal.SubtleText);

        // 2. Main Studio Content Area
        float cx = 244f;
        float cw = Width - cx - 20f;

        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _activeTab, cx, 18, 300, 30, 20.5f, pal.PrimaryText, FontWeight.Bold);

        // Quick Liquid-Glass Switch right in header for instant Light/Dark optical testing
        bool isDarkNow = IsDarkModeActive();
        if (!_toggleSprings.TryGetValue("hdrDark", out var hdrSp))
            hdrSp = new SpringFloat(isDarkNow ? 1f : 0f);
        hdrSp.Target = isDarkNow ? 1f : 0f;
        _toggleSprings["hdrDark"] = hdrSp;
        var hdrSwR = new RectangleF(Width - 322f, 20f, 94f, 28f);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, isDarkNow ? "Dark" : "Light", hdrSwR.X, hdrSwR.Y, 42f, 28f, 11f, pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Trailing);
        CrystalUIHelpers.DrawGlassToggle(d2d, hdrSwR.X + 46f, hdrSwR.Y + 2f, hdrSp.Value, pal, 46f, 24f, hdrSp.Velocity);
        _controls.Add(("hdrDark", hdrSwR, _ =>
        {
            bool next = !IsDarkModeActive();
            DisplayBrightnessService.SetWindowsDarkMode(next);
            ConfigMgr.Update(c => c.Appearance.Theme = next ? ThemeAppearance.Dark : ThemeAppearance.Light, true);
            MarkDirty();
        }));

        CrystalUIHelpers.DrawRoundedRect(d2d, Width - 218f, 22f, 198f, 24f, 12f, pal.ControlSurface, pal.HairlineStroke, 0.7f);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, $"{_hardware.GpuName}", Width - 210f, 22f, 182f, 24f, 9.5f, pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Center);

        using (var div = d2d.CreateSolidColorBrush(pal.Divider))
            d2d.DrawLine(new System.Numerics.Vector2(cx, 54), new System.Numerics.Vector2(Width - 20, 54), div, 1f);

        switch (_activeTab)
        {
            case "Glass Lab": DrawGlassLabTab(d2d, pal, cx, cw); break;
            case "Appearance": DrawAppearanceTab(d2d, pal, cx, cw); break;
            case "Dock": DrawDockTab(d2d, pal, cx, cw); break;
            case "Menu Bar": DrawMenuBarTab(d2d, pal, cx, cw); break;
            case "Widgets": DrawWidgetsTab(d2d, pal, cx, cw); break;
            case "Notifications": DrawNotificationsTab(d2d, pal, cx, cw); break;
            case "Window Mgmt": DrawWindowMgmtTab(d2d, pal, cx, cw); break;
            case "Performance": DrawPerformanceTab(d2d, pal, cx, cw); break;
            case "Shell & Startup": DrawShellTab(d2d, pal, cx, cw); break;
            case "Accessibility": DrawAccessibilityTab(d2d, pal, cx, cw); break;
            case "Diagnostics": DrawDiagnosticsTab(d2d, pal, cx, cw); break;
        }

        if (!string.IsNullOrEmpty(_statusToast))
        {
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, cx, Height - 38, cw, 26, 9,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.30f),
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.14f));
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _statusToast, cx + 12, Height - 38, cw - 24, 26, 11f, pal.PrimaryText, FontWeight.SemiBold);
        }
    }

    private void DrawTabGlyph(ID2D1DeviceContext d2d, string tab, float cx, float cy, Color4 col, UiPalette pal)
    {
        switch (tab)
        {
            case "Glass Lab": CrystalUIHelpers.DrawCrystalEmblem(d2d, cx, cy, 13f, col); break;
            case "Appearance": CrystalUIHelpers.DrawSunIcon(d2d, cx, cy, 13f, col); break;
            case "Dock": CrystalUIHelpers.DrawDesktopHomeIcon(d2d, cx, cy, 13f, col); break;
            case "Menu Bar": CrystalUIHelpers.DrawControlCenterIcon(d2d, cx, cy, 13f, col); break;
            case "Widgets": CrystalUIHelpers.DrawWeatherGlyph(d2d, cx, cy, 13f, 2, pal); break;
            case "Notifications": CrystalUIHelpers.DrawBellIcon(d2d, cx, cy, 13f, col); break;
            case "Window Mgmt": CrystalUIHelpers.DrawExposeGridIcon(d2d, cx, cy, 13f, col); break;
            case "Performance": CrystalUIHelpers.DrawRefreshIcon(d2d, cx, cy, 12f, col); break;
            case "Shell & Startup": CrystalUIHelpers.DrawFolderIcon(d2d, cx, cy, 12f, col); break;
            case "Accessibility": CrystalUIHelpers.DrawStarIcon(d2d, cx, cy, 13f, col, false); break;
            default: CrystalUIHelpers.DrawSearchIcon(d2d, cx, cy, 12f, col); break;
        }
    }

    private void DrawGlassLabTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        // Row 1: Material Preset Swatch Gallery Cards (4 sculpted optical cards)
        var presets = Enum.GetValues<GlassMaterialPreset>().Where(p => p != GlassMaterialPreset.Custom).ToArray();
        float cardGap = 10f;
        float pCardW = (cw - cardGap * (presets.Length - 1)) / presets.Length;
        float py = 64f;
        for (int i = 0; i < presets.Length; i++)
        {
            var preset = presets[i];
            var r = new RectangleF(cx + i * (pCardW + cardGap), py, pCardW, 56f);
            bool active = Config.Glass.Preset == preset;
            CrystalUIHelpers.DrawHairlineCard(d2d, r.X, r.Y, r.Width, r.Height, 13f, pal, active);
            if (active)
                CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, r.X, r.Y, r.Width, r.Height, 13f, pal.Accent, new Color4(1f, 1f, 1f, 0.28f), 1.4f);

            // Miniature Material Lens Preview Thumbnail on left of card
            float thumbX = r.X + 9f, thumbY = r.Y + 12f, thumbSz = 32f;
            var (gTop, gBot, titleShort, sub) = preset switch
            {
                GlassMaterialPreset.CrystalClear => (new Color4(0.55f, 0.92f, 1.0f, 0.85f), new Color4(0.18f, 0.56f, 0.95f, 0.65f), "Crystal", "Pure 1.52 IOR"),
                GlassMaterialPreset.LiquidMercury => (new Color4(0.86f, 0.92f, 0.98f, 0.90f), new Color4(0.45f, 0.54f, 0.66f, 0.82f), "Mercury", "Specular Chrome"),
                GlassMaterialPreset.FrostedQuartz => (new Color4(0.92f, 0.95f, 1.0f, 0.78f), new Color4(0.66f, 0.76f, 0.88f, 0.60f), "Quartz", "Satin Diffusion"),
                GlassMaterialPreset.ObsidianGlass => (new Color4(0.22f, 0.28f, 0.38f, 0.92f), new Color4(0.06f, 0.08f, 0.14f, 0.96f), "Obsidian", "Smoked Dark"),
                _ => (new Color4(0.46f, 0.74f, 1.0f, 0.92f), new Color4(0.38f, 0.24f, 0.86f, 0.86f), "Sapphire", "Prism Dispersion")
            };
            CrystalUIHelpers.DrawLuxurySquirclePlate(d2d, thumbX, thumbY, thumbSz, gTop, gBot, new Color4(gBot.R * 0.7f, gBot.G * 0.7f, gBot.B * 0.8f, 0.95f));
            CrystalUIHelpers.DrawCrystalEmblem(d2d, thumbX + thumbSz * 0.5f, thumbY + thumbSz * 0.5f, 15f, new Color4(1f, 1f, 1f, 0.94f));

            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, titleShort, r.X + 49f, r.Y + 10f, r.Width - 57f, 18f, 12f, pal.PrimaryText, FontWeight.Bold);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, sub, r.X + 49f, r.Y + 29f, r.Width - 57f, 16f, 9.6f, active ? pal.Accent : pal.SecondaryText, FontWeight.Medium);

            var pVal = preset;
            _controls.Add(($"preset:{pVal}", r, _ =>
            {
                ConfigMgr.Update(c => ConfigManager.ApplyMaterialPreset(c.Glass, pVal));
                _statusToast = $"Applied '{pVal}' Liquid Glass optical profile.";
                MarkDirty();
            }));
        }

        // Row 2: Left = Recessed Optical Specimen Test Chamber (with chromatic pattern & text right BEHIND the live GPU lens!)
        //        Right = Spectroscopic Dispersion Curve + 3 Machined Optical Dials
        float row2Y = 130f;
        float benchW = 420f;
        float benchH = 188f;
        DrawOpticalSpecimenBench(d2d, pal, cx, row2Y, benchW, benchH);

        float specX = cx + benchW + 12f;
        float specW = cw - benchW - 12f;
        CrystalUIHelpers.DrawSpectroscopicCurve(d2d, Gpu.DWriteFactory, specX, row2Y, specW, 108f,
            Config.Glass.RefractionIntensity, Config.Glass.ChromaticAberration, Config.Glass.EdgeLighting, _benchTime, pal);

        // 3 Precision Machined Optical Telemetry Dials under the Spectrograph
        float dialRowY = row2Y + 114f;
        CrystalUIHelpers.DrawHairlineCard(d2d, specX, dialRowY, specW, 74f, 12f, pal);
        float dStep = specW / 3f;
        float cyDial = dialRowY + 28f;
        CrystalUIHelpers.DrawPrecisionDial(d2d, Gpu.DWriteFactory, specX + dStep * 0.5f, cyDial, 16.5f,
            Config.Glass.Transparency, pal.Accent, "CLARITY", $"{Config.Glass.Transparency:P0}", pal);
        CrystalUIHelpers.DrawPrecisionDial(d2d, Gpu.DWriteFactory, specX + dStep * 1.5f, cyDial, 16.5f,
            Config.Glass.RefractionIntensity / 2.5f, new Color4(0.32f, 0.96f, 0.76f, 1f), "REFRACT", $"{Config.Glass.RefractionIntensity:0.00}x", pal);
        CrystalUIHelpers.DrawPrecisionDial(d2d, Gpu.DWriteFactory, specX + dStep * 2.5f, cyDial, 16.5f,
            Config.Glass.EdgeLighting / 2.5f, new Color4(1.0f, 0.56f, 0.86f, 1f), "FRESNEL", $"{Config.Glass.EdgeLighting:0.00}x", pal);

        // Row 3: Two Grouped Optical Instrument Control Cards
        float ctrlY = 328f;
        float colW = (cw - 12f) * 0.5f;
        float colH = 266f;
        CrystalUIHelpers.DrawHairlineCard(d2d, cx, ctrlY, colW, colH, 15f, pal);
        CrystalUIHelpers.DrawHairlineCard(d2d, cx + colW + 12f, ctrlY, colW, colH, 15f, pal);

        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "OPTICAL GEOMETRY & DISPERSION", cx + 14f, ctrlY + 10f, colW - 28f, 14f, 9.5f, pal.Accent, FontWeight.Bold);
        float y1 = ctrlY + 30f;
        float sw = colW - 28f;
        y1 = AddSliderRow(d2d, pal, "transparency", "Optical Transparency", cx + 14f, y1, sw, 0.30f, 0.99f, () => Config.Glass.Transparency, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.Transparency = v; }), $"{Config.Glass.Transparency:P0}", 38f, showTicks: true);
        y1 = AddSliderRow(d2d, pal, "refraction", "Refraction Index Boost", cx + 14f, y1, sw, 0f, 2.5f, () => Config.Glass.RefractionIntensity, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.RefractionIntensity = v; }), $"{Config.Glass.RefractionIntensity:0.00}x", 38f, showTicks: true);
        y1 = AddSliderRow(d2d, pal, "distortion", "Meniscus Curvature", cx + 14f, y1, sw, 0f, 2.5f, () => Config.Glass.DistortionStrength, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.DistortionStrength = v; }), $"{Config.Glass.DistortionStrength:0.00}x", 38f);
        y1 = AddSliderRow(d2d, pal, "magnify", "Lens Magnification", cx + 14f, y1, sw, 0.85f, 1.35f, () => Config.Glass.Magnification, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.Magnification = v; }), $"{Config.Glass.Magnification:0.00}x", 38f);
        y1 = AddSliderRow(d2d, pal, "blur", "Optical Diffusion (Blur)", cx + 14f, y1, sw, 0f, 14f, () => Config.Glass.BlurRadius, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.BlurRadius = v; }), $"{Config.Glass.BlurRadius:0.0} px", 38f);
        AddSliderRow(d2d, pal, "dispersion", "Chromatic Dispersion", cx + 14f, y1, sw, 0f, 3.0f, () => Config.Glass.ChromaticAberration, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.ChromaticAberration = v; }), $"{Config.Glass.ChromaticAberration:0.00}", 38f);

        float rx = cx + colW + 12f;
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "SURFACE LIGHTING & MATERIAL TINT", rx + 14f, ctrlY + 10f, colW - 28f, 14f, 9.5f, pal.Accent, FontWeight.Bold);
        float y2 = ctrlY + 30f;
        y2 = AddSliderRow(d2d, pal, "tintOp", "Body Tint Opacity", rx + 14f, y2, sw, 0f, 0.45f, () => Config.Glass.TintOpacity, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.TintOpacity = v; }), $"{Config.Glass.TintOpacity:P0}", 38f, showTicks: true);
        y2 = AddSliderRow(d2d, pal, "reflect", "Specular Glossiness", rx + 14f, y2, sw, 0f, 2.5f, () => Config.Glass.ReflectionIntensity, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.ReflectionIntensity = v; }), $"{Config.Glass.ReflectionIntensity:0.00}x", 38f, showTicks: true);
        y2 = AddSliderRow(d2d, pal, "edge", "Fresnel Rim Glint", rx + 14f, y2, sw, 0f, 2.5f, () => Config.Glass.EdgeLighting, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.EdgeLighting = v; }), $"{Config.Glass.EdgeLighting:0.00}x", 38f);
        y2 = AddSliderRow(d2d, pal, "depth", "Bevel Depth & Shadow", rx + 14f, y2, sw, 0f, 2.0f, () => Config.Glass.Depth, v => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.Depth = v; }), $"{Config.Glass.Depth:0.00}", 38f);
        y2 = AddSliderRow(d2d, pal, "anim", "Spring Physics Speed", rx + 14f, y2, sw, 0.25f, 2.0f, () => Config.Glass.AnimationSpeed, v => ConfigMgr.Update(c => { c.Glass.AnimationSpeed = v; }), $"{Config.Glass.AnimationSpeed:0.00}x", 38f);

        // Glass Tint Swatches right inside Right Column card
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Material Tint", rx + 14f, y2 + 4f, 100f, 24f, 11.5f, pal.SecondaryText, FontWeight.SemiBold);
        string[] tintSwatches = { "#EEF6FF", "#A8DCFF", "#D8C8FF", "#FFE2CC", "#C6FFE4", "#0B1018" };
        float sx = rx + 116f;
        foreach (string hex in tintSwatches)
        {
            var r = new RectangleF(sx, y2 + 4f, 24f, 24f);
            bool sel = string.Equals(Config.Glass.GlassTintHex, hex, StringComparison.OrdinalIgnoreCase);
            Color4 c4 = CrystalUIHelpers.ParseColor4(hex);
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, r.X + 12f, r.Y + 12f, 12f, 12f,
                new Color4(Math.Min(1f, c4.R + 0.15f), Math.Min(1f, c4.G + 0.15f), Math.Min(1f, c4.B + 0.15f), 1f),
                c4, -3f, -4f);
            using (var rim = d2d.CreateSolidColorBrush(sel ? pal.Accent : new Color4(1f, 1f, 1f, 0.42f)))
                d2d.DrawEllipse(new Ellipse(new System.Numerics.Vector2(r.X + 12f, r.Y + 12f), 12f, 12f), rim, sel ? 2.2f : 0.9f);
            string hCopy = hex;
            _controls.Add(($"tint:{hCopy}", r, _ => ConfigMgr.Update(c => { c.Glass.Preset = GlassMaterialPreset.Custom; c.Glass.GlassTintHex = hCopy; })));
            sx += 32f;
        }

        // Row 4: Capture Pipeline Bar + Reset Button
        float by = 604f;
        bool isWgc = Config.Glass.CaptureBackend == CaptureBackendMode.LiveDesktopWgc;
        var capBtn = new RectangleF(cx, by, cw - 196f, 36f);
        CrystalUIHelpers.DrawHairlineCard(d2d, capBtn.X, capBtn.Y, capBtn.Width, capBtn.Height, 11f, pal);
        using (var modeDot = d2d.CreateSolidColorBrush(isWgc ? pal.Success : pal.Warning))
            d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(capBtn.X + 16f, capBtn.Y + 18f), 4.5f, 4.5f), modeDot);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory,
            isWgc ? "Capture Engine: Live Desktop WGC Refraction (Exclusion Enabled)" : "Capture Engine: Wallpaper Composited (Screen-Recorder Safe Mode)",
            capBtn.X + 28f, capBtn.Y, capBtn.Width - 126f, capBtn.Height, 11.5f, pal.PrimaryText, FontWeight.SemiBold);
        CrystalUIHelpers.DrawRoundedRect(d2d, capBtn.Right - 94f, capBtn.Y + 6f, 84f, 24f, 8f, pal.AccentSoft);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Switch Mode", capBtn.Right - 94f, capBtn.Y + 6f, 84f, 24f, 10f, pal.Accent, FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
        _controls.Add(("capMode", capBtn, _ =>
        {
            ConfigMgr.Update(c => c.Glass.CaptureBackend = isWgc ? CaptureBackendMode.WallpaperComposited : CaptureBackendMode.LiveDesktopWgc, true);
            _onRestartShellSurfaces();
            _statusToast = $"Switched capture mode to {Config.Glass.CaptureBackend}";
        }));

        var resetBtn = new RectangleF(cx + cw - 184f, by, 184f, 36f);
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, resetBtn.X, resetBtn.Y, resetBtn.Width, resetBtn.Height, 11f,
            pal.Accent, new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.90f, 0.95f));
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Reset Studio Defaults", resetBtn.X, resetBtn.Y, resetBtn.Width, resetBtn.Height, 11.5f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center);
        _controls.Add(("resetGlass", resetBtn, _ =>
        {
            ConfigMgr.Update(c => ConfigManager.ApplyMaterialPreset(c.Glass, GlassMaterialPreset.CrystalClear), true);
            _statusToast = "Glass Studio calibrated to glossy Crystal Clear defaults.";
        }));
    }

    private void DrawOpticalSpecimenBench(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, float h)
    {
        // Chromatic calibration backdrop stage positioned directly behind the floating shader lens (BuildStudioGlassShapes)
        CrystalUIHelpers.FillRoundedRectMultiGradient(d2d, x, y, w, h, 14f,
            new[]
            {
                new GradientStop(0f, new Color4(0.05f, 0.08f, 0.16f, 0.85f)),
                new GradientStop(0.55f, new Color4(0.11f, 0.07f, 0.22f, 0.85f)),
                new GradientStop(1f, new Color4(0.04f, 0.14f, 0.20f, 0.85f))
            },
            new System.Numerics.Vector2(x, y), new System.Numerics.Vector2(x + w, y + h));

        // Optical grid & calibration targets directly underneath the live animated crystal lens (y=166..262)
        using (var gridB = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.11f)))
        {
            for (float gx = x + 20f; gx < x + w - 10f; gx += 28f)
                d2d.DrawLine(new System.Numerics.Vector2(gx, y + 18f), new System.Numerics.Vector2(gx, y + h - 28f), gridB, 0.8f);
            for (float gy = y + 24f; gy < y + h - 28f; gy += 24f)
                d2d.DrawLine(new System.Numerics.Vector2(x + 14f, gy), new System.Numerics.Vector2(x + w - 14f, gy), gridB, 0.8f);
        }

        // Liquid-motion undulating calibration spheres & optical specimen visible behind the live refracting lens
        float orb1Y = y + 84f + MathF.Sin(_benchTime * 1.4f) * 14f;
        float orb2Y = y + 92f + MathF.Cos(_benchTime * 1.2f) * 14f;
        float orb3X = x + 210f + MathF.Sin(_benchTime * 0.9f) * 28f;
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, x + 96f, orb1Y, 40f, 40f,
            new Color4(0.22f, 0.94f, 0.84f, 0.92f), new Color4(0.12f, 0.50f, 0.92f, 0f));
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, x + 314f, orb2Y, 42f, 42f,
            new Color4(1.0f, 0.40f, 0.72f, 0.88f), new Color4(0.65f, 0.20f, 0.95f, 0f));
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, orb3X, y + 90f, 34f, 34f,
            new Color4(1.0f, 0.80f, 0.28f, 0.82f), new Color4(1.0f, 0.50f, 0.18f, 0f));

        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "SPECIMEN • 1.52 IOR • REFRACTIVE OPTICS", x + 16f, y + 58f, w - 32f, 24f, 12.5f, new Color4(1f, 1f, 1f, 0.92f), FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "REAL-TIME GPU MENISCUS BEND & DISPERSION", x + 16f, y + 86f, w - 32f, 22f, 10.5f, new Color4(0.75f, 0.94f, 1f, 0.85f), FontWeight.SemiBold, TextAlignment.Center, ParagraphAlignment.Center, false);

        CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, x, y, w, h, 14f,
            new Color4(1f, 1f, 1f, 0.34f), new Color4(1f, 1f, 1f, 0.08f), 1.0f);

        // Header badge & bottom specimen telemetry bar
        CrystalUIHelpers.DrawRoundedRect(d2d, x + 10f, y + 8f, 168f, 18f, 9f, new Color4(0f, 0f, 0f, 0.45f));
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "OPTICAL BENCH • LIVE LENS", x + 14f, y + 8f, 160f, 18f, 8.8f, pal.Accent, FontWeight.Bold, TextAlignment.Center);

        CrystalUIHelpers.DrawRoundedRect(d2d, x + 10f, y + h - 26f, w - 20f, 19f, 6f, new Color4(0f, 0f, 0f, 0.48f));
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory,
            $"MAG {Config.Glass.Magnification:0.00}x   •   BLUR {Config.Glass.BlurRadius:0.0}px   •   CA {Config.Glass.ChromaticAberration:0.00}   •   GLOSS {Config.Glass.ReflectionIntensity:0.00}x",
            x + 14f, y + h - 26f, w - 28f, 19f, 9.2f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
    }

    private void DrawAppearanceTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        // Hero Theme Preview Strip
        CrystalUIHelpers.DrawHairlineCard(d2d, cx, y, cw, 82f, 14f, pal);
        CrystalUIHelpers.DrawLuxuryDesktopIcon(d2d, cx + 14f, y + 13f, 56f);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Active Windows Material Harmonization", cx + 84f, y + 16f, cw - 110f, 22f, 14f, pal.PrimaryText, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Synchronizes Crystal Glass translucency, contrast halos, and chromatic highlights across Windows 11.", cx + 84f, y + 40f, cw - 110f, 28f, 11.5f, pal.SecondaryText, FontWeight.Regular, TextAlignment.Leading, ParagraphAlignment.Near, true, true);
        y += 94f;

        y = AddSegmentedRow(d2d, pal, "Theme Mode", cx, y, cw, new[] { "Auto", "Light", "Dark" }, Config.Appearance.Theme.ToString(), val =>
        {
            var mode = Enum.Parse<ThemeAppearance>(val);
            if (mode == ThemeAppearance.Dark) DisplayBrightnessService.SetWindowsDarkMode(true);
            else if (mode == ThemeAppearance.Light) DisplayBrightnessService.SetWindowsDarkMode(false);
            ConfigMgr.Update(c => c.Appearance.Theme = mode, true);
        });

        y = AddToggleRow(d2d, pal, "followWinAccent", "Follow Windows System Accent Color", cx, y, cw, Config.Appearance.FollowWindowsAccent, v => ConfigMgr.Update(c => c.Appearance.FollowWindowsAccent = v));
        y = AddToggleRow(d2d, pal, "dynWall", "Dynamic Wallpaper Tinting", cx, y, cw, Config.Appearance.DynamicWallpaperTinting, v => ConfigMgr.Update(c => c.Appearance.DynamicWallpaperTinting = v));

        CrystalUIHelpers.DrawHairlineCard(d2d, cx, y + 6f, cw, 62f, 13f, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Curated Signature Accents", cx + 14f, y + 25f, 190f, 24f, 12.2f, pal.PrimaryText, FontWeight.SemiBold);
        string[] accents = { "#4CC2FF", "#34C759", "#AF52DE", "#FF9F0A", "#FF375F", "#0A84FF" };
        float ax = cx + 216f;
        foreach (string a in accents)
        {
            var r = new RectangleF(ax, y + 22f, 30f, 30f);
            bool sel = string.Equals(Config.Appearance.AccentColorHex, a, StringComparison.OrdinalIgnoreCase);
            Color4 c4 = CrystalUIHelpers.ParseColor4(a);
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, r.X + 15f, r.Y + 15f, 15f, 15f,
                new Color4(Math.Min(1f, c4.R + 0.2f), Math.Min(1f, c4.G + 0.2f), Math.Min(1f, c4.B + 0.2f), 1f), c4, -4f, -4f);
            using (var rim = d2d.CreateSolidColorBrush(sel ? new Color4(1f, 1f, 1f, 0.96f) : new Color4(1f, 1f, 1f, 0.30f)))
                d2d.DrawEllipse(new Ellipse(new System.Numerics.Vector2(r.X + 15f, r.Y + 15f), 15f, 15f), rim, sel ? 2.4f : 0.9f);
            string copy = a;
            _controls.Add(($"acc:{copy}", r, _ => ConfigMgr.Update(c => { c.Appearance.FollowWindowsAccent = false; c.Appearance.AccentColorHex = copy; })));
            ax += 42f;
        }
    }

    private void DrawDockTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddToggleRow(d2d, pal, "dockEn", "Enable Liquid Glass Dock", cx, y, cw, Config.Dock.Enabled, v => { ConfigMgr.Update(c => c.Dock.Enabled = v, true); _onRestartShellSurfaces(); });
        y = AddSegmentedRow(d2d, pal, "Dock Position", cx, y, cw, new[] { "Bottom", "Left", "Right" }, Config.Dock.Position.ToString(), val =>
        {
            ConfigMgr.Update(c => c.Dock.Position = Enum.Parse<DockPosition>(val), true);
            _onRestartShellSurfaces();
        });
        y = AddToggleRow(d2d, pal, "dockHide", "Auto-Hide Dock (Zero Bottom Screen Gap)", cx, y, cw, Config.Dock.AutoHide, v => ConfigMgr.Update(c => c.Dock.AutoHide = v));

        CrystalUIHelpers.DrawHairlineCard(d2d, cx, y + 2f, cw, 206f, 14f, pal);
        float sy = y + 14f;
        sy = AddSliderRow(d2d, pal, "dockSz", "Icon Size", cx + 14f, sy, cw - 28f, 34f, 76f, () => Config.Dock.IconSize, v => ConfigMgr.Update(c => c.Dock.IconSize = v), $"{Config.Dock.IconSize:0} px", 44f, showTicks: true);
        sy = AddSliderRow(d2d, pal, "dockMag", "Hover Magnification Wave", cx + 14f, sy, cw - 28f, 1.0f, 2.0f, () => Config.Dock.MagnificationScale, v => ConfigMgr.Update(c => c.Dock.MagnificationScale = v), $"{Config.Dock.MagnificationScale:0.00}x", 44f, showTicks: true);
        sy = AddSliderRow(d2d, pal, "dockDelay", "Auto-Hide Reveal Dwell", cx + 14f, sy, cw - 28f, 150f, 2000f, () => Config.Dock.AutoHideDelayMs, v => ConfigMgr.Update(c => c.Dock.AutoHideDelayMs = (int)v), $"{Config.Dock.AutoHideDelayMs} ms", 44f);
        AddSliderRow(d2d, pal, "dockSens", "Edge Trigger Band", cx + 14f, sy, cw - 28f, 1f, 12f, () => Config.Dock.RevealSensitivityPx, v => ConfigMgr.Update(c => c.Dock.RevealSensitivityPx = (int)v), $"{Config.Dock.RevealSensitivityPx} px", 44f);
        y += 216f;

        y = AddToggleRow(d2d, pal, "dockRec", "Show Minimized & Running Apps in Dock", cx, y, cw, Config.Dock.ShowRecentApps, v => ConfigMgr.Update(c => c.Dock.ShowRecentApps = v));

        var syncBtn = new RectangleF(cx, y + 6, 310, 34);
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, syncBtn.X, syncBtn.Y, syncBtn.Width, syncBtn.Height, 10,
            pal.Accent, new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.90f, 0.95f));
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Sync Pinned Apps from Windows Taskbar", syncBtn.X, syncBtn.Y, syncBtn.Width, syncBtn.Height, 11.5f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center);
        _controls.Add(("syncPins", syncBtn, _ =>
        {
            _onSyncTaskbarPins();
            _statusToast = "Synced pinned applications directly from your Windows Taskbar.";
        }));
    }

    private void DrawMenuBarTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddToggleRow(d2d, pal, "mbEn", "Enable Floating Glass Menu Bar", cx, y, cw, Config.MenuBar.Enabled, v => { ConfigMgr.Update(c => c.MenuBar.Enabled = v, true); _onRestartShellSurfaces(); });
        y = AddToggleRow(d2d, pal, "mbHide", "Auto-Hide Menu Bar (Zero Title-Bar Overlap)", cx, y, cw, Config.MenuBar.AutoHide, v => ConfigMgr.Update(c => c.MenuBar.AutoHide = v));

        CrystalUIHelpers.DrawHairlineCard(d2d, cx, y + 2f, cw, 160f, 14f, pal);
        float sy = y + 14f;
        sy = AddSliderRow(d2d, pal, "mbDelay", "Auto-Hide Dwell Delay", cx + 14f, sy, cw - 28f, 150f, 2000f, () => Config.MenuBar.AutoHideDelayMs, v => ConfigMgr.Update(c => c.MenuBar.AutoHideDelayMs = (int)v), $"{Config.MenuBar.AutoHideDelayMs} ms", 44f, showTicks: true);
        sy = AddSliderRow(d2d, pal, "mbSens", "Top Edge Reveal Zone", cx + 14f, sy, cw - 28f, 1f, 12f, () => Config.MenuBar.RevealSensitivityPx, v => ConfigMgr.Update(c => c.MenuBar.RevealSensitivityPx = (int)v), $"{Config.MenuBar.RevealSensitivityPx} px", 44f);
        AddSliderRow(d2d, pal, "mbH", "Menu Bar Height", cx + 14f, sy, cw - 28f, 26f, 42f, () => Config.MenuBar.Height, v => { ConfigMgr.Update(c => c.MenuBar.Height = (int)v); _onRestartShellSurfaces(); }, $"{Config.MenuBar.Height} px", 44f);
        y += 170f;

        y = AddSegmentedRow(d2d, pal, "Display Scope", cx, y, cw, new[] { "PrimaryOnly", "AllMonitors" }, Config.MenuBar.MonitorMode.ToString(), val =>
        {
            ConfigMgr.Update(c => c.MenuBar.MonitorMode = Enum.Parse<MenuBarMonitorMode>(val), true);
            _onRestartShellSurfaces();
        });
        y = AddToggleRow(d2d, pal, "mbApp", "Show Foreground Application Name", cx, y, cw, Config.MenuBar.ShowActiveApp, v => ConfigMgr.Update(c => c.MenuBar.ShowActiveApp = v));
        AddToggleRow(d2d, pal, "mb24", "Use 24-Hour Digital Clock", cx, y, cw, Config.MenuBar.Use24HourClock, v => ConfigMgr.Update(c => c.MenuBar.Use24HourClock = v));
    }

    private void DrawWidgetsTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddToggleRow(d2d, pal, "wgMaster", "Enable Translucent Desktop Widgets", cx, y, cw, Config.Widgets.Enabled, v => { ConfigMgr.Update(c => c.Widgets.Enabled = v, true); _onRestartShellSurfaces(); });
        y = AddToggleRow(d2d, pal, "wgFahr", "Weather Temperature in Fahrenheit (°F)", cx, y, cw, Config.Widgets.UseFahrenheit, v => ConfigMgr.Update(c => c.Widgets.UseFahrenheit = v));

        foreach (var item in Config.Widgets.Items)
        {
            string id = item.Id;
            y = AddToggleRow(d2d, pal, $"wg:{id}", $"Widget: {item.Title}  (drag anywhere on desktop)", cx, y, cw, item.Enabled, v =>
            {
                ConfigMgr.Update(c =>
                {
                    var match = c.Widgets.Items.FirstOrDefault(w => w.Id == id);
                    if (match != null) match.Enabled = v;
                }, true);
                _onRestartShellSurfaces();
            });
        }

        var resetPos = new RectangleF(cx, y + 10, 230, 34);
        CrystalUIHelpers.DrawHairlineCard(d2d, resetPos.X, resetPos.Y, resetPos.Width, resetPos.Height, 10, pal, true);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Reset Widget Positions", resetPos.X, resetPos.Y, resetPos.Width, resetPos.Height, 11.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        _controls.Add(("wgReset", resetPos, _ =>
        {
            var defaults = ConfigManager.CreateDefaults().Widgets.Items;
            ConfigMgr.Update(c => c.Widgets.Items = defaults, true);
            _onRestartShellSurfaces();
            _statusToast = "Widget positions restored.";
        }));
    }

    private void DrawNotificationsTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddInfoBox(d2d, pal, cx, y, cw, "Windows Notification Listener", _notifSvc.StatusNote);
        y += 10f;
        y = AddToggleRow(d2d, pal, "ntEn", "Enable Crystal Notification Center", cx, y, cw, Config.Notifications.Enabled, v => ConfigMgr.Update(c => c.Notifications.Enabled = v));
        y = AddToggleRow(d2d, pal, "ntDnd", "Do Not Disturb (Silence Popups)", cx, y, cw, Config.Notifications.DoNotDisturb, v => ConfigMgr.Update(c => c.Notifications.DoNotDisturb = v));
        y = AddToggleRow(d2d, pal, "ntGrp", "Group Notifications by Application", cx, y, cw, Config.Notifications.GroupByApp, v => ConfigMgr.Update(c => c.Notifications.GroupByApp = v));
        CrystalUIHelpers.DrawHairlineCard(d2d, cx, y + 4f, cw, 58f, 13f, pal);
        AddSliderRow(d2d, pal, "ntMax", "Max History Retention", cx + 14f, y + 12f, cw - 28f, 10f, 100f, () => Config.Notifications.MaxHistoryCount, v => ConfigMgr.Update(c => c.Notifications.MaxHistoryCount = (int)v), $"{Config.Notifications.MaxHistoryCount} items", 44f, showTicks: true);
    }

    private void DrawWindowMgmtTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddToggleRow(d2d, pal, "wmHot", "Enable Global Desktop & Stage Hotkeys", cx, y, cw, Config.WindowManagement.EnableHotkeys, v => ConfigMgr.Update(c => c.WindowManagement.EnableHotkeys = v, true));
        y = AddToggleRow(d2d, pal, "wmWork", "Reserve Top Work Area (when Menu Bar Auto-Hide is Off)", cx, y, cw, Config.WindowManagement.ReserveDesktopWorkArea, v => { ConfigMgr.Update(c => c.WindowManagement.ReserveDesktopWorkArea = v, true); _onRestartShellSurfaces(); });

        y += 6f;
        y = AddInfoBox(d2d, pal, cx, y, cw, "Global Shortcut Bindings",
            $"Launcher: {Config.WindowManagement.LauncherShortcut}  |  Exposé: {Config.WindowManagement.ExposeShortcut}  |  Control Center: {Config.WindowManagement.ControlCenterShortcut}\nSnap Left/Right/Max: {Config.WindowManagement.SnapLeftShortcut}, {Config.WindowManagement.SnapRightShortcut}, {Config.WindowManagement.SnapMaximizeShortcut}");

        y += 14f;
        var rowBtns = new (string Label, int GlyphMode, Action Act)[]
        {
            ("App Spotlight", 3, _onOpenLauncher),
            ("Exposé Stage", 2, _onOpenExpose),
            ("Prev Space", 0, () => _windowMgr.SwitchVirtualDesktop(false)),
            ("Next Space", 1, () => _windowMgr.SwitchVirtualDesktop(true))
        };
        float bx = cx;
        float bw = (cw - 30f) / 4f;
        foreach (var b in rowBtns)
        {
            var r = new RectangleF(bx, y, bw, 56f);
            CrystalUIHelpers.DrawHairlineCard(d2d, r.X, r.Y, r.Width, r.Height, 12f, pal, true);
            CrystalUIHelpers.DrawSnapTileGlyph(d2d, r.X + r.Width * 0.5f, r.Y + 20f, 18f, pal.Accent, b.GlyphMode);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, b.Label, r.X, r.Y + 32f, r.Width, 20f, 11f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
            var act = b.Act;
            _controls.Add(($"wmBtn:{b.Label}", r, _ => act()));
            bx += bw + 10f;
        }
    }

    private void DrawPerformanceTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddSegmentedRow(d2d, pal, "Quality Profile", cx, y, cw, new[] { "Auto", "Ultra", "Balanced", "Eco" }, Config.Performance.Quality.ToString(), val =>
        {
            ConfigMgr.Update(c =>
            {
                var q = Enum.Parse<QualityPreset>(val);
                c.Performance.Quality = q;
                if (q == QualityPreset.Eco) { c.Performance.TargetFps = 30; c.Performance.CaptureRateFps = 30; }
                else if (q == QualityPreset.Balanced) { c.Performance.TargetFps = 60; c.Performance.CaptureRateFps = 45; }
                else { c.Performance.TargetFps = 60; c.Performance.CaptureRateFps = 60; }
            }, true);
        });
        y = AddToggleRow(d2d, pal, "pfIdle", "Zero-Overhead Idle Rendering Pause", cx, y, cw, Config.Performance.PauseWhenIdle, v => ConfigMgr.Update(c => c.Performance.PauseWhenIdle = v));

        // Precision Gauge Strip + Sliders
        CrystalUIHelpers.DrawHairlineCard(d2d, cx, y + 2f, cw, 118f, 14f, pal);
        CrystalUIHelpers.DrawAnalogGaugeArc(d2d, Gpu.DWriteFactory, cx + 68f, y + 60f, 36f, Config.Performance.TargetFps / 120f,
            pal.Accent, new Color4(0.32f, 0.96f, 0.76f, 1f), $"{Config.Performance.TargetFps}", "FPS CAP", pal);
        CrystalUIHelpers.DrawAnalogGaugeArc(d2d, Gpu.DWriteFactory, cx + 162f, y + 60f, 36f, Math.Clamp((float)_hardware.TotalRamGb / 32f, 0.1f, 1f),
            new Color4(0.56f, 0.48f, 1f, 1f), pal.Accent, $"{_hardware.TotalRamGb:0}GB", "SYSTEM RAM", pal);

        float sx = cx + 224f, sw = cw - 240f;
        float sy = AddSliderRow(d2d, pal, "pfFps", "Render Refresh Rate Cap", sx, y + 14f, sw, 24f, 120f, () => Config.Performance.TargetFps, v => ConfigMgr.Update(c => c.Performance.TargetFps = (int)v), $"{Config.Performance.TargetFps} FPS", 42f, showTicks: true);
        AddSliderRow(d2d, pal, "pfPoll", "Metrics Poll Interval", sx, sy, sw, 500f, 5000f, () => Config.Performance.MetricsPollIntervalMs, v => ConfigMgr.Update(c => c.Performance.MetricsPollIntervalMs = (int)v), $"{Config.Performance.MetricsPollIntervalMs} ms", 42f);
        y += 128f;

        y = AddInfoBox(d2d, pal, cx, y, cw, "Detected Hardware Profile",
            $"CPU: {_hardware.CpuName} ({_hardware.LogicalProcessors} threads)\nGPU: {_hardware.GpuName}  |  RAM: {_hardware.TotalRamGb:0.0} GB  |  Recommended: {_hardware.RecommendedQuality}");

        var mons = _displaySvc.GetMonitors();
        string monText = string.Join("\n", mons.Select(m => $"Display {m.Index + 1} ({m.DeviceName}): {m.Width}x{m.Height} @ {m.Dpi} DPI {(m.IsPrimary ? "[Primary]" : "")}"));
        AddInfoBox(d2d, pal, cx, y + 10f, cw, "Connected Displays", monText);
    }

    private void DrawShellTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddSegmentedRow(d2d, pal, "Windows Taskbar", cx, y, cw, new[] { "AutoHideAndReplace", "WindowsAutoHideOnly", "KeepVisible" }, Config.ShellMode.TaskbarMode.ToString(), val =>
        {
            var tm = Enum.Parse<WindowsTaskbarMode>(val);
            ConfigMgr.Update(c =>
            {
                c.ShellMode.TaskbarMode = tm;
                c.ShellMode.AutoHideWindowsTaskbar = tm != WindowsTaskbarMode.KeepVisible;
                c.ShellMode.Mode = tm == WindowsTaskbarMode.KeepVisible ? ShellOperatingMode.SafeDesktop : ShellOperatingMode.AdvancedShellIntegration;
            }, true);
            _shellSvc.ApplyShellMode(Config.ShellMode, _hardware);
            _statusToast = $"Windows Taskbar mode set to '{tm}'.";
        });

        y = AddInfoBox(d2d, pal, cx, y + 6, cw, $"OS Shell Compatibility ({_hardware.OsCaption})", ShellIntegrationService.GetShellCompatibilityExplanation(_hardware));
        y += 12f;

        y = AddToggleRow(d2d, pal, "shStart", "Launch Project Crystal Automatically at Windows Sign-In", cx, y, cw, Config.ShellMode.StartWithWindows, v =>
        {
            ConfigMgr.Update(c => c.ShellMode.StartWithWindows = v, true);
            ShellIntegrationService.SetRunAtStartup(v);
        });

        var peekBtn = new RectangleF(cx, y + 12, 248, 36);
        CrystalUIHelpers.DrawHairlineCard(d2d, peekBtn.X, peekBtn.Y, peekBtn.Width, peekBtn.Height, 10, pal, true);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, _shellSvc.IsOriginalTaskbarPeeked ? "Hide Original Windows Taskbar" : "Peek Original Windows Taskbar", peekBtn.X, peekBtn.Y, peekBtn.Width, peekBtn.Height, 11.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        _controls.Add(("peekTaskbar", peekBtn, _ => { _shellSvc.TogglePeekOriginalTaskbar(Config.ShellMode); MarkDirty(); }));

        var exitBtn = new RectangleF(cx + 260, y + 12, 268, 36);
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, exitBtn.X, exitBtn.Y, exitBtn.Width, exitBtn.Height, 10,
            new Color4(0.95f, 0.30f, 0.34f, 0.92f), new Color4(0.72f, 0.16f, 0.22f, 0.92f));
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "Safe Exit & Restore Windows Desktop", exitBtn.X, exitBtn.Y, exitBtn.Width, exitBtn.Height, 11.5f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center);
        _controls.Add(("safeExit", exitBtn, _ => _onSafeExit()));
    }

    private void DrawAccessibilityTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        y = AddSegmentedRow(d2d, pal, "Reduced Motion", cx, y, cw, new[] { "FollowWindows", "AlwaysReduce", "AlwaysFull" }, Config.Accessibility.MotionMode.ToString(), val =>
        {
            ConfigMgr.Update(c => c.Accessibility.MotionMode = Enum.Parse<ReducedMotionMode>(val), true);
        });
        y = AddToggleRow(d2d, pal, "accEdges", "High-Visibility Specular Borders", cx, y, cw, Config.Accessibility.HighVisibilityEdges, v => ConfigMgr.Update(c => c.Accessibility.HighVisibilityEdges = v));
        AddToggleRow(d2d, pal, "accContrast", "Enforce Minimum Text Contrast Halos", cx, y, cw, Config.Accessibility.EnforceMinimumTextContrast, v => ConfigMgr.Update(c => c.Accessibility.EnforceMinimumTextContrast = v));
    }

    private void DrawDiagnosticsTab(ID2D1DeviceContext d2d, UiPalette pal, float cx, float cw)
    {
        float y = 68f;
        var btns = new (string Label, Action Act)[]
        {
            ("Backup Config", () =>
            {
                using var sfd = new SaveFileDialog { Filter = "JSON Config|*.json", FileName = $"crystal-backup-{DateTime.Now:yyyyMMdd}.json" };
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    ConfigMgr.ExportBackup(sfd.FileName);
                    _statusToast = $"Saved backup to {Path.GetFileName(sfd.FileName)}";
                }
            }),
            ("Restore Config", () =>
            {
                using var ofd = new OpenFileDialog { Filter = "JSON Config|*.json" };
                if (ofd.ShowDialog() == DialogResult.OK && ConfigMgr.ImportBackup(ofd.FileName))
                {
                    _onRestartShellSurfaces();
                    _statusToast = "Restored configuration backup.";
                }
            }),
            ("Open Logs Folder", () =>
            {
                Directory.CreateDirectory(CrystalLogger.LogDirectory);
                Process.Start(new ProcessStartInfo { FileName = CrystalLogger.LogDirectory, UseShellExecute = true });
            }),
            ("Factory Reset", () =>
            {
                ConfigMgr.ResetToDefaults();
                _onSyncTaskbarPins();
                _onRestartShellSurfaces();
                _statusToast = "All settings restored to factory defaults.";
            })
        };

        float bx = cx;
        float bw = (cw - 30f) / 4f;
        foreach (var b in btns)
        {
            var r = new RectangleF(bx, y, bw, 34);
            CrystalUIHelpers.DrawRoundedRect(d2d, r.X, r.Y, r.Width, r.Height, 10, b.Label == "Factory Reset" ? new Color4(0.85f, 0.25f, 0.28f, 0.78f) : pal.ControlSurface, pal.HairlineStroke, 0.75f);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, b.Label, r.X, r.Y, r.Width, r.Height, 11.5f, b.Label == "Factory Reset" ? new Color4(1f, 1f, 1f, 1f) : pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
            var act = b.Act;
            _controls.Add(($"diag:{b.Label}", r, _ => act()));
            bx += bw + 10f;
        }

        y += 46f;
        string captureTxt = _backdrop?.BackendStatusText ?? "Fallback Backdrop";
        y = AddInfoBox(d2d, pal, cx, y, cw, "Active Capture & Rendering Pipeline",
            $"GPU: {_hardware.GpuName}  |  Capture: {captureTxt}  |  Config: {ConfigMgr.ConfigPath}");

        y += 12f;
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, "RECENT DIAGNOSTIC LOGS", cx, y, 260, 16, 10f, pal.SubtleText, FontWeight.Bold);
        y += 20f;
        var logs = CrystalLogger.GetRecentEntries().TakeLast(14).Reverse().ToArray();
        CrystalUIHelpers.DrawHairlineCard(d2d, cx, y, cw, Height - y - 48, 12, pal);
        float ly = y + 8f;
        foreach (var entry in logs)
        {
            if (ly > Height - 70) break;
            Color4 c = entry.Level == LogLevel.Error ? pal.Danger : (entry.Level == LogLevel.Warn ? pal.Warning : pal.SecondaryText);
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, $"[{entry.Timestamp:HH:mm:ss}] [{entry.Category}] {entry.Message}", cx + 10, ly, cw - 20, 18, 11f, c);
            ly += 20f;
        }
    }

    private float AddSliderRow(ID2D1DeviceContext d2d, UiPalette pal, string id, string label, float x, float y, float w, float min, float max, Func<float> getter, Action<float> setter, string valueText, float rowStep = 44f, bool showTicks = false)
    {
        float cur = getter();
        float t = Math.Clamp((cur - min) / Math.Max(0.0001f, max - min), 0f, 1f);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, label, x, y, w - 78f, 18f, 11.5f, pal.PrimaryText, FontWeight.Medium);

        // Tabular readout pill badge
        CrystalUIHelpers.DrawRoundedRect(d2d, x + w - 66f, y, 66f, 18f, 6f, pal.AccentSoft);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, valueText, x + w - 64f, y, 62f, 18f, 10.5f, pal.Accent, FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);

        var trackRect = new RectangleF(x, y + 18f, w, 18f);
        CrystalUIHelpers.DrawGlassSlider(d2d, x, y + 26f, w, t, pal, 5.5f, _draggingSliderId == id, showTicks);
        _sliders.Add((id, trackRect, min, max, getter, setter));
        return y + rowStep;
    }

    private float AddToggleRow(ID2D1DeviceContext d2d, UiPalette pal, string id, string label, float x, float y, float w, bool current, Action<bool> setter)
    {
        if (!_toggleSprings.TryGetValue(id, out var sp))
            sp = new SpringFloat(current ? 1f : 0f);
        sp.Target = current ? 1f : 0f;
        _toggleSprings[id] = sp;

        var rowR = new RectangleF(x, y, w, 36);
        CrystalUIHelpers.DrawHairlineCard(d2d, rowR.X, rowR.Y, rowR.Width, rowR.Height, 11, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, label, x + 14, y, w - 82, 36, 12f, pal.PrimaryText, FontWeight.Medium);
        CrystalUIHelpers.DrawGlassToggle(d2d, x + w - 60, y + 6f, sp.Value, pal, 46f, 24f, sp.Velocity);
        _controls.Add((id, rowR, _ => { setter(!current); MarkDirty(); }));
        return y + 42f;
    }

    private float AddSegmentedRow(ID2D1DeviceContext d2d, UiPalette pal, string label, float x, float y, float w, string[] options, string current, Action<string> onSelect)
    {
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, label, x, y, 150, 32, 12f, pal.PrimaryText, FontWeight.SemiBold);
        float segX = x + 156;
        float segW = (w - 156 - (options.Length - 1) * 6f) / options.Length;
        foreach (string opt in options)
        {
            var r = new RectangleF(segX, y + 2, segW, 29);
            bool sel = string.Equals(opt, current, StringComparison.OrdinalIgnoreCase);
            if (sel)
            {
                CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, r.X, r.Y, r.Width, r.Height, 9,
                    pal.Accent, new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.90f, 0.95f));
            }
            else
            {
                CrystalUIHelpers.DrawRoundedRect(d2d, r.X, r.Y, r.Width, r.Height, 9, pal.ControlSurface, pal.HairlineStroke, 0.7f);
            }
            CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, opt, r.X, r.Y, r.Width, r.Height, 11f, sel ? new Color4(1f, 1f, 1f, 1f) : pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
            string oVal = opt;
            _controls.Add(($"seg:{label}:{oVal}", r, _ => { onSelect(oVal); MarkDirty(); }));
            segX += segW + 6f;
        }
        return y + 40f;
    }

    private float AddInfoBox(ID2D1DeviceContext d2d, UiPalette pal, float x, float y, float w, string title, string body)
    {
        int lines = body.Split('\n').Length;
        float h = 36f + lines * 18f;
        CrystalUIHelpers.DrawHairlineCard(d2d, x, y, w, h, 13, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, title, x + 14, y + 8, w - 28, 18, 11.5f, pal.Accent, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, Gpu.DWriteFactory, body, x + 14, y + 28, w - 28, h - 32, 11f, pal.SecondaryText, FontWeight.Regular, TextAlignment.Leading, ParagraphAlignment.Near, true, true);
        return y + h;
    }

    private void OnWinMouseDown(object? sender, MouseEventArgs e)
    {
        foreach (var c in _controls)
        {
            if (c.Rect.Contains(e.X, e.Y)) { c.OnAction(e); return; }
        }
        foreach (var s in _sliders)
        {
            if (s.TrackRect.Contains(e.X, e.Y))
            {
                _draggingSliderId = s.Id;
                ApplySliderMouse(s, e.X);
                return;
            }
        }
        if (e.Y < 56)
        {
            _draggingWindow = true;
            _dragStartMouse = PointToScreen(e.Location);
            _dragStartWin = Location;
        }
    }

    private void OnWinMouseMove(object? sender, MouseEventArgs e)
    {
        if (_draggingWindow)
        {
            var cur = PointToScreen(e.Location);
            Location = new Point(_dragStartWin.X + (cur.X - _dragStartMouse.X), _dragStartWin.Y + (cur.Y - _dragStartMouse.Y));
            MarkDirty();
            return;
        }
        if (!string.IsNullOrEmpty(_draggingSliderId))
        {
            var s = _sliders.FirstOrDefault(x => x.Id == _draggingSliderId);
            if (!string.IsNullOrEmpty(s.Id))
                ApplySliderMouse(s, e.X);
        }
    }

    private void ApplySliderMouse((string Id, RectangleF TrackRect, float Min, float Max, Func<float> Getter, Action<float> Setter) s, int mouseX)
    {
        float t = Math.Clamp((mouseX - s.TrackRect.X) / s.TrackRect.Width, 0f, 1f);
        float val = s.Min + t * (s.Max - s.Min);
        if (MathF.Abs(val - s.Getter()) <= (s.Max - s.Min) * 0.002f)
            return;
        s.Setter(val);
        MarkDirty();
    }
}
