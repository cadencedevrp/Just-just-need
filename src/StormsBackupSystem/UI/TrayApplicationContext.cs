using StormsBackupSystem.Core;
using StormsBackupSystem.Services;

namespace StormsBackupSystem.UI;

/// <summary>
/// Owns the tray icon, the main window and the 24-hour scheduler.
/// The program keeps running in the system tray when the window is closed.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    public const string ShowEventName = @"Local\StormsTechnologiesBackupSystem_Show";

    private readonly NotifyIcon _tray;
    private readonly MainForm _form;
    private readonly System.Windows.Forms.Timer _scheduleTimer;
    private readonly BackupEngine _engine = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly EventWaitHandle? _showEvent;
    private readonly ToolStripMenuItem _backupNowItem;
    private bool _exiting;

    public TrayApplicationContext(bool startMinimized)
    {
        Icon = AppIcon.Load();
        _form = new MainForm(this);
        _ = _form.Handle; // create the window handle now so background threads can marshal to the UI

        _backupNowItem = new ToolStripMenuItem("Back up now", null, async (_, _) => await RunBackupAsync("manual"));
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open " + AppPaths.AppName, null, (_, _) => ShowMainWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_backupNowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = AppPaths.AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        _scheduleTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _scheduleTimer.Tick += async (_, _) => await CheckScheduleAsync();
        _scheduleTimer.Start();

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var thread = new Thread(WaitForShowRequests) { IsBackground = true, Name = "ShowRequests" };
            thread.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Single-instance signalling unavailable: " + ex.Message);
        }

        Log.Info($"{AppPaths.AppName} started. Next backup: {NextRun:dddd dd MMM yyyy HH:mm}");

        if (!startMinimized)
        {
            ShowMainWindow();
        }
        else
        {
            _tray.ShowBalloonTip(3000, AppPaths.AppName, $"Running in the background. Next backup: {NextRun:ddd HH:mm}", ToolTipIcon.Info);
        }

        // Catch up immediately if a backup was missed while the PC was off.
        _ = CheckScheduleAsync();
    }

    public Icon Icon { get; }

    public event Action<bool>? BackupRunningChanged;

    public event Action<BackupResult>? BackupCompleted;

    public bool IsExiting => _exiting;

    public DateTime NextRun
    {
        get
        {
            var settings = SettingsStore.LoadSettings();
            var state = SettingsStore.LoadState();
            return ScheduleCalculator.GetNextRun(
                state.LastAttemptUtc?.ToLocalTime(),
                state.LastAttemptFailed,
                state.ConsecutiveFailures,
                settings.BackupTimeOfDay,
                settings.IntervalHours,
                DateTime.Now);
        }
    }

    private async Task CheckScheduleAsync()
    {
        if (BackupEngine.IsRunning || _exiting)
        {
            return;
        }

        if (DateTime.Now >= NextRun)
        {
            await RunBackupAsync("scheduled");
        }
    }

    public async Task<BackupResult?> RunBackupAsync(string trigger)
    {
        if (BackupEngine.IsRunning)
        {
            _tray.ShowBalloonTip(3000, AppPaths.AppName, "A backup is already running.", ToolTipIcon.Info);
            return null;
        }

        _backupNowItem.Enabled = false;
        _tray.Text = "Storms Backup - backing up...";
        BackupRunningChanged?.Invoke(true);

        BackupResult result;
        try
        {
            result = await _engine.RunAsync(trigger, _shutdown.Token);
        }
        finally
        {
            _backupNowItem.Enabled = true;
            _tray.Text = AppPaths.AppName;
            BackupRunningChanged?.Invoke(false);
        }

        if (!_exiting)
        {
            if (result.Success)
            {
                _tray.ShowBalloonTip(5000, "Backup completed", result.Summary, result.Warnings.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            else
            {
                _tray.ShowBalloonTip(10000, "Backup FAILED", string.Join("\n", result.Errors.Take(3)), ToolTipIcon.Error);
            }

            BackupCompleted?.Invoke(result);
        }

        return result;
    }

    public void ShowMainWindow()
    {
        if (!_form.Visible)
        {
            _form.Show();
        }

        if (_form.WindowState == FormWindowState.Minimized)
        {
            _form.WindowState = FormWindowState.Normal;
        }

        _form.Activate();
        _form.BringToFront();
    }

    public void NotifyHiddenToTray()
    {
        _tray.ShowBalloonTip(3000, AppPaths.AppName, "Still running in the system tray - backups will continue on schedule.", ToolTipIcon.Info);
    }

    public void ExitApplication()
    {
        if (BackupEngine.IsRunning &&
            MessageBox.Show(
                "A backup is running right now. Exit anyway? The backup will be cancelled.",
                AppPaths.AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _exiting = true;
        _shutdown.Cancel();
        _scheduleTimer.Stop();
        _tray.Visible = false;
        _form.Close();
        ExitThread();
    }

    private void WaitForShowRequests()
    {
        while (_showEvent is not null)
        {
            try
            {
                _showEvent.WaitOne();
                if (_exiting)
                {
                    return;
                }

                _form.BeginInvoke(ShowMainWindow);
            }
            catch
            {
                return;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _scheduleTimer.Dispose();
            _form.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal static class AppIcon
{
    public static Icon Load()
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("StormsBackupSystem.storm.ico");
            if (stream is not null)
            {
                return new Icon(stream);
            }
        }
        catch
        {
            // fall back below
        }

        return SystemIcons.Application;
    }
}
