using Microsoft.Win32;

namespace CS2ModVerifier.App;

/// <summary>
/// "Start with Windows", for the current user only (HKCU\...\Run, no admin rights). Off until the user
/// turns it on in the tray menu. Antivirus software watches this key, because malware uses it too.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CS2ModVerifier";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Command());
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>When autostart is on but the exe was moved, points the entry at this exe again.</summary>
    public static void KeepPathCurrent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command(), StringComparison.OrdinalIgnoreCase))
        {
            Set(true);
        }
    }

    private static string Command() => $"\"{Environment.ProcessPath}\"";
}
