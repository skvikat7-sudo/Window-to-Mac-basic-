using System.Collections.Concurrent;
using System.Text;

namespace ProjectCrystal.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);

public static class CrystalLogger
{
    private static readonly object _fileLock = new();
    private static readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int MaxRecent = 400;

    public static string LogDirectory { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectCrystal", "logs");

    public static string CurrentLogPath => Path.Combine(
        LogDirectory, $"crystal-{DateTime.Now:yyyyMMdd}.log");

    public static void Initialize(string? customLogDir = null)
    {
        if (!string.IsNullOrWhiteSpace(customLogDir))
            LogDirectory = customLogDir;
        Directory.CreateDirectory(LogDirectory);
        Info("System", $"Project Crystal logger initialized at {CurrentLogPath}");
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public static void Error(string category, string message, Exception? ex = null)
        => Write(LogLevel.Error, category, ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static IReadOnlyList<LogEntry> GetRecentEntries() => _recent.ToArray();

    private static void Write(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        _recent.Enqueue(entry);
        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }

        string line = $"[{entry.Timestamp:HH:mm:ss.fff}] [{level,-5}] [{category}] {message}{Environment.NewLine}";
        lock (_fileLock)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
            catch
            {
                // Avoid secondary crash if disk is temporarily busy
            }
        }
    }
}
