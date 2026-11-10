using Microsoft.Win32;
using StormsBackupSystem.Core;

namespace StormsBackupSystem.Services;

/// <summary>Adds/removes the program from "start when I sign in to Windows" (HKCU Run key).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StormsTechnologiesBackupSystem";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the Windows startup setting", ex);
        }
    }
}
