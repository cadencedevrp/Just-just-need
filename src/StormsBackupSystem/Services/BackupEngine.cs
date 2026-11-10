using System.IO.Compression;
using StormsBackupSystem.Core;

namespace StormsBackupSystem.Services;

public sealed class BackupResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public string? ArchivePath { get; set; }
    public long ArchiveSize { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Runs one complete backup: database dump (optional) → zip the server folders →
/// upload to Google Drive and/or GitHub → clean up old copies → notify.
/// </summary>
public sealed class BackupEngine
{
    private static readonly SemaphoreSlim InProcessGate = new(1, 1);

    public static bool IsRunning => InProcessGate.CurrentCount == 0;

    public async Task<BackupResult> RunAsync(string trigger, CancellationToken ct)
    {
        var result = new BackupResult();

        if (!await InProcessGate.WaitAsync(0, ct))
        {
            result.Errors.Add("A backup is already running.");
            result.Summary = "Skipped: a backup is already running.";
            return result;
        }

        try
        {
            using var crossProcess = CrossProcessLock.TryAcquire();
            if (crossProcess is null)
            {
                result.Errors.Add("Another copy of the program is already running a backup.");
                result.Summary = "Skipped: another backup is already running.";
                Log.Warn(result.Summary);
                return result;
            }

            return await RunCoreAsync(trigger, result, ct);
        }
        finally
        {
            InProcessGate.Release();
        }
    }

    private static async Task<BackupResult> RunCoreAsync(string trigger, BackupResult result, CancellationToken ct)
    {
        var started = DateTime.Now;
        var settings = SettingsStore.LoadSettings();
        var state = SettingsStore.LoadState();
        var workDir = Path.Combine(AppPaths.TempDirectory, $"run-{started:yyyyMMdd-HHmmss}");

        Log.Info($"===== Backup started ({trigger}) =====");

        try
        {
            var sources = settings.SourceFolders.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (sources.Count == 0 && !settings.MySqlEnabled)
            {
                throw new InvalidOperationException("Nothing to back up: add at least one server folder on the Sources tab.");
            }

            foreach (var missing in sources.Where(s => !Directory.Exists(s)))
            {
                result.Warnings.Add($"Source folder not found: {missing}");
                Log.Warn($"Source folder not found: {missing}");
            }

            Directory.CreateDirectory(workDir);
            var localFolder = string.IsNullOrWhiteSpace(settings.LocalBackupFolder) ? AppPaths.DefaultBackupDirectory : settings.LocalBackupFolder;
            Directory.CreateDirectory(localFolder);

            // 1) Database dump
            var extras = new List<ArchiveExtraFile>();
            if (settings.MySqlEnabled)
            {
                var dumpFile = Path.Combine(workDir, "mysql-dump.sql");
                try
                {
                    Log.Info("Dumping MySQL database(s)...");
                    await MySqlDumper.DumpAsync(settings, dumpFile, ct);
                    extras.Add(new ArchiveExtraFile(dumpFile, "_database/mysql-dump.sql"));
                    Log.Info($"Database dump complete ({ArchiveBuilder.FormatBytes(new FileInfo(dumpFile).Length)}).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors.Add("Database dump failed: " + ex.Message);
                    Log.Error("Database dump failed", ex);
                }
            }

            // 2) Zip
            var archiveName = $"{SafePrefix(settings.BackupNamePrefix)}_{started:yyyy-MM-dd_HH-mm-ss}.zip";
            var archivePath = Path.Combine(localFolder, archiveName);
            Log.Info($"Creating archive {archivePath}...");

            var summary = await Task.Run(
                () => ArchiveBuilder.Create(
                    archivePath,
                    sources,
                    new ExcludeMatcher(settings.ExcludePatterns),
                    extras,
                    new[] { localFolder, AppPaths.DataDirectory },
                    CompressionLevel.Optimal,
                    ct,
                    Log.Info),
                ct);

            foreach (var problem in summary.Problems.Take(50))
            {
                Log.Warn(problem);
            }

            if (summary.Problems.Count > 0)
            {
                result.Warnings.Add($"{summary.Problems.Count} file(s)/folder(s) had problems (see log / _backup-info.txt in the zip).");
            }

            result.ArchivePath = archivePath;
            result.ArchiveSize = new FileInfo(archivePath).Length;
            Log.Info($"Archive ready: {summary.FilesAdded:N0} files, {ArchiveBuilder.FormatBytes(summary.BytesAdded)} → {ArchiveBuilder.FormatBytes(result.ArchiveSize)} compressed.");

            // 3) Uploads
            var destinations = new List<string>();
            if (settings.GoogleDriveEnabled)
            {
                try
                {
                    await GoogleDriveUploader.UploadAsync(archivePath, settings, state, ct);
                    destinations.Add("Google Drive");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    result.Errors.Add("Google Drive upload failed: " + ex.Message);
                    Log.Error("Google Drive upload failed", ex);
                }
            }

            if (settings.GitHubEnabled)
            {
                try
                {
                    using var github = new GitHubUploader(
                        settings.GitHubOwner,
                        settings.GitHubRepository,
                        SecretProtector.Unprotect(settings.GitHubTokenProtected));
                    await github.UploadAsync(archivePath, settings, ct);
                    destinations.Add("GitHub");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    result.Errors.Add("GitHub upload failed: " + ex.Message);
                    Log.Error("GitHub upload failed", ex);
                }
            }

            if (!settings.GoogleDriveEnabled && !settings.GitHubEnabled)
            {
                result.Warnings.Add("No cloud destination is enabled - the backup was only saved locally.");
                Log.Warn(result.Warnings[^1]);
            }

            // 4) Local retention
            ApplyLocalRetention(localFolder, settings);

            result.Success = result.Errors.Count == 0;
            var where = destinations.Count > 0 ? "uploaded to " + string.Join(" and ", destinations) : "saved locally";
            result.Summary = $"{archiveName} ({ArchiveBuilder.FormatBytes(result.ArchiveSize)}) {where}.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Errors.Add("Backup was cancelled.");
            result.Summary = "Backup was cancelled.";
            Log.Warn(result.Summary);
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
            result.Summary = "Backup failed: " + ex.Message;
            Log.Error("Backup failed", ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }

        result.Duration = DateTime.Now - started;
        if (string.IsNullOrEmpty(result.Summary))
        {
            result.Summary = "Backup finished with errors.";
        }

        // Record the attempt for the scheduler and the dashboard.
        state.LastAttemptUtc = started.ToUniversalTime();
        state.LastAttemptFailed = !result.Success;
        state.ConsecutiveFailures = result.Success ? 0 : state.ConsecutiveFailures + 1;
        state.LastMessage = result.Success && result.Warnings.Count > 0
            ? result.Summary + " (with warnings)"
            : result.Success ? result.Summary : string.Join(" | ", result.Errors);
        if (result.Success)
        {
            state.LastSuccessUtc = started.ToUniversalTime();
        }

        if (result.ArchivePath is not null)
        {
            state.LastBackupFile = result.ArchivePath;
            state.LastBackupSizeBytes = result.ArchiveSize;
        }

        try
        {
            SettingsStore.SaveState(state);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save state", ex);
        }

        Log.Info($"===== Backup {(result.Success ? "SUCCEEDED" : "FAILED")} in {result.Duration:hh\\:mm\\:ss} - {result.Summary} =====");

        var webhook = SecretProtector.Unprotect(settings.DiscordWebhookProtected);
        if (!string.IsNullOrWhiteSpace(webhook) && (!result.Success || settings.DiscordNotifyOnSuccess))
        {
            await DiscordNotifier.SendAsync(webhook, result);
        }

        return result;
    }

    private static void ApplyLocalRetention(string folder, AppSettings settings)
    {
        if (settings.KeepLocalCopies <= 0)
        {
            return;
        }

        var old = Directory.GetFiles(folder, $"{SafePrefix(settings.BackupNamePrefix)}_*.zip")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(Math.Max(1, settings.KeepLocalCopies));

        foreach (var file in old)
        {
            try
            {
                File.Delete(file);
                Log.Info($"Removed old local backup {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete old backup {file}: {ex.Message}");
            }
        }
    }

    internal static string SafePrefix(string prefix)
    {
        var cleaned = new string((prefix ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()).Trim('_', '-');
        return cleaned.Length == 0 ? "server-backup" : cleaned;
    }

    /// <summary>Prevents the tray app and a command-line run from backing up at the same time.</summary>
    private sealed class CrossProcessLock : IDisposable
    {
        private readonly Semaphore? _semaphore;

        private CrossProcessLock(Semaphore? semaphore) => _semaphore = semaphore;

        public static CrossProcessLock? TryAcquire()
        {
            try
            {
                var semaphore = new Semaphore(1, 1, @"Local\StormsTechnologiesBackupSystem_Running");
                if (semaphore.WaitOne(0))
                {
                    return new CrossProcessLock(semaphore);
                }

                semaphore.Dispose();
                return null;
            }
            catch (PlatformNotSupportedException)
            {
                return new CrossProcessLock(null);
            }
        }

        public void Dispose()
        {
            if (_semaphore is not null)
            {
                _semaphore.Release();
                _semaphore.Dispose();
            }
        }
    }
}
