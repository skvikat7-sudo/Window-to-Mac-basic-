using System.Text.Json;
using ProjectCrystal.Core.Diagnostics;

namespace ProjectCrystal.Core.Configuration;

public sealed class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private CancellationTokenSource? _saveDebounceCts;

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string BackupPath { get; }

    public CrystalConfig Current { get; private set; }
    public event Action<CrystalConfig>? ConfigChanged;

    public ConfigManager(string? customDirectory = null)
    {
        ConfigDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectCrystal");
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
        BackupPath = Path.Combine(ConfigDirectory, "config.bak.json");
        Current = LoadInternal();
    }

    private CrystalConfig LoadInternal()
    {
        Directory.CreateDirectory(ConfigDirectory);
        foreach (string candidate in new[] { ConfigPath, BackupPath })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                string json = File.ReadAllText(candidate);
                var cfg = JsonSerializer.Deserialize<CrystalConfig>(json, JsonOptions);
                if (cfg != null)
                {
                    bool migrated = MigrateIfNeeded(cfg);
                    Sanitize(cfg);
                    if (migrated)
                        SaveImmediate(cfg);
                    if (candidate == BackupPath)
                        CrystalLogger.Warn("Config", "Primary config corrupted; restored from backup.");
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                CrystalLogger.Error("Config", $"Failed loading {candidate}", ex);
            }
        }

        var defaults = CreateDefaults();
        SaveImmediate(defaults);
        return defaults;
    }

    private static bool MigrateIfNeeded(CrystalConfig cfg)
    {
        bool changed = false;
        if (cfg.SchemaVersion < 2)
        {
            // Upgrade v1 defaults that caused taskbar overlap, visible old taskbar, and cloudy glass
            cfg.SchemaVersion = 2;
            cfg.MenuBar.AutoHide = true;
            cfg.Dock.AutoHide = true;
            cfg.ShellMode.Mode = ShellOperatingMode.AdvancedShellIntegration;
            cfg.ShellMode.AutoHideWindowsTaskbar = true;
            cfg.ShellMode.TaskbarMode = WindowsTaskbarMode.AutoHideAndReplace;
            ApplyMaterialPreset(cfg.Glass, GlassMaterialPreset.CrystalClear);

            // Mark v1 hardcoded default pinned list for live repair by WindowsTaskbarPinsService
            if (LooksLikeOldHardcodedDefaultPins(cfg.Dock.PinnedApps))
            {
                cfg.Dock.PinnedApps.Clear();
            }
            CrystalLogger.Info("Config", "Migrated configuration from v1 to v2 (auto-hide bars, glossy clear glass, real taskbar pin sync).");
            changed = true;
        }

        if (cfg.SchemaVersion < 3)
        {
            // Repair broken v2 edge-trigger settings, ensure Dock is enabled at Bottom, and restore CrystalClear if zeroed out
            cfg.SchemaVersion = 3;
            cfg.Dock.Enabled = true;
            cfg.Dock.Position = DockPosition.Bottom;
            cfg.Dock.AutoHide = true;
            cfg.Dock.RevealSensitivityPx = Math.Max(cfg.Dock.RevealSensitivityPx, 8);
            cfg.Dock.AutoHideDelayMs = Math.Max(cfg.Dock.AutoHideDelayMs, 650);
            cfg.MenuBar.RevealSensitivityPx = Math.Max(cfg.MenuBar.RevealSensitivityPx, 6);
            if (cfg.Glass.TintOpacity <= 0.005f && cfg.Glass.BlurRadius <= 0.1f)
            {
                ApplyMaterialPreset(cfg.Glass, GlassMaterialPreset.CrystalClear);
            }
            CrystalLogger.Info("Config", "Migrated configuration to v3 (enabled Bottom Dock auto-hide, responsive 8px edge trigger, luminous CrystalClear glass).");
            changed = true;
        }

        return changed;
    }

    private static bool LooksLikeOldHardcodedDefaultPins(List<PinnedAppEntry> pins)
    {
        if (pins.Count == 0) return true;
        var ids = new HashSet<string>(pins.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        return ids.Contains("notepad") && ids.Contains("cmd") && ids.Contains("win-settings");
    }

    public static CrystalConfig CreateDefaults()
    {
        var cfg = new CrystalConfig();
        ApplyMaterialPreset(cfg.Glass, GlassMaterialPreset.CrystalClear);
        return cfg;
    }

    public void Update(Action<CrystalConfig> mutator, bool saveNow = false)
    {
        CrystalConfig currentRef;
        lock (_lock)
        {
            mutator(Current);
            Sanitize(Current);
            currentRef = Current;
        }

        ConfigChanged?.Invoke(currentRef);

        if (saveNow)
            SaveImmediate(currentRef);
        else
            ScheduleDebouncedSave(currentRef);
    }

    public void SaveImmediate(CrystalConfig? target = null)
    {
        lock (_lock)
        {
            _saveDebounceCts?.Cancel();
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                var cfg = target ?? Current;
                Sanitize(cfg);
                string json = JsonSerializer.Serialize(cfg, JsonOptions);
                string tempPath = ConfigPath + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(ConfigPath))
                    File.Copy(ConfigPath, BackupPath, overwrite: true);
                File.Move(tempPath, ConfigPath, overwrite: true);
            }
            catch (Exception ex)
            {
                CrystalLogger.Error("Config", "Failed to save config", ex);
            }
        }
    }

    private void ScheduleDebouncedSave(CrystalConfig snapshot)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _saveDebounceCts?.Cancel();
            cts = new CancellationTokenSource();
            _saveDebounceCts = cts;
        }
        Task.Delay(350, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || cts.IsCancellationRequested) return;
            lock (_lock)
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(_saveDebounceCts, cts)) return;
                SaveImmediate(Current);
            }
        }, TaskScheduler.Default);
    }

    public void ResetToDefaults()
    {
        var defaults = CreateDefaults();
        lock (_lock) { Current = defaults; }
        SaveImmediate(defaults);
        ConfigChanged?.Invoke(defaults.Clone());
        CrystalLogger.Info("Config", "Reset all settings to defaults.");
    }

    public string ExportBackup(string destinationPath)
    {
        SaveImmediate();
        File.Copy(ConfigPath, destinationPath, overwrite: true);
        CrystalLogger.Info("Config", $"Exported configuration backup to {destinationPath}");
        return destinationPath;
    }

    public bool ImportBackup(string sourcePath)
    {
        try
        {
            string json = File.ReadAllText(sourcePath);
            var loaded = JsonSerializer.Deserialize<CrystalConfig>(json, JsonOptions);
            if (loaded is null) return false;
            Sanitize(loaded);
            lock (_lock) { Current = loaded; }
            SaveImmediate(loaded);
            ConfigChanged?.Invoke(loaded.Clone());
            CrystalLogger.Info("Config", $"Restored configuration from {sourcePath}");
            return true;
        }
        catch (Exception ex)
        {
            CrystalLogger.Error("Config", $"Failed importing backup from {sourcePath}", ex);
            return false;
        }
    }

    public static void ApplyMaterialPreset(GlassMaterialConfig g, GlassMaterialPreset preset)
    {
        g.Preset = preset;
        switch (preset)
        {
            case GlassMaterialPreset.CrystalClear:
                g.Transparency = 0.94f;
                g.RefractionIntensity = 1.35f;
                g.DistortionStrength = 1.15f;
                g.Magnification = 1.05f;
                g.BlurRadius = 0.55f;
                g.ChromaticAberration = 0.75f;
                g.GlassTintHex = "#EEF6FF";
                g.TintOpacity = 0.03f;
                g.ReflectionIntensity = 1.35f;
                g.EdgeLighting = 1.30f;
                g.Depth = 0.95f;
                break;

            case GlassMaterialPreset.LiquidMercury:
                g.Transparency = 0.90f;
                g.RefractionIntensity = 1.75f;
                g.DistortionStrength = 1.45f;
                g.Magnification = 1.09f;
                g.BlurRadius = 0.35f;
                g.ChromaticAberration = 1.15f;
                g.GlassTintHex = "#F4F9FF";
                g.TintOpacity = 0.06f;
                g.ReflectionIntensity = 1.75f;
                g.EdgeLighting = 1.65f;
                g.Depth = 1.15f;
                break;

            case GlassMaterialPreset.FrostedQuartz:
                g.Transparency = 0.80f;
                g.RefractionIntensity = 1.05f;
                g.DistortionStrength = 0.90f;
                g.Magnification = 1.03f;
                g.BlurRadius = 4.5f;
                g.ChromaticAberration = 0.45f;
                g.GlassTintHex = "#F7FBFF";
                g.TintOpacity = 0.10f;
                g.ReflectionIntensity = 1.15f;
                g.EdgeLighting = 1.15f;
                g.Depth = 0.90f;
                break;

            case GlassMaterialPreset.ObsidianGlass:
                g.Transparency = 0.86f;
                g.RefractionIntensity = 1.40f;
                g.DistortionStrength = 1.20f;
                g.Magnification = 1.05f;
                g.BlurRadius = 0.9f;
                g.ChromaticAberration = 0.75f;
                g.GlassTintHex = "#0B1018";
                g.TintOpacity = 0.22f;
                g.ReflectionIntensity = 1.45f;
                g.EdgeLighting = 1.40f;
                g.Depth = 1.10f;
                break;

            case GlassMaterialPreset.SapphirePrism:
                g.Transparency = 0.92f;
                g.RefractionIntensity = 1.60f;
                g.DistortionStrength = 1.30f;
                g.Magnification = 1.07f;
                g.BlurRadius = 0.50f;
                g.ChromaticAberration = 1.65f;
                g.GlassTintHex = "#9AD6FF";
                g.TintOpacity = 0.07f;
                g.ReflectionIntensity = 1.55f;
                g.EdgeLighting = 1.50f;
                g.Depth = 1.05f;
                break;
        }
    }

    public static void Sanitize(CrystalConfig cfg)
    {
        cfg.Appearance.UiScale = Math.Clamp(cfg.Appearance.UiScale, 0.8f, 1.5f);
        cfg.Glass.Transparency = Math.Clamp(cfg.Glass.Transparency, 0.2f, 0.99f);
        cfg.Glass.RefractionIntensity = Math.Clamp(cfg.Glass.RefractionIntensity, 0f, 2.5f);
        cfg.Glass.DistortionStrength = Math.Clamp(cfg.Glass.DistortionStrength, 0f, 2.5f);
        cfg.Glass.Magnification = Math.Clamp(cfg.Glass.Magnification, 0.85f, 1.35f);
        cfg.Glass.BlurRadius = Math.Clamp(cfg.Glass.BlurRadius, 0f, 18f);
        cfg.Glass.ChromaticAberration = Math.Clamp(cfg.Glass.ChromaticAberration, 0f, 3.0f);
        cfg.Glass.TintOpacity = Math.Clamp(cfg.Glass.TintOpacity, 0f, 0.55f);
        cfg.Glass.ReflectionIntensity = Math.Clamp(cfg.Glass.ReflectionIntensity, 0f, 2.5f);
        cfg.Glass.EdgeLighting = Math.Clamp(cfg.Glass.EdgeLighting, 0f, 2.5f);
        cfg.Glass.Depth = Math.Clamp(cfg.Glass.Depth, 0f, 2.0f);
        cfg.Glass.AnimationSpeed = Math.Clamp(cfg.Glass.AnimationSpeed, 0.25f, 2.5f);

        cfg.Dock.IconSize = Math.Clamp(cfg.Dock.IconSize, 32f, 80f);
        cfg.Dock.MagnificationScale = Math.Clamp(cfg.Dock.MagnificationScale, 1.0f, 2.1f);
        cfg.Dock.MagnificationSpread = Math.Clamp(cfg.Dock.MagnificationSpread, 60f, 260f);
        cfg.Dock.AutoHideDelayMs = Math.Clamp(cfg.Dock.AutoHideDelayMs, 100, 3000);
        cfg.Dock.RevealSensitivityPx = Math.Clamp(cfg.Dock.RevealSensitivityPx, 1, 16);
        cfg.Dock.AnimationSpeed = Math.Clamp(cfg.Dock.AnimationSpeed, 0.25f, 2.5f);

        cfg.MenuBar.Height = Math.Clamp(cfg.MenuBar.Height, 26, 44);
        cfg.MenuBar.AutoHideDelayMs = Math.Clamp(cfg.MenuBar.AutoHideDelayMs, 100, 3000);
        cfg.MenuBar.RevealSensitivityPx = Math.Clamp(cfg.MenuBar.RevealSensitivityPx, 1, 16);

        cfg.Performance.TargetFps = Math.Clamp(cfg.Performance.TargetFps, 24, 144);
        cfg.Performance.CaptureRateFps = Math.Clamp(cfg.Performance.CaptureRateFps, 15, 120);
        cfg.Performance.MetricsPollIntervalMs = Math.Clamp(cfg.Performance.MetricsPollIntervalMs, 500, 10000);
    }
}
