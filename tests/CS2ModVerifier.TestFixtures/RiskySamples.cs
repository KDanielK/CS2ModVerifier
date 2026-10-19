using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CS2ModVerifier.TestFixtures;

public static class RiskySamples
{
    // Deliberately bland: this DLL only has to trigger each rule. Avoid strings that make it
    // look like real malware to antivirus software (no "upload", "cmd.exe", wallet names).
    private static readonly string SampleUrl = "https://example.invalid/";

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void NeverCalled()
    {
        _ = new HttpClient().GetStringAsync(SampleUrl);
        _ = Process.Start("notepad");
        _ = GetCurrentProcess();
        _ = Assembly.LoadFrom("some.dll");
        _ = File.ReadAllText("somefile.txt");
        _ = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (OperatingSystem.IsWindows())
        {
            _ = Registry.CurrentUser.OpenSubKey("Software");
        }
    }

    public sealed class Nested
    {
        public int Value { get; set; }
    }

    public static int MethodToReference() => 42;
}
