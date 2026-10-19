using CS2ModVerifier.Core;

namespace CS2ModVerifier.Core.Tests;

public class ManifestVerifierTests
{
    [Fact]
    public void Verify_AllMatch_WithBase64UrlNoPaddingAndCommaPath()
    {
        using var dir = new TempDir();
        var content = "hello,world"u8.ToArray();
        File.WriteAllBytes(TestData.Combine(dir.Path, "A,B.dll"), content);

        TestData.WriteManifest(dir.Path, "1",
            TestData.ManifestFor("A,B.dll", content.Length,
                TestData.Base64UrlNoPadding(System.Security.Cryptography.SHA256.HashData(content))));

        var result = ManifestVerifier.Verify(
            new ModFolder(ModSource.Paradox, "163289", "1", dir.Path),
            FileInventory.Build(dir.Path));

        Assert.Equal(ManifestStatus.AllMatch, result.Status);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Verify_MismatchOnChangedByte()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(TestData.Combine(dir.Path, "mod.dll"), [1, 2, 3]);

        TestData.WriteManifest(dir.Path, "1",
            TestData.ManifestFor("mod.dll", 3, TestData.Base64UrlNoPadding([9, 9, 9])));

        var result = ManifestVerifier.Verify(
            new ModFolder(ModSource.Paradox, "1", "1", dir.Path),
            FileInventory.Build(dir.Path));

        Assert.Equal(ManifestStatus.Mismatch, result.Status);
        Assert.Contains(result.Issues, f => f.Message == "File differs from Paradox manifest");
    }

    [Fact]
    public void Verify_ExtraFileNotInManifest()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(TestData.Combine(dir.Path, "mod.dll"), [1, 2, 3]);

        TestData.WriteManifest(dir.Path, "1", "Name: x\nHash: sha256\n\n");

        var result = ManifestVerifier.Verify(
            new ModFolder(ModSource.Paradox, "1", "1", dir.Path),
            FileInventory.Build(dir.Path));

        Assert.Equal(ManifestStatus.Mismatch, result.Status);
        Assert.Contains(result.Issues, f => f.Message.StartsWith("File not in Paradox manifest", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_NoManifest()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(TestData.Combine(dir.Path, "mod.dll"), [1, 2, 3]);

        var result = ManifestVerifier.Verify(
            new ModFolder(ModSource.Paradox, "1", "1", dir.Path),
            FileInventory.Build(dir.Path));

        Assert.Equal(ManifestStatus.NoManifest, result.Status);
    }

    [Fact]
    public void Verify_UnsupportedHash()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(TestData.Combine(dir.Path, "mod.dll"), [1, 2, 3]);

        TestData.WriteManifest(dir.Path, "1", "Name: x\nHash: md5\n\nmod.dll,3,abc,rw\n");

        var result = ManifestVerifier.Verify(
            new ModFolder(ModSource.Paradox, "1", "1", dir.Path),
            FileInventory.Build(dir.Path));

        Assert.Equal(ManifestStatus.Unsupported, result.Status);
    }
}
