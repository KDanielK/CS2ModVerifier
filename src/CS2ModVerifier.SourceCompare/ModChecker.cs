using CS2ModVerifier.Core;

namespace CS2ModVerifier.SourceCompare;

public sealed record ModCheckResult(
    string DisplayName,
    ModReport Scan,
    PdxModInfo? Page,
    IReadOnlyList<SourceVerification> Sources)
{
    /// <summary>The weakest source result over the mod's own DLLs, or null for mods without code DLLs.</summary>
    public SourceStatus? SourceStatus => Sources.Count == 0
        ? null
        : Sources.Select(s => s.Status).OrderBy(Rank).First();

    private static int Rank(SourceStatus status) => status switch
    {
        SourceCompare.SourceStatus.Differences => 0,
        SourceCompare.SourceStatus.Error or SourceCompare.SourceStatus.RateLimited => 1,
        SourceCompare.SourceStatus.NoSourceLinked => 2,
        _ => 3,
    };
}

/// <summary>Runs every check on one installed mod: file scan, Paradox manifest, and source comparison.</summary>
public static class ModChecker
{
    public static async Task<ModCheckResult> CheckAsync(
        ModFolder mod,
        IReadOnlyDictionary<string, PdxModInfo> pages,
        VerificationStore? store,
        bool compareSource,
        CancellationToken cancellationToken = default)
    {
        pages.TryGetValue(PdxModsCache.Key(mod.Id, mod.Version ?? string.Empty), out var page);

        // Links to the mod's own linked repository and to Paradox Mods are expected text, not suspicious.
        IReadOnlyList<string> trustedLinks = PdxModsCache.ParseGitHubRepository(page?.GitHubUrl) is { } own
            ? [.. RiskRules.DefaultTrustedLinks, $"https://github.com/{own.Owner}/{own.Repo}"]
            : RiskRules.DefaultTrustedLinks;
        var scan = ModScanner.Scan(mod, trustedLinks);

        // A bundled library in a version the list does not have yet: learn it from the library's releases, then scan again.
        if (compareSource && scan.Findings.Any(f => f.Category == "UnlistedLibrary"))
        {
            var learnedAny = false;
            foreach (var assembly in scan.Assemblies)
            {
                if (KnownLibraries.NameFor(assembly.RelativePath, assembly.Name) is { } library && assembly.Version is { } version
                    && !KnownLibraries.IsKnownVersion(library, version) && !KnownLibraries.IsMissingVersion(library, version))
                {
                    try
                    {
                        await KnownLibraryUpdater.LearnVersionAsync(library, version, cancellationToken);
                        learnedAny = true;
                    }
                    catch (Exception e) when (e is HttpRequestException or InvalidDataException or InvalidOperationException
                        or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        // Offline or rate-limited: the finding stays "could not be checked yet" and is retried next time.
                    }
                }
            }

            if (learnedAny)
            {
                scan = ModScanner.Scan(mod, trustedLinks);
            }
        }

        var sources = new List<SourceVerification>();
        if (compareSource && scan.HasCode)
        {
            var mainNames = FileRules.MainAssemblyBaseNames(scan.Files);
            foreach (var file in scan.Files.Where(f => f.Kind == FileKind.ManagedAssembly && mainNames.Contains(Path.GetFileNameWithoutExtension(f.RelativePath))))
            {
                var key = VerificationStore.Key(file.Sha256Hex, page?.GitHubUrl);
                if (store is not null && store.TryGet(key, out var cached))
                {
                    sources.Add(cached);
                    continue;
                }

                var verification = await SourceVerifier.VerifyAsync(Path.Combine(mod.Path, file.RelativePath), page, cancellationToken);
                store?.Save(key, verification);
                sources.Add(verification);
            }
        }

        return new ModCheckResult(DisplayName(mod, scan, page), scan, page, sources);
    }

    public static string DisplayName(ModFolder mod, ModReport scan, PdxModInfo? page)
    {
        if (!string.IsNullOrWhiteSpace(page?.DisplayName))
        {
            return page.DisplayName;
        }

        var main = FileRules.MainAssemblyBaseNames(scan.Files).FirstOrDefault();
        return main ?? Path.GetFileName(mod.Path);
    }
}
