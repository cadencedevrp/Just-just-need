using System.Diagnostics;
using System.Text;

namespace StormsBackupSystem.Services;

/// <summary>Dumps MySQL / MariaDB databases with mysqldump so they can be included in the backup.</summary>
public static class MySqlDumper
{
    public static string? FindMySqlDump(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        var candidates = new List<string>();
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 }.Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            foreach (var vendor in new[] { "MySQL", "MariaDB*" })
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(programFiles, vendor))
                    {
                        candidates.AddRange(Directory.GetFiles(dir, "mysqldump.exe", SearchOption.AllDirectories));
                        candidates.AddRange(Directory.GetFiles(dir, "mariadb-dump.exe", SearchOption.AllDirectories));
                    }
                }
                catch
                {
                    // ignore folders we can't read
                }
            }
        }

        candidates.Add(@"C:\xampp\mysql\bin\mysqldump.exe");
        candidates.Add(@"C:\laragon\bin\mysql\mysqldump.exe");

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                candidates.Add(Path.Combine(dir.Trim(), "mysqldump.exe"));
                candidates.Add(Path.Combine(dir.Trim(), "mariadb-dump.exe"));
            }
        }

        return candidates.Where(File.Exists).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public static async Task DumpAsync(AppSettings settings, string outputFile, CancellationToken ct)
    {
        var exe = FindMySqlDump(settings.MySqlDumpPath)
                  ?? throw new FileNotFoundException("mysqldump.exe was not found. Set its location on the Sources tab.");

        var password = SecretProtector.Unprotect(settings.MySqlPasswordProtected);

        // Credentials go in a temporary option file so they never appear on a command line.
        var optionFile = Path.Combine(Path.GetDirectoryName(outputFile)!, $"client-{Guid.NewGuid():N}.cnf");
        var cnf = new StringBuilder()
            .AppendLine("[client]")
            .AppendLine($"host={settings.MySqlHost}")
            .AppendLine($"port={settings.MySqlPort}")
            .AppendLine($"user={settings.MySqlUser}")
            .AppendLine($"password=\"{password.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        await File.WriteAllTextAsync(optionFile, cnf.ToString(), new UTF8Encoding(false), ct);

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            psi.ArgumentList.Add($"--defaults-extra-file={optionFile}"); // must be first
            psi.ArgumentList.Add("--single-transaction");
            psi.ArgumentList.Add("--routines");
            psi.ArgumentList.Add("--triggers");
            psi.ArgumentList.Add("--events");
            psi.ArgumentList.Add("--default-character-set=utf8mb4");
            psi.ArgumentList.Add($"--result-file={outputFile}");

            var databases = settings.MySqlDatabases
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (databases.Length == 0)
            {
                psi.ArgumentList.Add("--all-databases");
            }
            else
            {
                psi.ArgumentList.Add("--databases");
                foreach (var db in databases)
                {
                    psi.ArgumentList.Add(db);
                }
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start mysqldump.");
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }

            var stderr = (await stderrTask).Trim();
            await stdoutTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"mysqldump failed (exit code {process.ExitCode}): {stderr}");
            }

            if (!string.IsNullOrEmpty(stderr))
            {
                Core.Log.Warn("mysqldump: " + stderr);
            }
        }
        finally
        {
            try { File.Delete(optionFile); } catch { /* ignore */ }
        }
    }
}
