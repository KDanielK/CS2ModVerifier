using System.IO.Compression;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.Core.Tests;

public class ProjectScopeTests
{
    private const string RealMod = "namespace MyMod { public class Mod { public void OnLoad() { } } }";
    private const string TestStub = "namespace MyMod { public static class Mod { public static void Fake() { } } }";

    [Fact]
    public void StandInClassesInTestProjects_AreNotMergedIntoTheRealOne()
    {
        var zip = Zip(
            ("repo/MyMod.csproj", "<Project><ItemGroup><Compile Remove=\"Tests\\**\\*.cs\" /></ItemGroup></Project>"),
            ("repo/Mod.cs", RealMod),
            ("repo/Tests/Stubs/Stubs.csproj", "<Project />"),
            ("repo/Tests/Stubs/Mod.cs", TestStub));

        var scoped = SourceModel.FromZip(zip, "MyMod");
        var everything = SourceModel.FromZip(zip);

        Assert.Equal("MyMod.csproj", scoped.Project);
        Assert.False(scoped.Types["MyMod.Mod"].IsStatic);
        Assert.DoesNotContain("MyMod.Mod::Fake()", scoped.Types["MyMod.Mod"].MethodKeys);
        Assert.True(everything.Types["MyMod.Mod"].IsStatic); // the old behaviour, without a project
    }

    [Fact]
    public void NestedProjectsAreLeftOut_EvenWithoutCompileRemove()
    {
        var model = SourceModel.FromZip(Zip(
            ("repo/MyMod.csproj", "<Project />"),
            ("repo/Mod.cs", RealMod),
            ("repo/Tools/Gen/Gen.csproj", "<Project />"),
            ("repo/Tools/Gen/Mod.cs", TestStub)), "MyMod");

        Assert.False(model.Types["MyMod.Mod"].IsStatic);
    }

    [Fact]
    public void ProjectIsFoundByAssemblyName_AndSharedFilesAreIncluded()
    {
        var model = SourceModel.FromZip(Zip(
            ("repo/Code/Project.csproj", """
                <Project>
                  <PropertyGroup><AssemblyName>MyMod</AssemblyName></PropertyGroup>
                  <ItemGroup><Compile Include="..\Shared\Helper.cs" /></ItemGroup>
                  <Import Project="..\Common\Common.projitems" Label="Shared" />
                </Project>
                """),
            ("repo/Code/Mod.cs", RealMod),
            ("repo/Shared/Helper.cs", "namespace MyMod { class Helper { } }"),
            ("repo/Shared/NotIncluded.cs", "namespace MyMod { class NotIncluded { } }"),
            ("repo/Common/Common.projitems", "<Project />"),
            ("repo/Common/Util/Tools.cs", "namespace MyMod { class Tools { } }")), "MyMod");

        Assert.Equal("Code/Project.csproj", model.Project);
        Assert.Contains("MyMod.Helper", model.Types.Keys);
        Assert.Contains("MyMod.Tools", model.Types.Keys);
        Assert.DoesNotContain("MyMod.NotIncluded", model.Types.Keys);
    }

    [Fact]
    public void WithoutAMatchingProject_EverythingIsReadWithANote()
    {
        var model = SourceModel.FromZip(Zip(
            ("repo/Other.csproj", "<Project />"),
            ("repo/Mod.cs", RealMod)), "MyMod");

        Assert.Null(model.Project);
        Assert.Contains("MyMod.Mod", model.Types.Keys);
        Assert.Contains(model.Warnings, w => w.Contains("No project file", StringComparison.Ordinal));
    }

    [Fact]
    public void LargeRepositories_DownloadOnlyCodeAndProjectFiles()
    {
        var needed = SourceDownloader.SelectNeeded(
        [
            ("Mod.cs", "a", 1000),
            ("MyMod.csproj", "b", 2000),
            ("Directory.Build.props", "c", 300),
            ("Icons/Thumbnail_01.png", "d", 50_000),
            ("bin/Release/Generated.cs", "e", 100),
            ("Huge.cs", "f", SourceModel.MaxFileBytes + 1),
        ]);

        Assert.Equal(["Mod.cs", "MyMod.csproj", "Directory.Build.props"], needed.Select(f => f.Path));
    }

    [Fact]
    public void FromFiles_ScopesToTheProjectLikeAZip()
    {
        var model = SourceModel.FromFiles(
        [
            ("MyMod.csproj", "<Project />"),
            ("Mod.cs", RealMod),
            ("Tests/Stubs/Stubs.csproj", "<Project />"),
            ("Tests/Stubs/Mod.cs", TestStub),
        ], "MyMod");

        Assert.Equal("MyMod.csproj", model.Project);
        Assert.False(model.Types["MyMod.Mod"].IsStatic);
    }

    private static byte[] Zip(params (string Path, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }
}
