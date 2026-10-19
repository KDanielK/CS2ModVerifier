using System.Security.Cryptography;
using System.Text;
using CS2ModVerifier.Core;

namespace CS2ModVerifier.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2mv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}

internal static class TestData
{
    public static string FixtureDll() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "CS2ModVerifier.TestFixtures.dll");

    public static byte[] ElfBytes() => [0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] MachOBytes() => [0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0];

    public static byte[] GarbagePeBytes()
    {
        var bytes = new byte[1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Base64UrlNoPadding(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Combine(params string[] parts) => System.IO.Path.Combine(parts);

    public static void WriteManifest(string root, string version, string content)
    {
        var manifestPath = System.IO.Path.Combine(root, ".cpatch", "some-guid", version, "complete", "manifest");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, content);
    }

    public static string ManifestFor(string relativePath, long size, string base64UrlHash) =>
        $"Name: Windows__Any\nHash: sha256\n\n{relativePath},{size},{base64UrlHash},rw\n";
}
