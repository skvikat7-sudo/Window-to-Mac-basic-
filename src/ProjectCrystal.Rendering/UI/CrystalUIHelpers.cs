using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProjectCrystal.Native.Apps;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ProjectCrystal.Rendering.UI;

public sealed record UiPalette(
    Color4 PrimaryText,
    Color4 SecondaryText,
    Color4 SubtleText,
    Color4 Accent,
    Color4 AccentSoft,
    Color4 Divider,
    Color4 ControlTrack,
    Color4 ControlSurface,
    Color4 ControlHover,
    Color4 HairlineStroke,
    Color4 Success,
    Color4 Warning,
    Color4 Danger
);

public static class CrystalUIHelpers
{
    private sealed class D2DContextCache : IDisposable
    {
        public ID2D1SolidColorBrush? SolidBrush;
        public ID2D1StrokeStyle? RoundCap;
        public ID2D1Layer? OpacityLayer;
        public readonly GradientStop[] Stops2 = new GradientStop[2];
        public readonly GradientStop[] Stops3 = new GradientStop[3];

        public ID2D1SolidColorBrush GetSolid(ID2D1DeviceContext d2d, Color4 color)
        {
            SolidBrush ??= d2d.CreateSolidColorBrush(color);
            SolidBrush.Color = color;
            return SolidBrush;
        }

        public ID2D1StrokeStyle GetRoundCap(ID2D1Factory factory)
        {
            return RoundCap ??= factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        }

        public ID2D1Layer GetLayer(ID2D1DeviceContext d2d)
        {
            return OpacityLayer ??= d2d.CreateLayer();
        }

        public void Dispose()
        {
            OpacityLayer?.Dispose();
            OpacityLayer = null;
            RoundCap?.Dispose();
            RoundCap = null;
            SolidBrush?.Dispose();
            SolidBrush = null;
        }
    }

    private readonly record struct TextFormatKey(
        int SizeDeciPx,
        FontWeight Weight,
        TextAlignment Align,
        ParagraphAlignment VAlign,
        bool Wrap);

    private static readonly ConditionalWeakTable<ID2D1DeviceContext, D2DContextCache> ContextCaches = new();
    private static readonly ConditionalWeakTable<IDWriteFactory, Dictionary<TextFormatKey, IDWriteTextFormat>> TextFormatCaches = new();
    private static readonly object TextFormatCacheLock = new();

    public static void ReleaseContextCache(ID2D1DeviceContext? d2d)
    {
        if (d2d == null) return;
        if (ContextCaches.TryGetValue(d2d, out var cache))
        {
            ContextCaches.Remove(d2d);
            cache.Dispose();
        }
    }

    public static void PushOpacityLayer(ID2D1DeviceContext d2d, float opacity)
    {
        var layer = GetCache(d2d).GetLayer(d2d);
        var lp = new LayerParameters1
        {
            ContentBounds = new RawRectF(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity),
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = Math.Clamp(opacity, 0f, 1f),
            LayerOptions = LayerOptions1.None
        };
        d2d.PushLayer(lp, layer);
    }

    public static void DrawSolidLine(ID2D1DeviceContext d2d, Vector2 p0, Vector2 p1, Color4 color, float strokeWidth = 1f)
    {
        d2d.DrawLine(p0, p1, GetCache(d2d).GetSolid(d2d, color), strokeWidth);
    }

    private static D2DContextCache GetCache(ID2D1DeviceContext d2d)
        => ContextCaches.GetOrCreateValue(d2d);

