namespace ProjectCrystal.Core.Models;

public sealed record AppEntry(
    string Id,
    string Name,
    string LaunchTarget,
    string Arguments,
    string Category,
    string IconKey,
    bool IsPackagedUwp,
    DateTimeOffset? LastLaunched = null,
    int LaunchCount = 0
);

public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    uint ProcessId,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsMinimized,
    bool IsMaximized,
    bool IsForeground,
    int MonitorIndex,
    string ExecutablePath
);

public sealed record NotificationItem(
    string Id,
    uint PlatformId,
    string AppName,
    string AppId,
    string Title,
    string Body,
    DateTimeOffset Timestamp,
    bool IsDismissed = false
);

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    bool IsActive
);

public sealed record NetworkStatusInfo(
    bool IsConnected,
    bool HasWifiHardware,
    bool IsWifiConnected,
    bool IsWifiEnabled,
    string ActiveConnectionName,
    string ConnectionType,
    string IpAddress,
    long LinkSpeedMbps,
    int SignalPercent
);

public sealed record BluetoothStatusInfo(
    bool IsSupported,
    bool IsEnabled,
    string RadioName,
    int ConnectedDeviceCount
);

public sealed record BatteryStatusInfo(
    bool HasBattery,
    bool IsCharging,
    int ChargePercent,
    int EstimatedMinutesRemaining,
    string PowerMode
);

public sealed record BrightnessStatusInfo(
    bool IsSupported,
    string Method,
    int Min,
    int Current,
    int Max,
    string MonitorName
);

public sealed record MediaSessionInfo(
    bool HasSession,
    string SourceApp,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    bool CanPlayPause,
    bool CanNext,
    bool CanPrevious,
    byte[]? ThumbnailPng
);

public sealed record SystemMetricsSnapshot(
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double RamPercent,
    double NetworkDownKbps,
    double NetworkUpKbps,
    IReadOnlyList<DriveUsageInfo> Drives,
    int ProcessCount,
    TimeSpan Uptime
);

public sealed record DriveUsageInfo(
    string Name,
    string Label,
    double UsedGb,
    double TotalGb,
    double UsedPercent
);

public sealed record WeatherInfo(
    bool IsAvailable,
    string LocationName,
    double TemperatureC,
    double ApparentTemperatureC,
    int WeatherCode,
    string ConditionText,
    double WindKmh,
    int HumidityPercent,
    DateTimeOffset UpdatedAt,
    string Source
);

public sealed record DisplayMonitorInfo(
    int Index,
    string DeviceName,
    bool IsPrimary,
    int X,
    int Y,
    int Width,
    int Height,
    int WorkX,
    int WorkY,
    int WorkWidth,
    int WorkHeight,
    uint Dpi
);
