using System.IO.Compression;
using StormsBackupSystem.Core;
using Xunit;

namespace StormsBackupSystem.Tests;

public sealed class ArchiveBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "storms-tests-" + Guid.NewGuid().ToString("N"));

    public ArchiveBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ZipsServerFolderWithExclusionsExtrasAndManifest()
    {
        Write("server/server.cfg", "endpoint_add_tcp 0.0.0.0:30120");
        Write("server/resources/[local]/myres/fxmanifest.lua", "fx_version 'cerulean'");
        Write("server/cache/files/big.bin", "junk");
        Write("server/logs/today.log", "log");
        Directory.CreateDirectory(Path.Combine(_root, "server", "empty"));
        var dump = Write("dump.sql", "CREATE TABLE users (id int);");
        var backups = Path.Combine(_root, "server", "backups"); // output folder inside the source must be skipped
        Directory.CreateDirectory(backups);
        Write("server/backups/old.zip", "previous backup");

        var zipPath = Path.Combine(backups, "server-backup_test.zip");
        var summary = ArchiveBuilder.Create(
            zipPath,
            new[] { Path.Combine(_root, "server") },
            new ExcludeMatcher(new[] { "cache", "*.log" }),
            new[] { new ArchiveExtraFile(dump, "_database/mysql-dump.sql") },
            new[] { backups },
            CompressionLevel.Fastest,
            CancellationToken.None);

        Assert.True(File.Exists(zipPath));
        Assert.False(File.Exists(zipPath + ".partial"));
        Assert.Equal(3, summary.FilesAdded); // server.cfg, fxmanifest.lua, dump
        Assert.Empty(summary.Problems);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("server/server.cfg", names);
        Assert.Contains("server/resources/[local]/myres/fxmanifest.lua", names);
        Assert.Contains("server/empty/", names);
        Assert.Contains("_database/mysql-dump.sql", names);
        Assert.Contains("_backup-info.txt", names);
        Assert.DoesNotContain(names, n => n.Contains("cache") || n.EndsWith(".log") || n.Contains("backups"));

        using var reader = new StreamReader(zip.GetEntry("server/server.cfg")!.Open());
        Assert.Equal("endpoint_add_tcp 0.0.0.0:30120", reader.ReadToEnd());
    }

    [Fact]
    public void DuplicateFolderNamesGetUniqueRoots()
    {
        Write("a/server/one.txt", "1");
        Write("b/server/two.txt", "2");
        var zipPath = Path.Combine(_root, "out.zip");

        ArchiveBuilder.Create(
            zipPath,
            new[] { Path.Combine(_root, "a", "server"), Path.Combine(_root, "b", "server") },
            new ExcludeMatcher(Array.Empty<string>()),
            Array.Empty<ArchiveExtraFile>(),
            Array.Empty<string>(),
            CompressionLevel.Fastest,
            CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("server/one.txt", names);
        Assert.Contains("server_2/two.txt", names);
    }

    [Fact]
    public void MissingSourceIsReportedNotFatal()
    {
        var zipPath = Path.Combine(_root, "out.zip");
        var summary = ArchiveBuilder.Create(
            zipPath,
            new[] { Path.Combine(_root, "does-not-exist") },
            new ExcludeMatcher(Array.Empty<string>()),
            Array.Empty<ArchiveExtraFile>(),
            Array.Empty<string>(),
            CompressionLevel.Fastest,
            CancellationToken.None);

        Assert.Single(summary.Problems);
        Assert.True(File.Exists(zipPath));
    }

    [Fact]
    public void CancellationRemovesPartialFile()
    {
        Write("server/file.txt", "x");
        var zipPath = Path.Combine(_root, "out.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveBuilder.Create(
            zipPath,
            new[] { Path.Combine(_root, "server") },
            new ExcludeMatcher(Array.Empty<string>()),
            Array.Empty<ArchiveExtraFile>(),
            Array.Empty<string>(),
            CompressionLevel.Fastest,
            cts.Token));

        Assert.False(File.Exists(zipPath));
        Assert.False(File.Exists(zipPath + ".partial"));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void FormatsSizes(long bytes, string expected) =>
        Assert.Equal(expected, ArchiveBuilder.FormatBytes(bytes).Replace(',', '.'));
}
