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

public sealed class DesktopWidgetWindow : GlassWindowBase
{
    private readonly WidgetPlacement _placement;
    private readonly DesktopWidgetsHost _host;
    private bool _dragging;
    private Point _dragStartMouseScreen;
    private Point _dragStartWinPos;
    private float _mediaPulseAccum;
    private readonly List<(RectangleF Rect, Action OnClick)> _buttons = new();

    public string WidgetId => _placement.Id;

    public DesktopWidgetWindow(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        WidgetPlacement placement,
        DesktopWidgetsHost host)
        : base(gpu, configMgr, backdrop, noActivate: true, topMost: false)
    {
        _placement = placement;
        _host = host;

        SetBounds(placement.X, placement.Y, placement.Width, placement.Height);

        MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _host.ShowWidgetMenu(PointToScreen(e.Location), _placement);
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                foreach (var b in _buttons)
                {
                    if (b.Rect.Contains(e.X, e.Y))
                    {
                        b.OnClick();
                        return;
                    }
                }
                _dragging = true;
                _dragStartMouseScreen = PointToScreen(e.Location);
                _dragStartWinPos = Location;
            }
        };

        MouseMove += (_, e) =>
        {
            if (_dragging)
            {
                var cur = PointToScreen(e.Location);
                Location = new Point(_dragStartWinPos.X + (cur.X - _dragStartMouseScreen.X), _dragStartWinPos.Y + (cur.Y - _dragStartMouseScreen.Y));
                MarkDirty();
            }
        };

        MouseUp += (_, __) =>
        {
            if (_dragging)
            {
                _dragging = false;
                ConfigMgr.Update(cfg =>
                {
                    var item = cfg.Widgets.Items.FirstOrDefault(w => w.Id == _placement.Id);
                    if (item != null) { item.X = Left; item.Y = Top; }
                });
            }
        };
    }

    protected override bool StepAnimations(float dtSeconds)
    {
        if (_placement.Kind == WidgetKind.Media && Visible && _host.Media.HasSession && _host.Media.IsPlaying && !ShouldReduceMotion())
        {
            _mediaPulseAccum += dtSeconds;
            if (_mediaPulseAccum >= 0.065f)
            {
                _mediaPulseAccum = 0f;
                return true;
            }
        }
        return false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32Interop.SetWindowPos(Handle, Win32Interop.HWND_BOTTOM, 0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
    }

    protected override IReadOnlyList<GlassShapeItem> BuildGlassShapes()
    {
        var list = new List<GlassShapeItem>
        {
            new GlassShapeItem
            {
                X = 4,
                Y = 4,
                Width = Width - 8,
                Height = Height - 8,
                CornerRadius = 20,
                SquirclePower = 3.2f,
                HoverGlow = IsMouseInside ? 0.16f : 0.04f,
                BevelScale = 0.92f
            }
        };

        if (_placement.Kind == WidgetKind.Media)
        {
            // Circular Liquid-Glass Play/Pause lens orb (liquid-glass-music-player layered optics)
            list.Add(new GlassShapeItem
            {
                X = 98f + 36f,
                Y = Height - 43f,
                Width = 34f,
                Height = 34f,
                CornerRadius = 17f,
                SquirclePower = 2.0f,
                HoverGlow = IsMouseInside ? 0.36f : 0.18f,
                BevelScale = 1.15f
            });
        }
        return list;
    }

    protected override void DrawForeground2D(ID2D1DeviceContext d2d)
    {
        _buttons.Clear();
        var pal = CrystalUIHelpers.GetPalette(IsDarkModeActive(), Config.Appearance.AccentColorHex);

        switch (_placement.Kind)
        {
            case WidgetKind.Clock: DrawClockWidget(d2d, Gpu.DWriteFactory, pal, Config, Width, Height); break;
            case WidgetKind.Weather: DrawWeatherWidget(d2d, Gpu.DWriteFactory, pal, Config, _host.Weather, Width, Height, () => _ = _host.RefreshWeatherAsync(true), _buttons); break;
            case WidgetKind.Telemetry: DrawTelemetryWidget(d2d, Gpu.DWriteFactory, pal, _host.Metrics, _host.CpuHistory, Width, Height); break;
            case WidgetKind.Media: DrawMediaWidget(d2d, Gpu.DWriteFactory, pal, _host.Media, Width, Height, (float)(DateTime.UtcNow - DateTime.Today).TotalSeconds, () => _ = _host.MediaPreviousAsync(), () => _ = _host.MediaPlayPauseAsync(), () => _ = _host.MediaNextAsync(), _buttons); break;
            case WidgetKind.Calendar: DrawCalendarWidget(d2d, Gpu.DWriteFactory, pal, Width, Height); break;
        }
    }

    public static void DrawClockWidget(ID2D1DeviceContext d2d, IDWriteFactory dw, UiPalette pal, CrystalConfig cfg, int width, int height)
    {
        var now = DateTime.Now;

        // Left Precision Chronometer Analog Dial
        float dialCx = 62f, dialCy = height * 0.5f, dialR = 44f;
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, dialCx, dialCy, dialR, dialR,
            new Color4(0.06f, 0.08f, 0.14f, 0.52f), new Color4(0.02f, 0.04f, 0.08f, 0.72f), -10f, -12f);
        using (var bezel = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.24f)))
            d2d.DrawEllipse(new Ellipse(new System.Numerics.Vector2(dialCx, dialCy), dialR, dialR), bezel, 1.0f);

        // 12 Hour Index Ticks
        for (int i = 0; i < 12; i++)
        {
            float a = (i * 30f - 90f) * MathF.PI / 180f;
            float rIn = (i % 3 == 0) ? dialR - 8f : dialR - 5f;
            using var tickB = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, i % 3 == 0 ? 0.85f : 0.34f));
            d2d.DrawLine(new System.Numerics.Vector2(dialCx + MathF.Cos(a) * rIn, dialCy + MathF.Sin(a) * rIn),
                         new System.Numerics.Vector2(dialCx + MathF.Cos(a) * (dialR - 2.2f), dialCy + MathF.Sin(a) * (dialR - 2.2f)),
                         tickB, i % 3 == 0 ? 1.6f : 1.0f);
        }

        // Hour, Minute, and Second hands
        float sec = now.Second + now.Millisecond / 1000f;
        float minAng = ((now.Minute + sec / 60f) * 6f - 90f) * MathF.PI / 180f;
        float hrAng = (((now.Hour % 12) + now.Minute / 60f) * 30f - 90f) * MathF.PI / 180f;
        float secAng = (sec * 6f - 90f) * MathF.PI / 180f;

        using var capStyle = d2d.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        using (var hrB = d2d.CreateSolidColorBrush(pal.PrimaryText))
            d2d.DrawLine(new System.Numerics.Vector2(dialCx, dialCy), new System.Numerics.Vector2(dialCx + MathF.Cos(hrAng) * (dialR * 0.52f), dialCy + MathF.Sin(hrAng) * (dialR * 0.52f)), hrB, 2.8f, capStyle);
        using (var minB = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.92f)))
            d2d.DrawLine(new System.Numerics.Vector2(dialCx, dialCy), new System.Numerics.Vector2(dialCx + MathF.Cos(minAng) * (dialR * 0.74f), dialCy + MathF.Sin(minAng) * (dialR * 0.74f)), minB, 2.0f, capStyle);
        using (var secB = d2d.CreateSolidColorBrush(pal.Accent))
            d2d.DrawLine(new System.Numerics.Vector2(dialCx - MathF.Cos(secAng) * 7f, dialCy - MathF.Sin(secAng) * 7f), new System.Numerics.Vector2(dialCx + MathF.Cos(secAng) * (dialR * 0.84f), dialCy + MathF.Sin(secAng) * (dialR * 0.84f)), secB, 1.3f, capStyle);
        using (var pinB = d2d.CreateSolidColorBrush(pal.Accent))
            d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(dialCx, dialCy), 2.8f, 2.8f), pinB);

        // Right Digital Horology Stack
        float rx = 120f, rw = width - 134f;
        string fmt = cfg.MenuBar.Use24HourClock ? "HH:mm" : "h:mm";
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString(fmt), rx, 18f, rw, 44f, 32f, pal.PrimaryText, FontWeight.Light);
        if (!cfg.MenuBar.Use24HourClock)
        {
            CrystalUIHelpers.DrawRoundedRect(d2d, width - 46f, 24f, 30f, 16f, 6f, pal.AccentSoft);
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("tt"), width - 46f, 24f, 30f, 16f, 9f, pal.Accent, FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
        }
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("ddd, MMM d"), rx, 66f, rw, 20f, 12.5f, pal.SecondaryText, FontWeight.SemiBold);

        float sec01 = sec / 60f;
        CrystalUIHelpers.DrawRoundedRect(d2d, rx, height - 26f, rw, 4f, 2f, pal.ControlTrack);
        CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, rx, height - 26f, Math.Max(6f, rw * sec01), 4f, 2f,
            new Color4(Math.Min(1f, pal.Accent.R + 0.2f), Math.Min(1f, pal.Accent.G + 0.2f), Math.Min(1f, pal.Accent.B + 0.2f), 1f), pal.Accent, vertical: false);
    }

    public static void DrawWeatherWidget(
        ID2D1DeviceContext d2d, IDWriteFactory dw, UiPalette pal, CrystalConfig cfg, WeatherInfo w,
        int width, int height, Action? onRefresh, List<(RectangleF Rect, Action OnClick)>? buttons = null)
    {
        // Ambient sky aura orb
        Color4 skyCol = w.WeatherCode <= 1 ? new Color4(1.0f, 0.72f, 0.24f, 0.26f) : new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.24f);
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, width - 54f, 52f, 64f, 54f, skyCol, new Color4(skyCol.R, skyCol.G, skyCol.B, 0f));

        CrystalUIHelpers.DrawRoundedRect(d2d, 18f, 14f, Math.Clamp(w.LocationName.Length * 7.8f + 22f, 70f, width - 72f), 19f, 9.5f, pal.AccentSoft);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, w.LocationName.ToUpperInvariant(), 24f, 14f, width - 84f, 19f, 9.2f, pal.Accent, FontWeight.Bold);

        if (buttons != null && onRefresh != null)
        {
            var refBtn = new RectangleF(width - 38f, 12f, 22f, 22f);
            CrystalUIHelpers.DrawRefreshIcon(d2d, refBtn.X + 11f, refBtn.Y + 11f, 12.5f, pal.SecondaryText);
            buttons.Add((refBtn, onRefresh));
        }

        if (!w.IsAvailable)
        {
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, w.ConditionText, 18f, 54f, width - 36f, 40f, 13f, pal.SecondaryText);
            return;
        }

        double temp = cfg.Widgets.UseFahrenheit ? w.TemperatureC * 9 / 5 + 32 : w.TemperatureC;
        string unit = cfg.Widgets.UseFahrenheit ? "°F" : "°C";
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"{Math.Round(temp)}{unit}", 18f, 34f, 134f, 48f, 36f, pal.PrimaryText, FontWeight.Light);
        CrystalUIHelpers.DrawWeatherGlyph(d2d, width - 52f, 56f, 44f, w.WeatherCode, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, w.ConditionText, 18f, 82f, width - 36f, 20f, 12.5f, pal.PrimaryText, FontWeight.SemiBold);

        // Two precision micro-telemetry pills at bottom
        float pillW = (width - 44f) * 0.5f;
        CrystalUIHelpers.DrawHairlineCard(d2d, 18f, height - 34f, pillW, 22f, 7f, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"Humidity {w.HumidityPercent}%", 20f, height - 34f, pillW - 4f, 22f, 9.8f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Center);
        CrystalUIHelpers.DrawHairlineCard(d2d, 26f + pillW, height - 34f, pillW, 22f, 7f, pal);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"Wind {Math.Round(w.WindKmh)} km/h", 28f + pillW, height - 34f, pillW - 4f, 22f, 9.8f, pal.SecondaryText, FontWeight.Medium, TextAlignment.Center);
    }

    public static void DrawTelemetryWidget(
        ID2D1DeviceContext d2d, IDWriteFactory dw, UiPalette pal, SystemMetricsSnapshot m, IReadOnlyList<float> cpuHistory, int width, int height)
    {
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "HARDWARE TELEMETRY", 18f, 13f, 150f, 16f, 9.5f, pal.Accent, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"{m.ProcessCount} PROCS", width - 92f, 13f, 74f, 16f, 9.2f, pal.SubtleText, FontWeight.Bold, TextAlignment.Trailing);

        // Left Dual Arc Gauges (CPU + RAM) & Right CPU Sparkline Trough
        CrystalUIHelpers.DrawAnalogGaugeArc(d2d, dw, 50f, 68f, 27f, (float)(m.CpuPercent / 100.0),
            pal.Accent, new Color4(0.32f, 0.96f, 0.76f, 1f), $"{m.CpuPercent:0}%", "CPU", pal);
        CrystalUIHelpers.DrawAnalogGaugeArc(d2d, dw, 114f, 68f, 27f, (float)(m.RamPercent / 100.0),
            new Color4(0.56f, 0.48f, 1f, 1f), pal.Accent, $"{m.RamPercent:0}%", "RAM", pal);

        CrystalUIHelpers.DrawHairlineCard(d2d, 150f, 36f, width - 166f, 62f, 10f, pal);
        CrystalUIHelpers.DrawSparklineWithArea(d2d, 156f, 44f, width - 178f, 46f, cpuHistory, pal.Accent);

        // Bottom Telemetry Strips
        string downTxt = m.NetworkDownKbps > 1000 ? $"{m.NetworkDownKbps / 1000:0.0} Mbps" : $"{m.NetworkDownKbps:0} kbps";
        string upTxt = m.NetworkUpKbps > 1000 ? $"{m.NetworkUpKbps / 1000:0.0} Mbps" : $"{m.NetworkUpKbps:0} kbps";
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"NET   ↓ {downTxt}   •   ↑ {upTxt}", 18f, 110f, width - 36f, 16f, 10.5f, pal.SecondaryText, FontWeight.SemiBold);

        var primaryDrive = m.Drives.FirstOrDefault();
        if (primaryDrive != null)
        {
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, $"DISK {primaryDrive.Name}   {primaryDrive.UsedGb:0}/{primaryDrive.TotalGb:0} GB", 18f, 134f, width - 36f, 16f, 10.5f, pal.PrimaryText, FontWeight.Medium);
            CrystalUIHelpers.DrawRoundedRect(d2d, 18f, 154f, width - 36f, 6f, 3f, pal.ControlTrack);
            CrystalUIHelpers.FillRoundedRectLinearGradient(d2d, 18f, 154f, Math.Max(8f, (width - 36f) * (float)(primaryDrive.UsedPercent / 100.0)), 6f, 3f,
                pal.Warning, new Color4(1f, 0.52f, 0.24f, 1f), vertical: false);
        }
    }

    public static void DrawMediaWidget(
        ID2D1DeviceContext d2d, IDWriteFactory dw, UiPalette pal, MediaSessionInfo m, int width, int height,
        float pulseSec, Action? onPrev, Action? onPlayPause, Action? onNext, List<(RectangleF Rect, Action OnClick)>? buttons = null)
    {
        // Left Rotating Studio Crystal-Vinyl Turntable
        float discCx = 50f, discCy = height * 0.5f, discR = 35f;
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, discCx, discCy, discR + 10f, discR + 10f,
            new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, m.IsPlaying ? 0.30f : 0.12f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, discCx, discCy, discR, discR,
            new Color4(0.18f, 0.22f, 0.32f, 0.96f), new Color4(0.05f, 0.06f, 0.10f, 0.98f), -6f, -8f);
        using (var groove = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.14f)))
        {
            d2d.DrawEllipse(new Ellipse(new System.Numerics.Vector2(discCx, discCy), discR, discR), groove, 1.0f);
            d2d.DrawEllipse(new Ellipse(new System.Numerics.Vector2(discCx, discCy), discR * 0.72f, discR * 0.72f), groove, 0.8f);
            d2d.DrawEllipse(new Ellipse(new System.Numerics.Vector2(discCx, discCy), discR * 0.48f, discR * 0.48f), groove, 0.8f);
        }
        // Rotating tone highlight dot
        float spinAng = m.IsPlaying ? pulseSec * 1.8f : -0.6f;
        using (var glare = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.42f)))
            d2d.DrawLine(
                new System.Numerics.Vector2(discCx + MathF.Cos(spinAng) * (discR * 0.34f), discCy + MathF.Sin(spinAng) * (discR * 0.34f)),
                new System.Numerics.Vector2(discCx + MathF.Cos(spinAng) * (discR * 0.88f), discCy + MathF.Sin(spinAng) * (discR * 0.88f)),
                glare, 1.8f);
        CrystalUIHelpers.FillEllipseRadialGradient(d2d, discCx, discCy, 11f, 11f, pal.Accent, new Color4(pal.Accent.R * 0.5f, pal.Accent.G * 0.5f, pal.Accent.B * 0.8f, 1f));
        using (var hub = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.96f)))
            d2d.FillEllipse(new Ellipse(new System.Numerics.Vector2(discCx, discCy), 3f, 3f), hub);

        float rx = 98f, rw = width - 114f;
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, "NOW PLAYING", rx, 14f, 84f, 14f, 9f, pal.Accent, FontWeight.Bold);
        CrystalUIHelpers.DrawAcousticWaveform(d2d, rx + 82f, 21f, pulseSec, pal.Accent, m.HasSession && m.IsPlaying, 7);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, m.HasSession ? m.Title : "Nothing Playing", rx, 30f, rw, 20f, 13f, pal.PrimaryText, FontWeight.SemiBold);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, m.HasSession ? m.Artist : "No active audio stream", rx, 50f, rw, 16f, 10.8f, pal.SecondaryText);

        float by = height - 42f;
        var prevR = new RectangleF(rx, by + 2f, 28f, 28f);
        var playR = new RectangleF(rx + 36f, by - 1f, 34f, 34f);
        var nextR = new RectangleF(rx + 78f, by + 2f, 28f, 28f);
        CrystalUIHelpers.DrawGlassOrbButton(d2d, prevR.X + 14f, prevR.Y + 14f, 14f, pal.Accent, false);
        CrystalUIHelpers.DrawGlassOrbButton(d2d, playR.X + 17f, playR.Y + 17f, 17f, pal.Accent, true);
        CrystalUIHelpers.DrawGlassOrbButton(d2d, nextR.X + 14f, nextR.Y + 14f, 14f, pal.Accent, false);

        CrystalUIHelpers.DrawSkipIcon(d2d, prevR.X + 14f, prevR.Y + 14f, 13f, pal.PrimaryText, false);
        if (m.IsPlaying) CrystalUIHelpers.DrawPauseIcon(d2d, playR.X + 17f, playR.Y + 17f, 14f, new Color4(1f, 1f, 1f, 1f));
        else CrystalUIHelpers.DrawPlayIcon(d2d, playR.X + 17f, playR.Y + 17f, 14f, new Color4(1f, 1f, 1f, 1f));
        CrystalUIHelpers.DrawSkipIcon(d2d, nextR.X + 14f, nextR.Y + 14f, 13f, pal.PrimaryText, true);

        if (buttons != null)
        {
            if (onPrev != null) buttons.Add((prevR, onPrev));
            if (onPlayPause != null) buttons.Add((playR, onPlayPause));
            if (onNext != null) buttons.Add((nextR, onNext));
        }
    }

    public static void DrawCalendarWidget(ID2D1DeviceContext d2d, IDWriteFactory dw, UiPalette pal, int width, int height)
    {
        var now = DateTime.Now;
        CrystalUIHelpers.DrawHairlineCard(d2d, 16f, 14f, width - 32f, 42f, 12f, pal, true);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("dd"), 24f, 14f, 40f, 42f, 24f, pal.Accent, FontWeight.Bold, TextAlignment.Center);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("MMMM yyyy").ToUpperInvariant(), 68f, 18f, width - 92f, 16f, 11f, pal.PrimaryText, FontWeight.Bold);
        CrystalUIHelpers.DrawTextCrisp(d2d, dw, now.ToString("dddd"), 68f, 34f, width - 92f, 16f, 10.2f, pal.SecondaryText, FontWeight.Medium);

        string[] days = { "S", "M", "T", "W", "T", "F", "S" };
        float colW = (width - 36f) / 7f;
        for (int i = 0; i < 7; i++)
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, days[i], 18f + i * colW, 64f, colW, 16f, 9.5f, (i == 0 || i == 6) ? pal.Accent : pal.SubtleText, FontWeight.Bold, TextAlignment.Center);

        var first = new DateTime(now.Year, now.Month, 1);
        int startCol = (int)first.DayOfWeek;
        int dim = DateTime.DaysInMonth(now.Year, now.Month);
        for (int d = 1; d <= dim; d++)
        {
            int cell = startCol + d - 1;
            int col = cell % 7;
            int row = cell / 7;
            float cx = 18f + col * colW;
            float cy = 82f + row * 23.5f;
            if (cy + 20f > height - 8f) break;
            if (d == now.Day)
            {
                CrystalUIHelpers.FillEllipseRadialGradient(d2d, cx + colW * 0.5f, cy + 10.5f, 11f, 11f,
                    pal.Accent, new Color4(pal.Accent.R * 0.72f, pal.Accent.G * 0.72f, pal.Accent.B * 0.9f, 1f), -2f, -3f);
            }
            CrystalUIHelpers.DrawTextCrisp(d2d, dw, d.ToString(), cx, cy, colW, 21f, 10.8f,
                d == now.Day ? new Color4(1f, 1f, 1f, 1f) : pal.PrimaryText, d == now.Day ? FontWeight.Bold : FontWeight.Regular, TextAlignment.Center);
        }
    }
}

