using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Core.Models;
using ProjectCrystal.Native.Apps;
using ProjectCrystal.Native.Capture;
using ProjectCrystal.Native.System;
using ProjectCrystal.Native.WindowManagement;
using ProjectCrystal.Rendering.Core;
using ProjectCrystal.Rendering.UI;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace ProjectCrystal.Shell.Surfaces;

public sealed class LauncherWindow : GlassWindowBase
{
    private const int GridCols = 3;
    private const int GridMaxItems = 12;

    protected override PanelMotionPreset MotionPreset => PanelMotionPreset.MacLaunchpad;

    private readonly InstalledAppsService _appsService;
    private readonly IconTextureRegistry _icons;
    private readonly WindowManagerService _windowMgr;
    private readonly Action _onOpenSettings;
    private readonly Action _onOpenExpose;

    private string _query = "";
    private string _selectedCategory = "All";
    private int _selectedIdx;
    private float _animTime;
    private SpringFloat _selBoxX = new(22f);
    private SpringFloat _selBoxY = new(126f);
    private SpringFloat _selBoxAlpha = new(0f);
    private SpringFloat _catPillX = new(22f);
    private SpringFloat _catPillW = new(44f);
    private bool _catPillInitialized;

    private IReadOnlyList<AppEntry> _allApps = Array.Empty<AppEntry>();
    private List<AppEntry> _filtered = new();
    private IReadOnlyList<WindowInfo> _openWindows = Array.Empty<WindowInfo>();
    private string? _calcResult;
    private string _copiedToast = "";

    private readonly List<(string Category, RectangleF Rect)> _catRects = new();
    private readonly List<(AppEntry App, RectangleF CardRect, RectangleF PinRect)> _itemRects = new();
    private readonly List<(RectangleF Rect, Action OnClick)> _actionRects = new();

    private static readonly string[] Categories =
        { "All", "Recent", "Internet", "Developer", "Productivity", "Media & Creative", "System", "Utilities" };

