using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using StormsBackupSystem.Core;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace StormsBackupSystem.Services;

/// <summary>
/// Uploads backups to Google Drive using the user's own OAuth "Desktop app" client.
/// Uses the narrow drive.file scope: the program can only see files and folders it created itself.
/// </summary>
public static class GoogleDriveUploader
{
    private const string UserKey = "storms-backup-user";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private static readonly string[] Scopes = { DriveService.Scope.DriveFile };

    public static async Task<bool> IsSignedInAsync()
    {
        var store = new FileDataStore(AppPaths.GoogleTokenDirectory, fullPath: true);
        var token = await store.GetAsync<TokenResponse>(UserKey);
        return token is not null && (!string.IsNullOrEmpty(token.RefreshToken) || !string.IsNullOrEmpty(token.AccessToken));
    }

    /// <summary>Opens the browser so the user can sign in and grant access. The token is saved for future runs.</summary>
    public static async Task SignInAsync(string clientSecretPath, CancellationToken ct)
    {
        await AuthorizeAsync(clientSecretPath, interactive: true, ct);
    }

    public static async Task SignOutAsync()
    {
        var store = new FileDataStore(AppPaths.GoogleTokenDirectory, fullPath: true);
        await store.ClearAsync();
    }

    private static async Task<UserCredential> AuthorizeAsync(string clientSecretPath, bool interactive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientSecretPath) || !File.Exists(clientSecretPath))
        {
            throw new FileNotFoundException("Google client secret JSON file not found. Choose it on the Destinations tab.");
        }

        if (!interactive && !await IsSignedInAsync())
        {
            throw new InvalidOperationException("Not signed in to Google Drive. Open the program and click \"Sign in to Google\" on the Destinations tab.");
        }

        GoogleClientSecrets secrets;
        await using (var stream = File.OpenRead(clientSecretPath))
        {
            secrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);
        }

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets.Secrets,
            Scopes,
            UserKey,
            ct,
            new FileDataStore(AppPaths.GoogleTokenDirectory, fullPath: true));
    }

    public static async Task UploadAsync(string filePath, AppSettings settings, AppState state, CancellationToken ct)
    {
        var credential = await AuthorizeAsync(settings.GoogleClientSecretPath, interactive: false, ct);
        using var drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = AppPaths.AppName,
        });

        var folderId = await EnsureFolderAsync(drive, settings, state, ct);

        var fileInfo = new FileInfo(filePath);
        Log.Info($"Google Drive: uploading {fileInfo.Name} ({ArchiveBuilder.FormatBytes(fileInfo.Length)})...");

        var metadata = new DriveFile
        {
            Name = fileInfo.Name,
            Parents = new[] { folderId },
            Description = $"Server backup created by {AppPaths.AppName} on {Environment.MachineName}",
        };

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var request = drive.Files.Create(metadata, stream, "application/zip");
        request.Fields = "id,name,size";
        request.ChunkSize = ResumableUpload.MinimumChunkSize * 40; // 10 MB chunks, resumable

        var lastPercent = -10;
        request.ProgressChanged += p =>
        {
            if (p.Status != UploadStatus.Uploading || fileInfo.Length == 0)
            {
                return;
            }

            var percent = (int)(p.BytesSent * 100 / fileInfo.Length);
            if (percent >= lastPercent + 10)
            {
                lastPercent = percent - (percent % 10);
                Log.Info($"Google Drive: {lastPercent}% uploaded");
            }
        };

        var result = await request.UploadAsync(ct);
        if (result.Status != UploadStatus.Completed)
        {
            throw new InvalidOperationException("Google Drive upload failed: " + (result.Exception?.Message ?? result.Status.ToString()), result.Exception);
        }

        Log.Info($"Google Drive: upload complete (file id {request.ResponseBody?.Id}).");

        await ApplyRetentionAsync(drive, folderId, settings, ct);
    }

    private static async Task<string> EnsureFolderAsync(DriveService drive, AppSettings settings, AppState state, CancellationToken ct)
    {
        var folderName = string.IsNullOrWhiteSpace(settings.GoogleDriveFolderName)
            ? "Storms Technologies Backups"
            : settings.GoogleDriveFolderName.Trim();

        // Re-use the folder we created before, as long as it still exists and has the same name.
        if (!string.IsNullOrEmpty(state.GoogleDriveFolderId) && state.GoogleDriveFolderName == folderName)
        {
            try
            {
                var get = drive.Files.Get(state.GoogleDriveFolderId);
                get.Fields = "id,trashed";
                var existing = await get.ExecuteAsync(ct);
                if (existing.Trashed != true)
                {
                    return existing.Id;
                }
            }
            catch (Google.GoogleApiException)
            {
                // deleted or no longer accessible - create a new one below
            }
        }

        // Look for a folder with that name that this app created earlier.
        var list = drive.Files.List();
        list.Q = $"mimeType = '{FolderMime}' and name = '{folderName.Replace("\\", "\\\\").Replace("'", "\\'")}' and trashed = false";
        list.Fields = "files(id,name)";
        list.Spaces = "drive";
        var found = (await list.ExecuteAsync(ct)).Files?.FirstOrDefault();

        string id;
        if (found is not null)
        {
            id = found.Id;
        }
        else
        {
            var create = drive.Files.Create(new DriveFile { Name = folderName, MimeType = FolderMime });
            create.Fields = "id";
            id = (await create.ExecuteAsync(ct)).Id;
            Log.Info($"Google Drive: created folder \"{folderName}\".");
        }

        state.GoogleDriveFolderId = id;
        state.GoogleDriveFolderName = folderName;
        return id;
    }

    private static async Task ApplyRetentionAsync(DriveService drive, string folderId, AppSettings settings, CancellationToken ct)
    {
        if (settings.GoogleDriveKeepCopies <= 0)
        {
            return;
        }

        var backups = new List<DriveFile>();
        string? pageToken = null;
        do
        {
            var list = drive.Files.List();
            list.Q = $"'{folderId}' in parents and trashed = false and mimeType != '{FolderMime}'";
            list.Fields = "nextPageToken, files(id,name,createdTime)";
            list.PageSize = 100;
            list.PageToken = pageToken;
            var page = await list.ExecuteAsync(ct);
            backups.AddRange(page.Files ?? new List<DriveFile>());
            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        var old = backups
            .Where(f => f.Name.StartsWith(settings.BackupNamePrefix + "_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.CreatedTimeDateTimeOffset)
            .Skip(Math.Max(1, settings.GoogleDriveKeepCopies));

        foreach (var file in old)
        {
            await drive.Files.Delete(file.Id).ExecuteAsync(ct);
            Log.Info($"Google Drive: removed old backup {file.Name}");
        }
    }
}
