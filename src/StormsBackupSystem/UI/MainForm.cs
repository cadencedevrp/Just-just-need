using System.Diagnostics;
using System.Drawing.Drawing2D;
using StormsBackupSystem.Core;
using StormsBackupSystem.Services;

namespace StormsBackupSystem.UI;

/// <summary>Main window: dashboard, sources, destinations and schedule settings.</summary>
internal sealed class MainForm : Form
{
    private static readonly Color Navy = Color.FromArgb(16, 24, 48);
    private static readonly Color StormBlue = Color.FromArgb(30, 64, 120);
    private static readonly Color Lightning = Color.FromArgb(255, 205, 40);

    private readonly TrayApplicationContext _app;
    private AppSettings _settings = SettingsStore.LoadSettings();

    // Dashboard
    private readonly Label _lblLastBackup = new() { AutoSize = true };
    private readonly Label _lblLastResult = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Label _lblNextBackup = new() { AutoSize = true };
    private readonly Label _lblDestinations = new() { AutoSize = true };
    private readonly Button _btnBackupNow = new() { Text = "⚡  Back Up Now", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 200, MarqueeAnimationSpeed = 30 };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f),
        BackColor = Color.FromArgb(14, 18, 28),
        ForeColor = Color.Gainsboro,
        WordWrap = false,
    };

    // Sources
    private readonly ListBox _lstSources = new() { Height = 110, Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly TextBox _txtExcludes = new() { Multiline = true, Height = 90, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };
    private readonly TextBox _txtLocalFolder = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _numKeepLocal = new() { Minimum = 0, Maximum = 365, Width = 80 };
    private readonly TextBox _txtPrefix = new() { Width = 200 };
    private readonly CheckBox _chkMySql = new() { Text = "Also back up a MySQL / MariaDB database (mysqldump)", AutoSize = true };
    private readonly TextBox _txtMySqlDump = new() { Dock = DockStyle.Fill, PlaceholderText = "Leave empty to find mysqldump.exe automatically" };
    private readonly TextBox _txtMySqlHost = new() { Width = 200 };
    private readonly NumericUpDown _numMySqlPort = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    private readonly TextBox _txtMySqlUser = new() { Width = 200 };
    private readonly TextBox _txtMySqlPassword = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly TextBox _txtMySqlDatabases = new() { Dock = DockStyle.Fill, PlaceholderText = "e.g. es_extended, vrp  (empty = all databases)" };

    // Destinations
    private readonly CheckBox _chkGoogle = new() { Text = "Upload to Google Drive", AutoSize = true };
    private readonly TextBox _txtGoogleSecret = new() { Dock = DockStyle.Fill, PlaceholderText = "client_secret_xxxx.json downloaded from Google Cloud Console" };
    private readonly TextBox _txtGoogleFolder = new() { Width = 260 };
    private readonly NumericUpDown _numGoogleKeep = new() { Minimum = 0, Maximum = 365, Width = 80 };
    private readonly Label _lblGoogleStatus = new() { AutoSize = true };
    private readonly CheckBox _chkGitHub = new() { Text = "Upload to GitHub (as Releases in a PRIVATE repository)", AutoSize = true };
    private readonly TextBox _txtGhOwner = new() { Width = 200, PlaceholderText = "user or organisation" };
    private readonly TextBox _txtGhRepo = new() { Width = 200, PlaceholderText = "e.g. server-backups" };
    private readonly TextBox _txtGhToken = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "github_pat_... (Contents: Read and write)" };
    private readonly NumericUpDown _numGhKeep = new() { Minimum = 0, Maximum = 365, Width = 80 };
    private readonly Label _lblGhStatus = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly TextBox _txtDiscord = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "https://discord.com/api/webhooks/... (optional)" };
    private readonly CheckBox _chkDiscordSuccess = new() { Text = "Also notify when a backup succeeds (failures are always sent)", AutoSize = true };

    // Schedule
    private readonly NumericUpDown _numInterval = new() { Minimum = 1, Maximum = 168, Width = 80 };
    private readonly DateTimePicker _dtpTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 90 };
    private readonly CheckBox _chkStartup = new() { Text = "Start automatically when I sign in to Windows (runs in the system tray)", AutoSize = true };
    private readonly Label _lblScheduleHint = new() { AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DimGray };

    private readonly Label _lblSaved = new() { AutoSize = true, ForeColor = Color.SeaGreen, Anchor = AnchorStyles.Left };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 15_000 };

    public MainForm(TrayApplicationContext app)
    {
        _app = app;
        Text = AppPaths.AppName;
        Icon = app.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 640);
        Size = new Size(900, 720);
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
        tabs.TabPages.Add(BuildDashboardTab());
        tabs.TabPages.Add(BuildSourcesTab());
        tabs.TabPages.Add(BuildDestinationsTab());
        tabs.TabPages.Add(BuildScheduleTab());

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        var btnSave = new Button { Text = "💾  Save Settings", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        btnSave.Click += (_, _) => SaveSettings(showConfirmation: true);
        bottom.Controls.Add(btnSave);
        bottom.Controls.Add(_lblSaved);

        Controls.Add(tabs);
        Controls.Add(bottom);
        Controls.Add(new HeaderPanel { Dock = DockStyle.Top, Height = 70 });

        LoadSettingsIntoControls();
        RefreshStatus();

        Log.MessageLogged += OnLogMessage;
        _app.BackupRunningChanged += running =>
        {
            _btnBackupNow.Enabled = !running;
            _progress.Visible = running;
            RefreshStatus();
        };
        _app.BackupCompleted += _ => RefreshStatus();

        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
    }

    // ------------------------------------------------------------------ tabs

    private TabPage BuildDashboardTab()
    {
        var page = new TabPage("Dashboard") { Padding = new Padding(10) };

        var status = Grid();
        AddRow(status, "Last backup:", _lblLastBackup);
        AddRow(status, "Result:", _lblLastResult);
        AddRow(status, "Next backup:", _lblNextBackup);
        AddRow(status, "Destinations:", _lblDestinations);
        var statusBox = Group("Status", status);

        _btnBackupNow.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        _btnBackupNow.Click += async (_, _) =>
        {
            if (SaveSettings(showConfirmation: false))
            {
                await _app.RunBackupAsync("manual");
            }
        };

        var btnOpenBackups = new Button { Text = "📁  Open Backup Folder", AutoSize = true };
        btnOpenBackups.Click += (_, _) => OpenFolder(string.IsNullOrWhiteSpace(_settings.LocalBackupFolder) ? AppPaths.DefaultBackupDirectory : _settings.LocalBackupFolder);
        var btnOpenLogs = new Button { Text = "📄  Open Logs", AutoSize = true };
        btnOpenLogs.Click += (_, _) => OpenFolder(AppPaths.LogDirectory);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8), WrapContents = true };
        actions.Controls.AddRange(new Control[] { _btnBackupNow, btnOpenBackups, btnOpenLogs, _progress });
        _progress.Margin = new Padding(12, 10, 3, 3);

        var logBox = new GroupBox { Text = "Activity log", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_txtLog);

        page.Controls.Add(logBox);
        page.Controls.Add(actions);
        page.Controls.Add(statusBox);
        return page;
    }

    private TabPage BuildSourcesTab()
    {
        var page = new TabPage("What to Back Up") { AutoScroll = true, Padding = new Padding(10) };

        // Server folders
        var folders = Grid();
        var btnAdd = new Button { Text = "Add Folder...", AutoSize = true };
        btnAdd.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Choose a server folder to back up", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK && !_lstSources.Items.Contains(dlg.SelectedPath))
            {
                _lstSources.Items.Add(dlg.SelectedPath);
            }
        };
        var btnRemove = new Button { Text = "Remove", AutoSize = true };
        btnRemove.Click += (_, _) =>
        {
            if (_lstSources.SelectedIndex >= 0)
            {
                _lstSources.Items.RemoveAt(_lstSources.SelectedIndex);
            }
        };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        buttons.Controls.Add(btnAdd);
        buttons.Controls.Add(btnRemove);
        AddRow(folders, "Server folders:", _lstSources, buttons);
        AddRow(folders, "Exclude:", _txtExcludes, Hint("One per line: a name (cache),\na wildcard (*.log) or a path\n(resources/[local]/temp)."));
        AddRow(folders, "Save backups in:", _txtLocalFolder, BrowseButton(_txtLocalFolder, folder: true));
        AddRow(folders, "Keep local copies:", WithHint(_numKeepLocal, "newest backups kept on this PC (0 = keep all)"));
        AddRow(folders, "File name prefix:", _txtPrefix);

        // Database
        var db = Grid();
        AddRow(db, string.Empty, _chkMySql);
        AddRow(db, "mysqldump.exe:", _txtMySqlDump, BrowseButton(_txtMySqlDump, folder: false, filter: "mysqldump|mysqldump.exe;mariadb-dump.exe|Programs|*.exe"));
        AddRow(db, "Host:", _txtMySqlHost);
        AddRow(db, "Port:", _numMySqlPort);
        AddRow(db, "User:", _txtMySqlUser);
        AddRow(db, "Password:", _txtMySqlPassword);
        AddRow(db, "Databases:", _txtMySqlDatabases);

        page.Controls.Add(Group("Database (optional)", db));
        page.Controls.Add(Group("Server files", folders));
        return page;
    }

    private TabPage BuildDestinationsTab()
    {
        var page = new TabPage("Upload To") { AutoScroll = true, Padding = new Padding(10) };

        // Google Drive
        var google = Grid();
        AddRow(google, string.Empty, _chkGoogle);
        AddRow(google, "OAuth client file:", _txtGoogleSecret, BrowseButton(_txtGoogleSecret, folder: false, filter: "Google client secret|*.json"));
        AddRow(google, "Drive folder name:", _txtGoogleFolder);
        AddRow(google, "Keep copies:", WithHint(_numGoogleKeep, "newest backups kept on Google Drive (0 = keep all)"));
        var btnSignIn = new Button { Text = "Sign in to Google...", AutoSize = true };
        btnSignIn.Click += async (_, _) => await GoogleSignInAsync(btnSignIn);
        var btnSignOut = new Button { Text = "Sign out", AutoSize = true };
        btnSignOut.Click += async (_, _) =>
        {
            await GoogleDriveUploader.SignOutAsync();
            await RefreshGoogleStatusAsync();
        };
        var googleButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        googleButtons.Controls.AddRange(new Control[] { btnSignIn, btnSignOut, _lblGoogleStatus });
        _lblGoogleStatus.Margin = new Padding(8, 8, 3, 3);
        AddRow(google, "Account:", googleButtons);

        // GitHub
        var github = Grid();
        AddRow(github, string.Empty, _chkGitHub);
        AddRow(github, "Owner:", _txtGhOwner);
        AddRow(github, "Repository:", _txtGhRepo);
        AddRow(github, "Access token:", _txtGhToken);
        AddRow(github, "Keep copies:", WithHint(_numGhKeep, "newest backup releases kept on GitHub (0 = keep all)"));
        var btnTest = new Button { Text = "Test Connection", AutoSize = true };
        btnTest.Click += async (_, _) => await TestGitHubAsync(btnTest);
        var ghButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        ghButtons.Controls.AddRange(new Control[] { btnTest, _lblGhStatus });
        _lblGhStatus.Margin = new Padding(8, 8, 3, 3);
        AddRow(github, string.Empty, ghButtons);

        // Discord
        var discord = Grid();
        AddRow(discord, "Webhook URL:", _txtDiscord);
        AddRow(discord, string.Empty, _chkDiscordSuccess);

        page.Controls.Add(Group("Discord notifications (optional)", discord));
        page.Controls.Add(Group("GitHub", github));
        page.Controls.Add(Group("Google Drive", google));
        return page;
    }

    private TabPage BuildScheduleTab()
    {
        var page = new TabPage("Schedule") { AutoScroll = true, Padding = new Padding(10) };
        var grid = Grid();
        AddRow(grid, "Back up every:", WithHint(_numInterval, "hours  (24 = once a day)"));
        AddRow(grid, "Starting at:", WithHint(_dtpTime, "time of day for the first backup (pick a quiet time for your server)"));
        AddRow(grid, string.Empty, _chkStartup);
        AddRow(grid, string.Empty, _lblScheduleHint);

        _numInterval.ValueChanged += (_, _) => UpdateScheduleHint();
        _dtpTime.ValueChanged += (_, _) => UpdateScheduleHint();

        page.Controls.Add(Group("Automatic backups", grid));
        return page;
    }

    // ------------------------------------------------------------------ settings <-> controls

    private void LoadSettingsIntoControls()
    {
        var s = _settings;
        _lstSources.Items.Clear();
        foreach (var folder in s.SourceFolders)
        {
            _lstSources.Items.Add(folder);
        }

        _txtExcludes.Lines = s.ExcludePatterns.ToArray();
        _txtLocalFolder.Text = s.LocalBackupFolder;
        _numKeepLocal.Value = Math.Clamp(s.KeepLocalCopies, 0, 365);
        _txtPrefix.Text = s.BackupNamePrefix;

        _chkMySql.Checked = s.MySqlEnabled;
        _txtMySqlDump.Text = s.MySqlDumpPath;
        _txtMySqlHost.Text = s.MySqlHost;
        _numMySqlPort.Value = Math.Clamp(s.MySqlPort, 1, 65535);
        _txtMySqlUser.Text = s.MySqlUser;
        _txtMySqlPassword.Text = SecretProtector.Unprotect(s.MySqlPasswordProtected);
        _txtMySqlDatabases.Text = s.MySqlDatabases;

        _chkGoogle.Checked = s.GoogleDriveEnabled;
        _txtGoogleSecret.Text = s.GoogleClientSecretPath;
        _txtGoogleFolder.Text = s.GoogleDriveFolderName;
        _numGoogleKeep.Value = Math.Clamp(s.GoogleDriveKeepCopies, 0, 365);

        _chkGitHub.Checked = s.GitHubEnabled;
        _txtGhOwner.Text = s.GitHubOwner;
        _txtGhRepo.Text = s.GitHubRepository;
        _txtGhToken.Text = SecretProtector.Unprotect(s.GitHubTokenProtected);
        _numGhKeep.Value = Math.Clamp(s.GitHubKeepCopies, 0, 365);

        _txtDiscord.Text = SecretProtector.Unprotect(s.DiscordWebhookProtected);
        _chkDiscordSuccess.Checked = s.DiscordNotifyOnSuccess;

        _numInterval.Value = Math.Clamp(s.IntervalHours, 1, 168);
        _dtpTime.Value = DateTime.Today + s.BackupTimeOfDay;
        _chkStartup.Checked = s.StartWithWindows;
        UpdateScheduleHint();
        _ = RefreshGoogleStatusAsync();
    }

    private bool SaveSettings(bool showConfirmation)
    {
        var s = SettingsStore.LoadSettings(); // start from disk so nothing we don't show is lost
        s.SourceFolders = _lstSources.Items.Cast<string>().ToList();
        s.ExcludePatterns = _txtExcludes.Lines.Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        s.LocalBackupFolder = string.IsNullOrWhiteSpace(_txtLocalFolder.Text) ? AppPaths.DefaultBackupDirectory : _txtLocalFolder.Text.Trim();
        s.KeepLocalCopies = (int)_numKeepLocal.Value;
        s.BackupNamePrefix = BackupEngine.SafePrefix(_txtPrefix.Text);

        s.MySqlEnabled = _chkMySql.Checked;
        s.MySqlDumpPath = _txtMySqlDump.Text.Trim();
        s.MySqlHost = _txtMySqlHost.Text.Trim();
        s.MySqlPort = (int)_numMySqlPort.Value;
        s.MySqlUser = _txtMySqlUser.Text.Trim();
        s.MySqlPasswordProtected = SecretProtector.Protect(_txtMySqlPassword.Text);
        s.MySqlDatabases = _txtMySqlDatabases.Text.Trim();

        s.GoogleDriveEnabled = _chkGoogle.Checked;
        s.GoogleClientSecretPath = _txtGoogleSecret.Text.Trim();
        s.GoogleDriveFolderName = string.IsNullOrWhiteSpace(_txtGoogleFolder.Text) ? "Storms Technologies Backups" : _txtGoogleFolder.Text.Trim();
        s.GoogleDriveKeepCopies = (int)_numGoogleKeep.Value;

        s.GitHubEnabled = _chkGitHub.Checked;
        s.GitHubOwner = _txtGhOwner.Text.Trim();
        s.GitHubRepository = _txtGhRepo.Text.Trim();
        s.GitHubTokenProtected = SecretProtector.Protect(_txtGhToken.Text.Trim());
        s.GitHubKeepCopies = (int)_numGhKeep.Value;

        s.DiscordWebhookProtected = SecretProtector.Protect(_txtDiscord.Text.Trim());
        s.DiscordNotifyOnSuccess = _chkDiscordSuccess.Checked;

        s.IntervalHours = (int)_numInterval.Value;
        s.BackupTime = _dtpTime.Value.ToString("HH:mm");
        s.StartWithWindows = _chkStartup.Checked;

        try
        {
            SettingsStore.SaveSettings(s);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings: " + ex.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        _settings = s;
        StartupRegistration.Apply(s.StartWithWindows);
        RefreshStatus();

        var problems = Validate(s);
        if (showConfirmation)
        {
            _lblSaved.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            if (problems.Count > 0)
            {
                MessageBox.Show(this, "Settings saved, but please check:\n\n• " + string.Join("\n• ", problems), AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else if (problems.Count > 0)
        {
            return MessageBox.Show(this, "Some settings need attention:\n\n• " + string.Join("\n• ", problems) + "\n\nRun the backup anyway?", AppPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        return true;
    }

    private static List<string> Validate(AppSettings s)
    {
        var problems = new List<string>();
        if (s.SourceFolders.Count == 0 && !s.MySqlEnabled)
        {
            problems.Add("Add at least one server folder on the \"What to Back Up\" tab.");
        }

        foreach (var missing in s.SourceFolders.Where(f => !Directory.Exists(f)))
        {
            problems.Add("Folder does not exist: " + missing);
        }

        if (!s.GoogleDriveEnabled && !s.GitHubEnabled)
        {
            problems.Add("No upload destination is enabled - backups will only be stored on this PC.");
        }

        if (s.GoogleDriveEnabled && !File.Exists(s.GoogleClientSecretPath))
        {
            problems.Add("Google Drive: choose your OAuth client JSON file, then click \"Sign in to Google\".");
        }

        if (s.GitHubEnabled && (string.IsNullOrWhiteSpace(s.GitHubOwner) || string.IsNullOrWhiteSpace(s.GitHubRepository) || string.IsNullOrWhiteSpace(s.GitHubTokenProtected)))
        {
            problems.Add("GitHub: fill in owner, repository and access token.");
        }

        return problems;
    }

    // ------------------------------------------------------------------ actions

    private async Task GoogleSignInAsync(Button button)
    {
        if (!File.Exists(_txtGoogleSecret.Text.Trim()))
        {
            MessageBox.Show(this, "First choose the OAuth client JSON file you downloaded from Google Cloud Console (see README).", AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        button.Enabled = false;
        _lblGoogleStatus.Text = "Waiting for you to sign in in your browser...";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await GoogleDriveUploader.SignInAsync(_txtGoogleSecret.Text.Trim(), timeout.Token);
            SaveSettings(showConfirmation: false);
            Log.Info("Signed in to Google Drive.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Google sign-in failed: " + ex.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            button.Enabled = true;
            await RefreshGoogleStatusAsync();
        }
    }

    private async Task RefreshGoogleStatusAsync()
    {
        try
        {
            var signedIn = await GoogleDriveUploader.IsSignedInAsync();
            _lblGoogleStatus.Text = signedIn ? "✔ Signed in" : "Not signed in";
            _lblGoogleStatus.ForeColor = signedIn ? Color.SeaGreen : Color.Firebrick;
        }
        catch
        {
            _lblGoogleStatus.Text = "Not signed in";
        }
    }

    private async Task TestGitHubAsync(Button button)
    {
        button.Enabled = false;
        _lblGhStatus.ForeColor = Color.DimGray;
        _lblGhStatus.Text = "Checking...";
        try
        {
            using var gh = new GitHubUploader(_txtGhOwner.Text, _txtGhRepo.Text, _txtGhToken.Text);
            var repo = await gh.CheckRepositoryAsync(CancellationToken.None);
            if (!repo.IsPrivate)
            {
                _lblGhStatus.ForeColor = Color.Firebrick;
                _lblGhStatus.Text = $"⚠ {repo.FullName} is PUBLIC. Make it private - uploads are blocked for public repos.";
            }
            else if (!repo.CanPush)
            {
                _lblGhStatus.ForeColor = Color.Firebrick;
                _lblGhStatus.Text = "⚠ Token can read but not write. Give it \"Contents: Read and write\".";
            }
            else
            {
                _lblGhStatus.ForeColor = Color.SeaGreen;
                _lblGhStatus.Text = $"✔ Connected to private repository {repo.FullName}";
            }
        }
        catch (Exception ex)
        {
            _lblGhStatus.ForeColor = Color.Firebrick;
            _lblGhStatus.Text = "✖ " + ex.Message;
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private void RefreshStatus()
    {
        var state = SettingsStore.LoadState();
        _lblLastBackup.Text = state.LastAttemptUtc is { } last
            ? $"{last.ToLocalTime():dddd dd MMM yyyy HH:mm}" + (state.LastBackupSizeBytes > 0 ? $"  ({ArchiveBuilder.FormatBytes(state.LastBackupSizeBytes)})" : string.Empty)
            : "Never";

        if (BackupEngine.IsRunning)
        {
            _lblLastResult.Text = "Backing up now...";
            _lblLastResult.ForeColor = StormBlue;
        }
        else if (state.LastAttemptUtc is null)
        {
            _lblLastResult.Text = "No backups yet";
            _lblLastResult.ForeColor = Color.DimGray;
        }
        else
        {
            _lblLastResult.Text = (state.LastAttemptFailed ? "✖ " : "✔ ") + state.LastMessage;
            _lblLastResult.ForeColor = state.LastAttemptFailed ? Color.Firebrick : Color.SeaGreen;
        }

        var next = _app.NextRun;
        _lblNextBackup.Text = next <= DateTime.Now ? "Due now" : $"{next:dddd dd MMM yyyy HH:mm}  (in {FormatSpan(next - DateTime.Now)})";

        var destinations = new List<string>();
        if (_settings.GoogleDriveEnabled)
        {
            destinations.Add("Google Drive");
        }

        if (_settings.GitHubEnabled)
        {
            destinations.Add($"GitHub ({_settings.GitHubOwner}/{_settings.GitHubRepository})");
        }

        _lblDestinations.Text = destinations.Count > 0 ? string.Join(", ", destinations) : "None - local copy only";
    }

    private void UpdateScheduleHint()
    {
        var hours = (int)_numInterval.Value;
        _lblScheduleHint.Text =
            $"A backup runs every {hours} hour{(hours == 1 ? string.Empty : "s")}, starting at {_dtpTime.Value:HH:mm}. " +
            "The program must be running (it sits in the system tray). If the PC was off when a backup was due, " +
            "it runs as soon as the program starts. Failed backups are retried after 1 hour (up to 3 times).";
    }

    private void OnLogMessage(DateTime time, LogLevel level, string message)
    {
        if (IsDisposed)
        {
            return;
        }

        var line = $"{time:HH:mm:ss}  {(level == LogLevel.Error ? "ERROR " : level == LogLevel.Warning ? "WARN  " : string.Empty)}{message}{Environment.NewLine}";
        try
        {
            BeginInvoke(() =>
            {
                if (_txtLog.TextLength > 200_000)
                {
                    _txtLog.Text = _txtLog.Text[^100_000..];
                }

                _txtLog.AppendText(line);
            });
        }
        catch (InvalidOperationException)
        {
            // window handle gone during shutdown
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X button hides to the tray so scheduled backups keep running.
        if (e.CloseReason == CloseReason.UserClosing && !_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyHiddenToTray();
            return;
        }

        Log.MessageLogged -= OnLogMessage;
        _refreshTimer.Stop();
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ layout helpers

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control, Control? extra = null)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 7, 8, 3) }, 0, row);
        control.Margin = new Padding(3, 4, 3, 4);
        if (control.Dock == DockStyle.Fill)
        {
            // stretch horizontally but keep the control's own height
            control.Dock = DockStyle.None;
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        }
        else
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        }

        grid.Controls.Add(control, 1, row);
        if (extra is not null)
        {
            extra.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            grid.Controls.Add(extra, 2, row);
        }
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 10) };
        box.Controls.Add(content);
        return box;
    }

    private static Control WithHint(Control control, string hint)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        control.Margin = new Padding(0, 0, 6, 0);
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 0) });
        return panel;
    }

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.DimGray };

    private Button BrowseButton(TextBox target, bool folder, string filter = "All files|*.*")
    {
        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            if (folder)
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = target.Text, UseDescriptionForTitle = true, Description = "Choose a folder" };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dlg.SelectedPath;
                }
            }
            else
            {
                using var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dlg.FileName;
                }
            }
        };
        return button;
    }

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, span.Minutes)}m";

    /// <summary>Branded header: storm gradient with the product name.</summary>
    private sealed class HeaderPanel : Panel
    {
        private readonly Icon _icon = AppIcon.Load();

        public HeaderPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var brush = new LinearGradientBrush(ClientRectangle, Navy, StormBlue, LinearGradientMode.Horizontal))
            {
                g.FillRectangle(brush, ClientRectangle);
            }

            using (var accent = new SolidBrush(Lightning))
            {
                g.FillRectangle(accent, 0, Height - 3, Width, 3);
            }

            var iconSize = Height - 22;
            using (var sized = new Icon(_icon, iconSize, iconSize))
            {
                g.DrawIcon(sized, new Rectangle(14, 9, iconSize, iconSize));
            }

            using var title = new Font("Segoe UI Semibold", 16f);
            using var subtitle = new Font("Segoe UI", 9f);
            g.DrawString("Storms Technologies", subtitle, Brushes.Gainsboro, 24 + iconSize, 10);
            g.DrawString("Backup System", title, Brushes.White, 22 + iconSize, 26);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
