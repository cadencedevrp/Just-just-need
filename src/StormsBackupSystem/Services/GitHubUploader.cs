using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StormsBackupSystem.Core;

namespace StormsBackupSystem.Services;

/// <summary>
/// Stores backups on GitHub as Release assets in a PRIVATE repository
/// (one release per backup, tagged "backup-yyyyMMdd-HHmmss").
/// Release assets can be up to 2 GB, so bigger archives are split into numbered parts.
/// </summary>
public sealed class GitHubUploader : IDisposable
{
    public const string TagPrefix = "backup-";

    /// <summary>GitHub's limit is "under 2 GiB" per asset; stay safely below it.</summary>
    internal const long MaxAssetBytes = 1_900L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubUploader(string owner, string repo, string token)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GitHub owner, repository and token must all be filled in on the Destinations tab.");
        }

        _owner = owner.Trim();
        _repo = repo.Trim();
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StormsTechnologiesBackupSystem/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public sealed record RepositoryInfo(string FullName, bool IsPrivate, string DefaultBranch, bool CanPush);

    public void Dispose() => _http.Dispose();

    private string Api(string path = "") =>
        $"https://api.github.com/repos/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repo)}{path}";

    public async Task<RepositoryInfo> CheckRepositoryAsync(CancellationToken ct)
    {
        using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        shortTimeout.CancelAfter(TimeSpan.FromSeconds(60));

        using var response = await _http.GetAsync(Api(), shortTimeout.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("GitHub rejected the token (expired or invalid).");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Repository {_owner}/{_repo} was not found, or the token has no access to it.");
        }

        await EnsureSuccessAsync(response, "read repository");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return new RepositoryInfo(
            json["full_name"]?.GetValue<string>() ?? $"{_owner}/{_repo}",
            json["private"]?.GetValue<bool>() ?? false,
            json["default_branch"]?.GetValue<string>() ?? "main",
            json["permissions"]?["push"]?.GetValue<bool>() ?? true);
    }

    public async Task UploadAsync(string filePath, AppSettings settings, CancellationToken ct)
    {
        var repo = await CheckRepositoryAsync(ct);
        if (!repo.IsPrivate)
        {
            throw new InvalidOperationException(
                $"Refusing to upload: {repo.FullName} is a PUBLIC repository, so anyone could download your server backup. Make the repository private first.");
        }

        if (!repo.CanPush)
        {
            throw new InvalidOperationException($"The token does not have write access to {repo.FullName}.");
        }

        await EnsureRepositoryHasCommitAsync(ct);

        var fileName = Path.GetFileName(filePath);
        var length = new FileInfo(filePath).Length;
        var tag = TagPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var parts = SplitIfNeeded(filePath, length);

        try
        {
            var body = new StringBuilder()
                .AppendLine($"Automatic server backup from **{Environment.MachineName}** created by {AppPaths.AppName}.")
                .AppendLine()
                .AppendLine($"- Archive: `{fileName}`")
                .AppendLine($"- Size: {ArchiveBuilder.FormatBytes(length)}")
                .AppendLine($"- Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");

            if (parts.Count > 1)
            {
                body.AppendLine()
                    .AppendLine($"The archive was split into {parts.Count} parts. Download all parts into one folder and join them in Command Prompt:")
                    .AppendLine()
                    .AppendLine("```")
                    .AppendLine($"copy /b {string.Join("+", parts.Select(Path.GetFileName))} {fileName}")
                    .AppendLine("```");
            }

            var release = await CreateReleaseAsync(tag, repo.DefaultBranch, $"Server backup {DateTime.Now:yyyy-MM-dd HH:mm}", body.ToString(), ct);

            try
            {
                foreach (var part in parts)
                {
                    await UploadAssetAsync(release.uploadUrl, part, ct);
                }
            }
            catch
            {
                Log.Warn("GitHub: upload failed, removing the incomplete release.");
                await DeleteReleaseAsync(release.id, tag, CancellationToken.None);
                throw;
            }

            Log.Info($"GitHub: backup stored as release \"{tag}\" in {repo.FullName}.");
        }
        finally
        {
            if (parts.Count > 1)
            {
                foreach (var part in parts)
                {
                    try { File.Delete(part); } catch { /* ignore */ }
                }
            }
        }

        await ApplyRetentionAsync(settings.GitHubKeepCopies, ct);
    }

    private async Task EnsureRepositoryHasCommitAsync(CancellationToken ct)
    {
        // Releases need a commit to tag. A brand new empty repository has none, so add a README.
        using var response = await _http.GetAsync(Api("/commits?per_page=1"), ct);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            await EnsureSuccessAsync(response, "check repository");
            return;
        }

        Log.Info("GitHub: repository is empty, adding a README so releases can be created.");
        var readme = "# Server backups\n\nThis private repository stores server backups as **Releases**, uploaded automatically by "
                     + AppPaths.AppName + ".\n";
        var payload = new JsonObject
        {
            ["message"] = "Initialize backup repository",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(readme)),
        };

        using var put = await _http.PutAsync(Api("/contents/README.md"), JsonBody(payload), ct);
        await EnsureSuccessAsync(put, "initialize repository");
    }

    private async Task<(long id, string uploadUrl)> CreateReleaseAsync(string tag, string branch, string name, string body, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["tag_name"] = tag,
            ["target_commitish"] = branch,
            ["name"] = name,
            ["body"] = body,
            ["draft"] = false,
            ["prerelease"] = false,
        };

        using var response = await _http.PostAsync(Api("/releases"), JsonBody(payload), ct);
        await EnsureSuccessAsync(response, "create release");
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var uploadUrl = json["upload_url"]!.GetValue<string>();
        var brace = uploadUrl.IndexOf('{');
        if (brace >= 0)
        {
            uploadUrl = uploadUrl[..brace];
        }

        return (json["id"]!.GetValue<long>(), uploadUrl);
    }

    private async Task UploadAssetAsync(string uploadUrl, string path, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        var length = new FileInfo(path).Length;
        Log.Info($"GitHub: uploading {name} ({ArchiveBuilder.FormatBytes(length)})...");

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        await using var progress = new ProgressStream(file, length, p => Log.Info($"GitHub: {p}% uploaded"));
        using var content = new StreamContent(progress, 1 << 20);
        content.Headers.ContentType = new MediaTypeHeaderValue(name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip" : "application/octet-stream");
        content.Headers.ContentLength = length;

        using var response = await _http.PostAsync($"{uploadUrl}?name={Uri.EscapeDataString(name)}", content, ct);
        await EnsureSuccessAsync(response, "upload asset");
    }

    private async Task ApplyRetentionAsync(int keep, CancellationToken ct)
    {
        if (keep <= 0)
        {
            return;
        }

        var releases = new List<(long id, string tag, DateTime created)>();
        for (var page = 1; page <= 20; page++)
        {
            using var response = await _http.GetAsync(Api($"/releases?per_page=100&page={page}"), ct);
            await EnsureSuccessAsync(response, "list releases");
            var array = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsArray();
            foreach (var r in array)
            {
                var tag = r?["tag_name"]?.GetValue<string>() ?? string.Empty;
                if (tag.StartsWith(TagPrefix, StringComparison.Ordinal))
                {
                    releases.Add((r!["id"]!.GetValue<long>(), tag, r["created_at"]!.GetValue<DateTime>()));
                }
            }

            if (array.Count < 100)
            {
                break;
            }
        }

        foreach (var old in releases.OrderByDescending(r => r.created).Skip(Math.Max(1, keep)))
        {
            await DeleteReleaseAsync(old.id, old.tag, ct);
            Log.Info($"GitHub: removed old backup release {old.tag}");
        }
    }

    private async Task DeleteReleaseAsync(long id, string tag, CancellationToken ct)
    {
        try
        {
            using (await _http.DeleteAsync(Api($"/releases/{id}"), ct)) { }
            using (await _http.DeleteAsync(Api($"/git/refs/tags/{Uri.EscapeDataString(tag)}"), ct)) { }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"GitHub: could not delete release {tag}: {ex.Message}");
        }
    }

    internal static List<string> SplitIfNeeded(string filePath, long length, long maxPartBytes = MaxAssetBytes)
    {
        if (length <= maxPartBytes)
        {
            return new List<string> { filePath };
        }

        var parts = new List<string>();
        var dir = Path.Combine(AppPaths.TempDirectory, "github-parts");
        Directory.CreateDirectory(dir);
        var buffer = new byte[1 << 20];

        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var index = 1;
        while (input.Position < length)
        {
            var partPath = Path.Combine(dir, $"{Path.GetFileName(filePath)}.{index++:000}");
            using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write))
            {
                long remaining = Math.Min(maxPartBytes, length - input.Position);
                while (remaining > 0)
                {
                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }

            parts.Add(partPath);
        }

        Log.Info($"GitHub: archive is larger than 2 GB, split into {parts.Count} parts.");
        return parts;
    }

    private static StringContent JsonBody(JsonNode node) =>
        new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync();
        string message;
        try
        {
            message = JsonDocument.Parse(text).RootElement.GetProperty("message").GetString() ?? text;
        }
        catch
        {
            message = text.Length > 300 ? text[..300] : text;
        }

        throw new InvalidOperationException($"GitHub could not {action}: {(int)response.StatusCode} {message}");
    }

    /// <summary>Read-only stream wrapper that reports upload progress in 10% steps.</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private readonly Action<int> _report;
        private long _read;
        private int _lastReported;

        public ProgressStream(Stream inner, long length, Action<int> report)
        {
            _inner = inner;
            _length = Math.Max(1, length);
            _report = report;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Track(_inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Track(await _inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Track(int n)
        {
            _read += n;
            var percent = (int)(_read * 100 / _length);
            if (percent >= _lastReported + 10)
            {
                _lastReported = percent - (percent % 10);
                _report(_lastReported);
            }

            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
