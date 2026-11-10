using System.IO.Compression;
using System.Text;

namespace StormsBackupSystem.Core;

/// <summary>An extra file (e.g. a database dump) to put into the archive under <see cref="EntryName"/>.</summary>
public sealed record ArchiveExtraFile(string SourcePath, string EntryName);

public sealed class ArchiveSummary
{
    public int FilesAdded { get; set; }
    public int FilesSkipped { get; set; }
    public long BytesAdded { get; set; }
    public List<string> Problems { get; } = new();
}

/// <summary>Builds the backup .zip from one or more server folders.</summary>
public static class ArchiveBuilder
{
    private const int BufferSize = 1 << 20;
    private static readonly DateTime MinZipDate = new(1980, 1, 2);
    private static readonly DateTime MaxZipDate = new(2107, 12, 30);

    /// <param name="zipPath">Final archive path. It is written to "*.partial" first and renamed when complete.</param>
    /// <param name="sourceFolders">Folders to back up. Each one becomes a top-level folder in the zip.</param>
    /// <param name="excludes">Exclusion rules, evaluated relative to each source folder.</param>
    /// <param name="extras">Additional files to add (database dumps, etc.).</param>
    /// <param name="skipAbsolutePaths">Folders never to descend into (e.g. the backup output folder itself).</param>
    public static ArchiveSummary Create(
        string zipPath,
        IReadOnlyList<string> sourceFolders,
        ExcludeMatcher excludes,
        IReadOnlyList<ArchiveExtraFile> extras,
        IEnumerable<string> skipAbsolutePaths,
        CompressionLevel level,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        var summary = new ArchiveSummary();
        var skip = skipAbsolutePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeDir)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var partial = zipPath + ".partial";

        try
        {
            using (var fs = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var usedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var buffer = new byte[BufferSize];

                foreach (var source in sourceFolders)
                {
                    var root = Path.GetFullPath(source);
                    if (!Directory.Exists(root))
                    {
                        summary.Problems.Add($"Source folder not found: {root}");
                        continue;
                    }

                    var rootName = UniqueRootName(root, usedRoots);
                    progress?.Invoke($"Adding {root} as \\{rootName}");
                    AddDirectoryTree(zip, root, rootName, excludes, skip, level, summary, buffer, cancellationToken, progress);
                }

                foreach (var extra in extras)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddFile(zip, extra.SourcePath, extra.EntryName, level, summary, buffer, cancellationToken);
                }

                WriteManifest(zip, sourceFolders, extras, summary);
            }

            File.Move(partial, zipPath, overwrite: true);
            return summary;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void AddDirectoryTree(
        ZipArchive zip,
        string root,
        string rootName,
        ExcludeMatcher excludes,
        List<string> skip,
        CompressionLevel level,
        ArchiveSummary summary,
        byte[] buffer,
        CancellationToken ct,
        Action<string>? progress)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var lastReport = DateTime.UtcNow;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.GetFiles(dir);
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                summary.Problems.Add($"Cannot read folder {dir}: {ex.Message}");
                continue;
            }

            var relDir = Path.GetRelativePath(root, dir);
            var entryDir = relDir == "." ? rootName : $"{rootName}/{relDir.Replace('\\', '/')}";

            var addedSomething = false;

            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(root, file);
                if (excludes.IsExcluded(rel))
                {
                    continue;
                }

                AddFile(zip, file, $"{entryDir}/{Path.GetFileName(file)}", level, summary, buffer, ct);
                addedSomething = true;

                if ((DateTime.UtcNow - lastReport).TotalSeconds >= 10)
                {
                    lastReport = DateTime.UtcNow;
                    progress?.Invoke($"  ... {summary.FilesAdded:N0} files, {FormatBytes(summary.BytesAdded)} so far");
                }
            }

            foreach (var sub in subDirs)
            {
                var rel = Path.GetRelativePath(root, sub);
                if (excludes.IsExcluded(rel))
                {
                    continue;
                }

                var normalized = NormalizeDir(sub);
                if (skip.Any(s => normalized.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    // Don't follow symlinks / junctions - they can loop or point outside the server.
                    if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    summary.Problems.Add($"Cannot read folder {sub}: {ex.Message}");
                    continue;
                }

                pending.Push(sub);
                addedSomething = true;
            }

            if (!addedSomething && dir != root)
            {
                // keep empty folders so the restored server layout is identical
                zip.CreateEntry(entryDir + "/");
            }
        }
    }

    private static void AddFile(
        ZipArchive zip,
        string path,
        string entryName,
        CompressionLevel level,
        ArchiveSummary summary,
        byte[] buffer,
        CancellationToken ct)
    {
        FileStream input;
        try
        {
            // FileShare.ReadWrite lets us copy files a running server still has open (logs, databases...).
            input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            summary.FilesSkipped++;
            summary.Problems.Add($"Skipped locked/unreadable file {path}: {ex.Message}");
            return;
        }

        using (input)
        {
            var entry = zip.CreateEntry(entryName, level);
            try
            {
                var modified = File.GetLastWriteTime(path);
                entry.LastWriteTime = modified < MinZipDate ? MinZipDate : modified > MaxZipDate ? MaxZipDate : modified;
            }
            catch
            {
                // keep default timestamp
            }

            using var output = entry.Open();
            try
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    summary.BytesAdded += read;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                summary.Problems.Add($"File may be incomplete in backup (changed or locked while reading) {path}: {ex.Message}");
            }

            summary.FilesAdded++;
        }
    }

    private static void WriteManifest(ZipArchive zip, IReadOnlyList<string> sources, IReadOnlyList<ArchiveExtraFile> extras, ArchiveSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Storms Technologies Backup System");
        sb.AppendLine("=================================");
        sb.AppendLine($"Created:  {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"Machine:  {Environment.MachineName}");
        sb.AppendLine($"Files:    {summary.FilesAdded:N0} ({FormatBytes(summary.BytesAdded)} uncompressed)");
        sb.AppendLine($"Skipped:  {summary.FilesSkipped:N0}");
        sb.AppendLine();
        sb.AppendLine("Source folders:");
        foreach (var s in sources)
        {
            sb.AppendLine("  " + Path.GetFullPath(s));
        }

        if (extras.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Extra files:");
            foreach (var e in extras)
            {
                sb.AppendLine("  " + e.EntryName);
            }
        }

        if (summary.Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Problems:");
            foreach (var p in summary.Problems)
            {
                sb.AppendLine("  " + p);
            }
        }

        var entry = zip.CreateEntry("_backup-info.txt", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(sb.ToString());
    }

    private static string UniqueRootName(string root, HashSet<string> used)
    {
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            // a whole drive, e.g. "D:\"
            name = "drive_" + root.TrimEnd(':', '\\', '/').Replace(":", string.Empty);
        }

        var candidate = name;
        var n = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{name}_{n++}";
        }

        return candidate;
    }

    private static string NormalizeDir(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