public sealed class DesktopWidgetsHost : IDisposable
{
    private readonly GpuDeviceContext _gpu;
    private readonly ConfigManager _configMgr;
    private readonly IDesktopBackdropSource? _backdrop;
    private readonly SystemMetricsService _metricsSvc;
    private readonly WeatherService _weatherSvc;
    private readonly MediaSessionService _mediaSvc;
    private readonly Action<Point, WidgetPlacement> _onContextMenu;

    private readonly List<DesktopWidgetWindow> _windows = new();
    private readonly List<float> _cpuHistory = new();

    public SystemMetricsSnapshot Metrics { get; private set; } = new(0, 0, 16, 0, 0, 0, Array.Empty<DriveUsageInfo>(), 0, TimeSpan.Zero);
    public WeatherInfo Weather { get; private set; } = new(false, "Local", 0, 0, 0, "Loading...", 0, 0, DateTimeOffset.Now, "Open-Meteo");
    public MediaSessionInfo Media { get; private set; } = new(false, "", "", "", "", false, false, false, false, null);
    public IReadOnlyList<float> CpuHistory => _cpuHistory;

    public DesktopWidgetsHost(
        GpuDeviceContext gpu,
        ConfigManager configMgr,
        IDesktopBackdropSource? backdrop,
        SystemMetricsService metricsSvc,
        WeatherService weatherSvc,
        MediaSessionService mediaSvc,
        Action<Point, WidgetPlacement> onContextMenu)
    {
        _gpu = gpu;
        _configMgr = configMgr;
        _backdrop = backdrop;
        _metricsSvc = metricsSvc;
        _weatherSvc = weatherSvc;
        _mediaSvc = mediaSvc;
        _onContextMenu = onContextMenu;
    }

