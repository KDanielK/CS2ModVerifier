using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Read-only access to a public GitHub repository. The only network access in the tool:
/// requests go to api.github.com and, after a checked redirect, codeload.github.com.
/// Only repositories named by a mod's Paradox Mods page are ever requested; nothing is searched.
/// </summary>
public static partial class GitHubSource
{
    public const long MaxZipBytes = 200L * 1024 * 1024;
    private const long MaxJsonBytes = 5L * 1024 * 1024;

    // Requests always start at api.github.com (see RepoUri); the other hosts are only followed as GitHub's own redirects:
    // codeload for source zips, and GitHub's release download hosts for release files.
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "codeload.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    public sealed record ReleaseAsset(long Id, string Name, long Size);

    public sealed record Release(string Tag, bool Prerelease, string Url, IReadOnlyList<ReleaseAsset> Assets);

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,40}$")]
    private static partial Regex ShaPattern();

    public static Task<byte[]> DownloadZipballAsync(string owner, string repo, string gitRef, CancellationToken cancellationToken = default) =>
        GetBytesAsync(RepoUri(owner, repo, $"zipball/{Uri.EscapeDataString(gitRef)}"), MaxZipBytes, $"{owner}/{repo}@{gitRef}", cancellationToken);

    /// <summary>The full SHA of a commit in this repository, or null when the repository does not contain it.</summary>
    public static async Task<string?> FindCommitAsync(string owner, string repo, string sha, CancellationToken cancellationToken = default)
    {
        if (!ShaPattern().IsMatch(sha))
        {
            return null;
        }

        try
        {
            var bytes = await GetBytesAsync(RepoUri(owner, repo, $"commits/{sha}"), MaxJsonBytes, $"{owner}/{repo}@{sha}", cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.GetProperty("sha").GetString();
        }
        catch (GitHubNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Tag names and their commit SHAs (first 100).</summary>
    public static async Task<IReadOnlyList<(string Name, string Sha)>> ListTagsAsync(string owner, string repo, CancellationToken cancellationToken = default)
    {
        var bytes = await GetBytesAsync(RepoUri(owner, repo, "tags?per_page=100"), MaxJsonBytes, $"{owner}/{repo} tags", cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        return [.. document.RootElement.EnumerateArray().Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("commit").GetProperty("sha").GetString()!))];
    }

    /// <summary>The newest commits on the default branch made at or before a moment, newest first.</summary>
    public static async Task<IReadOnlyList<string>> ListCommitsUntilAsync(string owner, string repo, DateTimeOffset until, int count, CancellationToken cancellationToken = default)
    {
        var query = $"commits?per_page={count}&until={Uri.EscapeDataString(until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";
        var bytes = await GetBytesAsync(RepoUri(owner, repo, query), MaxJsonBytes, $"{owner}/{repo} commits", cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        return [.. document.RootElement.EnumerateArray().Select(c => c.GetProperty("sha").GetString()!)];
    }

    /// <summary>The repository's releases (newest 100), with their downloadable files.</summary>
    public static async Task<IReadOnlyList<Release>> ListReleasesAsync(string owner, string repo, CancellationToken cancellationToken = default)
    {
        var bytes = await GetBytesAsync(RepoUri(owner, repo, "releases?per_page=100"), MaxJsonBytes, $"{owner}/{repo} releases", cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        return [.. document.RootElement.EnumerateArray().Select(r => new Release(
            r.GetProperty("tag_name").GetString()!,
            r.GetProperty("prerelease").GetBoolean(),
            r.GetProperty("html_url").GetString()!,
            [.. r.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(
                a.GetProperty("id").GetInt64(), a.GetProperty("name").GetString()!, a.GetProperty("size").GetInt64()))]))];
    }

    /// <summary>Downloads one file attached to a release, through the API (which redirects to GitHub's download host).</summary>
    public static Task<byte[]> DownloadReleaseAssetAsync(string owner, string repo, long assetId, CancellationToken cancellationToken = default) =>
        GetBytesAsync(RepoUri(owner, repo, $"releases/assets/{assetId}"), MaxZipBytes, $"{owner}/{repo} release file {assetId}", cancellationToken,
            accept: "application/octet-stream");

    private static Uri RepoUri(string owner, string repo, string path)
    {
        if (!NamePattern().IsMatch(owner) || !NamePattern().IsMatch(repo))
        {
            throw new ArgumentException("Owner and repository names may only contain letters, digits, '.', '_' and '-'.");
        }

        return new Uri($"https://api.github.com/repos/{owner}/{repo}/{path}");
    }

    private static async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, string what, CancellationToken cancellationToken,
        string accept = "application/vnd.github+json")
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
            {
                throw new InvalidOperationException($"Refusing to download from {uri.Host}: only GitHub hosts are allowed.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CS2ModVerifier", "0.1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            // An optional token raises the rate limit. It is only ever sent to the API host.
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token) && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                uri = response.Headers.Location is { } location
                    ? (location.IsAbsoluteUri ? location : new Uri(uri, location))
                    : throw new InvalidOperationException("GitHub sent a redirect without a location.");
                continue;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            {
                throw new GitHubNotFoundException($"GitHub has no {what}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    ? " GitHub's limit for anonymous requests may be reached; try again later or set GITHUB_TOKEN."
                    : string.Empty;
                throw new HttpRequestException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} for {what}.{hint}");
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new InvalidDataException($"The download for {what} is larger than {maxBytes / (1024 * 1024)} MB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new InvalidDataException($"The download for {what} is larger than {maxBytes / (1024 * 1024)} MB.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        throw new InvalidOperationException("Too many redirects from GitHub.");
    }
}

public sealed class GitHubNotFoundException(string message) : HttpRequestException(message);
