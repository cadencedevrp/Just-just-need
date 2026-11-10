# ⚡ Storms Technologies Backup System

A Windows program that backs up your server every 24 hours and uploads the backup to **Google Drive**, **GitHub**, or both.

- Zips your server folders (any game or app server, e.g. FiveM, Minecraft, a website)
- Optionally dumps a **MySQL / MariaDB** database into the same backup
- Runs automatically every 24 hours (you choose the time and the interval) from the system tray
- Uploads to **Google Drive** (resumable, any size) and/or **GitHub** (as Releases in a *private* repo)
- Deletes old copies automatically (locally, on Google Drive and on GitHub)
- Can post success/failure messages to a **Discord** channel
- Starts with Windows. If the PC was off when a backup was due, it runs as soon as the program starts again
- Passwords and tokens are encrypted with Windows DPAPI, never stored as plain text

---

## 1. Get the program

**Download a build:** open the repository's **Actions** tab, click the latest *Build Storms Technologies Backup System* run, and download **StormsBackupSystem-win-x64** (it contains `StormsBackupSystem.exe`).
If you push a tag such as `v1.0.0`, the exe is also attached to a GitHub Release.

**Or build it yourself** (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
dotnet publish src/StormsBackupSystem/StormsBackupSystem.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

`StormsBackupSystem.exe` is a single, self-contained file. It runs on Windows 10/11 and Windows Server 2016 or newer, and you don't need to install .NET. Put it somewhere permanent, for example `C:\Storms Backup\StormsBackupSystem.exe`, and run it.

> Windows SmartScreen may warn about an unknown publisher because the exe isn't code-signed. Click **More info → Run anyway**.

## 2. Choose what to back up

On the **What to Back Up** tab:

1. Click **Add Folder...** and choose your server folder(s). For FiveM, add the server-data folder (the one with `server.cfg` and `resources`).
2. Under **Exclude**, add anything you don't want in the backup, one rule per line:
   - `cache`: any file or folder named *cache* (strongly recommended for FiveM)
   - `*.log`: wildcards
   - `resources/[local]/temp`: a specific path inside the server folder
3. Under **Save backups in**, choose where the local `.zip` files go, and how many to keep.
4. **Database (optional):** tick the MySQL box and enter the host, port, user, password and database names. `mysqldump.exe` is found automatically if MySQL, MariaDB or XAMPP is installed. The dump is saved inside the zip as `_database/mysql-dump.sql`.

Files the server holds open, such as logs, are still copied. Any file that can't be read is skipped and listed in the log and in `_backup-info.txt` inside the zip.

## 3. Choose where to upload

### Google Drive

Google requires every program that uploads to Drive to have its own "OAuth client". Making one is free and takes about 5 minutes:

1. Go to <https://console.cloud.google.com/> and create a project, for example *Storms Backup*.
2. **APIs & Services → Library** → search for **Google Drive API** → **Enable**.
3. **APIs & Services → OAuth consent screen** (also called *Google Auth Platform*):
   - User type: **External**. Fill in an app name and your email address.
   - Under **Audience**, add your own Google account as a **test user**.
   - ⚠️ Then click **Publish app** so the status is **In production**. In *Testing* mode, Google signs you out every 7 days and backups would stop uploading. The app only uses the limited `drive.file` permission, so Google doesn't need to review it.
4. **APIs & Services → Credentials → Create credentials → OAuth client ID** → Application type **Desktop app** → **Create** → **Download JSON**.
5. In the program, go to **Upload To**, tick **Upload to Google Drive**, **Browse...** to the JSON file, and click **Sign in to Google...**. A browser window opens. Sign in and allow access. (You may see "Google hasn't verified this app". It's your own app, so click **Continue**.)

Backups go into a Drive folder called **Storms Technologies Backups**, which the program creates. The program can only see files it created itself, not the rest of your Drive.

### GitHub

Each backup is stored as a **Release** in a **private** repository. Release files can be up to 2 GB each, and bigger backups are split into parts automatically.

1. Create a **private** repository on GitHub, for example `server-backups`. The program refuses to upload to a public repository, so your server files and passwords can't leak.
2. Create a token at <https://github.com/settings/personal-access-tokens/new> (fine-grained):
   - Repository access: **Only select repositories** → your backup repo
   - Permissions → **Contents: Read and write**
3. In the program, go to **Upload To**, tick **Upload to GitHub**, and enter the owner (your username or organisation), the repository name and the token. Click **Test Connection**.

To restore, open the repository's **Releases** page and download the zip. For split backups, download all parts into one folder and join them with the `copy /b` command shown in the release notes.

### Discord (optional)

Paste a channel webhook URL (**Channel settings → Integrations → Webhooks**) to get a message after every backup. Failures are always sent; successes are optional.

## 4. Schedule

On the **Schedule** tab, choose **Back up every 24 hours** (the default) and a **starting time**, such as 03:00. Leave **Start automatically when I sign in to Windows** ticked.

Click **Save Settings**. To check that everything works, go to **Dashboard → ⚡ Back Up Now**.

Closing the window keeps the program running in the **system tray** (bottom right, next to the clock). To stop it completely, right-click the tray icon and choose **Exit**.

If a backup fails, it is retried after 1 hour, up to 3 times.

### Running without the window (Task Scheduler)

The program can also run a single backup with no window and then exit:

```
StormsBackupSystem.exe --backup-now
```

The exit code is `0` on success and `1` on failure. You can use this from Windows Task Scheduler if you prefer that to the tray app. Run the task as **the same Windows user** that set up the program, because the saved tokens are encrypted for that user. Configure everything in the window first.

## Where things are stored

| What | Location |
|---|---|
| Settings (secrets encrypted) | `%LOCALAPPDATA%\Storms Technologies\Backup System\settings.json` |
| Logs (kept 30 days) | `%LOCALAPPDATA%\Storms Technologies\Backup System\logs\` |
| Google sign-in token | `%LOCALAPPDATA%\Storms Technologies\Backup System\google-token\` |
| Backups (default) | `%LOCALAPPDATA%\Storms Technologies\Backup System\Backups\` |

## Restoring a backup

1. Stop the server.
2. Extract the zip. Each server folder is a top-level folder inside it.
3. Copy the files back.
4. If you backed up a database, import it: `mysql -u root -p < _database\mysql-dump.sql`

## Project layout

```
src/StormsBackupSystem/
  Core/        zip builder, exclusion rules, schedule maths, logging (no Windows dependencies, unit tested)
  Services/    backup engine, Google Drive, GitHub, MySQL dump, Discord, settings, DPAPI secrets
  UI/          main window and system tray / scheduler
tests/StormsBackupSystem.Tests/   xUnit tests (dotnet test)
```
