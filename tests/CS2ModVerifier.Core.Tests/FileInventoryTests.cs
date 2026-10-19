using CS2ModVerifier.Core;

namespace CS2ModVerifier.Core.Tests;

public class FileInventoryTests
{
    [Fact]
    public void Build_ClassifiesByContentThenExtension()
    {
        using var dir = new TempDir();

        File.Copy(TestData.FixtureDll(), TestData.Combine(dir.Path, "Foo.dll"));
        File.WriteAllBytes(TestData.Combine(dir.Path, "Foo_linux_x86_64.so"), TestData.ElfBytes());
        File.Copy(TestData.FixtureDll(), TestData.Combine(dir.Path, "evil.so"));
        File.WriteAllBytes(TestData.Combine(dir.Path, "asset.cok"), [1, 2, 3]);
        File.WriteAllBytes(TestData.Combine(dir.Path, "ui.mjs"), "export default {};"u8.ToArray());

        var inventory = FileInventory.Build(dir.Path);

        Assert.Equal(FileKind.ManagedAssembly, Kind(inventory, "Foo.dll"));
        Assert.Equal(FileKind.Elf, Kind(inventory, "Foo_linux_x86_64.so"));
        Assert.Equal(FileKind.ManagedAssembly, Kind(inventory, "evil.so"));
        Assert.Equal(FileKind.Asset, Kind(inventory, "asset.cok"));
        Assert.Equal(FileKind.UiScript, Kind(inventory, "ui.mjs"));
    }

    [Fact]
    public void Build_ExcludesMetadataFolders()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(TestData.Combine(dir.Path, "mod.dll"), [1, 2, 3]);
        Directory.CreateDirectory(TestData.Combine(dir.Path, ".cpatch"));
        File.WriteAllBytes(TestData.Combine(dir.Path, ".cpatch", "manifest"), [1, 2, 3]);
        Directory.CreateDirectory(TestData.Combine(dir.Path, ".metadata"));
        File.WriteAllBytes(TestData.Combine(dir.Path, ".metadata", "thumbnail.png"), [1, 2, 3]);

        var inventory = FileInventory.Build(dir.Path);

        Assert.Single(inventory);
        Assert.Equal("mod.dll", inventory[0].RelativePath);
    }

    private static FileKind Kind(IReadOnlyList<ModFile> inventory, string relativePath) =>
        inventory.Single(f => f.RelativePath == relativePath).Kind;
}
