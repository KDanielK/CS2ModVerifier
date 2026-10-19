using CS2ModVerifier.Core;

namespace CS2ModVerifier.Core.Tests;

public class FileRulesTests
{
    [Fact]
    public void Evaluate_FlagsUnexpectedNative()
    {
        using var dir = new TempDir();
        File.Copy(TestData.FixtureDll(), TestData.Combine(dir.Path, "MainMod.dll"));
        File.WriteAllBytes(TestData.Combine(dir.Path, "evil.so"), TestData.ElfBytes());

        var findings = FileRules.Evaluate(FileInventory.Build(dir.Path));

        Assert.Contains(findings, f => f.Category == "UnexpectedNative" && f.File == "evil.so");
    }

    [Fact]
    public void Evaluate_FlagsExecutable()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(TestData.Combine(dir.Path, "run.bat"), "echo hi"u8.ToArray());

        var findings = FileRules.Evaluate(FileInventory.Build(dir.Path));

        Assert.Contains(findings, f => f.Category == "Executable" && f.File == "run.bat");
    }

    [Fact]
    public void Evaluate_FlagsDisguisedPe()
    {
        using var dir = new TempDir();
        File.Copy(TestData.FixtureDll(), TestData.Combine(dir.Path, "evil.so"));

        var findings = FileRules.Evaluate(FileInventory.Build(dir.Path));

        Assert.Contains(findings, f => f.Category == "DisguisedFile" && f.File == "evil.so");
    }

    [Fact]
    public void Evaluate_FlagsExtraAssemblyButNotMain()
    {
        using var dir = new TempDir();
        File.Copy(TestData.FixtureDll(), TestData.Combine(dir.Path, "MainMod.dll"));
        File.Copy(TestData.FixtureDll(), TestData.Combine(dir.Path, "0Harmony.dll"));
        File.WriteAllBytes(TestData.Combine(dir.Path, "MainMod_win_x86_64.dll"), TestData.ElfBytes());

        var findings = FileRules.Evaluate(FileInventory.Build(dir.Path));

        Assert.Contains(findings, f => f.Category == "ExtraAssembly" && f.File == "0Harmony.dll");
        Assert.DoesNotContain(findings, f => f.Category == "ExtraAssembly" && f.File == "MainMod.dll");
    }
}
