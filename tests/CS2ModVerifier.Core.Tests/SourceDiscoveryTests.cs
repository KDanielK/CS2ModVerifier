using System.IO.Compression;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.Core.Tests;

public class SourceDiscoveryTests
{
    [Fact]
    public void PdxModsCache_ReadsGitHubLinkVersionAndReleaseDate()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "pdx_mods_cache.json");
        File.WriteAllText(path, """
            {"items":[
              {"id":"163289","version":"4","userModVersion":"1.0.3","displayName":"Bottlenecks","latestVersion":"5",
               "latestUpdate":"2026-10-19T08:00:00.000Z","tags":[{"id":"Code Mod"}],
               "externalLinks":[{"type":"discord","url":"https://discord.gg/x"},{"type":"github","url":"https://github.com/KDanielK/Bottlenecks-CS2-mod"}],
               "changelog":[{"Version":"4","ReleasedDate":"2026-10-17T18:00:10.000Z"}]},
              {"id":"91930","version":"4","displayName":"French Pack","tags":[{"id":"Asset"}]}
            ],"configVersion":1}
            """);

        var pages = PdxModsCache.Load(path);

        var mod = pages["163289_4"];
        Assert.Equal("https://github.com/KDanielK/Bottlenecks-CS2-mod", mod.GitHubUrl);
        Assert.Equal("1.0.3", mod.UserModVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 18, 0, 10, TimeSpan.Zero), mod.ReleasedDate);
        Assert.True(mod.IsCodeMod);
        Assert.Null(pages["91930_4"].GitHubUrl);
        Assert.False(pages["91930_4"].IsCodeMod);
    }

    [Fact]
    public void PdxModsCache_MissingFile_IsEmpty()
    {
        Assert.Empty(PdxModsCache.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "none.json")));
    }

    [Theory]
    [InlineData("https://github.com/KDanielK/Bottlenecks-CS2-mod", "KDanielK", "Bottlenecks-CS2-mod")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("https://www.github.com/owner/repo/tree/main/Code", "owner", "repo")]
    public void ParseGitHubRepository_AcceptsRepositoryLinks(string url, string owner, string repo)
    {
        Assert.Equal((owner, repo), PdxModsCache.ParseGitHubRepository(url));
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://github.com.evil.example/owner/repo")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData(null)]
    public void ParseGitHubRepository_RejectsOtherLinks(string? url)
    {
        Assert.Null(PdxModsCache.ParseGitHubRepository(url));
    }

    [Theory]
    [InlineData("v1.0.3", "1.0.3", true)]
    [InlineData("1.0.3", "1.0.3", true)]
    [InlineData("release-1.0.3", "1.0.3", true)]
    [InlineData("Bottlenecks/1.0.3", "1.0.3", true)]
    [InlineData("release-v1.0.3", "1.0.3", true)]
    [InlineData("1.0.30", "1.0.3", false)]
    [InlineData("11.0.3", "1.0.3", false)]
    [InlineData("v1.0.4", "1.0.3", false)]
    public void TagMatchesVersion(string tag, string version, bool expected)
    {
        Assert.Equal(expected, SourceVerifier.TagMatchesVersion(tag, version));
    }

    [Fact]
    public void RateLimit_StopsAtTheReserve_UntilTheReset()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var reset = now.AddMinutes(20);
            RateLimit.Reset();

            RateLimit.Update("10", reset.ToUnixTimeSeconds().ToString());
            Assert.False(RateLimit.IsExhausted(now));

            RateLimit.Update(RateLimit.Reserve.ToString(), reset.ToUnixTimeSeconds().ToString());
            var exception = Assert.Throws<GitHubRateLimitException>(() => RateLimit.ThrowIfExhausted(now));
            Assert.Equal(reset.ToUnixTimeSeconds(), exception.ResetAt.ToUnixTimeSeconds());

            // After the reset time, requests are allowed again until a new response says otherwise.
            Assert.False(RateLimit.IsExhausted(reset.AddSeconds(1)));
        }
        finally
        {
            RateLimit.Reset();
        }
    }

    [Theory]
    [InlineData(null, "1700000000")]
    [InlineData("0", null)]
    [InlineData("many", "1700000000")]
    public void RateLimit_IgnoresMissingOrInvalidHeaders(string? remaining, string? reset)
    {
        try
        {
            RateLimit.Reset();
            RateLimit.Update(remaining, reset);
            Assert.False(RateLimit.IsExhausted(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000)));
        }
        finally
        {
            RateLimit.Reset();
        }
    }

    [Fact]
    public void ProjectFile_SymbolsAreUsedForIfBranches()
    {
        var model = SourceModel.FromZip(Zip(
            ("repo-sha/Code/Mod/Mod.csproj", "<Project><PropertyGroup><DefineConstants>$(DefineConstants);USE_BURST</DefineConstants></PropertyGroup></Project>"),
            ("repo-sha/Code/Mod/A.cs", "namespace M { class A {\n#if USE_BURST\n void Burst() { }\n#endif\n} }")));

        Assert.Contains("USE_BURST", model.Symbols);
        Assert.Contains("M.A::Burst()", model.Types["M.A"].MethodKeys);
    }

    [Fact]
    public void ProjectFile_ImportFromOutsideRepository_IsWarned()
    {
        var model = SourceModel.FromZip(Zip(
            ("repo-sha/Code/Mod/Mod.csproj", """
                <Project>
                  <Import Project="$(CSII_TOOLPATH)\Mod.props" />
                  <Import Project="..\..\..\Shared\Shared.projitems" Label="Shared" />
                  <Compile Include="..\Common\Inside.cs" />
                </Project>
                """),
            ("repo-sha/Code/Mod/A.cs", "namespace M { class A { } }")));

        var warning = Assert.Single(model.Warnings);
        Assert.Contains(@"..\..\..\Shared\Shared.projitems", warning);
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
