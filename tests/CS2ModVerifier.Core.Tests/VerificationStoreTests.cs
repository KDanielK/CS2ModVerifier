using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.Core.Tests;

public class VerificationStoreTests
{
    [Fact]
    public void Key_IncludesTheToolVersion()
    {
        Assert.StartsWith(VerificationStore.ToolVersion + "|", VerificationStore.Key("abc", "owner/repo"));
        Assert.Matches(@"^\d+\.\d+\.\d+$", VerificationStore.ToolVersion);
    }

    [Fact]
    public void ResultsFromThisVersion_AreReused()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "source-results.json");
        var key = VerificationStore.Key("abc", "owner/repo");
        VerificationStore.Open(path).Save(key, Result(SourceStatus.Matches));

        Assert.True(VerificationStore.Open(path).TryGet(key, out var stored));
        Assert.Equal(SourceStatus.Matches, stored.Status);
    }

    [Fact]
    public void ResultsFromAnotherVersion_AreComparedAgain_AndDropped()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "source-results.json");
        File.WriteAllText(path, """{ "v2|abc|owner/repo": { "DllFile": "Mod.dll", "Status": "Differences", "Message": "old", "Notes": [] } }""");

        var store = VerificationStore.Open(path);
        Assert.False(store.TryGet("v2|abc|owner/repo", out _));

        store.Save(VerificationStore.Key("def", "owner/repo"), Result(SourceStatus.Matches));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("v2|abc", text);
        Assert.Contains(VerificationStore.ToolVersion + "|def", text);
    }

    private static SourceVerification Result(SourceStatus status) =>
        new("Mod.dll", status, "test", "owner/repo", null, null, "0123456", "a commit", [], null);
}
