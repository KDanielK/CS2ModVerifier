using CS2ModVerifier.Core;

namespace CS2ModVerifier.Core.Tests;

public class ModLocatorTests
{
    [Fact]
    public void FindMods_ParsesPdxFoldersAndSkipsOthers()
    {
        using var dir = new TempDir();
        var pdx = Path.Combine(dir.Path, ".cache", "Mods", "pdx_mods");
        Directory.CreateDirectory(Path.Combine(pdx, "163289_4"));
        Directory.CreateDirectory(Path.Combine(pdx, ".downloading"));
        Directory.CreateDirectory(Path.Combine(pdx, "not-a-mod"));

        var mods = ModLocator.FindMods(dir.Path);

        var mod = Assert.Single(mods);
        Assert.Equal(ModSource.Paradox, mod.Source);
        Assert.Equal("163289", mod.Id);
        Assert.Equal("4", mod.Version);
    }

    [Fact]
    public void FindMods_FindsLocalMods()
    {
        using var dir = new TempDir();
        var local = Path.Combine(dir.Path, "Mods");
        Directory.CreateDirectory(Path.Combine(local, "Bottlenecks"));
        Directory.CreateDirectory(Path.Combine(local, "MyMod"));

        var mods = ModLocator.FindMods(dir.Path);

        Assert.Equal(2, mods.Count);
        Assert.All(mods, m => Assert.Equal(ModSource.Local, m.Source));
        Assert.All(mods, m => Assert.Null(m.Version));
    }

    [Fact]
    public void FindMods_MissingRoot_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cs2mv-missing-" + Guid.NewGuid().ToString("N"));

        var mods = ModLocator.FindMods(missing);

        Assert.Empty(mods);
    }
}