    public void SyncWidgetsWithConfig()
    {
        foreach (var w in _windows) { w.Close(); w.Dispose(); }
        _windows.Clear();

        if (!Config.Widgets.Enabled) return;
        foreach (var item in Config.Widgets.Items.Where(i => i.Enabled))
        {
            var win = new DesktopWidgetWindow(_gpu, _configMgr, _backdrop, item, this);
            win.Show();
            _windows.Add(win);
        }
    }

    private CrystalConfig Config => _configMgr.Current;

    public bool TickAll(bool commitDComp = true)
    {
        bool any = false;
        foreach (var w in _windows)
            any |= w.TickAndRenderIfNeeded(commitDComp);
        return any;
    }

    public void ApplyCaptureExclusion()
    {
        foreach (var w in _windows)
            w.ApplyCaptureExclusionAffinity();
    }

    public void MarkAllDirty()
    {
        foreach (var w in _windows)
            w.MarkDirty();
    }

    public void UpdateMetrics()
    {
        Metrics = _metricsSvc.Sample();
        _cpuHistory.Add((float)Math.Clamp(Metrics.CpuPercent / 100.0, 0.0, 1.0));
        while (_cpuHistory.Count > 28) _cpuHistory.RemoveAt(0);
        foreach (var w in _windows)
        {
            if (w.WidgetId is "telemetry" or "clock")
                w.MarkDirty();
        }
    }

    public async Task RefreshWeatherAsync(bool force = false)
    {
        Weather = await _weatherSvc.GetWeatherAsync(Config.Widgets.WeatherLocation, force);
        foreach (var w in _windows)
        {
            if (w.WidgetId == "weather") w.MarkDirty();
        }
    }

    public async Task RefreshMediaAsync()
    {
        Media = await _mediaSvc.GetCurrentAsync();
        foreach (var w in _windows)
        {
            if (w.WidgetId == "media") w.MarkDirty();
        }
    }

    public async Task MediaPlayPauseAsync() { await _mediaSvc.TogglePlayPauseAsync(); await RefreshMediaAsync(); }
    public async Task MediaNextAsync() { await _mediaSvc.NextAsync(); await RefreshMediaAsync(); }
    public async Task MediaPreviousAsync() { await _mediaSvc.PreviousAsync(); await RefreshMediaAsync(); }

    internal void ShowWidgetMenu(Point screenPos, WidgetPlacement placement) => _onContextMenu(screenPos, placement);

    public void Dispose()
    {
        foreach (var w in _windows) { w.Close(); w.Dispose(); }
        _windows.Clear();
    }
}
