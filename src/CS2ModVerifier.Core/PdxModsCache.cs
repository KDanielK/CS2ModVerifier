using System.Text.Json;
using System.Text.RegularExpressions;

namespace CS2ModVerifier.Core;

/// <summary>What the Paradox Mods page says about one version of a mod, as cached by the game.</summary>
public sealed record PdxModInfo(
    string Id,
    string Version,
    string? DisplayName,
    string? Author,
    string? UserModVersion,
    DateTimeOffset? ReleasedDate,
    string? GitHubUrl,
    bool IsCodeMod);

/// <summary>
/// Reads the game's local copy of the Paradox Mods pages (`.cache\Mods\pdx_mods_cache.json`).
/// The game writes it from Paradox Mods, so the GitHub link here is the one on the mod's page,
/// and no network access is needed to find it.
/// </summary>
public static partial class PdxModsCache
{
    public static string DefaultPath(string gameDataRoot) => Path.Combine(gameDataRoot, ".cache", "Mods", "pdx_mods_cache.json");

    /// <summary>Entries keyed by "id_version". A missing or unreadable cache gives an empty result.</summary>
    public static IReadOnlyDictionary<string, PdxModInfo> Load(string cachePath)
    {
        var result = new Dictionary<string, PdxModInfo>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in items.EnumerateArray())
            {
                var info = Read(item);
                if (info is not null)
                {
                    result[Key(info.Id, info.Version)] = info;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // No cache: mods simply have no linked source.
        }

        return result;
    }

    public static string Key(string id, string version) => id + "_" + version;

    /// <summary>"owner/repo" from a GitHub link such as https://github.com/owner/repo(.git)(/tree/...), or null.</summary>
    public static (string Owner, string Repo)? ParseGitHubRepository(string? url)
    {
        if (url is null)
        {
            return null;
        }

        var match = GitHubUrlPattern().Match(url.Trim());
        if (!match.Success)
        {
            return null;
        }

        var repo = match.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return (match.Groups["owner"].Value, repo);
    }

    [GeneratedRegex(@"^https://(www\.)?github\.com/(?<owner>[A-Za-z0-9-]{1,39})/(?<repo>[A-Za-z0-9._-]{1,100})(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlPattern();

    private static PdxModInfo? Read(JsonElement item)
    {
        var id = String(item, "id");
        var version = String(item, "version");
        if (id is null || version is null)
        {
            return null;
        }

        string? gitHub = null;
        if (item.TryGetProperty("externalLinks", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            gitHub = links.EnumerateArray()
                .Where(l => string.Equals(String(l, "type"), "github", StringComparison.OrdinalIgnoreCase))
                .Select(l => String(l, "url"))
                .FirstOrDefault(u => u is not null);
        }

        var isCodeMod = item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            && tags.EnumerateArray().Any(t => String(t, "id") == "Code Mod");

        return new PdxModInfo(id, version, String(item, "displayName"), String(item, "author"), String(item, "userModVersion"),
            ReleasedDate(item, version), gitHub, isCodeMod);
    }

    /// <summary>The upload date of this version: its changelog entry, or the page's last update for the latest version.</summary>
    private static DateTimeOffset? ReleasedDate(JsonElement item, string version)
    {
        if (item.TryGetProperty("changelog", out var changelog) && changelog.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in changelog.EnumerateArray())
            {
                if (String(entry, "Version") == version && Date(String(entry, "ReleasedDate")) is { } released)
                {
                    return released;
                }
            }
        }

        return String(item, "latestVersion") == version ? Date(String(item, "latestUpdate")) : null;
    }

    // Empty strings count as missing: the cache sometimes has an empty "userModVersion" for a new upload.
    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
