using System.Text.Json;
using System.Text.Json.Serialization;

namespace StormsBackupSystem.Services;

/// <summary>User configuration, stored as JSON in %LOCALAPPDATA%. Secrets are DPAPI-encrypted.</summary>
public sealed class AppSettings
{
    // ---- What to back up ----
    public List<string> SourceFolders { get; set; } = new();

    public List<string> ExcludePatterns { get; set; } = new()
    {
        "# One rule per line. Examples:",
        "cache",
        "*.tmp",
        "*.dmp",
    };

    public string LocalBackupFolder { get; set; } = AppPaths.DefaultBackupDirectory;
    public int KeepLocalCopies { get; set; } = 7;
    public string BackupNamePrefix { get; set; } = "server-backup";

    // ---- Optional MySQL / MariaDB dump ----
    public bool MySqlEnabled { get; set; }
    public string MySqlDumpPath { get; set; } = string.Empty;
    public string MySqlHost { get; set; } = "127.0.0.1";
    public int MySqlPort { get; set; } = 3306;
    public string MySqlUser { get; set; } = "root";
    public string MySqlPasswordProtected { get; set; } = string.Empty;

    /// <summary>Comma separated. Empty = all databases.</summary>
    public string MySqlDatabases { get; set; } = string.Empty;

    // ---- Schedule ----
    public int IntervalHours { get; set; } = 24;
    public string BackupTime { get; set; } = "03:00";
    public bool StartWithWindows { get; set; } = true;

    // ---- Google Drive ----
    public bool GoogleDriveEnabled { get; set; }
    public string GoogleClientSecretPath { get; set; } = string.Empty;
    public string GoogleDriveFolderName { get; set; } = "Storms Technologies Backups";
    public int GoogleDriveKeepCopies { get; set; } = 14;

    // ---- GitHub ----
    public bool GitHubEnabled { get; set; }
    public string GitHubOwner { get; set; } = string.Empty;
    public string GitHubRepository { get; set; } = string.Empty;
    public string GitHubTokenProtected { get; set; } = string.Empty;
    public int GitHubKeepCopies { get; set; } = 14;

    // ---- Notifications ----
    public string DiscordWebhookProtected { get; set; } = string.Empty;
    public bool DiscordNotifyOnSuccess { get; set; } = true;

    [JsonIgnore]
    public TimeSpan BackupTimeOfDay =>
        TimeSpan.TryParse(BackupTime, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t
            : new TimeSpan(3, 0, 0);
}

/// <summary>Runtime state written by the backup engine (last run, Google folder id, ...).</summary>
public sealed class AppState
{
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public bool LastAttemptFailed { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public string LastBackupFile { get; set; } = string.Empty;
    public long LastBackupSizeBytes { get; set; }
    public string GoogleDriveFolderId { get; set; } = string.Empty;
    public string GoogleDriveFolderName { get; set; } = string.Empty;
}

public static class SettingsStore
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings LoadSettings() => Load<AppSettings>(AppPaths.SettingsFile);

    public static void SaveSettings(AppSettings settings) => Save(AppPaths.SettingsFile, settings);

    public static AppState LoadState() => Load<AppState>(AppPaths.StateFile);

    public static void SaveState(AppState state) => Save(AppPaths.StateFile, state);

    private static T Load<T>(string path)
        where T : new()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
                }
            }
            catch (Exception ex)
            {
                Core.Log.Error($"Could not read {Path.GetFileName(path)}, using defaults", ex);
            }

            return new T();
        }
    }

    private static void Save<T>(string path, T value)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
