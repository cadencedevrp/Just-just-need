using StormsBackupSystem.Core;
using StormsBackupSystem.Services;
using StormsBackupSystem.UI;

namespace StormsBackupSystem;

internal static class Program
{
    /// <summary>
    /// Usage:
    ///   StormsBackupSystem.exe                 open the program (stays in the system tray)
    ///   StormsBackupSystem.exe --minimized     start hidden in the system tray (used at Windows sign-in)
    ///   StormsBackupSystem.exe --backup-now    run one backup without a window and exit
    ///                                          (exit code 0 = success, 1 = failure; for Windows Task Scheduler)
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        Log.Initialize(AppPaths.LogDirectory);

        if (HasArg(args, "--backup-now", "/backup", "-b"))
        {
            return RunHeadless();
        }

        using var singleInstance = new Mutex(true, @"Local\StormsTechnologiesBackupSystem_UI", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: ask the existing instance to show its window.
            try
            {
                using var show = EventWaitHandle.OpenExisting(TrayApplicationContext.ShowEventName);
                show.Set();
            }
            catch
            {
                // ignore
            }

            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unexpected error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal error", e.ExceptionObject as Exception);

        using var context = new TrayApplicationContext(startMinimized: HasArg(args, "--minimized", "/minimized"));
        Application.Run(context);
        return 0;
    }

    private static int RunHeadless()
    {
        Log.Info("Command-line backup requested.");
        var result = new BackupEngine().RunAsync("command line", CancellationToken.None).GetAwaiter().GetResult();
        return result.Success ? 0 : 1;
    }

    private static bool HasArg(string[] args, params string[] names) =>
        args.Any(a => names.Any(n => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));
}
