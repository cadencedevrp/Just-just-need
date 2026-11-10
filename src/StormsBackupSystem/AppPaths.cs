namespace StormsBackupSystem;

/// <summary>Well-known names and folders used by the application.</summary>
internal static class AppPaths
{
    public const string AppName = "Storms Technologies Backup System";
    public const string ShortName = "StormsBackupSystem";

    /// <summary>%LOCALAPPDATA%\Storms Technologies\Backup System</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Storms Technologies",
        "Backup System");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string GoogleTokenDirectory => Path.Combine(DataDirectory, "google-token");
    public static string TempDirectory => Path.Combine(DataDirectory, "temp");
    public static string DefaultBackupDirectory => Path.Combine(DataDirectory, "Backups");
}
