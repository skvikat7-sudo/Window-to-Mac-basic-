using System.Text.Json.Serialization;

namespace ProjectCrystal.Core.Configuration;

public enum ThemeAppearance { Auto, Light, Dark }
public enum GlassMaterialPreset { CrystalClear, LiquidMercury, FrostedQuartz, ObsidianGlass, SapphirePrism, Custom }
public enum CaptureBackendMode { LiveDesktopWgc, WallpaperComposited }
public enum DockPosition { Bottom, Left, Right }
public enum MenuBarMonitorMode { PrimaryOnly, AllMonitors }
public enum QualityPreset { Auto, Ultra, Balanced, Eco }
public enum ShellOperatingMode { SafeDesktop, AdvancedShellIntegration }
public enum WindowsTaskbarMode { AutoHideAndReplace, WindowsAutoHideOnly, KeepVisible }
public enum ReducedMotionMode { FollowWindows, AlwaysReduce, AlwaysFull }

public sealed class CrystalConfig
{
    public int SchemaVersion { get; set; } = 3;
    public AppearanceConfig Appearance { get; set; } = new();
    public GlassMaterialConfig Glass { get; set; } = new();
    public DockConfig Dock { get; set; } = new();
    public MenuBarConfig MenuBar { get; set; } = new();
    public WidgetsConfig Widgets { get; set; } = new();
    public NotificationConfig Notifications { get; set; } = new();
    public WindowManagementConfig WindowManagement { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public ShellModeConfig ShellMode { get; set; } = new();
    public AccessibilityConfig Accessibility { get; set; } = new();

    public CrystalConfig Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<CrystalConfig>(json) ?? new CrystalConfig();
    }
}

public sealed class AppearanceConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeAppearance Theme { get; set; } = ThemeAppearance.Auto;
    public string AccentColorHex { get; set; } = "#4CC2FF";
    public bool FollowWindowsAccent { get; set; } = true;
    public bool DynamicWallpaperTinting { get; set; } = true;
    public float UiScale { get; set; } = 1.0f;
}

public sealed class GlassMaterialConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GlassMaterialPreset Preset { get; set; } = GlassMaterialPreset.CrystalClear;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CaptureBackendMode CaptureBackend { get; set; } = CaptureBackendMode.LiveDesktopWgc;

    public float Transparency { get; set; } = 0.94f;          // Crystal clear interior
    public float RefractionIntensity { get; set; } = 1.35f;   // Crisp optical rim bending
    public float DistortionStrength { get; set; } = 1.15f;
    public float Magnification { get; set; } = 1.05f;
    public float BlurRadius { get; set; } = 0.55f;            // Near-zero blur for pure crystal clarity
    public float ChromaticAberration { get; set; } = 0.75f;
    public string GlassTintHex { get; set; } = "#EEF6FF";
    public float TintOpacity { get; set; } = 0.03f;           // Minimal haze
    public float ReflectionIntensity { get; set; } = 1.35f;   // Glossy specular highlights
    public float EdgeLighting { get; set; } = 1.30f;          // Fine liquid edge glint
    public float Depth { get; set; } = 0.95f;
    public float AnimationSpeed { get; set; } = 1.0f;
}

public sealed class DockConfig
{
    public bool Enabled { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DockPosition Position { get; set; } = DockPosition.Bottom;
    public float IconSize { get; set; } = 48f;
    public float MagnificationScale { get; set; } = 1.52f;
    public float MagnificationSpread { get; set; } = 135f;
    public bool AutoHide { get; set; } = true;
    public int AutoHideDelayMs { get; set; } = 650;
    public int RevealSensitivityPx { get; set; } = 8;
    public bool ShowRecentApps { get; set; } = true;
    public bool ShowSeparators { get; set; } = true;
    public bool ShowRunningIndicators { get; set; } = true;
    public float AnimationSpeed { get; set; } = 1.0f;
    public List<PinnedAppEntry> PinnedApps { get; set; } = new();
    public List<DockStackEntry> Stacks { get; set; } = new()
    {
        new DockStackEntry { Id = "downloads", Name = "Downloads", FolderPath = "%USERPROFILE%\\Downloads", Enabled = true }
    };
}

public sealed class PinnedAppEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public bool IsUwp { get; set; }
}

