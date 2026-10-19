namespace CS2ModVerifier.Core.Tests;

public class KnownLibraryTests
{
    // Official Harmony 2.2.2.0 net48 build (pardeike/Harmony release v2.2.2.0).
    private const string OfficialHarmony = "0de0118c8f1d4408de389ca33b46d2ff7778f3a8541b430cae729ec913d899c7";

    [Fact]
    public void List_IsEmbeddedAndContainsHarmony222()
    {
        var known = KnownLibraries.FindByHash(OfficialHarmony);

        Assert.NotNull(known);
        Assert.Equal(("Harmony", "2.2.2.0"), (known.Name, known.Version));
        Assert.All(KnownLibraries.All, l => Assert.Matches("^[0-9a-f]{64}$", l.Sha256));
    }

    [Fact]
    public void OfficialBuild_IsGroupedIntoOneMediumFinding()
    {
        var inventory = new List<ModFile> { new("0Harmony.dll", 1, OfficialHarmony, FileKind.ManagedAssembly) };
        var findings = new List<Finding>
        {
            new(Severity.High, "DynamicCode", "References DynamicMethod", "0Harmony.dll"),
            new(Severity.Medium, "Process", "References Process::Start", "0Harmony.dll"),
            new(Severity.Info, "ExtraAssembly", "Additional managed assembly 0Harmony.dll", "0Harmony.dll"),
        };

        var result = LibraryRules.Apply(inventory, [Assembly("0Harmony.dll", "0Harmony", "2.2.2.0")], findings);

        var summary = Assert.Single(result, f => f.Severity > Severity.Info);
        Assert.Equal(("KnownLibrary", Severity.Medium), (summary.Category, summary.Severity));
        Assert.Contains("Official Harmony 2.2.2.0", summary.Message);
        Assert.DoesNotContain(result, f => f.Category == "ExtraAssembly");
        Assert.Contains(result, f => f.Category == "DynamicCode" && f.Severity == Severity.Info);
    }

    [Fact]
    public void KnownVersionWithUnknownHash_IsHigh()
    {
        var inventory = new List<ModFile> { new("0Harmony.dll", 1, new string('a', 64), FileKind.ManagedAssembly) };

        var result = LibraryRules.Apply(inventory, [Assembly("0Harmony.dll", "0Harmony", "2.2.2.0")], []);

        var finding = Assert.Single(result);
        Assert.Equal(("UnofficialLibrary", Severity.High), (finding.Category, finding.Severity));
    }

    [Fact]
    public void RenamedFileIsRecognisedByAssemblyName()
    {
        var inventory = new List<ModFile> { new("lib/Patcher.dll", 1, new string('b', 64), FileKind.ManagedAssembly) };

        var result = LibraryRules.Apply(inventory, [Assembly("lib/Patcher.dll", "0Harmony", "2.2.2.0")], []);

        Assert.Equal("UnofficialLibrary", Assert.Single(result).Category);
    }

    [Fact]
    public void UnlistedVersion_IsMedium()
    {
        var inventory = new List<ModFile> { new("0Harmony.dll", 1, new string('c', 64), FileKind.ManagedAssembly) };

        var result = LibraryRules.Apply(inventory, [Assembly("0Harmony.dll", "0Harmony", "9.9.9.9")], []);

        var finding = Assert.Single(result);
        Assert.Equal(("UnlistedLibrary", Severity.Medium), (finding.Category, finding.Severity));
    }

    [Fact]
    public void VersionWithNoOfficialRelease_IsHigh()
    {
        KnownLibraries.LearnMissing("Harmony", "7.7.7");
        var inventory = new List<ModFile> { new("0Harmony.dll", 1, new string('e', 64), FileKind.ManagedAssembly) };

        var result = LibraryRules.Apply(inventory, [Assembly("0Harmony.dll", "0Harmony", "7.7.7.0")], []);

        var finding = Assert.Single(result);
        Assert.Equal(("UnofficialLibrary", Severity.High), (finding.Category, finding.Severity));
        Assert.Contains("no official release", finding.Message);
    }

    [Theory]
    [InlineData("v2.4.3", "2.4.3")]
    [InlineData("v3.0.0-rc.3", "3.0.0")]
    [InlineData("2.2.2.0", "2.2.2.0")]
    public void TagVersion_StripsPrefixAndSuffix(string tag, string expected)
    {
        Assert.Equal(expected, SourceCompare.KnownLibraryUpdater.TagVersion(tag));
    }

    [Fact]
    public void Normalize_TreatsThreeAndFourPartVersionsAlike()
    {
        Assert.Equal(KnownLibraries.Normalize("2.4.3"), KnownLibraries.Normalize("2.4.3.0"));
    }

    [Fact]
    public void OtherDlls_AreUntouched()
    {
        var inventory = new List<ModFile> { new("MyMod.dll", 1, new string('d', 64), FileKind.ManagedAssembly) };
        var findings = new List<Finding> { new(Severity.High, "Network", "References HttpClient", "MyMod.dll") };

        var result = LibraryRules.Apply(inventory, [Assembly("MyMod.dll", "MyMod", "1.0.0.0")], findings);

        Assert.Equal(findings, result);
    }

    private static AssemblyReport Assembly(string path, string name, string version) =>
        new(path, name, version, Guid.NewGuid(), [], [], [], [], [], [], false, []);
}
