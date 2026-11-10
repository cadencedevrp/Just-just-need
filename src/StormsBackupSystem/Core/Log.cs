namespace StormsBackupSystem.Core;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Tiny thread-safe logger that writes daily log files and raises an event for the UI.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static event Action<DateTime, LogLevel, string>? MessageLogged;

    public static string? Directory => _directory;

    public static void Initialize(string directory, int keepDays = 30)
    {
        _directory = directory;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in System.IO.Directory.GetFiles(directory, "backup-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Logging must never take the application down.
        }
    }

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        var now = DateTime.Now;
        var tag = level switch
        {
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "INFO ",
        };

        lock (Gate)
        {
            if (_directory is not null)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(_directory, $"backup-{now:yyyy-MM-dd}.log"),
                        $"{now:yyyy-MM-dd HH:mm:ss} [{tag}] {message}{Environment.NewLine}");
                }
                catch
                {
                    // ignore
                }
            }
        }

        try
        {
            MessageLogged?.Invoke(now, level, message);
        }
        catch
        {
            // ignore subscriber failures
        }
    }
}
