using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProjectCrystal.Core.Configuration;

namespace ProjectCrystal.Core.Diagnostics;

public sealed record HardwareProfile(
    string OsCaption,
    string OsBuild,
    string WindowsEditionId,
    bool SupportsShellLauncher,
    string CpuName,
    int LogicalProcessors,
    double TotalRamGb,
    string GpuName,
    double GpuDedicatedOrAdapterMb,
    int PrimaryWidth,
    int PrimaryHeight,
    int RefreshRateHz,
    QualityPreset RecommendedQuality
);

public static class HardwareInspector
{
    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    public static HardwareProfile Inspect(string gpuDescription = "Direct3D 11 Hardware Adapter", double gpuMb = 2048, int width = 1920, int height = 1080, int refreshHz = 60)
    {
        string osBuild = Environment.OSVersion.Version.Build.ToString();
        string editionId = "Unknown";
        string osCaption = Environment.OSVersion.VersionString;
        string cpuName = "Unknown Processor";

        try
        {
            using var ntKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (ntKey != null)
            {
                editionId = ntKey.GetValue("EditionID")?.ToString() ?? editionId;
                string displayVer = ntKey.GetValue("DisplayVersion")?.ToString() ?? "";
                string currentBuild = ntKey.GetValue("CurrentBuild")?.ToString() ?? osBuild;
                osBuild = string.IsNullOrEmpty(displayVer) ? currentBuild : $"{displayVer} (Build {currentBuild})";
                int buildNum = int.TryParse(currentBuild, out int bn) ? bn : Environment.OSVersion.Version.Build;
                string winGen = buildNum >= 22000 ? "Windows 11" : "Windows 10";
                osCaption = $"{winGen} {editionId}";
            }

            using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpuName = cpuKey?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? cpuName;
        }
        catch (Exception ex)
        {
            CrystalLogger.Warn("Hardware", $"Registry hardware inspection partial: {ex.Message}");
        }

        double ramGb = 8.0;
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out long kb) && kb > 0)
                ramGb = Math.Round(kb / (1024.0 * 1024.0), 2);
        }
        catch { }

        bool supportsShellLauncher = editionId.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            || editionId.Contains("Education", StringComparison.OrdinalIgnoreCase)
            || editionId.Contains("IoT", StringComparison.OrdinalIgnoreCase);

        bool isBasicGpu = gpuDescription.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
            || gpuDescription.Contains("Software", StringComparison.OrdinalIgnoreCase);
        bool isIntegratedIntel = gpuDescription.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            && !gpuDescription.Contains("Arc", StringComparison.OrdinalIgnoreCase);

        QualityPreset recommended = isBasicGpu || ramGb < 6.0 || Environment.ProcessorCount < 4
            ? QualityPreset.Eco
            : (!isIntegratedIntel
               && (gpuDescription.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                   || gpuDescription.Contains("RX ", StringComparison.OrdinalIgnoreCase)
                   || (Environment.ProcessorCount >= 8 && ramGb >= 12.0)))
                ? QualityPreset.Ultra
                : QualityPreset.Balanced;

        return new HardwareProfile(
            osCaption,
            osBuild,
            editionId,
            supportsShellLauncher,
            cpuName,
            Environment.ProcessorCount,
            ramGb,
            gpuDescription,
            gpuMb,
            width,
            height,
            refreshHz,
            recommended);
    }
}