public sealed class DockStackEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class MenuBarConfig
{
    public bool Enabled { get; set; } = true;
    public bool AutoHide { get; set; } = true;
    public int AutoHideDelayMs { get; set; } = 550;
    public int RevealSensitivityPx { get; set; } = 6;
    public int Height { get; set; } = 32;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MenuBarMonitorMode MonitorMode { get; set; } = MenuBarMonitorMode.AllMonitors;
    public bool ShowActiveApp { get; set; } = true;
    public bool ShowMediaPill { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowBluetooth { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowVolume { get; set; } = true;
    public bool ShowSeconds { get; set; } = false;
    public bool Use24HourClock { get; set; } = false;
}

public sealed class WidgetsConfig
{
    public bool Enabled { get; set; } = true;
    public string WeatherLocation { get; set; } = "Auto";
    public bool UseFahrenheit { get; set; } = false;
    public List<WidgetPlacement> Items { get; set; } = new()
    {
        new WidgetPlacement { Id = "clock", Title = "Clock & Date", Kind = WidgetKind.Clock, Enabled = true, X = 36, Y = 56, Width = 280, Height = 128 },
        new WidgetPlacement { Id = "weather", Title = "Weather", Kind = WidgetKind.Weather, Enabled = true, X = 36, Y = 198, Width = 280, Height = 144 },
        new WidgetPlacement { Id = "telemetry", Title = "System Monitor", Kind = WidgetKind.Telemetry, Enabled = true, X = 36, Y = 356, Width = 280, Height = 184 },
        new WidgetPlacement { Id = "media", Title = "Now Playing", Kind = WidgetKind.Media, Enabled = true, X = 36, Y = 554, Width = 280, Height = 124 },
        new WidgetPlacement { Id = "calendar", Title = "Calendar", Kind = WidgetKind.Calendar, Enabled = false, X = 332, Y = 56, Width = 272, Height = 228 }
    };
}

public enum WidgetKind { Clock, Calendar, Weather, Telemetry, Media }

public sealed class WidgetPlacement
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WidgetKind Kind { get; set; }
    public bool Enabled { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int MonitorIndex { get; set; }
}

public sealed class NotificationConfig
{
    public bool Enabled { get; set; } = true;
    public bool DoNotDisturb { get; set; } = false;
    public bool GroupByApp { get; set; } = true;
    public bool ShowLiveBanners { get; set; } = true;
    public int MaxHistoryCount { get; set; } = 60;
}

public sealed class WindowManagementConfig
{
    public bool EnableHotkeys { get; set; } = true;
    public string LauncherShortcut { get; set; } = "Alt+Space";
    public string ExposeShortcut { get; set; } = "Ctrl+Alt+Up";
    public string ControlCenterShortcut { get; set; } = "Ctrl+Alt+C";
    public string NotificationCenterShortcut { get; set; } = "Ctrl+Alt+N";
    public string SnapLeftShortcut { get; set; } = "Ctrl+Alt+Left";
    public string SnapRightShortcut { get; set; } = "Ctrl+Alt+Right";
    public string SnapMaximizeShortcut { get; set; } = "Ctrl+Alt+M";
    public bool ReserveDesktopWorkArea { get; set; } = false;
    public bool EnableSnapBarOnDrag { get; set; } = true;
}

public sealed class PerformanceConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public QualityPreset Quality { get; set; } = QualityPreset.Auto;
    public int TargetFps { get; set; } = 60;
    public int CaptureRateFps { get; set; } = 60;
    public bool PauseWhenIdle { get; set; } = true;
    public bool PauseWhenFullscreenGame { get; set; } = true;
    public int MetricsPollIntervalMs { get; set; } = 1500;
}

public sealed class ShellModeConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ShellOperatingMode Mode { get; set; } = ShellOperatingMode.AdvancedShellIntegration;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WindowsTaskbarMode TaskbarMode { get; set; } = WindowsTaskbarMode.AutoHideAndReplace;
    public bool AutoHideWindowsTaskbar { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
}

public sealed class AccessibilityConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReducedMotionMode MotionMode { get; set; } = ReducedMotionMode.FollowWindows;
    public bool HighVisibilityEdges { get; set; } = false;
    public bool EnforceMinimumTextContrast { get; set; } = true;
}
