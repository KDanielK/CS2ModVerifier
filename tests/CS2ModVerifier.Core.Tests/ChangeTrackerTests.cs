namespace CS2ModVerifier.Core.Tests;

public class ChangeTrackerTests
{
    private const string Mod = "Paradox|1";
    private const string Folder = "1_100";

    [Fact]
    public void SameResult_IsNotAChange_AlsoAfterARestart()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.json");
        var tracker = ChangeTracker.Open(path);
        Assert.True(tracker.IsEmpty);
        Assert.True(tracker.Update(Mod, Folder,"files1", ["SourceMismatch|Mod.dll"], false).IsNew);

        Assert.False(tracker.Update(Mod, Folder,"files1", ["SourceMismatch|Mod.dll"], false).IsAnything);

        tracker.Save();
        var reopened = ChangeTracker.Open(path);
        Assert.False(reopened.IsEmpty);
        Assert.False(reopened.Update(Mod, Folder,"files1", ["SourceMismatch|Mod.dll"], false).IsAnything);
    }

    [Fact]
    public void UpdatedFiles_AndNewFindings_AreChanges()
    {
        using var dir = new TempDir();
        var tracker = ChangeTracker.Open(Path.Combine(dir.Path, "state.json"));
        tracker.Update(Mod, Folder,"files1", ["Process|Mod.dll"], false);

        var updated = tracker.Update(Mod, Folder,"files2", ["Process|Mod.dll"], false);
        Assert.True(updated.IsUpdated);
        Assert.Empty(updated.NewFindingKeys);

        var newFinding = tracker.Update(Mod, Folder,"files2", ["Process|Mod.dll", "Network|Mod.dll"], false);
        Assert.False(newFinding.IsUpdated);
        Assert.Equal(["Network|Mod.dll"], newFinding.NewFindingKeys);
    }

    [Fact]
    public void AFindingThatWentAway_IsNotAChange_ButCountsAsNewIfItReturns()
    {
        using var dir = new TempDir();
        var tracker = ChangeTracker.Open(Path.Combine(dir.Path, "state.json"));
        tracker.Update(Mod, Folder,"files1", ["Process|Mod.dll"], false);

        Assert.False(tracker.Update(Mod, Folder,"files1", [], false).IsAnything);
        Assert.Equal(["Process|Mod.dll"], tracker.Update(Mod, Folder,"files1", ["Process|Mod.dll"], false).NewFindingKeys);
    }

    [Fact]
    public void AnIncompleteComparison_DoesNotForgetFindings()
    {
        using var dir = new TempDir();
        var tracker = ChangeTracker.Open(Path.Combine(dir.Path, "state.json"));
        tracker.Update(Mod, Folder,"files1", ["SourceMismatch|Mod.dll"], false);

        // Waiting for GitHub: the comparison's findings are missing, not gone.
        Assert.False(tracker.Update(Mod, Folder,"files1", [], comparisonIncomplete: true).IsAnything);

        // The comparison finished with the same result: nothing new to report.
        Assert.False(tracker.Update(Mod, Folder,"files1", ["SourceMismatch|Mod.dll"], false).IsAnything);
    }

    [Fact]
    public void AfterAnUpdate_ALaterComparisonResultIsReported()
    {
        using var dir = new TempDir();
        var tracker = ChangeTracker.Open(Path.Combine(dir.Path, "state.json"));
        tracker.Update(Mod, Folder,"files1", ["SourceMismatch|Mod.dll"], false);

        // Updated while waiting for GitHub: the old finding was about the old files.
        Assert.True(tracker.Update(Mod, Folder,"files2", [], comparisonIncomplete: true).IsUpdated);

        Assert.Equal(["SourceMismatch|Mod.dll"], tracker.Update(Mod, Folder,"files2", ["SourceMismatch|Mod.dll"], false).NewFindingKeys);
    }

    [Fact]
    public void ANewFolderOfAKnownMod_IsAnUpdate_AndTwoVersionsOnDiskStayQuiet()
    {
        using var dir = new TempDir();
        var tracker = ChangeTracker.Open(Path.Combine(dir.Path, "state.json"));
        tracker.Update(Mod, "1_100", "files1", ["Process|Mod.dll"], false);

        var update = tracker.Update(Mod, "1_101", "files2", ["Network|Mod.dll"], false);
        Assert.True(update.IsUpdated);
        Assert.False(update.IsNew);

        // Both versions are still on disk: checking them in turn is not a change.
        Assert.False(tracker.Update(Mod, "1_100", "files1", ["Process|Mod.dll"], false).IsAnything);
        Assert.False(tracker.Update(Mod, "1_101", "files2", ["Network|Mod.dll"], false).IsAnything);

        // Another mod whose id starts the same way is not the same mod.
        Assert.True(tracker.Update("Paradox|11", "11_100", "files3", [], false).IsNew);
    }

    [Fact]
    public void FindingKey_IgnoresTheWording()
    {
        var before = new Finding(Severity.High, "Network", "References HttpClient", "Mod.dll");
        var reworded = before with { Message = "Can connect to the internet" };

        Assert.Equal(ChangeTracker.FindingKey(before), ChangeTracker.FindingKey(reworded));
    }

    [Fact]
    public void Fingerprint_ChangesWithAnyFile_NotWithTheirOrder()
    {
        ModFile a = new("a.dll", 1, "aaa", FileKind.ManagedAssembly), b = new("b.dll", 1, "bbb", FileKind.Data);

        Assert.Equal(ChangeTracker.Fingerprint([a, b]), ChangeTracker.Fingerprint([b, a]));
        Assert.NotEqual(ChangeTracker.Fingerprint([a, b]), ChangeTracker.Fingerprint([a, b with { Sha256Hex = "ccc" }]));
        Assert.NotEqual(ChangeTracker.Fingerprint([a, b]), ChangeTracker.Fingerprint([a]));
    }
}
