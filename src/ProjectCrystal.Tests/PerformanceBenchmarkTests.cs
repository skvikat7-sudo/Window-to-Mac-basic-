using System.Diagnostics;
using System.Drawing;
using ProjectCrystal.Core.Configuration;
using ProjectCrystal.Rendering.Core;
using Xunit;

namespace ProjectCrystal.Tests;

public sealed class PerformanceBenchmarkTests
{
    [Fact]
    public void LiquidGlassShader_RendersUnderRealTimeFrameBudget()
    {
        using var gpu = new GpuDeviceContext();
        using var dockSurf = new LiquidGlassSurface(gpu, 0, 760, 100);
        using var ccSurf = new LiquidGlassSurface(gpu, 0, 392, 566);

        var cfg = ConfigManager.CreateDefaults();
        cfg.Glass.CaptureBackend = CaptureBackendMode.WallpaperComposited;

        var dockShape = new[] { new GlassShapeItem { X = 10, Y = 10, Width = 740, Height = 80, CornerRadius = 24 } };
        var ccShapes = new[]
        {
            new GlassShapeItem { X = 4, Y = 4, Width = 384, Height = 558, CornerRadius = 24 },
            new GlassShapeItem { X = 16, Y = 16, Width = 174, Height = 126, CornerRadius = 18 },
            new GlassShapeItem { X = 200, Y = 16, Width = 176, Height = 126, CornerRadius = 18 },
            new GlassShapeItem { X = 16, Y = 152, Width = 360, Height = 66, CornerRadius = 16 },
            new GlassShapeItem { X = 16, Y = 228, Width = 360, Height = 98, CornerRadius = 16 }
        };

        // Warmup
        dockSurf.RenderFrame(null, cfg, new Point(0, 0), new PointF(200, 50), dockShape, 0f, true, null);
        ccSurf.RenderFrame(null, cfg, new Point(0, 0), new PointF(200, 200), ccShapes, 0f, true, null);

        var sw = Stopwatch.StartNew();
        const int frames = 100;
        for (int i = 0; i < frames; i++)
        {
            dockSurf.RenderFrame(null, cfg, new Point(0, 0), new PointF(200, 50), dockShape, i * 0.016f, true, null);
            ccSurf.RenderFrame(null, cfg, new Point(0, 0), new PointF(200, 200), ccShapes, i * 0.016f, true, null);
        }
        sw.Stop();

        double msPerCombinedTick = sw.Elapsed.TotalMilliseconds / frames;
        Assert.True(msPerCombinedTick < 16.0, $"Expected combined Dock+ControlCenter GPU render <16ms (60Hz budget), measured {msPerCombinedTick:F2} ms");
    }
}
