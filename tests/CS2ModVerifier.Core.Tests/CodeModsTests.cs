namespace CS2ModVerifier.Core.Tests;

public class CodeModsTests
{
    [Fact]
    public void TaggedCodeMod_IsChecked_EvenWithoutDll()
    {
        using var dir = new TempDir();
        var mod = new ModFolder(ModSource.Paradox, "1", "2", dir.Path);
        var pages = new Dictionary<string, PdxModInfo> { ["1_2"] = Page("1", "2", isCodeMod: true) };

        Assert.True(CodeMods.IsCodeMod(mod, pages));
    }

    [Fact]
    public void AssetMod_IsSkipped_WithoutReadingItsFiles()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "Building.cok"), new byte[1024]);
        var mod = new ModFolder(ModSource.Paradox, "1", "2", dir.Path);
        var pages = new Dictionary<string, PdxModInfo> { ["1_2"] = Page("1", "2", isCodeMod: false) };

        Assert.False(CodeMods.IsCodeMod(mod, pages));
    }

    [Fact]
    public void DllInMainFolder_MakesItACodeMod_WhateverTheTag()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Path, "Hidden.dll"), [0x4D, 0x5A]);
        var tagged = new ModFolder(ModSource.Paradox, "1", "2", dir.Path);
        var local = new ModFolder(ModSource.Local, "MyMod", null, dir.Path);
        var pages = new Dictionary<string, PdxModInfo> { ["1_2"] = Page("1", "2", isCodeMod: false) };

        Assert.True(CodeMods.IsCodeMod(tagged, pages));
        Assert.True(CodeMods.IsCodeMod(local, pages));
    }

    [Fact]
    public void Only_KeepsCodeModsInOrder()
    {
        using var code = new TempDir();
        using var asset = new TempDir();
        File.WriteAllBytes(Path.Combine(code.Path, "Mod.dll"), [0x4D, 0x5A]);
        var mods = new[]
        {
            new ModFolder(ModSource.Local, "Asset", null, asset.Path),
            new ModFolder(ModSource.Local, "Code", null, code.Path),
        };

        Assert.Equal(["Code"], CodeMods.Only(mods, new Dictionary<string, PdxModInfo>()).Select(m => m.Id));
    }

    [Theory]
    [InlineData("v0.1.4", "0.1.3.0", true)]
    [InlineData("v0.2.0", "0.1.9.0", true)]
    [InlineData("v0.1.3", "0.1.3.0", false)]
    [InlineData("v0.1.2", "0.1.3.0", false)]
    public void UpdateCheck_ComparesVersions(string tag, string current, bool newer)
    {
        var latest = SourceCompare.UpdateCheck.ParseTag(tag);
        Assert.NotNull(latest);
        Assert.Equal(newer, SourceCompare.UpdateCheck.IsNewer(latest, Version.Parse(current)));
    }

    [Theory]
    [InlineData("0.1.4")]
    [InlineData("v0.1")]
    [InlineData("latest")]
    public void UpdateCheck_IgnoresTagsThatAreNotVersions(string tag)
    {
        Assert.Null(SourceCompare.UpdateCheck.ParseTag(tag));
    }

    [Theory]
    [InlineData("https://github.com/KDanielK/CS2ModVerifier/releases/tag/v0.1.4", true)]
    [InlineData("https://github.com/someone/CS2ModVerifier/releases/tag/v0.1.4", false)]
    [InlineData("https://evil.example/KDanielK/CS2ModVerifier/releases/", false)]
    public void UpdateCheck_OnlyOpensThisRepositorysReleasePages(string url, bool allowed)
    {
        Assert.Equal(allowed, SourceCompare.UpdateCheck.IsReleasePage(url));
    }

    private static PdxModInfo Page(string id, string version, bool isCodeMod) =>
        new(id, version, "Test", "Author", "1.0", null, null, isCodeMod);
}
