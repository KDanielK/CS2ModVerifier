namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Tells the user when a newer CS2 Mod Verifier is released. Notify only: it never downloads or installs anything,
/// because an automatic updater would itself be a way to deliver malicious code. One request to this tool's own
/// repository (fixed here, never searched).
/// </summary>
public static class UpdateCheck
{
    public const string Owner = "KDanielK";
    public const string Repository = "CS2ModVerifier";

    public sealed record NewRelease(Version Version, string Tag, string Url);

    /// <summary>The latest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
    public static async Task<NewRelease?> FindNewerAsync(Version current, CancellationToken cancellationToken = default)
    {
        var (tag, url) = await GitHubSource.LatestReleaseAsync(Owner, Repository, cancellationToken);
        return ParseTag(tag) is { } latest && IsNewer(latest, current) && IsReleasePage(url)
            ? new NewRelease(latest, tag, url)
            : null;
    }

    /// <summary>"v1.2.3" → 1.2.3; anything else → null.</summary>
    public static Version? ParseTag(string tag) =>
        tag.StartsWith('v') && Version.TryParse(tag[1..], out var version) && version.Build >= 0 ? version : null;

    /// <summary>Compares major.minor.patch only, so 0.1.3 and 0.1.3.0 are the same version.</summary>
    public static bool IsNewer(Version latest, Version current) =>
        new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0)) > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>Only this repository's release pages are ever opened.</summary>
    public static bool IsReleasePage(string url) =>
        url.StartsWith($"https://github.com/{Owner}/{Repository}/releases/", StringComparison.Ordinal);
}
