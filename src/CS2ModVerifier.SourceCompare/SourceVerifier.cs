using System.Text.RegularExpressions;
using CS2ModVerifier.Core;

namespace CS2ModVerifier.SourceCompare;

public enum SourceStatus
{
    /// <summary>The Paradox Mods page has no GitHub link: there is no published source to compare.</summary>
    NoSourceLinked,

    /// <summary>A commit in the linked repository contains every type and method signature of the DLL.</summary>
    Matches,

    /// <summary>No candidate commit in the linked repository contains all of the DLL's code.</summary>
    Differences,

    /// <summary>The comparison could not be completed (network, rate limit, unreadable DLL).</summary>
    Error,
}

public sealed record SourceVerification(
    string DllFile,
    SourceStatus Status,
    string Message,
    string? Repository,
    string? ClaimedCommit,
    bool? ClaimedCommitInRepository,
    string? ComparedCommit,
    string? HowFound,
    IReadOnlyList<string> Notes,
    ComparisonResult? Comparison);

/// <summary>
/// Compares a mod's own DLL with the repository linked on its Paradox Mods page. Candidate commits,
/// tried in order until one matches: the commit the DLL names, a tag for the mod's version,
/// and the newest commits up to a day after the upload. Only the linked repository is ever read.
/// </summary>
public static partial class SourceVerifier
{
    public const int MaxCandidates = 5;

    [GeneratedRegex(@"\+([0-9a-fA-F]{7,40})\b")]
    private static partial Regex CommitInVersion();

    public static async Task<SourceVerification> VerifyAsync(string dllPath, PdxModInfo? page, CancellationToken cancellationToken = default)
    {
        var dllFile = Path.GetFileName(dllPath);
        var repository = PdxModsCache.ParseGitHubRepository(page?.GitHubUrl);
        if (repository is not { } repo)
        {
            var message = page is null
                ? "This mod was not installed from Paradox Mods, so there is no mod page with a source link to compare with."
                : page.GitHubUrl is null
                    ? "The mod's Paradox Mods page has no GitHub link, so there is no published source to compare."
                    : $"The GitHub link on the mod's page is not a repository address: {page.GitHubUrl}";
            return new SourceVerification(dllFile, SourceStatus.NoSourceLinked, message, null, null, null, null, null, [], null);
        }

        var repoName = $"{repo.Owner}/{repo.Repo}";
        DllModel dll;
        try
        {
            dll = DllModel.Read(dllPath);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidDataException or UnauthorizedAccessException)
        {
            return new SourceVerification(dllFile, SourceStatus.Error, $"Could not read the DLL: {e.Message}", repoName, null, null, null, null, [], null);
        }

        var notes = new List<string>();
        var claimed = dll.InformationalVersion is { } version && CommitInVersion().Match(version) is { Success: true } match
            ? match.Groups[1].Value.ToLowerInvariant()
            : null;
        bool? claimedInRepo = null;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (ComparisonResult Result, string Sha, string How)? best = null;

        try
        {
            // 1. The commit the DLL names. A hint only: the builder controls this text.
            if (claimed is not null)
            {
                var full = await GitHubSource.FindCommitAsync(repo.Owner, repo.Repo, claimed, cancellationToken);
                claimedInRepo = full is not null;
                if (full is null)
                {
                    notes.Add($"The DLL names commit {Short(claimed)}, which is not in {repoName}.");
                }
                else if (await TryAsync(full, "the commit named in the DLL") is { } done)
                {
                    return done;
                }
            }
            else
            {
                notes.Add("The DLL does not name the commit it was built from.");
            }

            // 2. A tag for the version shown on the mod's page.
            if (page?.UserModVersion is { Length: > 0 } modVersion)
            {
                var tags = await GitHubSource.ListTagsAsync(repo.Owner, repo.Repo, cancellationToken);
                foreach (var (name, sha) in tags.Where(t => TagMatchesVersion(t.Name, modVersion)))
                {
                    if (await TryAsync(sha, $"tag {name}") is { } done)
                    {
                        return done;
                    }
                }
            }

            // 3. The newest commits up to a day after the upload (authors often commit after publishing).
            if (page?.ReleasedDate is { } released)
            {
                var commits = await GitHubSource.ListCommitsUntilAsync(repo.Owner, repo.Repo, released.AddDays(1), 3, cancellationToken);
                foreach (var sha in commits)
                {
                    if (await TryAsync(sha, "a commit near the upload date") is { } done)
                    {
                        return done;
                    }
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or InvalidOperationException or TaskCanceledException or ArgumentException or System.Text.Json.JsonException)
        {
            return new SourceVerification(dllFile, SourceStatus.Error, $"The comparison could not be completed: {e.Message}",
                repoName, claimed, claimedInRepo, best?.Sha, best?.How, notes, best?.Result);
        }

        if (best is null)
        {
            return new SourceVerification(dllFile, SourceStatus.Differences,
                $"No commit in {repoName} could be matched to this version (no commit named in the DLL, no version tag, no commits before the upload).",
                repoName, claimed, claimedInRepo, null, null, notes, null);
        }

        return new SourceVerification(dllFile, SourceStatus.Differences,
            $"Some code in this DLL was not found in {repoName}. Closest: {best.Value.How} ({Short(best.Value.Sha)}).",
            repoName, claimed, claimedInRepo, best.Value.Sha, best.Value.How, notes, best.Value.Result);

        async Task<SourceVerification?> TryAsync(string sha, string how)
        {
            if (!tried.Add(sha) || tried.Count > MaxCandidates)
            {
                return null;
            }

            var zip = await GitHubSource.DownloadZipballAsync(repo.Owner, repo.Repo, sha, cancellationToken);
            var result = SourceComparer.Compare(dll, SourceModel.FromZip(zip), $"github.com/{repoName} at {Short(sha)}");
            if (result.Verdict == Verdict.Matches)
            {
                if (claimedInRepo == true && !sha.StartsWith(claimed!, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"The DLL names commit {Short(claimed!)}, but its code matches {Short(sha)}.");
                }

                return new SourceVerification(dllFile, SourceStatus.Matches,
                    $"All types and method signatures in this DLL were found in {repoName} at {how} ({Short(sha)}).",
                    repoName, claimed, claimedInRepo, sha, how, notes, result);
            }

            if (best is null || Weight(result) < Weight(best.Value.Result))
            {
                best = (result, sha, how);
            }

            return null;
        }
    }

    /// <summary>"v1.0.3", "1.0.3", "release-1.0.3" and "Bottlenecks/1.0.3" match version 1.0.3; "1.0.30" does not.</summary>
    public static bool TagMatchesVersion(string tag, string version)
    {
        if (!tag.EndsWith(version, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = tag[..^version.Length];
        return prefix.Length == 0 || prefix is "v" or "V" || prefix[^1] is '-' or '_' or '/' || prefix.EndsWith("-v", StringComparison.OrdinalIgnoreCase);
    }

    private static int Weight(ComparisonResult result) =>
        result.Findings.Sum(f => f.Severity switch { Severity.High => 100, Severity.Medium => 1, _ => 0 });

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
