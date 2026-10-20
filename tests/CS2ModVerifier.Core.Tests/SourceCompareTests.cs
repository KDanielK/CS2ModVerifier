using System.IO.Compression;
using CS2ModVerifier.SourceCompare;
using Microsoft.CodeAnalysis.CSharp;

namespace CS2ModVerifier.Core.Tests;

public class SourceCompareTests
{
    private const string SamplesFile = "CompareSamples.cs";

    [Fact]
    public void FixtureDll_MatchesItsOwnSource()
    {
        var result = Compare(SourceModel.FromDirectory(FixtureSourceDirectory()));

        Assert.Equal(Verdict.Matches, result.Verdict);
        Assert.DoesNotContain(result.Findings, f => f.Severity >= Severity.Medium);
        Assert.Equal(result.DllMethodCount, result.MatchedMethodCount);
    }

    [Fact]
    public void FixtureDll_MatchesSourceFromZip()
    {
        var result = Compare(SourceModel.FromZip(ZipDirectory(FixtureSourceDirectory())));

        Assert.Equal(Verdict.Matches, result.Verdict);
    }

    [Fact]
    public void RemovedMethod_IsReportedAsNotInSource()
    {
        var result = CompareEdited("public string Describe() => $\"{Name} ({Age})\";", string.Empty);

        Assert.Equal(Verdict.Differences, result.Verdict);
        Assert.Contains(result.Findings, f => f.Severity == Severity.High
            && f.Message.Contains("CS2ModVerifier.TestFixtures.Compare.Person::Describe()", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedParameterType_IsReportedAsSignatureDiffers()
    {
        var result = CompareEdited("public long OnlyOverload(int value)", "public long OnlyOverload(long value)");

        Assert.Contains(result.Findings, f => f.Severity == Severity.Medium && f.Category == "SignatureDiffers"
            && f.Message.Contains("OnlyOverload(Int32)", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovedType_IsReportedAsNotInSource()
    {
        var result = CompareEdited("public delegate int Transform(int value);", string.Empty);

        Assert.Contains(result.Findings, f => f.Severity == Severity.High
            && f.Message.StartsWith("Type CS2ModVerifier.TestFixtures.Compare.Transform ", StringComparison.Ordinal));
    }

    [Fact]
    public void RiskyCalls_AreTracedToTheirSourceLines()
    {
        var result = Compare(SourceModel.FromDirectory(FixtureSourceDirectory()));

        var start = Assert.Single(result.CallTraces, t => t.Api == "Process.Start");
        Assert.True(start.InSource);
        Assert.Equal("CS2ModVerifier.TestFixtures.RiskySamples::NeverCalled", start.Method);
        Assert.Equal("RiskySamples.cs", start.File);
        Assert.True(start.Line > 0);
        Assert.Contains(result.CallTraces, t => t.Api == "native call kernel32.dll!GetCurrentProcess" && t.InSource);
        Assert.All(result.CallTraces, t => Assert.True(t.InSource, $"{t.Api} in {t.Method}: {t.Reason}"));
    }

    [Fact]
    public void RiskyCallMissingFromSource_IsHigh()
    {
        // The DLL still calls Process.Start, but the "published" source no longer shows it.
        var result = CompareEditedFile("RiskySamples.cs", "_ = Process.Start(\"notepad\");", string.Empty);

        Assert.Equal(Verdict.Differences, result.Verdict);
        Assert.Contains(result.Findings, f => f.Category == "CallNotInSource" && f.Severity == Severity.High
            && f.Message.StartsWith("Process.Start is called in CS2ModVerifier.TestFixtures.RiskySamples::NeverCalled", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("System.Diagnostics.Process::Start", "Start")]
    [InlineData("System.Diagnostics.ProcessStartInfo::.ctor", "ProcessStartInfo")]
    [InlineData("System.Net.Http.HttpClient::get_Timeout", "Timeout")]
    [InlineData("native:kernel32.dll!GetCurrentProcess", "GetCurrentProcess")]
    public void CallTracer_LooksForTheNameWrittenInSource(string call, string identifier)
    {
        Assert.Equal(identifier, CallTracer.Identifier(call));
    }

    [Fact]
    public void ToolchainType_WithUnexpectedMethod_IsReported()
    {
        var dll = new DllModel("Mod.dll", "Mod", "1.0", null,
        [
            new DllType("Unity.Entities.CodeGeneratedRegistry.AssemblyTypeRegistry", false, false, false, null,
            [
                new DllMethod("Unity.Entities.CodeGeneratedRegistry.AssemblyTypeRegistry::.cctor()", ".cctor", [], null),
                new DllMethod("Unity.Entities.CodeGeneratedRegistry.AssemblyTypeRegistry::Steal()", "Steal", [], null),
            ]),
            new DllType("Mod.System+TypeHandle", false, false, false, null, []),
        ]);
        var source = new SourceModel();
        source.AddFile("namespace Mod { class System { } }");

        var result = SourceComparer.Compare(dll, source, "test");

        Assert.Contains(result.Findings, f => f.Severity == Severity.High && f.Message.Contains("Steal()", StringComparison.Ordinal));
        Assert.Contains(result.GeneratedSkipped, s => s.StartsWith("Mod.System+TypeHandle (unity-type-handle", StringComparison.Ordinal));
    }

    [Fact]
    public void IdiomaticForeachCode_IsAccepted_WhenTheSourceUsesSystemApiQuery()
    {
        var source = new SourceModel();
        source.AddFile("namespace Mod { public partial class Sys { void OnUpdate() { foreach (var item in SystemAPI.Query<RefRW<Foo>>()) { } } } }");

        var result = SourceComparer.Compare(IdiomaticForeachDll(), source, "test");

        Assert.Equal(Verdict.Matches, result.Verdict);
        Assert.Equal(4, result.GeneratedSkipped.Count(s => s.Contains("unity-idiomatic-foreach", StringComparison.Ordinal)));
    }

    [Fact]
    public void IdiomaticForeachCode_IsReported_WhenTheSourceHasNoSystemApiQuery()
    {
        var source = new SourceModel();
        source.AddFile("namespace Mod { public partial class Sys { void OnUpdate() { } } }");

        var result = SourceComparer.Compare(IdiomaticForeachDll(), source, "test");

        Assert.Equal(Verdict.Differences, result.Verdict);
        Assert.Contains(result.Findings, f => f.Severity == Severity.High && f.Message.Contains("IFE_123_0", StringComparison.Ordinal));
    }

    [Fact]
    public void IdiomaticForeachCode_WithAnExtraMethod_IsReported()
    {
        var source = new SourceModel();
        source.AddFile("namespace Mod { public partial class Sys { void OnUpdate() { foreach (var item in SystemAPI.Query<RefRW<Foo>>()) { } } } }");
        var dll = IdiomaticForeachDll(extraEnumeratorMethod: "Upload()");

        var result = SourceComparer.Compare(dll, source, "test");

        Assert.Equal(Verdict.Differences, result.Verdict);
        Assert.Contains(result.Findings, f => f.Severity == Severity.High && f.Message.Contains("IFE_123_0+Enumerator", StringComparison.Ordinal));
    }

    private static DllModel IdiomaticForeachDll(string? extraEnumeratorMethod = null)
    {
        static DllMethod M(string type, string signature) =>
            new($"{type}::{signature}", signature[..signature.IndexOf('(')], [], null);

        const string Owner = "Mod.Sys";
        const string Ife = Owner + "+IFE_123_0";
        string[] enumerator = [".ctor(EntityQuery,TypeHandle,SystemState&)", "Dispose()", "MoveNext()", "get_Current()", "GetEnumerator()", "Reset()"];
        return new DllModel("Mod.dll", "Mod", "1.0", null,
        [
            new DllType(Owner, false, false, false, null, [M(Owner, ".ctor()"), M(Owner, "OnUpdate()")]),
            new DllType(Ife, false, false, false, null, [M(Ife, "Query(EntityQuery,TypeHandle,SystemState&)"), M(Ife, "CompleteDependencies(SystemState&)")]),
            new DllType(Ife + "+Enumerator", false, false, false, null,
                [.. enumerator.Concat(extraEnumeratorMethod is null ? [] : [extraEnumeratorMethod]).Select(s => M(Ife + "+Enumerator", s))]),
            new DllType(Ife + "+ResolvedChunk", false, false, false, null, [M(Ife + "+ResolvedChunk", "Get(Int32)")]),
            new DllType(Ife + "+TypeHandle", false, false, false, null,
                [M(Ife + "+TypeHandle", ".ctor(SystemState&)"), M(Ife + "+TypeHandle", "Update(SystemState&)"), M(Ife + "+TypeHandle", "Resolve(ArchetypeChunk)")]),
        ]);
    }

    [Fact]
    public void ToolchainTypeHandle_WithMethods_IsReported()
    {
        var dll = new DllModel("Mod.dll", "Mod", "1.0", null,
        [
            new DllType("Mod.System+TypeHandle", false, false, false, null,
                [new DllMethod("Mod.System+TypeHandle::Run()", "Run", [], null)]),
        ]);
        var source = new SourceModel();
        source.AddFile("namespace Mod { class System { } }");

        var result = SourceComparer.Compare(dll, source, "test");

        Assert.Equal(Verdict.Differences, result.Verdict);
    }

    [Fact]
    public void Zip_SkipsOversizedEntry()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("repo/Big.cs").Open()))
            {
                writer.Write(new string('/', (int)SourceModel.MaxFileBytes + 1));
            }

            using (var writer = new StreamWriter(archive.CreateEntry("repo/Small.cs").Open()))
            {
                writer.Write("namespace A { class B { } }");
            }
        }

        var model = SourceModel.FromZip(buffer.ToArray());

        Assert.Contains("A.B", model.Types.Keys);
        Assert.Contains(model.Warnings, w => w.Contains("Big.cs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("int", "Int32")]
    [InlineData("string?", "String?")]
    [InlineData("int?", "Nullable")]
    [InlineData("System.Collections.Generic.List<int>", "List")]
    [InlineData("global::System.String", "String")]
    [InlineData("(int, string)", "ValueTuple")]
    [InlineData("int[,]", "Int32[,]")]
    [InlineData("byte[][]", "Byte[][]")]
    [InlineData("int*", "Int32*")]
    [InlineData("nint", "IntPtr")]
    public void Normalize_MapsSyntaxToMetadataNames(string syntax, string expected)
    {
        Assert.Equal(expected, TypeNames.Normalize(SyntaxFactory.ParseTypeName(syntax)));
    }

    [Theory]
    [InlineData("Foo?", "Foo", true)]
    [InlineData("Foo?", "Nullable", true)]
    [InlineData("Foo?[]", "Nullable[]", true)]
    [InlineData("Foo?", "Bar", false)]
    [InlineData("Int32", "Int64", false)]
    public void ParameterMatches_HandlesUnknownNullable(string source, string metadata, bool expected)
    {
        Assert.Equal(expected, TypeNames.ParameterMatches(source, metadata));
    }

    [Theory]
    [InlineData("<>c", "compiler")]
    [InlineData("<Main>b__0_0", "compiler")]
    [InlineData("__TypeHandle", "unity-codegen")]
    [InlineData("OnCreateForCompiler", "unity-codegen")]
    [InlineData("Foo$BurstDirectCall", "burst")]
    [InlineData("Foo", null)]
    [InlineData("Bar_Baz", null)]
    public void GeneratedCodeRules_ClassifyNames(string name, string? expected)
    {
        Assert.Equal(expected, GeneratedCodeRules.Match(name));
    }

    private static ComparisonResult Compare(SourceModel source) =>
        SourceComparer.Compare(DllModel.Read(TestData.FixtureDll()), source, "fixture");

    private static ComparisonResult CompareEdited(string find, string replace) => CompareEditedFile(SamplesFile, find, replace);

    private static ComparisonResult CompareEditedFile(string fileName, string find, string replace)
    {
        using var dir = new TempDir();
        foreach (var file in Directory.EnumerateFiles(FixtureSourceDirectory(), "*.cs"))
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == fileName)
            {
                Assert.Contains(find, text);
                text = text.Replace(find, replace, StringComparison.Ordinal);
            }

            File.WriteAllText(Path.Combine(dir.Path, Path.GetFileName(file)), text);
        }

        return Compare(SourceModel.FromDirectory(dir.Path));
    }

    private static string FixtureSourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "CS2ModVerifier.TestFixtures");
            if (File.Exists(Path.Combine(candidate, SamplesFile)))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not find the TestFixtures source directory.");
    }

    private static byte[] ZipDirectory(string directory)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            {
                archive.CreateEntryFromFile(file, "repo-abc123/" + Path.GetFileName(file));
            }
        }

        return buffer.ToArray();
    }
}
