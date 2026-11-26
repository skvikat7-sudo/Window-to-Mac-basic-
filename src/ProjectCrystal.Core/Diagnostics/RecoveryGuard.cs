using System.Text.Json;

namespace ProjectCrystal.Core.Diagnostics;

public sealed class TaskbarStateBackup
{
    public bool Saved { get; set; }
    public bool WasAutoHide { get; set; }
    public bool WasWindowHiddenByCrystal { get; set; }
    public bool HasSavedWorkArea { get; set; }
    public int WorkLeft { get; set; }
    public int WorkTop { get; set; }
    public int WorkRight { get; set; }
    public int WorkBottom { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

public sealed class RecoveryGuard
{
    private readonly string _baseDir;
    private readonly string _lockPath;
    private readonly string _crashCountPath;
    private readonly string _taskbarBackupPath;

    public bool PreviousSessionCrashed { get; private set; }
    public int ConsecutiveCrashCount { get; private set; }

    public RecoveryGuard(string? customDir = null)
    {
        _baseDir = customDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectCrystal");
        Directory.CreateDirectory(_baseDir);
        _lockPath = Path.Combine(_baseDir, "session.lock");
        _crashCountPath = Path.Combine(_baseDir, "crash_count.txt");
        _taskbarBackupPath = Path.Combine(_baseDir, "taskbar_backup.json");
    }

    public void BeginSession()
    {
        if (File.Exists(_lockPath))
        {
            PreviousSessionCrashed = true;
            int count = ReadCrashCount() + 1;
            ConsecutiveCrashCount = count;
            File.WriteAllText(_crashCountPath, count.ToString());
            CrystalLogger.Warn("Recovery", $"Detected unclean previous shutdown (crash streak: {count}).");
        }
        else
        {
            PreviousSessionCrashed = false;
            ConsecutiveCrashCount = 0;
            if (File.Exists(_crashCountPath))
                File.WriteAllText(_crashCountPath, "0");
        }

        File.WriteAllText(_lockPath, $"{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}");
    }

    public void EndSessionCleanly()
    {
        try
        {
            if (File.Exists(_lockPath)) File.Delete(_lockPath);
            if (File.Exists(_crashCountPath)) File.WriteAllText(_crashCountPath, "0");
            CrystalLogger.Info("Recovery", "Session lock cleared cleanly.");
        }
        catch (Exception ex)
        {
            CrystalLogger.Error("Recovery", "Failed to clear session lock", ex);
        }
    }

    public void SaveTaskbarOriginalState(bool wasAutoHide, bool hiddenByCrystal = false, int? workLeft = null, int? workTop = null, int? workRight = null, int? workBottom = null)
    {
        bool hasWork = workLeft.HasValue && workTop.HasValue && workRight.HasValue && workBottom.HasValue;
        if (File.Exists(_taskbarBackupPath))
        {
            var existing = ReadTaskbarBackup();
            if (existing != null)
            {
                bool dirty = false;
                if (hiddenByCrystal && !existing.WasWindowHiddenByCrystal)
                {
                    existing.WasWindowHiddenByCrystal = true;
                    dirty = true;
                }
                if (hasWork && !existing.HasSavedWorkArea)
                {
                    existing.HasSavedWorkArea = true;
                    existing.WorkLeft = workLeft!.Value;
                    existing.WorkTop = workTop!.Value;
                    existing.WorkRight = workRight!.Value;
                    existing.WorkBottom = workBottom!.Value;
                    dirty = true;
                }
                if (dirty) File.WriteAllText(_taskbarBackupPath, JsonSerializer.Serialize(existing));
            }
            return;
        }
        var backup = new TaskbarStateBackup
        {
            Saved = true,
            WasAutoHide = wasAutoHide,
            WasWindowHiddenByCrystal = hiddenByCrystal,
            HasSavedWorkArea = hasWork,
            WorkLeft = workLeft ?? 0,
            WorkTop = workTop ?? 0,
            WorkRight = workRight ?? 0,
            WorkBottom = workBottom ?? 0,
            SavedAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(_taskbarBackupPath, JsonSerializer.Serialize(backup));
    }

    public TaskbarStateBackup? ReadTaskbarBackup()
    {
        if (!File.Exists(_taskbarBackupPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<TaskbarStateBackup>(File.ReadAllText(_taskbarBackupPath));
        }
        catch
        {
            return null;
        }
    }

    public void ClearTaskbarBackup()
    {
        if (File.Exists(_taskbarBackupPath)) File.Delete(_taskbarBackupPath);
    }

    private int ReadCrashCount()
    {
        if (!File.Exists(_crashCountPath)) return 0;
        return int.TryParse(File.ReadAllText(_crashCountPath).Trim(), out int n) ? n : 0;
    }
}
