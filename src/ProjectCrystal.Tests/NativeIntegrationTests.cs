using ProjectCrystal.Core.Diagnostics;
using ProjectCrystal.Native.Apps;
using ProjectCrystal.Native.System;
using ProjectCrystal.Native.WindowManagement;
using Xunit;

namespace ProjectCrystal.Tests;

public sealed class NativeIntegrationTests
{
    [Fact]
    public void HardwareInspector_ReadsGenuineHostProfile()
    {
        var hw = HardwareInspector.Inspect();
        Assert.False(string.IsNullOrWhiteSpace(hw.OsCaption));
        Assert.True(hw.LogicalProcessors >= 1);
        Assert.True(hw.TotalRamGb > 1.0);
    }

    [Fact]
    public async Task InstalledAppsService_EnumeratesRealWindowsAppsAndIcons()
    {
        var svc = new InstalledAppsService();
        var apps = await svc.RefreshInstalledAppsAsync();
        Assert.True(apps.Count >= 10, $"Expected >=10 installed Windows apps, found {apps.Count}");

        var withIcon = apps.Take(15).Select(a => svc.GetOrExtractIcon(a.IconKey, 64)).FirstOrDefault(i => i != null);
        Assert.NotNull(withIcon);
        Assert.Equal(64, withIcon!.Width);
        Assert.True(withIcon.BgraPixels.Length == 64 * 64 * 4);
    }

    [Fact]
    public async Task SystemMetricsAndAudioAndDisplay_ReturnRealData()
    {
        var metricsSvc = new SystemMetricsService();
        _ = metricsSvc.Sample();
        Thread.Sleep(150);
        var snap = metricsSvc.Sample();
        Assert.True(snap.RamTotalGb > 1.0);
        Assert.True(snap.Drives.Count >= 1);
        Assert.True(snap.ProcessCount >= 10);

        var audio = new CoreAudioService();
        var (vol, _, devName) = audio.GetMasterVolumeState();
        Assert.InRange(vol, 0f, 1f);
        Assert.False(string.IsNullOrWhiteSpace(devName));

        var display = new DisplayBrightnessService();
        var mons = display.GetMonitors();
        Assert.True(mons.Count >= 1);

        var winMgr = new WindowManagerService();
        var wins = winMgr.GetOpenWindows();
        Assert.NotNull(wins);
        Assert.DoesNotContain(wins, w => w.ProcessName.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase));

        var appsSvc = new InstalledAppsService();
        await appsSvc.RefreshInstalledAppsAsync();
        var pins = WindowsTaskbarPinsService.ReadRealWindowsTaskbarPins(appsSvc);
        Assert.NotEmpty(pins);
        Assert.DoesNotContain(pins, p => p.Target.Contains("ProjectCrystal", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(pins.Count, pins.Select(p => WindowsTaskbarPinsService.NormalizeKey(p.Target)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task NetworkAndBattery_ReportAccurateHardwareStatus()
    {
        var netSvc = new NetworkRadioService();
        var net = await netSvc.GetNetworkStatusAsync();
        var bt = await netSvc.GetBluetoothStatusAsync();
        var bat = PowerBatteryService.GetBatteryStatus();

        Assert.NotNull(net);
        Assert.NotNull(bt);
        Assert.NotNull(bat);
    }
}
