using CS2ModVerifier.Core;

namespace CS2ModVerifier.Core.Tests;

public class AssemblyScannerTests
{
    [Fact]
    public void Scan_FindsExpectedReferences()
    {
        var report = AssemblyScanner.Scan(TestData.FixtureDll(), "CS2ModVerifier.TestFixtures.dll");

        Assert.Equal("CS2ModVerifier.TestFixtures", report.Name);
        Assert.NotNull(report.Mvid);
        Assert.NotNull(report.Version);

        Assert.Contains(report.TypeReferences, t => t == "System.Net.Http.HttpClient");
        Assert.Contains(report.TypeReferences, t => t == "System.Diagnostics.Process");
        Assert.Contains(report.TypeReferences, t => t == "System.Reflection.Assembly");
        Assert.Contains(report.TypeReferences, t => t == "Microsoft.Win32.Registry");
        Assert.Contains(report.TypeReferences, t => t == "System.IO.File");
        Assert.Contains(report.TypeReferences, t => t == "System.Environment");

        Assert.Contains(report.MemberReferences, m => m == "System.Net.Http.HttpClient::GetStringAsync");
        Assert.Contains(report.MemberReferences, m => m == "System.Diagnostics.Process::Start");
        Assert.Contains(report.MemberReferences, m => m == "System.Reflection.Assembly::LoadFrom");
        Assert.Contains(report.MemberReferences, m => m == "System.IO.File::ReadAllText");
        Assert.Contains(report.MemberReferences, m => m == "System.Environment::GetFolderPath");

        Assert.Contains("kernel32.dll!GetCurrentProcess", report.PInvokes);

        Assert.Contains(report.DefinedTypes, t => t == "CS2ModVerifier.TestFixtures.RiskySamples+Nested");

        Assert.Contains(report.EmbeddedResources, r => r.Contains("sample.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_DoesNotThrowOnGarbagePe()
    {
        using var dir = new TempDir();
        var path = TestData.Combine(dir.Path, "garbage.dll");
        File.WriteAllBytes(path, TestData.GarbagePeBytes());

        var report = AssemblyScanner.Scan(path, "garbage.dll");

        Assert.Null(report.Name);
        Assert.Null(report.Mvid);
        Assert.Empty(report.TypeReferences);
    }

    [Fact]
    public void RiskRules_ProduceExpectedFindings()
    {
        var report = AssemblyScanner.Scan(TestData.FixtureDll(), "CS2ModVerifier.TestFixtures.dll");
        var findings = RiskRules.Evaluate(report);

        Assert.Contains(findings, f => f.Category == "Network" && f.Severity == Severity.High);
        Assert.Contains(findings, f => f.Category == "Process" && f.Severity == Severity.Medium);
        Assert.Contains(findings, f => f.Category == "DynamicCode" && f.Severity == Severity.High);
        Assert.Contains(findings, f => f.Category == "NativeInterop" && f.Severity == Severity.High);
        Assert.Contains(findings, f => f.Category == "Registry" && f.Severity == Severity.Medium);
        Assert.Contains(findings, f => f.Category == "FileSystem" && f.Severity == Severity.Low);
        Assert.Contains(findings, f => f.Category == "UserFolders" && f.Severity == Severity.Low);
        Assert.Contains(findings, f => f.Category == "NetworkWithFileAccess" && f.Severity == Severity.High);

        // The fixture also loads code at runtime, so its suspicious text counts as Medium.
        var suspicious = findings.Single(f => f.Category == "SuspiciousStrings");
        Assert.Equal(Severity.Medium, suspicious.Severity);
        Assert.Contains("https://example.invalid/", suspicious.Message);
    }

    [Fact]
    public void SuspiciousStrings_AloneAreInfo()
    {
        var report = new AssemblyReport("Mod.dll", "Mod", "1.0", Guid.NewGuid(), [], [], [], [], [], [], false,
            ["https://example.invalid/some-page"]);

        var finding = Assert.Single(RiskRules.Evaluate(report));

        Assert.Equal("SuspiciousStrings", finding.Category);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void NetworkTypeWithoutCalls_IsInfo()
    {
        // As in Harmony: the type is named (e.g. typeof(HttpWebRequest)) but never called.
        var report = new AssemblyReport("Mod.dll", "Mod", "1.0", Guid.NewGuid(), [], ["System.Net.HttpWebRequest", "System.IO.File"],
            ["System.IO.File::ReadAllText"], [], [], [], false, []);

        var findings = RiskRules.Evaluate(report);

        Assert.Contains(findings, f => f.Category == "NetworkTypes" && f.Severity == Severity.Info);
        Assert.DoesNotContain(findings, f => f.Category is "Network" or "NetworkWithFileAccess");
    }

    [Fact]
    public void ReadingOwnProcess_IsInfo_StartingOne_IsMedium()
    {
        var reading = new AssemblyReport("Mod.dll", "Mod", "1.0", Guid.NewGuid(), [], ["System.Diagnostics.Process"],
            ["System.Diagnostics.Process::GetCurrentProcess", "System.Diagnostics.Process::get_WorkingSet64"], [], [], [], false, []);
        var starting = reading with { MemberReferences = [.. reading.MemberReferences, "System.Diagnostics.Process::Start"] };

        Assert.Equal(Severity.Info, Assert.Single(RiskRules.Evaluate(reading), f => f.Category.StartsWith("Process", StringComparison.Ordinal)).Severity);
        var started = Assert.Single(RiskRules.Evaluate(starting), f => f.Category.StartsWith("Process", StringComparison.Ordinal));
        Assert.Equal(("Process", Severity.Medium), (started.Category, started.Severity));
    }

    [Fact]
    public void AssemblyLoad_ByName_IsMedium_FromBytes_IsHigh()
    {
        var byName = Report(members: ["System.Reflection.Assembly::Load", "System.Reflection.Assembly::Load(String)"]);
        var fromBytes = Report(members: ["System.Reflection.Assembly::Load", "System.Reflection.Assembly::Load(Byte[])"]);
        var unknown = Report(members: ["System.Reflection.Assembly::Load"]);

        Assert.Equal(("AssemblyByName", Severity.Medium), Single(RiskRules.Evaluate(byName)));
        Assert.Equal(("DynamicCode", Severity.High), Single(RiskRules.Evaluate(fromBytes)));
        Assert.Equal(("DynamicCode", Severity.High), Single(RiskRules.Evaluate(unknown)));

        static (string, Severity) Single(IReadOnlyList<Finding> findings) => (Assert.Single(findings).Category, findings[0].Severity);
    }

    [Fact]
    public void Scanner_RecordsTheAssemblyLoadOverload()
    {
        // The fixture calls Assembly.LoadFrom, not Load; check that only Load is recorded with parameters.
        var report = AssemblyScanner.Scan(TestData.FixtureDll(), "CS2ModVerifier.TestFixtures.dll");

        Assert.DoesNotContain(report.MemberReferences, m => m.StartsWith("System.Reflection.Assembly::LoadFrom(", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://github.com/owner/mod/issues/new", false)]
    [InlineData("https://github.com/owner/mod", false)]
    [InlineData("https://mods.paradoxplaza.com/mods/123/Windows", false)]
    [InlineData("https://github.com/owner/mod-other/releases", true)]
    [InlineData("https://example.invalid/upload", true)]
    [InlineData("https://github.com/owner/mod/raw/payload.exe", true)]
    public void LinksToTheModsOwnRepository_AreNotSuspicious(string text, bool suspicious)
    {
        var report = Report(strings: [text]);

        var findings = RiskRules.Evaluate(report, [.. RiskRules.DefaultTrustedLinks, "https://github.com/owner/mod"]);

        Assert.Equal(suspicious, findings.Any(f => f.Category == "SuspiciousStrings"));
    }

    private static AssemblyReport Report(string[]? members = null, string[]? strings = null) =>
        new("Mod.dll", "Mod", "1.0", Guid.NewGuid(), [], [], members ?? [], [], [], [], false, strings ?? []);

    [Fact]
    public void FileAccessWithoutNetwork_IsNotFlaggedAsCombination()
    {
        var report = new AssemblyReport("Mod.dll", "Mod", "1.0", Guid.NewGuid(), [], ["System.IO.File"], ["System.IO.File::ReadAllText"],
            [], [], [], false, []);

        var findings = RiskRules.Evaluate(report);

        Assert.Equal(Severity.Low, Assert.Single(findings).Severity);
        Assert.DoesNotContain(findings, f => f.Category == "NetworkWithFileAccess");
    }
}
