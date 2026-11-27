using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Rendering.Core;
using Xunit;

namespace ProjectCrystal.Tests;

public sealed class OpticalRefractionTests
{
    private static byte[] MakeHighContrastPattern(int w, int h, int textShiftX)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 20, 30, 55));
            using var pen = new Pen(Color.White, 3f);
            for (int x = (textShiftX % 40); x < w; x += 40)
                g.DrawLine(pen, x, 0, x, h);
            using var font = new Font("Segoe UI", 24f, FontStyle.Bold);
            using var brush = new SolidBrush(Color.Cyan);
            g.DrawString("OPTICAL REFRACTION TEST LINE", font, brush, 60 + textShiftX, 88);
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] outBytes = new byte[w * h * 4];
        Marshal.Copy(bd.Scan0, outBytes, 0, outBytes.Length);
        bmp.UnlockBits(bd);
        return outBytes;
    }

    private static double MeanAbsDiff(byte[] a, byte[] b, int w, int h, Rectangle region)
    {
        double sum = 0;
        long count = 0;
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                int idx = (y * w + x) * 4;
                sum += Math.Abs(a[idx] - b[idx]) + Math.Abs(a[idx + 1] - b[idx + 1]) + Math.Abs(a[idx + 2] - b[idx + 2]);
                count += 3;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    [Fact]
    public void LiquidGlassShader_RefractsDisplacesAndUpdatesDynamicallyOverMovingText()
    {
        using var gpu = new GpuDeviceContext();
        using var surface = new LiquidGlassSurface(gpu, 0, 600, 220);
        var shape = new[] { new GlassShapeItem { X = 40, Y = 30, Width = 520, Height = 160, CornerRadius = 30, SquirclePower = 3.2f } };

        var cfgRefract = ConfigManager.CreateDefaults();
        cfgRefract.Glass.CaptureBackend = CaptureBackendMode.WallpaperComposited;
        cfgRefract.Glass.BlurRadius = 0f; // Pure optical refraction without blur!
        cfgRefract.Glass.RefractionIntensity = 1.4f;
        cfgRefract.Glass.DistortionStrength = 1.25f;
        cfgRefract.Glass.Magnification = 1.12f;

        var cfgFlat = cfgRefract.Clone();
        cfgFlat.Glass.RefractionIntensity = 0f;
        cfgFlat.Glass.DistortionStrength = 0f;
        cfgFlat.Glass.Magnification = 1.0f;
        cfgFlat.Glass.ChromaticAberration = 0f;

        byte[] bgFrame1 = MakeHighContrastPattern(600, 220, textShiftX: 0);
        gpu.UpdateFallbackBackdrop(bgFrame1, 600, 220);

        surface.RenderFrame(null, cfgFlat, new Point(0, 0), new PointF(300, 110), shape, 0f, true, null);
        byte[] flatOutput = surface.CaptureOffscreenBgra();

        surface.RenderFrame(null, cfgRefract, new Point(0, 0), new PointF(300, 110), shape, 0f, true, null);
        byte[] refractOutput1 = surface.CaptureOffscreenBgra();

        // 1. Proves refraction+magnification differs significantly from flat transparent glass (NOT plain transparency/blur)
        double refractVsFlat = MeanAbsDiff(refractOutput1, flatOutput, 600, 220, new Rectangle(46, 36, 508, 148));
        Assert.True(refractVsFlat > 6.0, $"Expected clear refractive pixel displacement vs flat glass, got MeanAbsDiff={refractVsFlat:F2}");

        // 2. Move underlying text dynamically and verify refracted output updates
        byte[] bgFrame2 = MakeHighContrastPattern(600, 220, textShiftX: 22);
        gpu.UpdateFallbackBackdrop(bgFrame2, 600, 220);
        surface.RenderFrame(null, cfgRefract, new Point(0, 0), new PointF(300, 110), shape, 0.25f, true, null);
        byte[] refractOutput2 = surface.CaptureOffscreenBgra();

        double frameDelta = MeanAbsDiff(refractOutput1, refractOutput2, 600, 220, new Rectangle(46, 36, 508, 148));
        Assert.True(frameDelta > 5.0, $"Expected dynamic refraction update when underlying text moves, got MeanAbsDiff={frameDelta:F2}");
    }
}
