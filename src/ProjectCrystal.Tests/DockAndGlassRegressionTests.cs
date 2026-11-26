using System.Drawing;
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
using ProjectCrystal.Shell;
using ProjectCrystal.Shell.Surfaces;
using Xunit;

namespace ProjectCrystal.Tests;

public sealed class DockAndGlassRegressionTests
{
    [Fact]
    public void ConfigManager_MigratesV2ConfigToV3_EnablesDockAndResponsiveEdgeTrigger()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ProjectCrystalV3Mig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string v2Json = """
            {
              "SchemaVersion": 2,
              "Glass": { "Preset": "Custom", "TintOpacity": 0, "BlurRadius": 0 },
              "Dock": { "Enabled": false, "Position": "Right", "AutoHide": true, "RevealSensitivityPx": 3, "AutoHideDelayMs": 600 },
              "MenuBar": { "Enabled": true, "AutoHide": true, "RevealSensitivityPx": 3 }
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "config.json"), v2Json);

            var mgr = new ConfigManager(tempDir);
            Assert.Equal(3, mgr.Current.SchemaVersion);
            Assert.True(mgr.Current.Dock.Enabled);
            Assert.Equal(DockPosition.Bottom, mgr.Current.Dock.Position);
            Assert.True(mgr.Current.Dock.AutoHide);
            Assert.True(mgr.Current.Dock.RevealSensitivityPx >= 8);
            Assert.True(mgr.Current.MenuBar.RevealSensitivityPx >= 6);
            Assert.Equal(GlassMaterialPreset.CrystalClear, mgr.Current.Glass.Preset);
            Assert.True(mgr.Current.Glass.TintOpacity > 0f);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void WgcCapture_SynchronizedWithDeviceLockAndMultiRender_NeverTurnsBlack()
    {
        Exception? threadEx = null;
        var sta = new Thread(() =>
        {
            try
            {
                using var gpu = new GpuDeviceContext();
                var cfg = ConfigManager.CreateDefaults();
                cfg.Glass.CaptureBackend = CaptureBackendMode.LiveDesktopWgc;
                cfg.Appearance.Theme = ThemeAppearance.Dark;

                var hMon = Win32Interop.MonitorFromPoint(new Win32Interop.POINT { X = 10, Y = 10 }, 2);
                using var wgc = new WgcDesktopCaptureService(gpu.D3DDevice, gpu.D3DContext, hMon, gpu.DeviceLock);
                wgc.StartAsync().GetAwaiter().GetResult();

                using var surfA = new LiquidGlassSurface(gpu, 0, 480, 140);
                using var surfB = new LiquidGlassSurface(gpu, 0, 320, 120);
                var shapeA = new[] { new GlassShapeItem { X = 12, Y = 12, Width = 456, Height = 116, CornerRadius = 24, SquirclePower = 3.2f } };
                var shapeB = new[] { new GlassShapeItem { X = 10, Y = 10, Width = 300, Height = 100, CornerRadius = 20, SquirclePower = 3.2f } };

                int blankCenterCount = 0;
                var until = DateTime.UtcNow.AddMilliseconds(900);
                int frame = 0;
                while (DateTime.UtcNow < until)
                {
                    frame++;
                    Application.DoEvents();
                    surfA.RenderFrame(wgc, cfg, new Point(200, 200), new PointF(240, 70), shapeA, frame * 0.01f, true, null);
                    surfB.RenderFrame(wgc, cfg, new Point(600, 300), new PointF(160, 60), shapeB, frame * 0.01f, true, null);
                    var px = surfA.CaptureOffscreenBgra();
                    int center = (70 * 480 + 240) * 4;
                    if (px[center] == 0 && px[center + 1] == 0 && px[center + 2] == 0)
                        blankCenterCount++;
                    Thread.Sleep(8);
                }

                Assert.Equal(0, blankCenterCount);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (threadEx != null) throw new Exception("WGC sync test failed", threadEx);
    }

    [Fact]
    public void DockWindow_AutoHideEdgeReveal_And_EdgeToCapsuleBridge_RevealsCleanly()
    {
        Exception? threadEx = null;
        var sta = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                using var gpu = new GpuDeviceContext();
                var tempDir = Path.Combine(Path.GetTempPath(), "ProjectCrystalDock_" + Guid.NewGuid().ToString("N"));
                var cfgMgr = new ConfigManager(tempDir);
                cfgMgr.Update(c =>
                {
                    c.Dock.Enabled = true;
                    c.Dock.Position = DockPosition.Bottom;
                    c.Dock.AutoHide = true;
                    c.Dock.RevealSensitivityPx = 8;
                    c.Dock.AutoHideDelayMs = 200;
                }, false);

                var apps = new InstalledAppsService();
                var icons = new IconTextureRegistry(apps);
                var winMgr = new WindowManagerService();
                var mon = new Rectangle(0, 0, 1920, 1080);

                using var dock = new DockWindow(
                    gpu, cfgMgr, null, mon, icons, apps, winMgr,
                    () => false,
                    _ => false,
                    (_, __) => { },
                    (_, __) => { },
                    () => { },
                    () => { });

                Assert.False(dock.Visible);

                // Trigger within 8px bottom strip
                dock.PollEdgeTrigger(new Point(960, mon.Bottom - 6));
                for (int i = 0; i < 12; i++)
                {
                    Application.DoEvents();
                    dock.TickAndRenderIfNeeded();
                    Thread.Sleep(15);
                }

                Assert.True(dock.Visible);
                Assert.True(dock.IsHandleCreated);
                Assert.True(Win32Interop.IsWindowVisible(dock.Handle));
                int exStyle = Win32Interop.GetWindowLong(dock.Handle, Win32Interop.GWL_EXSTYLE);
                Assert.True((exStyle & Win32Interop.WS_EX_TOPMOST) != 0);

                // Edge-to-capsule bridge: moving slightly up (20px from bottom) should keep Dock revealed
                dock.PollEdgeTrigger(new Point(960, mon.Bottom - 20));
                Assert.True(dock.Visible);

                dock.Close();
                try { Directory.Delete(tempDir, true); } catch { }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (threadEx != null) throw new Exception("Dock reveal test failed", threadEx);
    }

    [Fact]
    public void DarkThemeGlass_PreservesLuminousCrystalPresenceOverDarkApp()
    {
        using var gpu = new GpuDeviceContext();
        var cfg = ConfigManager.CreateDefaults();
        cfg.Glass.CaptureBackend = CaptureBackendMode.WallpaperComposited;
        cfg.Glass.TintOpacity = 0f;
        cfg.Glass.Transparency = 0.94f;

        // Pure dark desktop background simulating dark VS Code / Antigravity window
        byte[] darkBg = new byte[400 * 140 * 4];
        for (int i = 0; i < darkBg.Length; i += 4)
        {
            darkBg[i] = 18;
            darkBg[i + 1] = 17;
            darkBg[i + 2] = 16;
            darkBg[i + 3] = 255;
        }
        gpu.UpdateFallbackBackdrop(darkBg, 400, 140);

        using var surface = new LiquidGlassSurface(gpu, 0, 400, 140);
        var shape = new[] { new GlassShapeItem { X = 20, Y = 20, Width = 360, Height = 100, CornerRadius = 24, SquirclePower = 3.2f } };
        surface.RenderFrame(null, cfg, new Point(0, 0), new PointF(200, 70), shape, 0f, true, null);
        byte[] px = surface.CaptureOffscreenBgra();

        // Top meniscus edge (y=21, x=200) must display a clear specular lip catchlight (> 85 avg intensity)
        int topEdgeIdx = (21 * 400 + 200) * 4;
        int edgeBrightness = px[topEdgeIdx] + px[topEdgeIdx + 1] + px[topEdgeIdx + 2];
        Assert.True(edgeBrightness > 85, $"Expected crisp crystal edge catchlight over dark background, got sum={edgeBrightness}");

        // Upper glass body (y=36, x=200) must have subtle crystal glaze rather than dead solid black
        int bodyIdx = (36 * 400 + 200) * 4;
        int bodySum = px[bodyIdx] + px[bodyIdx + 1] + px[bodyIdx + 2];
        Assert.True(bodySum > 56, $"Expected translucent crystal surface lift over dark background, got sum={bodySum}");
    }

    [Fact]
    public void ZeroBlackWgcCapture_With99Transparency_NeverRendersSolidBlack()
    {
        using var gpu = new GpuDeviceContext();
        var cfg = ConfigManager.CreateDefaults();
        cfg.Glass.CaptureBackend = CaptureBackendMode.LiveDesktopWgc;
        cfg.Glass.Transparency = 0.99f;
        cfg.Glass.BlurRadius = 0f;
        cfg.Glass.TintOpacity = 0.03f;

        // Colorful fallback wallpaper
        byte[] wp = new byte[400 * 140 * 4];
        for (int i = 0; i < wp.Length; i += 4)
        {
            wp[i] = 220; wp[i + 1] = 140; wp[i + 2] = 60; wp[i + 3] = 255;
        }
        gpu.UpdateFallbackBackdrop(wp, 400, 140);

        // Simulate pure-black WGC cutout texture
        using var wgc = new WgcDesktopCaptureService(gpu.D3DDevice, gpu.D3DContext, 0, gpu.DeviceLock);
        byte[] zeroWgc = new byte[400 * 140 * 4];
        wgc.UploadStaticBgraBackdrop(zeroWgc, 400, 140);

        using var surface = new LiquidGlassSurface(gpu, 0, 400, 140);
        var shape = new[] { new GlassShapeItem { X = 20, Y = 20, Width = 360, Height = 100, CornerRadius = 24, SquirclePower = 3.2f } };
        surface.RenderFrame(wgc, cfg, new Point(0, 0), new PointF(200, 70), shape, 0f, true, null);
        byte[] px = surface.CaptureOffscreenBgra();

        // Both top and lower-half interior must remain luminous glass, NEVER pure black
        int lowerIdx = (100 * 400 + 200) * 4;
        int lowerSum = px[lowerIdx] + px[lowerIdx + 1] + px[lowerIdx + 2];
        Assert.True(lowerSum > 70, $"Expected anti-black glass protection in lower half over zero WGC region, got sum={lowerSum}");
    }

    [Fact]
    public void DockWindow_IncludesRunningAppsWithoutExePath_AndPrioritizesAntigravity()
    {
        Exception? threadEx = null;
        var sta = new Thread(() =>
        {
            try
            {
                using var gpu = new GpuDeviceContext();
                var tempDir = Path.Combine(Path.GetTempPath(), "ProjectCrystalApps_" + Guid.NewGuid().ToString("N"));
                var cfgMgr = new ConfigManager(tempDir);
                cfgMgr.Update(c => { c.Dock.ShowRecentApps = true; c.Dock.PinnedApps.Clear(); }, false);

                var apps = new InstalledAppsService();
                var icons = new IconTextureRegistry(apps);
                var winMgr = new WindowManagerService();
                using var dock = new DockWindow(
                    gpu, cfgMgr, null, new Rectangle(0, 0, 1920, 1080), icons, apps, winMgr,
                    () => false, _ => false, (_, __) => { }, (_, __) => { }, () => { }, () => { });

                var windows = new List<WindowInfo>();
                for (int i = 0; i < 8; i++)
                    windows.Add(new WindowInfo(100 + i, $"BackgroundApp {i}", $"App{i}", (uint)(2000 + i), 0, 0, 800, 600, false, false, false, 0, $@"C:\Apps\App{i}.exe"));
                // Elevated/empty-path app
                windows.Add(new WindowInfo(501, "Elevated Utility", "Utility", 3001, 0, 0, 800, 600, false, false, false, 0, ""));
                // Foreground Antigravity past the old Take(6) boundary
                windows.Add(new WindowInfo(502, "Antigravity Workspace", "Antigravity", 3002, 0, 0, 1920, 1080, false, true, true, 0, @"C:\Users\Student\AppData\Local\Programs\antigravity\Antigravity.exe"));

                dock.RebuildItems(windows);
                var items = dock.GetCurrentItems();

                Assert.Contains(items, it => it.Name == "Antigravity" && it.IsRunning && it.IsForeground);
                Assert.Contains(items, it => it.Name == "Utility" && it.IsRunning);
                Assert.Contains(items, it => it.IsSpecialDesktop && it.Name == "Desktop");

                // Verify minimized ("temporarily closed") apps remain tracked cleanly with IsMinimized=true & WindowHandles
                var minWindows = new List<WindowInfo>
                {
                    new(701, "Antigravity Workspace", "Antigravity", 4001, -32000, -32000, 160, 28, true, false, false, 0, @"C:\Users\Student\AppData\Local\Programs\antigravity\Antigravity.exe"),
                    new(702, "Docs - Chrome", "Google Chrome", 4002, -32000, -32000, 160, 28, true, false, false, 0, @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
                    new(703, "New Tab - Chrome", "Google Chrome", 4002, 100, 100, 1200, 800, false, false, false, 0, @"C:\Program Files\Google\Chrome\Application\chrome.exe")
                };
                dock.RebuildItems(minWindows);
                var minItems = dock.GetCurrentItems();
                var agItem = minItems.FirstOrDefault(it => it.Name == "Antigravity");
                Assert.NotNull(agItem);
                Assert.True(agItem!.IsRunning);
                Assert.True(agItem.IsMinimized);
                Assert.Equal(701, agItem.ActiveWindowHandle);

                var chItem = minItems.FirstOrDefault(it => it.Name == "Google Chrome");
                Assert.NotNull(chItem);
                Assert.Equal(2, chItem!.WindowCount);
                Assert.False(chItem.IsMinimized);

                dock.Close();
                try { Directory.Delete(tempDir, true); } catch { }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (threadEx != null) throw new Exception("Dock running apps test failed", threadEx);
    }

    [Fact]
    public void RecoveryGuard_SavesAndRestoresTaskbarWorkAreaBackup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ProjectCrystalWorkArea_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rg = new RecoveryGuard(tempDir);
            rg.SaveTaskbarOriginalState(false, hiddenByCrystal: true, 0, 0, 1920, 1032);
            var b = rg.ReadTaskbarBackup();
            Assert.NotNull(b);
            Assert.True(b!.HasSavedWorkArea);
            Assert.Equal(1920, b.WorkRight);
            Assert.Equal(1032, b.WorkBottom);
            rg.ClearTaskbarBackup();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void AnimatedPanelMotion_RemainsStableWithoutOvershoot_AtMaxAnimationSpeed()
    {
        var motion = new AnimatedPanelMotion();
        motion.BeginOpen(-12f);
        float maxScale = 0f;
        bool stillMoving = true;
        for (int i = 0; i < 60 && stillMoving; i++)
        {
            stillMoving = motion.Step(0.033f, speedScale: 2.0f, instant: false, slideDistance: -12f);
            maxScale = MathF.Max(maxScale, motion.Scale);
            Assert.InRange(motion.Opacity, 0f, 1.001f);
            Assert.InRange(motion.Scale, 0.96f, 1.001f);
        }
        Assert.False(stillMoving, "Expected panel open motion to settle within 60 steps even at 2x speed");
        Assert.True(motion.Opacity >= 0.99f);
        Assert.InRange(maxScale, 0.99f, 1.001f);
    }

    [Fact]
    public void SettingsGlassLabWindow_ShowAndFocus_OpensTopMostAndRendersCleanly()
    {
        Exception? threadEx = null;
        var sta = new Thread(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ProjectCrystalLab_" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                using var gpu = new GpuDeviceContext();
                var cfgMgr = new ConfigManager(tempDir);
                var hw = HardwareInspector.Inspect(gpu.AdapterName, gpu.AdapterMemoryMb, 1920, 1080, 60);
                var disp = new DisplayBrightnessService();
                var rg = new RecoveryGuard(tempDir);
                var shell = new ShellIntegrationService(rg);
                var notif = new NotificationService();
                var winMgr = new WindowManagerService();

                using var lab = new SettingsGlassLabWindow(
                    gpu, cfgMgr, null, hw, disp, shell, notif, winMgr,
                    () => { }, () => { }, () => { }, () => { }, () => { });
                lab.ShowAndFocus("Glass Lab");

                Assert.True(lab.Visible);
                Assert.True(lab.TopMost);
                Assert.True(lab.Width >= 1000 && lab.Height >= 680);

                for (int i = 0; i < 15; i++)
                {
                    Application.DoEvents();
                    lab.TickAndRenderIfNeeded();
                }

                lab.Close();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (threadEx != null) throw new Exception("SettingsGlassLabWindow test failed", threadEx);
    }
}