    public LauncherWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        InstalledAppsService appsService,
        IconTextureRegistry icons,
        WindowManagerService windowMgr,
        Action onOpenSettings,
        Action onOpenExpose)
        : base(gpu, configMgr, backdrop, noActivate: false, topMost: true)
    {
        _appsService = appsService;
        _icons = icons;
        _windowMgr = windowMgr;
        _onOpenSettings = onOpenSettings;
        _onOpenExpose = onOpenExpose;

        SetBounds(0, 0, 840, 598);
        KeyPreview = true;

        Deactivate += (_, __) => HideAnimated(-14f);
        KeyDown += OnLauncherKeyDown;
        KeyPress += OnLauncherKeyPress;
        MouseMove += OnLauncherMouseMove;
        MouseClick += OnLauncherMouseClick;
    }

    public void ToggleCentered(Rectangle monitorBounds)
    {
        if (Visible && PanelMotion.IsOpenOrOpening) { HideAnimated(-14f); return; }
        _query = "";
        _copiedToast = "";
        _selectedIdx = 0;
        _allApps = _appsService.GetCachedApps();
        _openWindows = _windowMgr.GetOpenWindows();
        if (_allApps.Count == 0)
            _ = RefreshAppsAsync();
        ApplyFilter();

        int x = monitorBounds.X + (monitorBounds.Width - Width) / 2;
        int y = monitorBounds.Y + (int)(monitorBounds.Height * 0.14);
        SetBounds(x, y, Width, Height);
        ShowAnimated(-16f);
        Focus();
    }

    public async Task RefreshAppsAsync()
    {
        _allApps = await _appsService.RefreshInstalledAppsAsync();
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(() => { ApplyFilter(); MarkDirty(); });
    }

    private void ApplyFilter()
    {
        _calcResult = TryEvaluateQuickCommand(_query);
        IEnumerable<AppEntry> q = _allApps;

        if (_selectedCategory == "Recent")
            q = q.Where(a => a.LastLaunched.HasValue).OrderByDescending(a => a.LastLaunched);
        else if (_selectedCategory != "All")
            q = q.Where(a => string.Equals(a.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(_query) && !_query.StartsWith("="))
        {
            string term = _query.Trim();
            q = q.Where(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || a.Category.Contains(term, StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(a => a.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                 .ThenBy(a => a.Name);
        }

        _filtered = q.Take(GridMaxItems).ToList();
        foreach (var app in _filtered) _icons.PreloadIcon(app.IconKey);
        _selectedIdx = Math.Clamp(_selectedIdx, 0, Math.Max(0, _filtered.Count - 1));
    }

    private static string? TryEvaluateQuickCommand(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("=") && trimmed.Length > 1)
        {
            try
            {
                var dt = new DataTable();
                var res = dt.Compute(trimmed[1..], "");
                return $"= {res}";
            }
            catch { return null; }
        }
        return trimmed.ToLowerInvariant() switch
        {
            "dark" => "Switch Windows to Dark Appearance",
            "light" => "Switch Windows to Light Appearance",
            "expose" or "windows" => "Open Exposé Stage Overview",
            "glass" or "lab" or "settings" => "Open Project Crystal Glass Studio",
            _ => null
        };
    }

    private void ExecuteSelected()
    {
        string cmd = _query.Trim().ToLowerInvariant();
        if (cmd == "dark") { DisplayBrightnessService.SetWindowsDarkMode(true); HideAnimated(-12f); return; }
        if (cmd == "light") { DisplayBrightnessService.SetWindowsDarkMode(false); HideAnimated(-12f); return; }
        if (cmd is "expose" or "windows") { HideAnimated(-12f); _onOpenExpose(); return; }
        if (cmd is "glass" or "lab" or "settings") { HideAnimated(-12f); _onOpenSettings(); return; }
        if (!string.IsNullOrEmpty(_calcResult) && _query.TrimStart().StartsWith("="))
        {
            try
            {
                Clipboard.SetText(_calcResult.TrimStart('=', ' '));
                _copiedToast = "Copied result to clipboard";
                MarkDirty();
            }
            catch { }
            return;
        }

        if (_selectedIdx >= 0 && _selectedIdx < _filtered.Count)
        {
            var app = _filtered[_selectedIdx];
            var running = FindRunningWindow(app);
            HideAnimated(-12f);
            if (running != null)
                _windowMgr.FocusWindow(running.Handle);
            else
                _appsService.LaunchApp(app.LaunchTarget, app.Arguments);
        }
    }

    private WindowInfo? FindRunningWindow(AppEntry app)
    {
        string exeName = Path.GetFileNameWithoutExtension(app.LaunchTarget);
        return _openWindows.FirstOrDefault(w =>
            (!string.IsNullOrEmpty(exeName) && Path.GetFileNameWithoutExtension(w.ExecutablePath).Equals(exeName, StringComparison.OrdinalIgnoreCase))
            || string.Equals(w.ProcessName, app.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void OnLauncherKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { HideAnimated(-12f); e.Handled = true; return; }
        if (e.KeyCode == Keys.Enter) { ExecuteSelected(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Tab)
        {
            int curCat = Array.IndexOf(Categories, _selectedCategory);
            _selectedCategory = Categories[(curCat + 1) % Categories.Length];
            ApplyFilter();
            MarkDirty();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Back && _query.Length > 0)
        {
            _query = _query[..^1];
            _copiedToast = "";
            ApplyFilter();
            MarkDirty();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Right) { _selectedIdx = Math.Min(_filtered.Count - 1, _selectedIdx + 1); MarkDirty(); }
        else if (e.KeyCode == Keys.Left) { _selectedIdx = Math.Max(0, _selectedIdx - 1); MarkDirty(); }
        else if (e.KeyCode == Keys.Down) { _selectedIdx = Math.Min(_filtered.Count - 1, _selectedIdx + GridCols); MarkDirty(); }
        else if (e.KeyCode == Keys.Up) { _selectedIdx = Math.Max(0, _selectedIdx - GridCols); MarkDirty(); }
    }

    private void OnLauncherKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!char.IsControl(e.KeyChar))
        {
            _query += e.KeyChar;
            _copiedToast = "";
            ApplyFilter();
            MarkDirty();
            e.Handled = true;
        }
    }

    private void OnLauncherMouseMove(object? sender, MouseEventArgs e)
    {
        for (int i = 0; i < _itemRects.Count; i++)
        {
            if (_itemRects[i].CardRect.Contains(e.X, e.Y))
            {
                if (_selectedIdx != i) { _selectedIdx = i; MarkDirty(); }
                break;
            }
        }
    }

    private void OnLauncherMouseClick(object? sender, MouseEventArgs e)
    {
        foreach (var act in _actionRects)
        {
            if (act.Rect.Contains(e.X, e.Y)) { act.OnClick(); return; }
        }

        foreach (var c in _catRects)
        {
            if (c.Rect.Contains(e.X, e.Y))
            {
                _selectedCategory = c.Category;
                ApplyFilter();
                MarkDirty();
                return;
            }
        }

        for (int i = 0; i < _itemRects.Count; i++)
        {
            var entry = _itemRects[i];
            if (entry.PinRect.Contains(e.X, e.Y))
            {
                TogglePinToDock(entry.App);
                MarkDirty();
                return;
            }
            if (entry.CardRect.Contains(e.X, e.Y))
            {
                _selectedIdx = i;
                ExecuteSelected();
                return;
            }
        }
    }

    private void TogglePinToDock(AppEntry app)
    {
        ConfigMgr.Update(cfg =>
        {
            string normTarget = WindowsTaskbarPinsService.NormalizeKey(app.LaunchTarget);
            int idx = cfg.Dock.PinnedApps.FindIndex(p => string.Equals(WindowsTaskbarPinsService.NormalizeKey(p.Target), normTarget, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                cfg.Dock.PinnedApps.RemoveAt(idx);
            else
                cfg.Dock.PinnedApps.Add(new PinnedAppEntry { Id = app.Id, Name = app.Name, Target = app.LaunchTarget, Arguments = app.Arguments, IsUwp = app.IsPackagedUwp });
        });
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        return BuildLauncherShapes(Width, Height, IsMouseInside, !string.IsNullOrEmpty(_calcResult));
    }

    public static IReadOnlyList<GlassShapeItem> BuildLauncherShapes(int w, int h, bool mouseInside, bool hasCalcBanner = false)
    {
        float inspY = hasCalcBanner ? 176f : 122f;
        return new[]
        {
            new GlassShapeItem { X = 4, Y = 4, Width = w - 8, Height = h - 8, CornerRadius = 26, SquirclePower = 3.2f, HoverGlow = mouseInside ? 0.16f : 0.06f, BevelScale = 1.0f },
            new GlassShapeItem { X = 18, Y = 16, Width = w - 36, Height = 58, CornerRadius = 18, SquirclePower = 3.2f, HoverGlow = 0.24f, BevelScale = 0.92f },
            new GlassShapeItem { X = w - 268, Y = inspY, Width = 250, Height = h - inspY - 46f, CornerRadius = 16, SquirclePower = 3.2f, HoverGlow = 0.14f, BevelScale = 0.85f }
        };
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        RenderLauncherContent(
            d2d, Gpu.DWriteFactory, Width, Height, IsDarkModeActive(), Config,
            _icons, _allApps, _filtered, _openWindows,
            _query, _selectedCategory, _selectedIdx, _calcResult, _copiedToast, _animTime,
            _selBoxX.Value, _selBoxY.Value, _selBoxAlpha.Value, _catPillX.Value, _catPillW.Value, _catPillInitialized,
            _catRects, _itemRects, _actionRects,
            onClearQuery: () => { _query = ""; _copiedToast = ""; ApplyFilter(); MarkDirty(); },
            onTogglePin: app => { TogglePinToDock(app); MarkDirty(); },
            onLaunchApp: app =>
            {
                HideAnimated(-12f);
                var running = FindRunningWindow(app);
                if (running != null) _windowMgr.FocusWindow(running.Handle);
                else _appsService.LaunchApp(app.LaunchTarget, app.Arguments);
            },
            onOpenLocation: app =>
            {
                try
                {
                    if (File.Exists(app.LaunchTarget))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{app.LaunchTarget}\"") { UseShellExecute = true });
                }
                catch { }
            },
            onOpenExpose: () => { HideAnimated(-12f); _onOpenExpose(); },
            onOpenSettings: () => { HideAnimated(-12f); _onOpenSettings(); },
            onToggleTheme: () => { DisplayBrightnessService.SetWindowsDarkMode(!IsDarkModeActive()); MarkDirty(); },
            onIconLoaded: () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(MarkDirty); });
    }

    public static void RenderLauncherContent(
        ID2D1DeviceContext d2d,
        IDWriteFactory dw,
        int width, int height,
        bool isDark,
        CrystalConfig cfg,
        IconTextureRegistry icons,
        IReadOnlyList<AppEntry> allApps,
        IReadOnlyList<AppEntry> filtered,
        IReadOnlyList<WindowInfo> openWindows,
        string query,
        string selectedCategory,
        int selectedIdx,
        string? calcResult,
        string copiedToast,
        float animTime,
        float selBoxX, float selBoxY, float selBoxAlpha,
        float catPillX, float catPillW, bool catPillInit,
        List<(string Category, RectangleF Rect)> catRects,
        List<(AppEntry App, RectangleF CardRect, RectangleF PinRect)> itemRects,
        List<(RectangleF Rect, Action OnClick)> actionRects,
        Action? onClearQuery = null,
        Action<AppEntry>? onTogglePin = null,
        Action<AppEntry>? onLaunchApp = null,
        Action<AppEntry>? onOpenLocation = null,
        Action? onOpenExpose = null,
        Action? onOpenSettings = null,
        Action? onToggleTheme = null,
        Action? onIconLoaded = null)
    {
        catRects.Clear();
        itemRects.Clear();
        actionRects.Clear();
        var pal = CrystalUIHelpers.GetPalette(isDark, cfg.Appearance.AccentColorHex);

        // 1. Hero Optical Search Well with subtle radial focus aura & crystal trim
        float swX = 18f, swY = 16f, swW = width - 36f, swH = 58f;
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, swX + 46f, swY + swH * 0.5f, 54f, 34f,
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.34f),
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, swX, swY, swW, swH, 18f,
            new Color4(1f, 1f, 1f, isDark ? 0.10f : 0.44f),
            new Color4(0.04f, 0.06f, 0.10f, isDark ? 0.38f : 0.14f));
        CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, swX, swY, swW, swH, 18f,
            new Color4(1f, 1f, 1f, 0.52f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.32f), 1.1f);

        // Spotlight Emblem 3D Glass Orb + Magnifier on left (inspired by LiquidGlassDesign Day 14 & Search Modal)
        CrystalUIHelpers.DrawGlassOrbButton(d2d, swX + 30f, swY + 29f, 18.5f, pal.Accent, true);
        CrystalUIHelpers.DrawSearchIcon(d2d, swX + 30f, swY + 29f, 18f, new Color4(1f, 1f, 1f, 0.98f));

        // Search input typography + blinking precision caret
        bool hasQuery = !string.IsNullOrEmpty(query);
        bool caretVis = ((int)(animTime * 2.2f) % 2) == 0;
        string displayQuery = hasQuery ? query + (caretVis ? "|" : "") : "Search applications, calculate =math, or run commands...";
        Color4 qCol = hasQuery ? pal.PrimaryText : pal.SubtleText;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, displayQuery, swX + 60f, swY, swW - 246f, swH, 16.5f, qCol, hasQuery ? FontWeight.SemiBold : FontWeight.Normal);

        // Right-side command badges inside Search Well
        float rxSearch = swX + swW - 14f;
        if (hasQuery && onClearQuery != null)
        {
            float cw = 52f;
            var clrR = new RectangleF(rxSearch - cw, swY + 17f, cw, 24f);
            CrystalUIHelpers.DrawRoundedRect(d2d, clrR.X, clrR.Y, clrR.Width, clrR.Height, 8f, pal.ControlHover, pal.HairlineStroke, 0.8f);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Clear", clrR.X, clrR.Y, clrR.Width, clrR.Height, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center, ParagraphAlignment.Center, false);
            actionRects.Add((clrR, onClearQuery));
            rxSearch -= cw + 8f;
        }
        else
        {
            float keyW = "Alt+Space".Length * 7.6f + 16f;
            CrystalUIHelpers.DrawLuxuryKeycap(d2d, dw, "Alt+Space", rxSearch - keyW, swY + 18f, 22f, pal, true);
            rxSearch -= keyW + 10f;
        }
        CrystalUIHelpers.DrawRoundedRect(d2d, rxSearch - 80f, swY + 17f, 76f, 24f, 8f, pal.AccentSoft);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"{allApps.Count} Indexed", rxSearch - 80f, swY + 17f, 76f, 24f, 10.5f, pal.Accent, FontWeight.SemiBold, TextAlignment.Center, ParagraphAlignment.Center, false);

        float bodyTopY = 84f;

        // 2. Editorial Inline Math / Command Hero Showcase Banner (if triggered)
        if (!string.IsNullOrEmpty(calcResult))
        {
            float heroH = 44f;
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, 18f, bodyTopY, width - 36f, heroH, 13f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.26f),
                new Color4(pal.Accent.R * 0.4f, pal.Accent.G * 0.4f, pal.Accent.B * 0.7f, 0.12f), vertical: false);
            CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, 18f, bodyTopY, width - 36f, heroH, 13f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.65f), new Color4(1f, 1f, 1f, 0.14f), 1.0f);
            CrystalUIHelpers.DrawCrystalEmblem(d2d, 40f, bodyTopY + heroH * 0.5f, 18f, pal.Accent);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, calcResult, 62f, bodyTopY + 3f, width - 220f, 24f, 16f, pal.PrimaryText, FontWeight.Bold);
            string sub = string.IsNullOrEmpty(copiedToast) ? "Instant Evaluation  •  Press Enter to execute or copy" : copiedToast;
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, sub, 62f, bodyTopY + 23f, width - 220f, 16f, 10f, pal.SecondaryText);
            CrystalUIHelpers.DrawLuxuryKeycap(d2d, dw, "Enter  Run", width - 120f, bodyTopY + 11f, 22f, pal, true);
            bodyTopY += heroH + 10f;
        }

        // 3. Category Filter Ribbon with LiquidGlassDesign frosted white active pill
        float catY = bodyTopY;
        float cx = 18f;
        if (catPillInit && catPillW > 8f)
        {
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, catPillX, catY, catPillW, 27f, 13.5f,
                isDark ? new Color4(0.95f, 0.97f, 1f, 0.96f) : new Color4(0.12f, 0.14f, 0.20f, 0.94f),
                isDark ? new Color4(0.82f, 0.88f, 0.96f, 0.92f) : new Color4(0.06f, 0.08f, 0.12f, 0.94f));
            CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, catPillX, catY, catPillW, 27f, 13.5f,
                new Color4(1f, 1f, 1f, 0.95f), new Color4(1f, 1f, 1f, 0.25f), 0.9f);
        }

        foreach (string cat in Categories)
        {
            float cw = cat.Length * 7.2f + 22f;
            var r = new RectangleF(cx, catY, cw, 27f);
            bool active = cat == selectedCategory;
            if (!active || !catPillInit)
            {
                CrystalUIHelpers.DrawRoundedRect(d2d, r.X, r.Y, r.Width, r.Height, 13.5f,
                    active ? (isDark ? new Color4(0.95f, 0.97f, 1f, 0.96f) : pal.Accent) : pal.ControlSurface,
                    active ? null : pal.HairlineStroke, 0.75f);
            }
            Color4 txtC = active
                ? (isDark ? new Color4(0.07f, 0.09f, 0.14f, 0.98f) : new Color4(1f, 1f, 1f, 1f))
                : pal.SecondaryText;
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, cat, r.X, r.Y, r.Width, r.Height, 11f, txtC,
                active ? FontWeight.Bold : FontWeight.SemiBold, TextAlignment.Center, ParagraphAlignment.Center, false);
            catRects.Add((cat, r));
            cx += cw + 6f;
        }

        // 4. Dual-Zone Split Stage: Left Bento Grid + Right Spotlight Inspector Drawer
        float stageY = catY + 38f;
        float footerH = 42f;
        float stageH = height - stageY - footerH - 8f;
        float gridW = width - 36f - 250f - 14f;
        float inspX = 18f + gridW + 14f;
        float inspW = 250f;

        float cardW = (gridW - 20f) / GridCols;
        int maxRows = Math.Max(2, (int)((stageH + 8f) / 90f));
        float cardH = (stageH - (maxRows - 1) * 10f) / maxRows;
        int maxShow = maxRows * GridCols;

        for (int i = 0; i < Math.Min(filtered.Count, maxShow); i++)
        {
            var app = filtered[i];
            int col = i % GridCols;
            int row = i / GridCols;
            var cardR = new RectangleF(18f + col * (cardW + 10f), stageY + row * (cardH + 10f), cardW, cardH);
            bool selected = i == selectedIdx;
            bool isRunning = FindRunningWindowStatic(app, openWindows) != null;

            CrystalUIHelpers.DrawHairlineCard(d2d, cardR.X, cardR.Y, cardR.Width, cardR.Height, 14f, pal, selected);
            if (selected)
            {
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, cardR.X + cardR.Width * 0.5f, cardR.Y + 30f, 42f, 28f,
                    new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.24f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
            }

            if (isRunning)
            {
                using var runDot = d2d.CreateSolidColorBrush(pal.Success);
                d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(cardR.X + 11f, cardR.Y + 11f), 3.2f, 3.2f), runDot);
            }

            var iconObj = onIconLoaded != null
                ? icons.GetIconNonBlocking(d2d, app.IconKey, onIconLoaded)
                : icons.GetIcon(d2d, app.IconKey);
            float iconSz = Math.Min(38f, cardH - 36f);
            float ix = cardR.X + (cardR.Width - iconSz) * 0.5f;
            float iy = cardR.Y + 10f;
            if (iconObj != null)
            {
                CrystalUIHelpers.DrawBitmapRect(d2d, iconObj.Bitmap, ix, iy, iconSz, iconSz, 1f, iconObj.HasOpaqueCorners);
            }
            else
            {
                CrystalUIHelpers.DrawLuxurySquirclePlate(d2d, ix, iy, iconSz,
                    new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.92f),
                    new Color4(pal.Accent.R * 0.5f, pal.Accent.G * 0.5f, pal.Accent.B * 0.8f, 0.96f),
                    new Color4(0.08f, 0.12f, 0.24f, 0.98f));
                CrystalUIHelpers.DrawTextCrisp(d2d, dw, app.Name[..1].ToUpperInvariant(), ix, iy, iconSz, iconSz, 16f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center);
            }

            CrystalUIHelpers.DrawTextCrisp(d2d, dw, app.Name, cardR.X + 8f, cardR.Bottom - 26f, cardR.Width - 16f, 17f, 11.2f, pal.PrimaryText, selected ? FontWeight.SemiBold : FontWeight.Medium, TextAlignment.Center);

            string normApp = WindowsTaskbarPinsService.NormalizeKey(app.LaunchTarget);
            bool isPinned = cfg.Dock.PinnedApps.Any(p => string.Equals(WindowsTaskbarPinsService.NormalizeKey(p.Target), normApp, StringComparison.OrdinalIgnoreCase));
            var pinR = new RectangleF(cardR.Right - 22f, cardR.Y + 6f, 16f, 16f);
            if (selected || isPinned)
                CrystalUIHelpers.DrawStarIcon(d2d, pinR.X + 8f, pinR.Y + 8f, 12f, isPinned ? pal.Warning : pal.SubtleText, isPinned);
            itemRects.Add((app, cardR, pinR));
        }

        if (selBoxAlpha > 0.03f && itemRects.Count > 0 && selectedIdx < itemRects.Count)
        {
            float a = selBoxAlpha;
            CrystalUIHelpers.DrawRoundedRectBevelBorder(d2d, selBoxX, selBoxY, cardW, cardH, 14f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.95f * a),
                new Color4(1f, 1f, 1f, 0.42f * a), 1.65f);
        }

        // Right Spotlight Inspector Panel
        CrystalUIHelpers.DrawHairlineCard(d2d, inspX, stageY, inspW, stageH, 16f, pal);
        AppEntry? selApp = (selectedIdx >= 0 && selectedIdx < filtered.Count) ? filtered[selectedIdx] : filtered.FirstOrDefault();
        if (selApp != null)
        {
            float padX = inspX + 14f;
            float innerW = inspW - 28f;
            float pedestalX = inspX + (inspW - 68f) * 0.5f;
            float pedestalY = stageY + 14f;
            CrystalUIHelpers.FillEllipseRadialGradient(d2d, inspX + inspW * 0.5f, pedestalY + 34f, 54f, 48f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.28f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
            CrystalUIHelpers.DrawHairlineCard(d2d, pedestalX, pedestalY, 68f, 68f, 18f, pal, true);

            var selIcon = onIconLoaded != null
                ? icons.GetIconNonBlocking(d2d, selApp.IconKey, onIconLoaded)
                : icons.GetIcon(d2d, selApp.IconKey);
            if (selIcon != null)
                CrystalUIHelpers.DrawBitmapRect(d2d, selIcon.Bitmap, pedestalX + 9f, pedestalY + 9f, 50f, 50f, 1f, selIcon.HasOpaqueCorners);
            else
                CrystalUIHelpers.DrawCrystalEmblem(d2d, inspX + inspW * 0.5f, pedestalY + 34f, 32f, pal.Accent);

            CrystalUIHelpers.DrawTextCrisp(d2d, dw, selApp.Name, padX, stageY + 90f, innerW, 24f, 14f, pal.PrimaryText, FontWeight.Bold, TextAlignment.Center);

            var runWin = FindRunningWindowStatic(selApp, openWindows);
            string badgeTxt = runWin != null
                ? (runWin.IsMinimized ? "Minimized • Enter to Restore" : "Running • Active Instance")
                : $"{selApp.Category} App";
            Color4 badgeCol = runWin != null ? pal.Success : pal.Accent;
            CrystalUIHelpers.DrawRoundedRect(d2d, inspX + 20f, stageY + 116f, inspW - 40f, 20f, 10f,
                new Color4(badgeCol.R, badgeCol.G, badgeCol.B, 0.16f), new Color4(badgeCol.R, badgeCol.G, badgeCol.B, 0.35f), 0.7f);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, badgeTxt, inspX + 22f, stageY + 116f, inspW - 44f, 20f, 9.5f, badgeCol, FontWeight.SemiBold, TextAlignment.Center, ParagraphAlignment.Center, false);

            string targetFile = Path.GetFileName(selApp.LaunchTarget);
            if (string.IsNullOrEmpty(targetFile)) targetFile = selApp.Id;
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"Target: {targetFile}", padX, stageY + 142f, innerW, 18f, 10.2f, pal.SubtleText, FontWeight.Regular, TextAlignment.Center);

            bool selPinned = cfg.Dock.PinnedApps.Any(p => string.Equals(WindowsTaskbarPinsService.NormalizeKey(p.Target), WindowsTaskbarPinsService.NormalizeKey(selApp.LaunchTarget), StringComparison.OrdinalIgnoreCase));

            // Inspector Primary Action Buttons
            float btnY = stageY + 168f;
            var launchBtn = new RectangleF(padX, btnY, innerW, 30f);
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, launchBtn.X, launchBtn.Y, launchBtn.Width, launchBtn.Height, 10f,
                pal.Accent, new Color4(pal.Accent.R * 0.7f, pal.Accent.G * 0.7f, pal.Accent.B * 0.9f, 0.96f));
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, runWin != null ? "Switch to Window" : "Launch Application",
                launchBtn.X, launchBtn.Y, launchBtn.Width, launchBtn.Height, 11.5f, new Color4(1f, 1f, 1f, 1f), FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
            if (onLaunchApp != null) actionRects.Add((launchBtn, () => onLaunchApp(selApp)));

            var pinBtn = new RectangleF(padX, btnY + 36f, (innerW - 6f) * 0.5f, 28f);
            CrystalUIHelpers.DrawRoundedRect(d2d, pinBtn.X, pinBtn.Y, pinBtn.Width, pinBtn.Height, 9f, selPinned ? pal.AccentSoft : pal.ControlSurface, pal.HairlineStroke, 0.8f);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, selPinned ? "Pinned ✓" : "Pin Dock", pinBtn.X, pinBtn.Y, pinBtn.Width, pinBtn.Height, 10.5f, selPinned ? pal.Accent : pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
            if (onTogglePin != null) actionRects.Add((pinBtn, () => onTogglePin(selApp)));

            var locBtn = new RectangleF(pinBtn.Right + 6f, btnY + 36f, (innerW - 6f) * 0.5f, 28f);
            CrystalUIHelpers.DrawRoundedRect(d2d, locBtn.X, locBtn.Y, locBtn.Width, locBtn.Height, 9f, pal.ControlSurface, pal.HairlineStroke, 0.8f);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Location", locBtn.X, locBtn.Y, locBtn.Width, locBtn.Height, 10.5f, pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Center);
            if (onOpenLocation != null) actionRects.Add((locBtn, () => onOpenLocation(selApp)));

            // Quick Actions Divider + Studio Shortcuts inside Inspector
            float qy = stageY + stageH - 72f;
            using (var div = d2d.CreateSolidColorBrush(pal.Divider))
                d2d.DrawLine(new System.Numerics.Vector2(padX, qy - 10f), new System.Numerics.Vector2(padX + innerW, qy - 10f), div, 1f);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, "QUICK WORKFLOWS", padX, qy - 3f, innerW, 14f, 9f, pal.SubtleText, FontWeight.Bold);

            float qw = (innerW - 10f) / 3f;
            var qaItems = new (string Label, Action? Act)[]
            {
                ("Exposé", onOpenExpose),
                ("Glass Lab", onOpenSettings),
                (isDark ? "Light" : "Dark", onToggleTheme)
            };
            for (int qi = 0; qi < qaItems.Length; qi++)
            {
                var qr = new RectangleF(padX + qi * (qw + 5f), qy + 15f, qw, 42f);
                CrystalUIHelpers.DrawRoundedRect(d2d, qr.X, qr.Y, qr.Width, qr.Height, 9f, pal.ControlSurface, pal.HairlineStroke, 0.7f);
                if (qi == 0) CrystalUIHelpers.DrawExposeGridIcon(d2d, qr.X + qr.Width * 0.5f, qr.Y + 14f, 13f, pal.PrimaryText);
                else if (qi == 1) CrystalUIHelpers.DrawCrystalEmblem(d2d, qr.X + qr.Width * 0.5f, qr.Y + 14f, 13f, pal.Accent);
                else if (isDark) CrystalUIHelpers.DrawSunIcon(d2d, qr.X + qr.Width * 0.5f, qr.Y + 14f, 13f, pal.Warning);
                else CrystalUIHelpers.DrawMoonIcon(d2d, qr.X + qr.Width * 0.5f, qr.Y + 14f, 13f, pal.Accent);
                CrystalUIHelpers.DrawTextCrisp(d2d, dw, qaItems[qi].Label, qr.X, qr.Y + 24f, qr.Width, 16f, 9.5f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Center);
                if (qaItems[qi].Act != null) actionRects.Add((qr, qaItems[qi].Act!));
            }
        }

        // 5. Recessed Action Footer Bar with machined keycaps
        float fy = height - 38f;
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, 14f, fy, width - 28f, 28f, 12f,
            new Color4(0f, 0f, 0f, isDark ? 0.24f : 0.05f),
            new Color4(0f, 0f, 0f, isDark ? 0.34f : 0.08f));
        float kx = 26f;
        kx += CrystalUIHelpers.DrawLuxuryKeycap(d2d, dw, "Arrows", kx, fy + 4.5f, 19f, pal) + 6f;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Navigate", kx, fy, 56f, 28f, 10f, pal.SecondaryText);
        kx += 58f;
        kx += CrystalUIHelpers.DrawLuxuryKeycap(d2d, dw, "Enter", kx, fy + 4.5f, 19f, pal, true) + 6f;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Launch", kx, fy, 46f, 28f, 10f, pal.SecondaryText);
        kx += 50f;
        kx += CrystalUIHelpers.DrawLuxuryKeycap(d2d, dw, "Tab", kx, fy + 4.5f, 19f, pal) + 6f;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Category", kx, fy, 54f, 28f, 10f, pal.SecondaryText);
        kx += 58f;
        kx += CrystalUIHelpers.DrawLuxuryKeycap(d2d, dw, "=", kx, fy + 4.5f, 19f, pal) + 6f;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Calculate", kx, fy, 58f, 28f, 10f, pal.SecondaryText);
        kx += 62f;
        kx += CrystalUIHelpers.DrawLuxuryKeycap(d2d, dw, "Esc", kx, fy + 4.5f, 19f, pal) + 6f;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "Close", kx, fy, 42f, 28f, 10f, pal.SecondaryText);

        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"Showing {filtered.Count} • Crystal Spotlight", width - 210f, fy, 186f, 28f, 10f, pal.SubtleText, FontWeight.Medium, TextAlignment.Trailing);
    }

    private static WindowInfo? FindRunningWindowStatic(AppEntry app, IReadOnlyList<WindowInfo> openWindows)
    {
        string exeName = Path.GetFileNameWithoutExtension(app.LaunchTarget);
        return openWindows.FirstOrDefault(w =>
            (!string.IsNullOrEmpty(exeName) && Path.GetFileNameWithoutExtension(w.ExecutablePath).Equals(exeName, StringComparison.OrdinalIgnoreCase))
            || string.Equals(w.ProcessName, app.Name, StringComparison.OrdinalIgnoreCase));
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        bool instant = ShouldReduceMotion();
        bool moving = false;

        if (Visible && !instant && !string.IsNullOrEmpty(_query))
        {
            _animTime += dtSeconds;
            if ((int)(_animTime * 2.2f) != (int)((_animTime - dtSeconds) * 2.2f))
                moving = true;
        }

        if (_selectedIdx >= 0 && _selectedIdx < _itemRects.Count)
        {
            var r = _itemRects[_selectedIdx].CardRect;
            if (_selBoxAlpha.Value < 0.05f)
            {
                _selBoxX.Value = r.X;
                _selBoxY.Value = r.Y;
            }
            _selBoxX.Target = r.X;
            _selBoxY.Target = r.Y;
            _selBoxAlpha.Target = 1f;
        }
        else
        {
            _selBoxAlpha.Target = 0f;
        }

        var activeCat = _catRects.FirstOrDefault(c => c.Category == _selectedCategory);
        if (!string.IsNullOrEmpty(activeCat.Category))
        {
            if (!_catPillInitialized)
            {
                _catPillX.Value = activeCat.Rect.X;
                _catPillW.Value = activeCat.Rect.Width;
                _catPillInitialized = true;
            }
            _catPillX.Target = activeCat.Rect.X;
            _catPillW.Target = activeCat.Rect.Width;
        }

        moving |= _selBoxX.Step(dtSeconds, 540f, 34f, instant);
        moving |= _selBoxY.Step(dtSeconds, 540f, 34f, instant);
        moving |= _selBoxAlpha.Step(dtSeconds, 440f, 30f, instant);
        moving |= _catPillX.Step(dtSeconds, 560f, 34f, instant);
        moving |= _catPillW.Step(dtSeconds, 560f, 34f, instant);
        return moving;
    }
}
