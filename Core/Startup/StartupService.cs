using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace BarControl.Core.Startup;

public static class StartupService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BarControl";

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            var value = key?.GetValue(AppName);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Ignore registry permissions error
        }
    }

    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BarControl";

    public static void SyncInstalledAppVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKey, true);
            if (key == null) return;

            var v = typeof(StartupService).Assembly.GetName().Version;
            if (v == null) return;

            string versionStr = $"{v.Major}.{v.Minor}.{v.Build}";

            // Never downgrade the registered DisplayVersion if an older dev build is run outside the install folder
            if (key.GetValue("DisplayVersion") is string existingStr &&
                Version.TryParse(existingStr, out var existingVer) &&
                existingVer > new Version(v.Major, v.Minor, v.Build))
            {
                return;
            }

            key.SetValue("DisplayVersion", versionStr);
        }
        catch
        {
            // Ignore registry permissions or missing key
        }
    }
}