    private static IDWriteTextFormat GetCachedTextFormat(
        IDWriteFactory dw,
        float size,
        FontWeight weight,
        TextAlignment align,
        ParagraphAlignment vAlign,
        bool wrap)
    {
        var key = new TextFormatKey(
            Math.Max(10, (int)MathF.Round(size * 10f)),
            weight,
            align,
            vAlign,
            wrap);

        lock (TextFormatCacheLock)
        {
            var dict = TextFormatCaches.GetOrCreateValue(dw);
            if (dict.TryGetValue(key, out var cached))
                return cached;

            if (dict.Count > 256)
            {
                foreach (var old in dict.Values)
                    old.Dispose();
                dict.Clear();
            }

            var tf = dw.CreateTextFormat("Segoe UI Variable Text", null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, key.SizeDeciPx / 10f, "en-us");
            tf.TextAlignment = align;
            tf.ParagraphAlignment = vAlign;
            tf.WordWrapping = wrap ? WordWrapping.Wrap : WordWrapping.NoWrap;
            using var trimSign = dw.CreateEllipsisTrimmingSign(tf);
            tf.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, trimSign);
            dict[key] = tf;
            return tf;
        }
    }

    public static UiPalette GetPalette(bool isDark, string accentHex = "#4CC2FF")
    {
        Color4 accent = ParseColor4(accentHex, 1f);
        Color4 accentSoft = new(accent.R, accent.G, accent.B, 0.24f);

        if (isDark)
        {
            return new UiPalette(
                PrimaryText: new Color4(0.98f, 0.99f, 1.00f, 0.98f),
                SecondaryText: new Color4(0.88f, 0.92f, 0.98f, 0.84f),
                SubtleText: new Color4(0.76f, 0.84f, 0.94f, 0.66f),
                Accent: accent,
                AccentSoft: accentSoft,
                Divider: new Color4(1f, 1f, 1f, 0.13f),
                ControlTrack: new Color4(1f, 1f, 1f, 0.16f),
                ControlSurface: new Color4(0.06f, 0.08f, 0.12f, 0.26f),
                ControlHover: new Color4(1f, 1f, 1f, 0.14f),
                HairlineStroke: new Color4(1f, 1f, 1f, 0.16f),
                Success: new Color4(0.24f, 0.88f, 0.54f, 1f),
                Warning: new Color4(1.00f, 0.78f, 0.28f, 1f),
                Danger: new Color4(1.00f, 0.36f, 0.40f, 1f));
        }

        return new UiPalette(
            PrimaryText: new Color4(0.07f, 0.09f, 0.14f, 0.94f),
            SecondaryText: new Color4(0.18f, 0.22f, 0.30f, 0.80f),
            SubtleText: new Color4(0.26f, 0.32f, 0.40f, 0.62f),
            Accent: accent,
            AccentSoft: accentSoft,
            Divider: new Color4(0.05f, 0.08f, 0.14f, 0.13f),
            ControlTrack: new Color4(0.08f, 0.12f, 0.20f, 0.16f),
            ControlSurface: new Color4(1f, 1f, 1f, 0.32f),
            ControlHover: new Color4(1f, 1f, 1f, 0.48f),
            HairlineStroke: new Color4(0.10f, 0.15f, 0.24f, 0.14f),
            Success: new Color4(0.10f, 0.66f, 0.36f, 1f),
            Warning: new Color4(0.86f, 0.54f, 0.08f, 1f),
            Danger: new Color4(0.86f, 0.20f, 0.24f, 1f));
    }

    public static Color4 ParseColor4(string hex, float alpha = 1f)
    {
        try
        {
            var c = ColorTranslator.FromHtml(hex);
            return new Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }
        catch
        {
            return new Color4(0.30f, 0.76f, 1.0f, alpha);
        }
    }

    public static void DrawTextCrisp(
        ID2D1DeviceContext d2d,
        IDWriteFactory dw,
        string text,
        float x, float y, float w, float h,
        float size,
        Color4 color,
        FontWeight weight = FontWeight.Regular,
        TextAlignment align = TextAlignment.Leading,
        ParagraphAlignment vAlign = ParagraphAlignment.Center,
        bool subtleBackdropHalo = true,
        bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || w <= 2f || h <= 2f) return;
        var tf = GetCachedTextFormat(dw, size, weight, align, vAlign, wrap);
        var cache = GetCache(d2d);

        var rect = new RawRectF(x, y, x + w, y + h);
        if (subtleBackdropHalo)
        {
            float lum = color.R * 0.299f + color.G * 0.587f + color.B * 0.114f;
            var haloColor = lum > 0.5f ? new Color4(0f, 0f, 0f, 0.28f) : new Color4(1f, 1f, 1f, 0.28f);
            var haloBrush = cache.GetSolid(d2d, haloColor);
            d2d.DrawText(text, tf, new RawRectF(x, y + 0.75f, x + w, y + h + 0.75f), haloBrush);
        }

        var brush = cache.GetSolid(d2d, color);
        d2d.DrawText(text, tf, rect, brush);
    }

    public static void DrawRoundedRect(ID2D1DeviceContext d2d, float x, float y, float w, float h, float r, Color4 fill, Color4? stroke = null, float strokeWidth = 1f)
    {
        var rr = new RoundedRectangle(new RawRectF(x, y, x + w, y + h), r, r);
        var cache = GetCache(d2d);
        if (fill.A > 0.001f)
        {
            d2d.FillRoundedRectangle(rr, cache.GetSolid(d2d, fill));
        }
        if (stroke.HasValue && stroke.Value.A > 0.001f)
        {
            d2d.DrawRoundedRectangle(rr, cache.GetSolid(d2d, stroke.Value), strokeWidth);
        }
    }

    public static void FillRoundedRectLinearGradient(
        ID2D1DeviceContext d2d,
        float x, float y, float w, float h, float r,
        Color4 startColor, Color4 endColor,
        bool vertical = true)
    {
        var cache = GetCache(d2d);
        cache.Stops2[0] = new GradientStop(0f, startColor);
        cache.Stops2[1] = new GradientStop(1f, endColor);
        using var col = d2d.CreateGradientStopCollection(cache.Stops2);
        var props = new LinearGradientBrushProperties
        {
            StartPoint = new Vector2(x, y),
            EndPoint = vertical ? new Vector2(x, y + h) : new Vector2(x + w, y)
        };
        using var brush = d2d.CreateLinearGradientBrush(props, col);
        d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, y, x + w, y + h), r, r), brush);
    }

    public static void FillRoundedRectMultiGradient(
        ID2D1DeviceContext d2d,
        float x, float y, float w, float h, float r,
        GradientStop[] stops,
        Vector2 startPt, Vector2 endPt)
    {
        using var col = d2d.CreateGradientStopCollection(stops);
        using var brush = d2d.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = startPt, EndPoint = endPt }, col);
        d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x, y, x + w, y + h), r, r), brush);
    }

    public static void DrawRoundedRectBevelBorder(
        ID2D1DeviceContext d2d,
        float x, float y, float w, float h, float r,
        Color4 topColor, Color4 bottomColor,
        float strokeWidth = 1f)
    {
        var cache = GetCache(d2d);
        cache.Stops3[0] = new GradientStop(0f, topColor);
        cache.Stops3[1] = new GradientStop(0.45f, new Color4(topColor.R, topColor.G, topColor.B, topColor.A * 0.42f));
        cache.Stops3[2] = new GradientStop(1f, bottomColor);
        using var col = d2d.CreateGradientStopCollection(cache.Stops3);
        using var brush = d2d.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x + w * 0.25f, y + h) },
            col);
        d2d.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x, y, x + w, y + h), r, r), brush, strokeWidth);
    }

    public static void FillEllipseRadialGradient(
        ID2D1DeviceContext d2d,
        float cx, float cy, float rx, float ry,
        Color4 centerColor, Color4 edgeColor,
        float originOffsetX = 0f, float originOffsetY = 0f)
    {
        var cache = GetCache(d2d);
        cache.Stops2[0] = new GradientStop(0f, centerColor);
        cache.Stops2[1] = new GradientStop(1f, edgeColor);
        using var col = d2d.CreateGradientStopCollection(cache.Stops2);
        using var brush = d2d.CreateRadialGradientBrush(new RadialGradientBrushProperties
        {
            Center = new Vector2(cx, cy),
            GradientOriginOffset = new Vector2(originOffsetX, originOffsetY),
            RadiusX = Math.Max(1f, rx),
            RadiusY = Math.Max(1f, ry)
        }, col);
        d2d.FillEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), brush);
    }

    public static void FillPolygonGradient(
        ID2D1DeviceContext d2d,
        Vector2[] points,
        Color4 startColor, Color4 endColor,
        Vector2 gradStart, Vector2 gradEnd,
        Color4? strokeColor = null, float strokeWidth = 0.8f)
    {
        if (points.Length < 3) return;
        using var path = d2d.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(points[0], FigureBegin.Filled);
            for (int i = 1; i < points.Length; i++)
                sink.AddLine(points[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        var cache = GetCache(d2d);
        cache.Stops2[0] = new GradientStop(0f, startColor);
        cache.Stops2[1] = new GradientStop(1f, endColor);
        using var col = d2d.CreateGradientStopCollection(cache.Stops2);
        using var brush = d2d.CreateLinearGradientBrush(new LinearGradientBrushProperties { StartPoint = gradStart, EndPoint = gradEnd }, col);
        d2d.FillGeometry(path, brush);
        if (strokeColor.HasValue && strokeColor.Value.A > 0.001f)
        {
            d2d.DrawGeometry(path, cache.GetSolid(d2d, strokeColor.Value), strokeWidth);
        }
    }

    public static void DrawHairlineCard(ID2D1DeviceContext d2d, float x, float y, float w, float h, float r, UiPalette pal, bool highlighted = false)
    {
        var cache = GetCache(d2d);
        if (highlighted)
        {
            Color4 topBg = new(pal.ControlHover.R, pal.ControlHover.G, pal.ControlHover.B, Math.Min(0.45f, pal.ControlHover.A + 0.08f));
            Color4 botBg = new(pal.ControlSurface.R * 0.72f, pal.ControlSurface.G * 0.72f, pal.ControlSurface.B * 0.78f, Math.Max(0.10f, pal.ControlSurface.A * 0.85f));
            FillRoundedRectLinearGradient(d2d, x, y, w, h, r, topBg, botBg);

            Color4 topRim = new(1f, 1f, 1f, 0.48f);
            Color4 botRim = new(pal.HairlineStroke.R, pal.HairlineStroke.G, pal.HairlineStroke.B, pal.HairlineStroke.A * 0.35f);
            DrawRoundedRectBevelBorder(d2d, x, y, w, h, r, topRim, botRim, 1.15f);
        }
        else
        {
            Color4 bg = new(pal.ControlSurface.R, pal.ControlSurface.G, pal.ControlSurface.B, Math.Max(0.13f, pal.ControlSurface.A));
            Color4 rim = new(1f, 1f, 1f, Math.Max(0.16f, pal.HairlineStroke.A * 1.15f));
            var rr = new RoundedRectangle(new RawRectF(x, y, x + w, y + h), r, r);
            d2d.FillRoundedRectangle(rr, cache.GetSolid(d2d, bg));
            d2d.DrawRoundedRectangle(rr, cache.GetSolid(d2d, rim), 0.85f);
        }

        if (w > r * 2.2f)
        {
            float inset = Math.Max(8f, r * 0.65f);
            var sheenColor = new Color4(1f, 1f, 1f, highlighted ? 0.38f : 0.20f);
            d2d.DrawLine(new Vector2(x + inset, y + 0.85f), new Vector2(x + w - inset, y + 0.85f), cache.GetSolid(d2d, sheenColor), 0.9f);
        }
    }

    /// <summary>
    /// 3D optical refractive glass sphere button (inspired by LiquidGlassDesign Music Player & Day 14 orbs).
    /// </summary>
    public static void DrawGlassOrbButton(
        ID2D1DeviceContext d2d,
        float cx, float cy, float radius,
        Color4 accent, bool isPrimary = false, bool isHovered = false)
    {
        if (isPrimary || isHovered)
        {
            FillEllipseRadialGradient(d2d, cx, cy, radius * 1.55f, radius * 1.55f,
                new Color4(accent.R, accent.G, accent.B, isPrimary ? 0.38f : 0.26f),
                new Color4(accent.R, accent.G, accent.B, 0f));
        }

        FillEllipseRadialGradient(d2d, cx, cy + radius * 0.14f, radius * 1.05f, radius * 0.96f,
            new Color4(0f, 0f, 0f, 0.36f), new Color4(0f, 0f, 0f, 0f));

        Color4 topTint = isPrimary
            ? new Color4(Math.Min(1f, accent.R + 0.18f), Math.Min(1f, accent.G + 0.18f), Math.Min(1f, accent.B + 0.20f), 0.92f)
            : new Color4(1f, 1f, 1f, isHovered ? 0.34f : 0.20f);
        Color4 botTint = isPrimary
            ? new Color4(accent.R * 0.65f, accent.G * 0.65f, accent.B * 0.85f, 0.96f)
            : new Color4(0.10f, 0.12f, 0.18f, 0.42f);

        FillEllipseRadialGradient(d2d, cx, cy, radius, radius, topTint, botTint, -radius * 0.28f, -radius * 0.32f);

        var rimStops = new[]
        {
            new GradientStop(0f, new Color4(1f, 1f, 1f, isPrimary ? 0.95f : 0.78f)),
            new GradientStop(0.55f, new Color4(1f, 1f, 1f, 0.22f)),
            new GradientStop(1f, new Color4(accent.R, accent.G, accent.B, isPrimary ? 0.65f : 0.18f))
        };
        using var rCol = d2d.CreateGradientStopCollection(rimStops);
        using var rimBrush = d2d.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(cx - radius, cy - radius), EndPoint = new Vector2(cx + radius, cy + radius) },
            rCol);
        d2d.DrawEllipse(new Ellipse(new Vector2(cx, cy), radius - 0.5f, radius - 0.5f), rimBrush, isPrimary ? 1.25f : 0.95f);

        DrawArcSegments(d2d, cx, cy, radius - 2.6f, -2.55f, -0.75f, new Color4(1f, 1f, 1f, isPrimary ? 0.55f : 0.38f), 1.1f, 12);
    }

    /// <summary>
    /// Apple WWDC25 translucent glass pill capsule housing Close (X) / Minimize (-) / Expand orbs.
    /// </summary>
    public static void DrawTrafficLightsCapsule(ID2D1DeviceContext d2d, float x, float y, bool showThirdDot = true)
    {
        float pillW = showThirdDot ? 74f : 52f;
        float pillH = 26f;
        FillRoundedRectLinearGradient(d2d, x, y, pillW, pillH, 13f,
            new Color4(1f, 1f, 1f, 0.22f), new Color4(0.08f, 0.10f, 0.16f, 0.38f));
        DrawRoundedRectBevelBorder(d2d, x, y, pillW, pillH, 13f,
            new Color4(1f, 1f, 1f, 0.62f), new Color4(1f, 1f, 1f, 0.12f), 0.95f);

        var cache = GetCache(d2d);
        void DrawOrb(float cx, float cy, Color4 bright, Color4 deep, int glyph)
        {
            FillEllipseRadialGradient(d2d, cx, cy, 7.2f, 7.2f, bright, deep, -2.0f, -2.0f);
            d2d.DrawEllipse(new Ellipse(new Vector2(cx, cy), 7.0f, 7.0f), cache.GetSolid(d2d, new Color4(1f, 1f, 1f, 0.55f)), 0.75f);
            var gB = cache.GetSolid(d2d, new Color4(0.06f, 0.08f, 0.12f, 0.85f));
            if (glyph == 0)
            {
                d2d.DrawLine(new Vector2(cx - 2.6f, cy - 2.6f), new Vector2(cx + 2.6f, cy + 2.6f), gB, 1.35f);
                d2d.DrawLine(new Vector2(cx + 2.6f, cy - 2.6f), new Vector2(cx - 2.6f, cy + 2.6f), gB, 1.35f);
            }
            else if (glyph == 1)
            {
                d2d.DrawLine(new Vector2(cx - 3.0f, cy), new Vector2(cx + 3.0f, cy), gB, 1.45f);
            }
            else
            {
                d2d.DrawLine(new Vector2(cx - 2.4f, cy + 2.4f), new Vector2(cx + 2.4f, cy - 2.4f), gB, 1.3f);
                d2d.DrawLine(new Vector2(cx + 0.4f, cy - 2.4f), new Vector2(cx + 2.4f, cy - 2.4f), gB, 1.3f);
                d2d.DrawLine(new Vector2(cx + 2.4f, cy - 2.4f), new Vector2(cx + 2.4f, cy - 0.4f), gB, 1.3f);
            }
        }

        DrawOrb(x + 15f, y + 13f, new Color4(1f, 0.46f, 0.44f, 1f), new Color4(0.88f, 0.18f, 0.20f, 1f), 0);
        DrawOrb(x + 37f, y + 13f, new Color4(1f, 0.84f, 0.30f, 1f), new Color4(0.90f, 0.56f, 0.06f, 1f), 1);
        if (showThirdDot)
            DrawOrb(x + 59f, y + 13f, new Color4(0.32f, 0.92f, 0.48f, 1f), new Color4(0.10f, 0.68f, 0.28f, 1f), 2);
    }

    /// <summary>
    /// Symmetric glowing acoustic audio waveform capsule inspired by LiquidGlassDesign components.
    /// </summary>
    public static void DrawAcousticWaveform(ID2D1DeviceContext d2d, float x, float cy, float timeSec, Color4 color, bool isPlaying, int bars = 9)
    {
        var b = GetCache(d2d).GetSolid(d2d, color);
        for (int i = 0; i < bars; i++)
        {
            float centerNorm = 1f - MathF.Abs((i - (bars - 1) * 0.5f) / Math.Max(1f, (bars - 1) * 0.5f));
            float env = 0.35f + 0.65f * centerNorm;
            float wave = isPlaying ? (0.45f + 0.55f * MathF.Sin(timeSec * 6.4f + i * 0.95f)) : 0.38f;
            float h = Math.Clamp(3.5f + 13.5f * env * wave, 3.2f, 17f);
            float alpha = 0.55f + 0.45f * centerNorm;
            b.Color = new Color4(color.R, color.G, color.B, alpha);
            float bx = x + i * 4.0f;
            d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(bx, cy - h * 0.5f, bx + 2.3f, cy + h * 0.5f), 1.15f, 1.15f), b);
        }
    }

    public static float DrawLuxuryKeycap(ID2D1DeviceContext d2d, IDWriteFactory dw, string text, float x, float y, float h, UiPalette pal, bool accented = false)
    {
        float w = Math.Max(h + 2f, text.Length * 7.6f + 16f);
        // Subtle 3D mechanical key step shadow beneath keycap
        DrawRoundedRect(d2d, x, y + 1.4f, w, h, 5.5f, new Color4(0f, 0f, 0f, 0.34f));
        Color4 topFill = accented ? new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.34f) : new Color4(1f, 1f, 1f, 0.14f);
        Color4 botFill = accented ? new Color4(pal.Accent.R * 0.7f, pal.Accent.G * 0.7f, pal.Accent.B * 0.7f, 0.22f) : new Color4(0.08f, 0.10f, 0.14f, 0.44f);
        FillRoundedRectLinearGradient(d2d, x, y, w, h - 1.2f, 5.2f, topFill, botFill);
        DrawRoundedRectBevelBorder(d2d, x, y, w, h - 1.2f, 5.2f,
            accented ? new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.78f) : new Color4(1f, 1f, 1f, 0.34f),
            new Color4(1f, 1f, 1f, 0.08f), 0.85f);
        DrawTextCrisp(d2d, dw, text, x + 2f, y, w - 4f, h - 1.2f, Math.Clamp(h * 0.46f, 8.8f, 11.2f),
            accented ? pal.PrimaryText : pal.SecondaryText, FontWeight.SemiBold, TextAlignment.Center, ParagraphAlignment.Center, false);
        return w;
    }

    public static void DrawAnimatedEqualizer(ID2D1DeviceContext d2d, float x, float cy, float timeSec, Color4 color, bool isPlaying, int bars = 3)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        for (int i = 0; i < bars; i++)
        {
            float phase = timeSec * 7.2f + i * 1.35f;
            float h = isPlaying ? (3.5f + 7.5f * (0.5f + 0.5f * MathF.Sin(phase))) : 3.2f;
            d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x + i * 3.8f, cy - h * 0.5f, x + i * 3.8f + 2.2f, cy + h * 0.5f), 1.1f, 1.1f), brush);
        }
    }

    /// <summary>
    /// Viscous Liquid-Glass toggle switch (inspired by liquid-glass-switch & Apple iOS 26 refraction):
    /// thumb elongates into a translucent refractive capsule while sliding and snaps back to a gleaming crystal sphere.
    /// </summary>
    public static void DrawGlassToggle(ID2D1DeviceContext d2d, float x, float y, float progress01, UiPalette pal, float width = 46f, float height = 24f, float velocity = 0f)
    {
        progress01 = Math.Clamp(progress01, 0f, 1f);
        float midMotion = MathF.Sin(progress01 * MathF.PI);
        float velStretch = Math.Clamp(MathF.Abs(velocity) * 0.14f + midMotion * 0.65f, 0f, 0.72f);

        Color4 trackTop = new(
            pal.ControlTrack.R + (pal.Accent.R - pal.ControlTrack.R) * progress01,
            pal.ControlTrack.G + (pal.Accent.G - pal.ControlTrack.G) * progress01,
            pal.ControlTrack.B + (pal.Accent.B - pal.ControlTrack.B) * progress01,
            pal.ControlTrack.A + (0.94f - pal.ControlTrack.A) * progress01);
        Color4 trackBot = new(trackTop.R * 0.72f, trackTop.G * 0.72f, trackTop.B * 0.78f, trackTop.A);

        if (progress01 > 0.12f)
        {
            FillEllipseRadialGradient(d2d, x + width * (0.35f + 0.35f * progress01), y + height * 0.5f, width * 0.60f, height * 0.85f,
                new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.28f * progress01), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));
        }

        FillRoundedRectLinearGradient(d2d, x, y, width, height, height * 0.5f, trackTop, trackBot);
        DrawRoundedRectBevelBorder(d2d, x, y, width, height, height * 0.5f,
            new Color4(1f, 1f, 1f, 0.42f), new Color4(1f, 1f, 1f, 0.08f), 0.9f);

        float baseDiam = height - 4.4f;
        float knobW = baseDiam * (1f + velStretch * 0.52f);
        float knobH = baseDiam * (1f - velStretch * 0.08f);
        float travel = Math.Max(1f, width - 4.4f - knobW);
        float kx = x + 2.2f + travel * progress01;
        float ky = y + (height - knobH) * 0.5f;
        float kr = knobH * 0.5f;

        // Soft cast shadow beneath floating crystal droplet
        DrawRoundedRect(d2d, kx, ky + 1.2f, knobW, knobH, kr, new Color4(0f, 0f, 0f, 0.34f));

        // Translucent frosted lens core when stretching vs luminous crystal orb when settled
        float clarityAlpha = 0.98f - velStretch * 0.38f;
        FillRoundedRectLinearGradient(d2d, kx, ky, knobW, knobH, kr,
            new Color4(1f, 1f, 1f, clarityAlpha),
            new Color4(0.84f, 0.90f, 0.98f, clarityAlpha * 0.92f));
        DrawRoundedRectBevelBorder(d2d, kx, ky, knobW, knobH, kr,
            new Color4(1f, 1f, 1f, 0.96f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.42f), 0.95f);

        // Specular catchlight crescent along top of switch thumb
        FillRoundedRectLinearGradient(d2d, kx + 2f, ky + 1.2f, Math.Max(4f, knobW - 4f), knobH * 0.42f, kr * 0.7f,
            new Color4(1f, 1f, 1f, 0.55f), new Color4(1f, 1f, 1f, 0f));
    }

    public static void DrawGlassToggle(ID2D1DeviceContext d2d, float x, float y, bool isOn, UiPalette pal, float width = 46f, float height = 24f)
        => DrawGlassToggle(d2d, x, y, isOn ? 1f : 0f, pal, width, height, 0f);

    /// <summary>
    /// DeskMinder-inspired frosted reminder/task pill with check orb and glowing completion ring.
    /// </summary>
    public static void DrawDeskMinderTaskRow(
        ID2D1DeviceContext d2d, IDWriteFactory dw,
        float x, float y, float w, float h,
        string title, string? timeTag, bool completed, bool hovered, UiPalette pal)
    {
        DrawHairlineCard(d2d, x, y, w, h, h * 0.5f, pal, hovered);
        float orbCx = x + h * 0.5f + 2f, orbCy = y + h * 0.5f;
        if (completed)
        {
            DrawGlassOrbButton(d2d, orbCx, orbCy, 8.5f, pal.Success, true);
            using var chk = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.98f));
            d2d.DrawLine(new Vector2(orbCx - 3.2f, orbCy + 0.2f), new Vector2(orbCx - 0.8f, orbCy + 2.8f), chk, 1.5f);
            d2d.DrawLine(new Vector2(orbCx - 0.8f, orbCy + 2.8f), new Vector2(orbCx + 3.4f, orbCy - 2.6f), chk, 1.5f);
        }
        else
        {
            DrawRoundedRectBevelBorder(d2d, orbCx - 8f, orbCy - 8f, 16f, 16f, 8f,
                new Color4(1f, 1f, 1f, hovered ? 0.85f : 0.45f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.35f), 1.2f);
        }

        float tagW = string.IsNullOrEmpty(timeTag) ? 0f : 54f;
        Color4 txtCol = completed ? pal.SubtleText : pal.PrimaryText;
        DrawTextCrisp(d2d, dw, title, x + h + 4f, y, w - h - tagW - 14f, h, 11.2f, txtCol, completed ? FontWeight.Regular : FontWeight.Medium);

        if (!string.IsNullOrEmpty(timeTag))
        {
            DrawRoundedRect(d2d, x + w - tagW - 6f, y + (h - 17f) * 0.5f, tagW, 17f, 8.5f, completed ? pal.ControlSurface : pal.AccentSoft);
            DrawTextCrisp(d2d, dw, timeTag, x + w - tagW - 6f, y + (h - 17f) * 0.5f, tagW, 17f, 8.8f, completed ? pal.SubtleText : pal.Accent, FontWeight.Bold, TextAlignment.Center, ParagraphAlignment.Center, false);
        }
    }

    public static void DrawGlassSlider(ID2D1DeviceContext d2d, float x, float y, float w, float value01, UiPalette pal, float trackHeight = 6f, bool active = false, bool showTicks = false)
    {
        value01 = Math.Clamp(value01, 0f, 1f);
        float ty = y - trackHeight * 0.5f;

        if (showTicks)
        {
            var tickBrush = GetCache(d2d).GetSolid(d2d, new Color4(1f, 1f, 1f, 0.16f));
            for (int t = 0; t <= 8; t++)
            {
                float tx = x + (w * t / 8f);
                float tickH = (t % 2 == 0) ? 5f : 3f;
                d2d.DrawLine(new Vector2(tx, ty + trackHeight + 2f), new Vector2(tx, ty + trackHeight + 2f + tickH), tickBrush, 0.8f);
            }
        }

        // Recessed channel
        FillRoundedRectLinearGradient(d2d, x, ty, w, trackHeight, trackHeight * 0.5f,
            new Color4(0f, 0f, 0f, 0.32f), new Color4(1f, 1f, 1f, 0.12f));
        DrawRoundedRectBevelBorder(d2d, x, ty, w, trackHeight, trackHeight * 0.5f,
            new Color4(0f, 0f, 0f, 0.25f), new Color4(1f, 1f, 1f, 0.18f), 0.7f);

        // Luminous fiber-optic accent fill
        float fillW = Math.Max(trackHeight, w * value01);
        Color4 glowLight = new(Math.Min(1f, pal.Accent.R + 0.22f), Math.Min(1f, pal.Accent.G + 0.22f), Math.Min(1f, pal.Accent.B + 0.22f), 0.98f);
        FillRoundedRectLinearGradient(d2d, x, ty, fillW, trackHeight, trackHeight * 0.5f, glowLight, pal.Accent, vertical: false);

        float kx = x + w * value01;
        float kw = active ? 18f : 15f;
        float kh = active ? 13.5f : 12f;
        if (active)
            FillEllipseRadialGradient(d2d, kx, y, 16f, 14f, new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.38f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0f));

        // Jeweled capsule quartz thumb
        DrawRoundedRect(d2d, kx - kw * 0.5f, y - kh * 0.5f + 1.2f, kw, kh, kh * 0.5f, new Color4(0f, 0f, 0f, 0.32f));
        FillRoundedRectLinearGradient(d2d, kx - kw * 0.5f, y - kh * 0.5f, kw, kh, kh * 0.5f,
            new Color4(1f, 1f, 1f, 0.99f), new Color4(0.82f, 0.88f, 0.96f, 0.96f));
        DrawRoundedRectBevelBorder(d2d, kx - kw * 0.5f, y - kh * 0.5f, kw, kh, kh * 0.5f,
            new Color4(1f, 1f, 1f, 0.95f), new Color4(pal.Accent.R, pal.Accent.G, pal.Accent.B, 0.45f), 0.9f);
    }

    public static void DrawPrecisionDial(
        ID2D1DeviceContext d2d,
        IDWriteFactory dw,
        float cx, float cy, float radius,
        float value01,
        Color4 accent,
        string label,
        string valueText,
        UiPalette pal)
    {
        value01 = Math.Clamp(value01, 0f, 1f);
        float startRad = 135f * MathF.PI / 180f;
        float sweepMax = 270f * MathF.PI / 180f;

        // Engraved perimeter tick marks
        var cache = GetCache(d2d);
        var dialBrush = cache.GetSolid(d2d, accent);
        for (int i = 0; i <= 16; i++)
        {
            float t = i / 16f;
            float ang = startRad + t * sweepMax;
            bool lit = t <= value01 + 0.01f;
            float rIn = radius + 2.5f;
            float rOut = radius + (i % 4 == 0 ? 6.5f : 4.5f);
            dialBrush.Color = lit ? new Color4(accent.R, accent.G, accent.B, 0.90f) : new Color4(1f, 1f, 1f, 0.18f);
            d2d.DrawLine(
                new Vector2(cx + MathF.Cos(ang) * rIn, cy + MathF.Sin(ang) * rIn),
                new Vector2(cx + MathF.Cos(ang) * rOut, cy + MathF.Sin(ang) * rOut),
                dialBrush, i % 4 == 0 ? 1.3f : 0.9f);
        }

        // Track & active illuminated arc
        DrawArcSegments(d2d, cx, cy, radius + 0.5f, startRad, startRad + sweepMax, new Color4(1f, 1f, 1f, 0.12f), 2.4f);
        if (value01 > 0.01f)
            DrawArcSegments(d2d, cx, cy, radius + 0.5f, startRad, startRad + sweepMax * value01, accent, 2.6f);

        // Recessed shadow + machined titanium dial cap
        FillEllipseRadialGradient(d2d, cx, cy + 2f, radius - 1f, radius - 1f, new Color4(0f, 0f, 0f, 0.45f), new Color4(0f, 0f, 0f, 0f));
        FillEllipseRadialGradient(d2d, cx, cy, radius - 2.5f, radius - 2.5f,
            new Color4(0.24f, 0.28f, 0.36f, 0.96f), new Color4(0.08f, 0.10f, 0.14f, 0.98f), -radius * 0.25f, -radius * 0.30f);
        d2d.DrawEllipse(new Ellipse(new Vector2(cx, cy), radius - 2.8f, radius - 2.8f), cache.GetSolid(d2d, new Color4(1f, 1f, 1f, 0.24f)), 0.9f);

        // Indicator tritium slit
        float needleAng = startRad + sweepMax * value01;
        var p0 = new Vector2(cx + MathF.Cos(needleAng) * (radius * 0.28f), cy + MathF.Sin(needleAng) * (radius * 0.28f));
        var p1 = new Vector2(cx + MathF.Cos(needleAng) * (radius - 4.8f), cy + MathF.Sin(needleAng) * (radius - 4.8f));
        d2d.DrawLine(p0, p1, cache.GetSolid(d2d, new Color4(accent.R, accent.G, accent.B, 0.45f)), 3.8f);
        d2d.DrawLine(p0, p1, cache.GetSolid(d2d, new Color4(1f, 1f, 1f, 0.96f)), 1.8f);

        DrawTextCrisp(d2d, dw, valueText, cx - radius - 14f, cy + radius + 5f, (radius + 14f) * 2f, 15f, 10.5f, pal.PrimaryText, FontWeight.SemiBold, TextAlignment.Center);
        DrawTextCrisp(d2d, dw, label, cx - radius - 14f, cy + radius + 19f, (radius + 14f) * 2f, 13f, 9f, pal.SubtleText, FontWeight.Bold, TextAlignment.Center);
    }

    public static void DrawArcSegments(
        ID2D1DeviceContext d2d,
        float cx, float cy, float radius,
        float startRad, float endRad,
        Color4 color, float thickness, int steps = 28)
    {
        if (MathF.Abs(endRad - startRad) < 0.01f) return;
        using var path = d2d.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            for (int i = 0; i <= steps; i++)
            {
                float a = startRad + (endRad - startRad) * (i / (float)steps);
                var pt = new Vector2(cx + MathF.Cos(a) * radius, cy + MathF.Sin(a) * radius);
                if (i == 0) sink.BeginFigure(pt, FigureBegin.Hollow);
                else sink.AddLine(pt);
            }
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        var cache = GetCache(d2d);
        d2d.DrawGeometry(path, cache.GetSolid(d2d, color), thickness, cache.GetRoundCap(d2d.Factory));
    }

    public static void DrawAnalogGaugeArc(
        ID2D1DeviceContext d2d, IDWriteFactory dw,
        float cx, float cy, float radius, float value01,
        Color4 startColor, Color4 endColor,
        string centerText, string subLabel, UiPalette pal)
    {
        value01 = Math.Clamp(value01, 0f, 1f);
        float startRad = 135f * MathF.PI / 180f;
        float sweepMax = 270f * MathF.PI / 180f;

        DrawArcSegments(d2d, cx, cy, radius, startRad, startRad + sweepMax, pal.ControlTrack, 4.5f, 32);
        if (value01 > 0.01f)
        {
            float endRad = startRad + sweepMax * value01;
            using var path = d2d.Factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                const int steps = 28;
                for (int i = 0; i <= steps; i++)
                {
                    float a = startRad + (endRad - startRad) * (i / (float)steps);
                    var pt = new Vector2(cx + MathF.Cos(a) * radius, cy + MathF.Sin(a) * radius);
                    if (i == 0) sink.BeginFigure(pt, FigureBegin.Hollow);
                    else sink.AddLine(pt);
                }
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            var stops = new[] { new GradientStop(0f, startColor), new GradientStop(1f, endColor) };
            using var col = d2d.CreateGradientStopCollection(stops);
            using var gb = d2d.CreateLinearGradientBrush(new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(cx - radius, cy + radius),
                EndPoint = new Vector2(cx + radius, cy - radius)
            }, col);
            using var style = d2d.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
            d2d.DrawGeometry(path, gb, 4.8f, style);

            float hx = cx + MathF.Cos(endRad) * radius;
            float hy = cy + MathF.Sin(endRad) * radius;
            FillEllipseRadialGradient(d2d, hx, hy, 7f, 7f, new Color4(endColor.R, endColor.G, endColor.B, 0.7f), new Color4(endColor.R, endColor.G, endColor.B, 0f));
            using var dot = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.98f));
            d2d.FillEllipse(new Ellipse(new Vector2(hx, hy), 2.3f, 2.3f), dot);
        }

        DrawTextCrisp(d2d, dw, centerText, cx - radius * 0.76f, cy - 13f, radius * 1.52f, 18f, 12.5f, pal.PrimaryText, FontWeight.Bold, TextAlignment.Center);
        DrawTextCrisp(d2d, dw, subLabel, cx - radius * 0.76f, cy + 3f, radius * 1.52f, 12f, 8.2f, pal.SubtleText, FontWeight.Bold, TextAlignment.Center);
    }

    public static void DrawSpectroscopicCurve(
        ID2D1DeviceContext d2d, IDWriteFactory dw,
        float x, float y, float w, float h,
        float refraction, float dispersion, float edgeGlow, float timeSec,
        UiPalette pal)
    {
        // Recessed obsidian spectrogram chamber
        FillRoundedRectLinearGradient(d2d, x, y, w, h, 12f,
            new Color4(0.03f, 0.04f, 0.07f, 0.62f), new Color4(0.05f, 0.07f, 0.12f, 0.48f));
        DrawRoundedRectBevelBorder(d2d, x, y, w, h, 12f,
            new Color4(1f, 1f, 1f, 0.18f), new Color4(1f, 1f, 1f, 0.05f), 0.9f);

        float gx = x + 12f, gy = y + 22f, gw = w - 24f, gh = h - 38f;
        using var gridP = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.07f));
        for (int r = 0; r <= 3; r++)
            d2d.DrawLine(new Vector2(gx, gy + gh * r / 3f), new Vector2(gx + gw, gy + gh * r / 3f), gridP, 0.7f);
        for (int c = 0; c <= 6; c++)
            d2d.DrawLine(new Vector2(gx + gw * c / 6f, gy), new Vector2(gx + gw * c / 6f, gy + gh), gridP, 0.7f);

        DrawTextCrisp(d2d, dw, "SPECTRAL DISPERSION & FRESNEL RESPONSE", x + 12f, y + 6f, w - 90f, 14f, 9f, pal.Accent, FontWeight.Bold);
        DrawTextCrisp(d2d, dw, "450–680nm", x + w - 82f, y + 6f, 70f, 14f, 9f, pal.SubtleText, FontWeight.Medium, TextAlignment.Trailing);

        // Draw 3 layered wavelength dispersion channels (Cyan/Blue, Mint/Green, Rose/Red)
        var channels = new (float Phase, float Spread, Color4 Col)[]
        {
            (-0.16f, -0.08f * dispersion, new Color4(0.30f, 0.78f, 1.0f, 0.92f)),
            ( 0.00f,  0.00f,              new Color4(0.32f, 0.96f, 0.76f, 0.88f)),
            ( 0.18f,  0.09f * dispersion, new Color4(1.00f, 0.42f, 0.68f, 0.88f))
        };

        const int pts = 28;
        foreach (var ch in channels)
        {
            var vals = new float[pts];
            for (int i = 0; i < pts; i++)
            {
                float u = i / (float)(pts - 1);
                float bell = MathF.Sin(u * MathF.PI);
                float fresnel = MathF.Pow(1f - bell, 2.2f) * Math.Clamp(edgeGlow * 0.25f, 0.05f, 0.55f);
                float wave = 0.38f * bell * Math.Clamp(0.45f + refraction * 0.26f, 0.25f, 1.0f)
                           + 0.08f * MathF.Sin(u * 9.5f + ch.Phase + timeSec * 1.8f)
                           + fresnel + ch.Spread;
                vals[i] = Math.Clamp(wave, 0.06f, 0.94f);
            }
            DrawSparklineWithArea(d2d, gx, gy, gw, gh, vals, ch.Col);
        }

        // Animated sampling cursor dot
        float cursorU = 0.5f + 0.42f * MathF.Sin(timeSec * 0.95f);
        float px = gx + gw * cursorU;
        using var curLine = d2d.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0.26f));
        d2d.DrawLine(new Vector2(px, gy), new Vector2(px, gy + gh), curLine, 1.0f);
        DrawTextCrisp(d2d, dw, "Normal 0°", gx, y + h - 15f, 80f, 12f, 8.5f, pal.SubtleText);
        DrawTextCrisp(d2d, dw, "Grazing 90°", gx + gw - 80f, y + h - 15f, 80f, 12f, 8.5f, pal.SubtleText, FontWeight.Regular, TextAlignment.Trailing);
    }

    public static void DrawSparklineWithArea(ID2D1DeviceContext d2d, float x, float y, float w, float h, IReadOnlyList<float> values01, Color4 lineColor)
    {
        if (values01.Count < 2) return;
        float stepX = w / Math.Max(1, values01.Count - 1);

        using var path = d2d.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2(x, y + h), FigureBegin.Filled);
            for (int i = 0; i < values01.Count; i++)
            {
                float px = x + i * stepX;
                float py = y + h - Math.Clamp(values01[i], 0f, 1f) * h;
                sink.AddLine(new Vector2(px, py));
            }
            sink.AddLine(new Vector2(x + (values01.Count - 1) * stepX, y + h));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        using var areaBrush = d2d.CreateSolidColorBrush(new Color4(lineColor.R, lineColor.G, lineColor.B, 0.22f));
        d2d.FillGeometry(path, areaBrush);

        using var brush = d2d.CreateSolidColorBrush(lineColor);
        for (int i = 0; i < values01.Count - 1; i++)
        {
            float x0 = x + i * stepX;
            float y0 = y + h - Math.Clamp(values01[i], 0f, 1f) * h;
            float x1 = x + (i + 1) * stepX;
            float y1 = y + h - Math.Clamp(values01[i + 1], 0f, 1f) * h;
            d2d.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), brush, 1.8f);
        }
    }

    // ---- Multi-Layer macOS Tahoe Squircle Artwork (Zero Wireframe Hacks) ----

    public static void DrawLuxurySquirclePlate(
        ID2D1DeviceContext d2d,
        float x, float y, float size,
        Color4 topGrad, Color4 midGrad, Color4 botGrad,
        Color4? ambientGlow = null)
    {
        float r = size * 0.24f;
        // Soft macro drop shadow grounding the icon in the Dock
        DrawRoundedRect(d2d, x + 0.5f, y + size * 0.045f, size - 1f, size, r, new Color4(0f, 0f, 0f, 0.36f));
        FillRoundedRectMultiGradient(
            d2d, x, y, size, size, r,
            new[] { new GradientStop(0f, topGrad), new GradientStop(0.52f, midGrad), new GradientStop(1f, botGrad) },
            new Vector2(x, y), new Vector2(x + size * 0.25f, y + size));

        if (ambientGlow.HasValue)
        {
            FillEllipseRadialGradient(d2d, x + size * 0.35f, y + size * 0.26f, size * 0.48f, size * 0.36f,
                ambientGlow.Value, new Color4(ambientGlow.Value.R, ambientGlow.Value.G, ambientGlow.Value.B, 0f));
        }

        // Upper liquid-glass glaze crest
        FillRoundedRectLinearGradient(d2d, x + 1.5f, y + 1.2f, size - 3f, size * 0.42f, r * 0.90f,
            new Color4(1f, 1f, 1f, 0.22f), new Color4(1f, 1f, 1f, 0f));
        // Machined top-left specular bevel border
        DrawRoundedRectBevelBorder(d2d, x, y, size, size, r,
            new Color4(1f, 1f, 1f, 0.62f), new Color4(1f, 1f, 1f, 0.14f), 1.05f);
    }

    public static void DrawLuxuryDesktopIcon(ID2D1DeviceContext d2d, float x, float y, float size)
    {
        DrawLuxurySquirclePlate(
            d2d, x, y, size,
            new Color4(0.08f, 0.42f, 0.54f, 0.98f),
            new Color4(0.05f, 0.22f, 0.38f, 0.98f),
            new Color4(0.04f, 0.10f, 0.22f, 0.99f),
            new Color4(0.24f, 0.88f, 0.86f, 0.28f));

        float cx = x + size * 0.5f;
        float mw = size * 0.72f;
        float mh = size * 0.50f;
        float mx = cx - mw * 0.5f;
        float my = y + size * 0.18f;

        // Ambient shadow behind Studio Display + aluminum stand
        DrawRoundedRect(d2d, cx - mw * 0.12f, my + mh - 1f, mw * 0.24f, size * 0.14f, 1.5f, new Color4(0f, 0f, 0f, 0.35f));
        FillRoundedRectLinearGradient(d2d, cx - mw * 0.08f, my + mh - 1f, mw * 0.16f, size * 0.12f, 1.2f,
            new Color4(0.75f, 0.82f, 0.90f, 0.95f), new Color4(0.45f, 0.52f, 0.62f, 0.95f));
        FillRoundedRectLinearGradient(d2d, cx - mw * 0.25f, my + mh + size * 0.105f, mw * 0.50f, Math.Max(2.2f, size * 0.042f), 1.5f,
            new Color4(0.92f, 0.96f, 1f, 0.96f), new Color4(0.55f, 0.62f, 0.72f, 0.95f));

        // Display chassis bezel
        DrawRoundedRect(d2d, mx, my + 1.5f, mw, mh, size * 0.07f, new Color4(0f, 0f, 0f, 0.42f));
        FillRoundedRectLinearGradient(d2d, mx, my, mw, mh, size * 0.065f,
            new Color4(0.86f, 0.91f, 0.98f, 0.98f), new Color4(0.48f, 0.56f, 0.68f, 0.98f));

        // Inside Retina display wallpaper
        float pad = Math.Max(1.8f, size * 0.036f);
        float sx = mx + pad, sy = my + pad, sw = mw - pad * 2f, sh = mh - pad * 2f;
        FillRoundedRectMultiGradient(d2d, sx, sy, sw, sh, size * 0.036f,
            new[]
            {
                new GradientStop(0f, new Color4(0.08f, 0.16f, 0.38f, 1f)),
                new GradientStop(0.55f, new Color4(0.28f, 0.22f, 0.58f, 1f)),
                new GradientStop(1f, new Color4(0.14f, 0.72f, 0.78f, 1f))
            },
            new Vector2(sx, sy), new Vector2(sx + sw, sy + sh));

        // Luminous horizon orb + aurora sweep inside mini display
        FillEllipseRadialGradient(d2d, sx + sw * 0.32f, sy + sh * 0.56f, sw * 0.24f, sh * 0.28f,
            new Color4(1.0f, 0.72f, 0.38f, 0.85f), new Color4(1.0f, 0.42f, 0.65f, 0f));
        // Miniature top Menu Bar
        DrawRoundedRect(d2d, sx + sw * 0.06f, sy + sh * 0.07f, sw * 0.88f, Math.Max(1.8f, sh * 0.10f), 1.2f, new Color4(1f, 1f, 1f, 0.38f));
        // Miniature Desktop Widget plaque on top-right
        DrawRoundedRect(d2d, sx + sw * 0.64f, sy + sh * 0.24f, sw * 0.28f, sh * 0.36f, 2f, new Color4(1f, 1f, 1f, 0.24f), new Color4(1f, 1f, 1f, 0.55f), 0.7f);
        // Miniature floating glowing Dock capsule at bottom
        DrawRoundedRect(d2d, cx - sw * 0.34f, sy + sh * 0.74f, sw * 0.68f, Math.Max(2.8f, sh * 0.18f), 2f, new Color4(1f, 1f, 1f, 0.52f), new Color4(1f, 1f, 1f, 0.75f), 0.6f);
    }

    public static void DrawLuxuryExposeIcon(ID2D1DeviceContext d2d, float x, float y, float size)
    {
        DrawLuxurySquirclePlate(
            d2d, x, y, size,
            new Color4(0.18f, 0.36f, 0.88f, 0.98f),
            new Color4(0.12f, 0.20f, 0.56f, 0.98f),
            new Color4(0.08f, 0.10f, 0.30f, 0.99f),
            new Color4(0.45f, 0.78f, 1.0f, 0.28f));

        // 1. Back-left slate window
        float w1 = size * 0.36f, h1 = size * 0.30f;
        float x1 = x + size * 0.14f, y1 = y + size * 0.18f;
        DrawRoundedRect(d2d, x1, y1 + 1.5f, w1, h1, size * 0.06f, new Color4(0f, 0f, 0f, 0.32f));
        FillRoundedRectLinearGradient(d2d, x1, y1, w1, h1, size * 0.06f,
            new Color4(0.54f, 0.82f, 0.98f, 0.78f), new Color4(0.28f, 0.46f, 0.76f, 0.82f));
        DrawRoundedRectBevelBorder(d2d, x1, y1, w1, h1, size * 0.06f, new Color4(1f, 1f, 1f, 0.65f), new Color4(1f, 1f, 1f, 0.15f), 0.8f);

        // 2. Back-right violet window
        float w2 = size * 0.36f, h2 = size * 0.30f;
        float x2 = x + size * 0.50f, y2 = y + size * 0.22f;
        DrawRoundedRect(d2d, x2, y2 + 1.5f, w2, h2, size * 0.06f, new Color4(0f, 0f, 0f, 0.32f));
        FillRoundedRectLinearGradient(d2d, x2, y2, w2, h2, size * 0.06f,
            new Color4(0.76f, 0.62f, 1.00f, 0.82f), new Color4(0.40f, 0.28f, 0.70f, 0.86f));
        DrawRoundedRectBevelBorder(d2d, x2, y2, w2, h2, size * 0.06f, new Color4(1f, 1f, 1f, 0.68f), new Color4(1f, 1f, 1f, 0.16f), 0.8f);

        // 3. Hero foreground active glass pane with ambient halo & traffic-light chrome
        float w3 = size * 0.56f, h3 = size * 0.44f;
        float x3 = x + size * 0.22f, y3 = y + size * 0.40f;
        FillEllipseRadialGradient(d2d, x3 + w3 * 0.5f, y3 + h3 * 0.5f, w3 * 0.7f, h3 * 0.7f,
            new Color4(0.30f, 0.86f, 1.0f, 0.34f), new Color4(0.30f, 0.86f, 1.0f, 0f));
        DrawRoundedRect(d2d, x3, y3 + 2.2f, w3, h3, size * 0.075f, new Color4(0f, 0f, 0f, 0.44f));
        FillRoundedRectLinearGradient(d2d, x3, y3, w3, h3, size * 0.075f,
            new Color4(0.96f, 0.99f, 1.00f, 0.95f), new Color4(0.68f, 0.86f, 0.98f, 0.88f));
        DrawRoundedRectBevelBorder(d2d, x3, y3, w3, h3, size * 0.075f, new Color4(1f, 1f, 1f, 0.95f), new Color4(0.30f, 0.86f, 1f, 0.6f), 1.1f);

        // Micro traffic lights + miniature content skeleton inside Hero Window
        float dotR = Math.Max(1.1f, size * 0.024f);
        float dy = y3 + size * 0.068f;
        using (var br = d2d.CreateSolidColorBrush(new Color4(1f, 0.37f, 0.34f, 0.95f)))
            d2d.FillEllipse(new Ellipse(new Vector2(x3 + size * 0.07f, dy), dotR, dotR), br);
        using (var by = d2d.CreateSolidColorBrush(new Color4(1f, 0.74f, 0.18f, 0.95f)))
            d2d.FillEllipse(new Ellipse(new Vector2(x3 + size * 0.13f, dy), dotR, dotR), by);
        using (var bg = d2d.CreateSolidColorBrush(new Color4(0.18f, 0.82f, 0.44f, 0.95f)))
            d2d.FillEllipse(new Ellipse(new Vector2(x3 + size * 0.19f, dy), dotR, dotR), bg);

        DrawRoundedRect(d2d, x3 + size * 0.07f, y3 + h3 * 0.34f, w3 * 0.36f, h3 * 0.48f, 2f, new Color4(0.16f, 0.42f, 0.84f, 0.28f));
        DrawRoundedRect(d2d, x3 + w3 * 0.50f, y3 + h3 * 0.34f, w3 * 0.38f, h3 * 0.18f, 1.5f, new Color4(0.12f, 0.24f, 0.48f, 0.32f));
        DrawRoundedRect(d2d, x3 + w3 * 0.50f, y3 + h3 * 0.60f, w3 * 0.28f, h3 * 0.18f, 1.5f, new Color4(0.12f, 0.24f, 0.48f, 0.24f));
    }

    public static void DrawLuxuryGlassLabIcon(ID2D1DeviceContext d2d, float x, float y, float size)
    {
        DrawLuxurySquirclePlate(
            d2d, x, y, size,
            new Color4(0.10f, 0.18f, 0.38f, 0.99f),
            new Color4(0.08f, 0.12f, 0.28f, 0.99f),
            new Color4(0.05f, 0.06f, 0.14f, 0.99f),
            new Color4(0.32f, 0.86f, 1.0f, 0.36f));

        float cx = x + size * 0.50f;
        float cy = y + size * 0.52f;
        float r = size * 0.34f;

        // Chromatic caustic halo behind prism (rose/violet on left, mint/cyan on right)
        FillEllipseRadialGradient(d2d, cx - r * 0.35f, cy, r * 0.9f, r * 0.9f,
            new Color4(0.76f, 0.32f, 1.0f, 0.36f), new Color4(0.76f, 0.32f, 1.0f, 0f));
        FillEllipseRadialGradient(d2d, cx + r * 0.35f, cy + r * 0.1f, r * 0.9f, r * 0.9f,
            new Color4(0.22f, 0.96f, 0.88f, 0.38f), new Color4(0.22f, 0.96f, 0.88f, 0f));

        // 3D Multi-Faceted Crystal Gem Geometry
        var apex = new Vector2(cx, cy - r * 1.04f);
        var topL = new Vector2(cx - r * 0.86f, cy - r * 0.22f);
        var topR = new Vector2(cx + r * 0.86f, cy - r * 0.22f);
        var midL = new Vector2(cx - r * 0.42f, cy - r * 0.14f);
        var midR = new Vector2(cx + r * 0.42f, cy - r * 0.14f);
        var botL = new Vector2(cx - r * 0.56f, cy + r * 0.88f);
        var botR = new Vector2(cx + r * 0.56f, cy + r * 0.88f);
        var keel = new Vector2(cx, cy + r * 0.96f);

        // Facet 1: Top-Left lit crown
        FillPolygonGradient(d2d, new[] { apex, topL, midL },
            new Color4(0.96f, 0.99f, 1.0f, 0.96f), new Color4(0.45f, 0.86f, 1.0f, 0.92f),
            apex, topL, new Color4(1f, 1f, 1f, 0.72f), 0.7f);
        // Facet 2: Center luminous table
        FillPolygonGradient(d2d, new[] { apex, midL, midR },
            new Color4(1f, 1f, 1f, 0.98f), new Color4(0.62f, 0.94f, 1.0f, 0.94f),
            apex, midR, new Color4(1f, 1f, 1f, 0.85f), 0.7f);
        // Facet 3: Top-Right teal-cyan crown
        FillPolygonGradient(d2d, new[] { apex, topR, midR },
            new Color4(0.45f, 0.95f, 0.94f, 0.95f), new Color4(0.18f, 0.58f, 0.96f, 0.92f),
            apex, topR, new Color4(1f, 1f, 1f, 0.72f), 0.7f);
        // Facet 4: Lower-Left amethyst-cobalt pavilion
        FillPolygonGradient(d2d, new[] { topL, botL, keel, midL },
            new Color4(0.42f, 0.64f, 1.0f, 0.90f), new Color4(0.32f, 0.22f, 0.72f, 0.94f),
            topL, botL, new Color4(1f, 1f, 1f, 0.50f), 0.7f);
        // Facet 5: Lower-Center refracted core
        FillPolygonGradient(d2d, new[] { midL, keel, midR },
            new Color4(0.68f, 0.96f, 1.0f, 0.92f), new Color4(0.18f, 0.52f, 0.92f, 0.94f),
            midL, keel, new Color4(1f, 1f, 1f, 0.75f), 0.75f);
        // Facet 6: Lower-Right deep sapphire pavilion
        FillPolygonGradient(d2d, new[] { midR, keel, botR, topR },
            new Color4(0.28f, 0.86f, 0.96f, 0.90f), new Color4(0.12f, 0.34f, 0.78f, 0.95f),
            topR, botR, new Color4(1f, 1f, 1f, 0.55f), 0.7f);

        // Star glint at top-right prism vertex
        FillEllipseRadialGradient(d2d, apex.X, apex.Y + r * 0.08f, r * 0.28f, r * 0.28f,
            new Color4(1f, 1f, 1f, 0.95f), new Color4(1f, 1f, 1f, 0f));
    }

    public static void DrawLuxuryFolderStackIcon(ID2D1DeviceContext d2d, float x, float y, float size)
    {
        DrawLuxurySquirclePlate(
            d2d, x, y, size,
            new Color4(0.18f, 0.52f, 0.96f, 0.98f),
            new Color4(0.11f, 0.34f, 0.78f, 0.98f),
            new Color4(0.08f, 0.18f, 0.48f, 0.99f),
            new Color4(0.55f, 0.88f, 1f, 0.25f));

        float fx = x + size * 0.16f;
        float fy = y + size * 0.24f;
        float fw = size * 0.68f;
        float fh = size * 0.54f;

        // Back folder plate with sculpted tab
        FillRoundedRectLinearGradient(d2d, fx, fy, fw * 0.42f, fh * 0.26f, size * 0.05f,
            new Color4(0.45f, 0.82f, 1f, 0.95f), new Color4(0.22f, 0.58f, 0.94f, 0.95f));
        FillRoundedRectLinearGradient(d2d, fx, fy + fh * 0.12f, fw, fh * 0.84f, size * 0.07f,
            new Color4(0.42f, 0.78f, 1f, 0.92f), new Color4(0.18f, 0.46f, 0.86f, 0.96f));

        // Peeking frosted document sheets fanned at top edge
        DrawRoundedRect(d2d, fx + fw * 0.10f, fy + fh * 0.06f, fw * 0.80f, fh * 0.34f, size * 0.04f,
            new Color4(1f, 1f, 1f, 0.85f), new Color4(1f, 1f, 1f, 0.95f), 0.7f);
        DrawRoundedRect(d2d, fx + fw * 0.16f, fy + fh * 0.12f, fw * 0.72f, fh * 0.34f, size * 0.04f,
            new Color4(0.92f, 0.97f, 1f, 0.96f));

        // Glossy glass front folder pocket with drop shadow and bevel highlight
        float pY = fy + fh * 0.24f;
        float pH = fh * 0.74f;
        DrawRoundedRect(d2d, fx, pY + 2f, fw, pH, size * 0.075f, new Color4(0f, 0f, 0f, 0.30f));
        FillRoundedRectLinearGradient(d2d, fx, pY, fw, pH, size * 0.075f,
            new Color4(0.62f, 0.92f, 1f, 0.94f), new Color4(0.22f, 0.58f, 0.96f, 0.96f));
        DrawRoundedRectBevelBorder(d2d, fx, pY, fw, pH, size * 0.075f,
            new Color4(1f, 1f, 1f, 0.90f), new Color4(1f, 1f, 1f, 0.22f), 0.95f);

        // Engraved subtle lines on front pocket
        DrawRoundedRect(d2d, fx + fw * 0.28f, pY + pH * 0.45f, fw * 0.44f, Math.Max(1.6f, size * 0.036f), 1.5f,
            new Color4(1f, 1f, 1f, 0.42f));
    }

    // ---- Upgraded Precision Direct2D Vector Iconography ----

    public static void DrawCrystalEmblem(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        float r = size * 0.50f;
        var apex = new Vector2(cx, cy - r * 0.98f);
        var topL = new Vector2(cx - r * 0.84f, cy - r * 0.18f);
        var topR = new Vector2(cx + r * 0.84f, cy - r * 0.18f);
        var midL = new Vector2(cx - r * 0.34f, cy - r * 0.12f);
        var midR = new Vector2(cx + r * 0.34f, cy - r * 0.12f);
        var keel = new Vector2(cx, cy + r * 0.94f);
        var botL = new Vector2(cx - r * 0.50f, cy + r * 0.82f);
        var botR = new Vector2(cx + r * 0.50f, cy + r * 0.82f);

        FillPolygonGradient(d2d, new[] { apex, topL, botL, keel, botR, topR },
            new Color4(color.R, color.G, color.B, color.A * 0.38f),
            new Color4(color.R, color.G, color.B, color.A * 0.14f),
            apex, keel, color, 1.25f);
        FillPolygonGradient(d2d, new[] { apex, midL, keel, midR },
            new Color4(1f, 1f, 1f, color.A * 0.45f),
            new Color4(color.R, color.G, color.B, color.A * 0.20f),
            apex, keel);
        using var edgeB = d2d.CreateSolidColorBrush(new Color4(color.R, color.G, color.B, color.A * 0.72f));
        d2d.DrawLine(topL, topR, edgeB, 0.9f);
        d2d.DrawLine(apex, midL, edgeB, 0.8f);
        d2d.DrawLine(apex, midR, edgeB, 0.8f);
    }

    public static void DrawWifiIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, bool connected)
    {
        Color4 c = connected ? color : new Color4(color.R, color.G, color.B, 0.35f);
        using var brush = d2d.CreateSolidColorBrush(c);
        float r = size * 0.45f;
        float yBase = cy + r * 0.58f;
        d2d.FillEllipse(new Ellipse(new Vector2(cx, yBase), 1.6f, 1.6f), brush);
        for (int i = 1; i <= 3; i++)
        {
            float rad = r * (i * 0.35f);
            DrawArcSegments(d2d, cx, yBase, rad, -135f * MathF.PI / 180f, -45f * MathF.PI / 180f, c, 1.45f, 12);
        }
    }

    public static void DrawEthernetIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, bool up)
    {
        Color4 c = up ? color : new Color4(color.R, color.G, color.B, 0.4f);
        using var brush = d2d.CreateSolidColorBrush(c);
        float s = size * 0.40f;
        FillRoundedRectLinearGradient(d2d, cx - s, cy - s * 0.82f, s * 2f, s * 1.42f, 2f,
            new Color4(c.R, c.G, c.B, c.A * 0.26f), new Color4(c.R, c.G, c.B, c.A * 0.08f));
        d2d.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(cx - s, cy - s * 0.82f, cx + s, cy + s * 0.60f), 2f, 2f), brush, 1.35f);
        d2d.DrawLine(new Vector2(cx - s * 0.45f, cy - s * 0.40f), new Vector2(cx - s * 0.45f, cy + s * 0.12f), brush, 1.35f);
        d2d.DrawLine(new Vector2(cx, cy - s * 0.40f), new Vector2(cx, cy + s * 0.12f), brush, 1.35f);
        d2d.DrawLine(new Vector2(cx + s * 0.45f, cy - s * 0.40f), new Vector2(cx + s * 0.45f, cy + s * 0.12f), brush, 1.35f);
    }

    public static void DrawBluetoothIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, bool active)
    {
        using var brush = d2d.CreateSolidColorBrush(active ? color : new Color4(color.R, color.G, color.B, 0.38f));
        float h = size * 0.44f;
        float w = size * 0.26f;
        d2d.DrawLine(new Vector2(cx, cy - h), new Vector2(cx, cy + h), brush, 1.45f);
        d2d.DrawLine(new Vector2(cx, cy - h), new Vector2(cx + w, cy - h * 0.42f), brush, 1.45f);
        d2d.DrawLine(new Vector2(cx + w, cy - h * 0.42f), new Vector2(cx - w * 0.8f, cy + h * 0.42f), brush, 1.45f);
        d2d.DrawLine(new Vector2(cx, cy + h), new Vector2(cx + w, cy + h * 0.42f), brush, 1.45f);
        d2d.DrawLine(new Vector2(cx + w, cy + h * 0.42f), new Vector2(cx - w * 0.8f, cy - h * 0.42f), brush, 1.45f);
    }

    public static void DrawVolumeIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, float level, bool muted)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float s = size * 0.42f;
        var cone = new[]
        {
            new Vector2(cx - s * 0.90f, cy - s * 0.34f),
            new Vector2(cx - s * 0.38f, cy - s * 0.34f),
            new Vector2(cx + s * 0.22f, cy - s * 0.78f),
            new Vector2(cx + s * 0.22f, cy + s * 0.78f),
            new Vector2(cx - s * 0.38f, cy + s * 0.34f),
            new Vector2(cx - s * 0.90f, cy + s * 0.34f)
        };
        FillPolygonGradient(d2d, cone, new Color4(color.R, color.G, color.B, color.A * 0.92f), new Color4(color.R, color.G, color.B, color.A * 0.62f),
            new Vector2(cx - s, cy - s), new Vector2(cx, cy + s));

        if (muted || level <= 0.01f)
        {
            d2d.DrawLine(new Vector2(cx + s * 0.48f, cy - s * 0.32f), new Vector2(cx + s * 0.98f, cy + s * 0.32f), brush, 1.5f);
            d2d.DrawLine(new Vector2(cx + s * 0.98f, cy - s * 0.32f), new Vector2(cx + s * 0.48f, cy + s * 0.32f), brush, 1.5f);
        }
        else
        {
            DrawArcSegments(d2d, cx + s * 0.16f, cy, s * 0.56f, -0.78f, 0.78f, color, 1.4f, 10);
            if (level > 0.45f)
                DrawArcSegments(d2d, cx + s * 0.16f, cy, s * 0.90f, -0.82f, 0.82f, new Color4(color.R, color.G, color.B, color.A * 0.72f), 1.4f, 12);
        }
    }

    public static void DrawBatteryIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, int percent, bool hasBattery)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float w = size * 0.95f;
        float h = size * 0.54f;
        float x = cx - w * 0.5f;
        float y = cy - h * 0.5f;
        d2d.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(x, y, x + w, y + h), 2.8f, 2.8f), brush, 1.3f);
        d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(x + w + 1.1f, cy - h * 0.22f, x + w + 2.9f, cy + h * 0.22f), 0.8f, 0.8f), brush);
        float fillPct = hasBattery ? Math.Clamp(percent / 100f, 0.08f, 1f) : 1f;
        Color4 fillCol = (hasBattery && percent <= 20) ? new Color4(1f, 0.35f, 0.35f, 1f) : new Color4(0.26f, 0.88f, 0.54f, 0.95f);
        FillRoundedRectLinearGradient(d2d, x + 2.1f, y + 2.1f, (w - 4.2f) * fillPct, h - 4.2f, 1.4f,
            fillCol, new Color4(fillCol.R * 0.75f, fillCol.G * 0.85f, fillCol.B * 0.85f, 0.95f));
    }

    public static void DrawControlCenterIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        float w = size * 0.86f;
        float rH = size * 0.28f;
        DrawRoundedRect(d2d, cx - w * 0.5f, cy - rH - 1.5f, w, rH, rH * 0.5f, new Color4(color.R, color.G, color.B, 0.16f), color, 1.15f);
        using var knobB = d2d.CreateSolidColorBrush(color);
        d2d.FillEllipse(new Ellipse(new Vector2(cx + w * 0.22f, cy - rH * 0.5f - 1.5f), rH * 0.34f, rH * 0.34f), knobB);
        DrawRoundedRect(d2d, cx - w * 0.5f, cy + 1.5f, w, rH, rH * 0.5f, new Color4(color.R, color.G, color.B, 0.36f), color, 1.15f);
        d2d.FillEllipse(new Ellipse(new Vector2(cx - w * 0.22f, cy + rH * 0.5f + 1.5f), rH * 0.34f, rH * 0.34f), knobB);
    }

    public static void DrawSearchIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        float r = size * 0.33f;
        float lx = cx - r * 0.24f, ly = cy - r * 0.24f;
        FillEllipseRadialGradient(d2d, lx, ly, r, r,
            new Color4(color.R, color.G, color.B, 0.26f), new Color4(color.R, color.G, color.B, 0.04f));
        using var brush = d2d.CreateSolidColorBrush(color);
        d2d.DrawEllipse(new Ellipse(new Vector2(lx, ly), r, r), brush, 1.7f);
        using var capStyle = d2d.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        d2d.DrawLine(new Vector2(cx + r * 0.48f, cy + r * 0.48f), new Vector2(cx + r * 1.22f, cy + r * 1.22f), brush, 2.2f, capStyle);
    }

    public static void DrawBellIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        float r = size * 0.36f;
        var bellPts = new[]
        {
            new Vector2(cx, cy - r * 1.05f),
            new Vector2(cx + r * 0.72f, cy - r * 0.45f),
            new Vector2(cx + r * 0.88f, cy + r * 0.48f),
            new Vector2(cx + r * 1.15f, cy + r * 0.74f),
            new Vector2(cx - r * 1.15f, cy + r * 0.74f),
            new Vector2(cx - r * 0.88f, cy + r * 0.48f),
            new Vector2(cx - r * 0.72f, cy - r * 0.45f)
        };
        FillPolygonGradient(d2d, bellPts, new Color4(color.R, color.G, color.B, color.A * 0.45f),
            new Color4(color.R, color.G, color.B, color.A * 0.16f),
            new Vector2(cx, cy - r), new Vector2(cx, cy + r), color, 1.35f);
        using var brush = d2d.CreateSolidColorBrush(color);
        d2d.FillEllipse(new Ellipse(new Vector2(cx, cy + r * 1.08f), 1.8f, 1.8f), brush);
    }

    public static void DrawExposeGridIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        // Crisp asymmetric Mission Control panes with clear breathing gutters (reads sharply even at 13px)
        float halfW = size * 0.46f;
        float halfH = size * 0.40f;
        float g = Math.Max(1.8f, size * 0.14f);
        float left = cx - halfW, top = cy - halfH, right = cx + halfW, bot = cy + halfH;
        Color4 fill = new(color.R, color.G, color.B, 0.24f);
        Color4 fillHero = new(color.R, color.G, color.B, 0.56f);

        DrawRoundedRect(d2d, left, top, halfW - g * 0.5f, halfH - g * 0.5f, 1.4f, fill, color, 1.05f);
        DrawRoundedRect(d2d, cx + g * 0.5f, top, halfW - g * 0.5f, halfH - g * 0.5f, 1.4f, fill, color, 1.05f);
        DrawRoundedRect(d2d, left, cy + g * 0.5f, halfW * 1.18f, halfH - g * 0.5f, 1.4f, fillHero, color, 1.15f);
        DrawRoundedRect(d2d, left + halfW * 1.18f + g, cy + g * 0.5f, (right - (left + halfW * 1.18f + g)), halfH - g * 0.5f, 1.4f, fill, color, 1.05f);
    }

    public static void DrawDesktopHomeIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        float w = size * 0.48f;
        float h = size * 0.34f;
        FillRoundedRectLinearGradient(d2d, cx - w, cy - h - size * 0.06f, w * 2f, h * 2f, 2.6f,
            new Color4(color.R, color.G, color.B, 0.38f), new Color4(color.R, color.G, color.B, 0.14f));
        using var brush = d2d.CreateSolidColorBrush(color);
        d2d.DrawRoundedRectangle(new RoundedRectangle(new RawRectF(cx - w, cy - h - size * 0.06f, cx + w, cy + h - size * 0.06f), 2.6f, 2.6f), brush, 1.35f);
        // Inner glowing dock pill + stand
        d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(cx - w * 0.56f, cy + h - size * 0.19f, cx + w * 0.56f, cy + h - size * 0.12f), 1f, 1f), brush);
        d2d.DrawLine(new Vector2(cx, cy + h - size * 0.05f), new Vector2(cx, cy + h + size * 0.10f), brush, 1.5f);
        d2d.DrawLine(new Vector2(cx - w * 0.42f, cy + h + size * 0.11f), new Vector2(cx + w * 0.42f, cy + h + size * 0.11f), brush, 1.5f);
    }

    public static void DrawSunIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        FillEllipseRadialGradient(d2d, cx, cy, size * 0.48f, size * 0.48f,
            new Color4(color.R, color.G, color.B, 0.34f), new Color4(color.R, color.G, color.B, 0f));
        using var brush = d2d.CreateSolidColorBrush(color);
        float r = size * 0.24f;
        d2d.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), brush);
        using var cap = d2d.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4f;
            float r1 = size * 0.35f;
            float r2 = size * 0.49f;
            d2d.DrawLine(new Vector2(cx + MathF.Cos(a) * r1, cy + MathF.Sin(a) * r1),
                         new Vector2(cx + MathF.Cos(a) * r2, cy + MathF.Sin(a) * r2), brush, 1.5f, cap);
        }
    }

    public static void DrawMoonIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        float r = size * 0.40f;
        using var path = d2d.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            // Clean crescent moon using point-sampled arcs
            const int segs = 20;
            var pts = new List<Vector2>();
            for (int i = 0; i <= segs; i++)
            {
                float a = (-65f + i * (255f / segs)) * MathF.PI / 180f;
                pts.Add(new Vector2(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r));
            }
            for (int i = segs; i >= 0; i--)
            {
                float t = i / (float)segs;
                float a = (-65f + t * 255f) * MathF.PI / 180f;
                float innerR = r * (0.48f + 0.52f * MathF.Abs(t - 0.5f) * 2f);
                pts.Add(new Vector2(cx + r * 0.36f + MathF.Cos(a) * (r * 0.72f), cy - r * 0.22f + MathF.Sin(a) * (r * 0.72f)));
                _ = innerR;
            }
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i <= segs; i++) sink.AddLine(pts[i]);
            for (int i = segs; i >= 0; i--)
            {
                float a = (-55f + (i / (float)segs) * 235f) * MathF.PI / 180f;
                sink.AddLine(new Vector2(cx + r * 0.38f + MathF.Cos(a) * (r * 0.78f), cy - r * 0.18f + MathF.Sin(a) * (r * 0.78f)));
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        var stops = new[] { new GradientStop(0f, new Color4(1f, 1f, 1f, 0.96f)), new GradientStop(1f, color) };
        using var col = d2d.CreateGradientStopCollection(stops);
        using var brush = d2d.CreateLinearGradientBrush(new LinearGradientBrushProperties
        {
            StartPoint = new Vector2(cx - r, cy - r),
            EndPoint = new Vector2(cx + r, cy + r)
        }, col);
        d2d.FillGeometry(path, brush);
    }

    public static void DrawPlayIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float r = size * 0.38f;
        using var path = d2d.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2(cx - r * 0.65f, cy - r * 0.85f), FigureBegin.Filled);
            sink.AddLine(new Vector2(cx + r * 0.95f, cy));
            sink.AddLine(new Vector2(cx - r * 0.65f, cy + r * 0.85f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        d2d.FillGeometry(path, brush);
    }

    public static void DrawPauseIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float h = size * 0.36f;
        float w = size * 0.14f;
        float gap = size * 0.14f;
        d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(cx - gap - w, cy - h, cx - gap, cy + h), 1f, 1f), brush);
        d2d.FillRoundedRectangle(new RoundedRectangle(new RawRectF(cx + gap, cy - h, cx + gap + w, cy + h), 1f, 1f), brush);
    }

    public static void DrawSkipIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, bool next)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float r = size * 0.32f;
        float dir = next ? 1f : -1f;
        using var path = d2d.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            sink.BeginFigure(new Vector2(cx - dir * r * 0.8f, cy - r * 0.75f), FigureBegin.Filled);
            sink.AddLine(new Vector2(cx + dir * r * 0.45f, cy));
            sink.AddLine(new Vector2(cx - dir * r * 0.8f, cy + r * 0.75f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        d2d.FillGeometry(path, brush);
        float barX = cx + dir * r * 0.65f;
        d2d.DrawLine(new Vector2(barX, cy - r * 0.72f), new Vector2(barX, cy + r * 0.72f), brush, 2f);
    }

    public static void DrawFolderIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        float w = size * 0.78f;
        float h = size * 0.58f;
        float x = cx - w * 0.5f;
        float y = cy - h * 0.45f;
        FillRoundedRectLinearGradient(d2d, x, y - h * 0.16f, w * 0.44f, h * 0.36f, 2f, color, new Color4(color.R * 0.7f, color.G * 0.7f, color.B * 0.8f, color.A));
        FillRoundedRectLinearGradient(d2d, x, y, w, h, 3.5f, color, new Color4(color.R * 0.65f, color.G * 0.72f, color.B * 0.90f, color.A));
        DrawRoundedRectBevelBorder(d2d, x, y, w, h, 3.5f, new Color4(1f, 1f, 1f, 0.55f), new Color4(1f, 1f, 1f, 0.10f), 0.7f);
    }

    public static void DrawRefreshIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float r = size * 0.34f;
        DrawArcSegments(d2d, cx, cy, r, -2.4f, 2.2f, color, 1.55f, 16);
        d2d.DrawLine(new Vector2(cx + r * 0.35f, cy - r * 1.10f), new Vector2(cx + r * 1.05f, cy - r * 0.45f), brush, 1.6f);
    }

    public static void DrawCloseXIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float r = size * 0.32f;
        d2d.DrawLine(new Vector2(cx - r, cy - r), new Vector2(cx + r, cy + r), brush, 1.6f);
        d2d.DrawLine(new Vector2(cx + r, cy - r), new Vector2(cx - r, cy + r), brush, 1.6f);
    }

    public static void DrawStarIcon(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, bool filled)
    {
        using var brush = d2d.CreateSolidColorBrush(color);
        float rOuter = size * 0.44f;
        float rInner = size * 0.20f;
        using var path = d2d.Factory.CreatePathGeometry();
        using (var sink = path.Open())
        {
            for (int i = 0; i < 10; i++)
            {
                float r = (i % 2 == 0) ? rOuter : rInner;
                float a = -MathF.PI * 0.5f + i * MathF.PI / 5f;
                var pt = new Vector2(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
                if (i == 0) sink.BeginFigure(pt, filled ? FigureBegin.Filled : FigureBegin.Hollow);
                else sink.AddLine(pt);
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        if (filled) d2d.FillGeometry(path, brush);
        else d2d.DrawGeometry(path, brush, 1.3f);
    }

    public static void DrawWeatherGlyph(ID2D1DeviceContext d2d, float cx, float cy, float size, int wmoCode, UiPalette pal)
    {
        if (wmoCode is 0 or 1)
        {
            FillEllipseRadialGradient(d2d, cx, cy, size * 0.72f, size * 0.72f,
                new Color4(1.0f, 0.76f, 0.24f, 0.44f), new Color4(1.0f, 0.60f, 0.14f, 0f));
            FillEllipseRadialGradient(d2d, cx, cy, size * 0.30f, size * 0.30f,
                new Color4(1.0f, 0.94f, 0.62f, 1f), new Color4(1.0f, 0.62f, 0.12f, 0.98f), -size * 0.08f, -size * 0.08f);
            return;
        }
        // Subtle glowing sun behind layered cloud for partly cloudy
        if (wmoCode == 2)
        {
            float sx = cx - size * 0.16f, sy = cy - size * 0.18f;
            FillEllipseRadialGradient(d2d, sx, sy, size * 0.44f, size * 0.44f,
                new Color4(1.0f, 0.78f, 0.28f, 0.38f), new Color4(1.0f, 0.58f, 0.12f, 0f));
            FillEllipseRadialGradient(d2d, sx, sy, size * 0.22f, size * 0.22f,
                new Color4(1.0f, 0.94f, 0.62f, 1f), new Color4(1.0f, 0.62f, 0.12f, 0.98f));
        }

        float w = size * 0.74f;
        float h = size * 0.32f;
        // Soft cloud drop shadow
        FillEllipseRadialGradient(d2d, cx, cy + h * 0.9f, w * 0.48f, h * 0.40f,
            new Color4(0f, 0f, 0f, 0.24f), new Color4(0f, 0f, 0f, 0f));
        // Shaded cloud dome
        FillEllipseRadialGradient(d2d, cx - w * 0.14f, cy - size * 0.02f, size * 0.22f, size * 0.22f,
            new Color4(1f, 1f, 1f, 0.98f), new Color4(0.78f, 0.86f, 0.96f, 0.94f), 0f, -size * 0.08f);
        FillEllipseRadialGradient(d2d, cx + w * 0.15f, cy + size * 0.04f, size * 0.17f, size * 0.17f,
            new Color4(1f, 1f, 1f, 0.98f), new Color4(0.76f, 0.84f, 0.95f, 0.94f), 0f, -size * 0.05f);
        FillRoundedRectLinearGradient(d2d, cx - w * 0.46f, cy + 1f, w * 0.92f, h, h * 0.48f,
            new Color4(0.98f, 0.99f, 1f, 0.98f), new Color4(0.74f, 0.84f, 0.96f, 0.94f));

        if (wmoCode >= 51)
        {
            using var rain = d2d.CreateSolidColorBrush(pal.Accent);
            using var cap = d2d.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
            for (int i = -1; i <= 1; i++)
                d2d.DrawLine(new Vector2(cx + i * 6.5f, cy + h + 3f), new Vector2(cx + i * 6.5f - 2.4f, cy + h + 9.5f), rain, 1.8f, cap);
        }
    }

    public static void DrawSnapTileGlyph(ID2D1DeviceContext d2d, float cx, float cy, float size, Color4 color, int mode)
    {
        float w = size * 0.84f;
        float h = size * 0.60f;
        float x = cx - w * 0.5f;
        float y = cy - h * 0.5f;
        DrawRoundedRect(d2d, x, y, w, h, 2.4f, new Color4(0f, 0f, 0f, 0.25f), new Color4(color.R, color.G, color.B, 0.55f), 1.1f);
        var activeFill = new Color4(color.R, color.G, color.B, 0.78f);
        var activeRim = new Color4(1f, 1f, 1f, 0.65f);
        switch (mode)
        {
            case 0:
                FillRoundedRectLinearGradient(d2d, x + 2f, y + 2f, w * 0.46f, h - 4f, 1.4f, activeFill, new Color4(activeFill.R * 0.7f, activeFill.G * 0.7f, activeFill.B * 0.8f, 0.6f));
                DrawRoundedRect(d2d, x + 2f, y + 2f, w * 0.46f, h - 4f, 1.4f, default, activeRim, 0.6f);
                break;
            case 1:
                FillRoundedRectLinearGradient(d2d, cx + 1f, y + 2f, w * 0.46f, h - 4f, 1.4f, activeFill, new Color4(activeFill.R * 0.7f, activeFill.G * 0.7f, activeFill.B * 0.8f, 0.6f));
                DrawRoundedRect(d2d, cx + 1f, y + 2f, w * 0.46f, h - 4f, 1.4f, default, activeRim, 0.6f);
                break;
            case 2:
                FillRoundedRectLinearGradient(d2d, x + 2f, y + 2f, w - 4f, h - 4f, 1.6f, activeFill, new Color4(activeFill.R * 0.7f, activeFill.G * 0.7f, activeFill.B * 0.8f, 0.6f));
                DrawRoundedRect(d2d, x + 2f, y + 2f, w - 4f, h - 4f, 1.6f, default, activeRim, 0.6f);
                break;
            default:
                FillRoundedRectLinearGradient(d2d, x + w * 0.16f, y + h * 0.16f, w * 0.68f, h * 0.68f, 1.6f, activeFill, new Color4(activeFill.R * 0.7f, activeFill.G * 0.7f, activeFill.B * 0.8f, 0.6f));
                DrawRoundedRect(d2d, x + w * 0.16f, y + h * 0.16f, w * 0.68f, h * 0.68f, 1.6f, default, activeRim, 0.6f);
                break;
        }
    }

    public static ID2D1Bitmap? CreateBitmapFromBgra(ID2D1DeviceContext d2d, int width, int height, byte[] bgraPixels)
    {
        if (width <= 0 || height <= 0 || bgraPixels.Length < width * height * 4) return null;
        var props = new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        unsafe
        {
            fixed (byte* p = bgraPixels)
            {
                return d2d.CreateBitmap(new SizeI(width, height), (nint)p, width * 4, props);
            }
        }
    }

    public static void DrawBitmapRect(ID2D1DeviceContext d2d, ID2D1Bitmap bmp, float x, float y, float w, float h, float opacity = 1f, bool clipRoundedTile = false)
    {
        var dest = new RawRectF(x, y, x + w, y + h);
        if (!clipRoundedTile)
        {
            d2d.DrawBitmap(bmp, dest, opacity, BitmapInterpolationMode.Linear, null);
            return;
        }

        float r = Math.Min(w, h) * 0.24f;
        using var bmpBrush = d2d.CreateBitmapBrush(bmp, new BitmapBrushProperties1
        {
            ExtendModeX = ExtendMode.Clamp,
            ExtendModeY = ExtendMode.Clamp,
            InterpolationMode = InterpolationMode.Linear
        });
        bmpBrush.Opacity = opacity;
        bmpBrush.Transform = Matrix3x2.CreateScale(w / bmp.Size.Width, h / bmp.Size.Height) * Matrix3x2.CreateTranslation(x, y);
        d2d.FillRoundedRectangle(new RoundedRectangle(dest, r, r), bmpBrush);
    }
}
