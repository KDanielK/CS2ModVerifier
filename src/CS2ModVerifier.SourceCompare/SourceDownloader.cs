namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Gets a repository's source at a commit. Normally one zip download, which does not count against GitHub's hourly
/// request limit. Repositories that are mostly other files, such as thousands of icons, would mean downloading
/// hundreds of MB for a few C# files: when the zip passes <see cref="ProbeZipBytes"/>, the download stops, the file list
/// is fetched and only the C# and project files are downloaded, one request each. Nothing is written to disk.
/// </summary>
public static class SourceDownloader
{
    /// <summary>A zip larger than this is not finished; normal mod repositories stay far below it.</summary>
    public const long ProbeZipBytes = 50L * 1024 * 1024;

    /// <summary>At most this many files are fetched one by one, to stay well within GitHub's hourly limit.</summary>
    public const int MaxSingleFiles = 50;

    private static readonly HashSet<string> NeededExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".csproj", ".props", ".targets", ".projitems" };

    public static async Task<SourceModel> LoadAsync(string owner, string repo, string commit, string? assemblyName, CancellationToken cancellationToken = default)
    {
        try
        {
            return SourceModel.FromZip(await GitHubSource.DownloadZipballAsync(owner, repo, commit, ProbeZipBytes, cancellationToken), assemblyName);
        }
        catch (GitHubZipTooLargeException)
        {
            // Mostly other files: try fetching only what the comparison needs.
        }

        var (files, truncated) = await GitHubSource.ListFilesAsync(owner, repo, commit, cancellationToken);
        var needed = SelectNeeded(files);
        if (!truncated && needed.Count <= MaxSingleFiles)
        {
            var texts = new List<(string Path, string Text)>();
            foreach (var file in needed)
            {
                texts.Add((file.Path, await GitHubSource.DownloadBlobTextAsync(owner, repo, file.Sha, SourceModel.MaxFileBytes, cancellationToken)));
            }

            return SourceModel.FromFiles(texts, assemblyName);
        }

        // Many source files in a large repository: the whole zip after all, up to the full limit.
        return SourceModel.FromZip(await GitHubSource.DownloadZipballAsync(owner, repo, commit, GitHubSource.MaxZipBytes, cancellationToken), assemblyName);
    }

    /// <summary>The C# and project files worth downloading: not in bin/obj/.git, and not larger than the per-file cap.</summary>
    public static IReadOnlyList<(string Path, string Sha, long Size)> SelectNeeded(IEnumerable<(string Path, string Sha, long Size)> files) =>
    [
        .. files.Where(f => NeededExtensions.Contains(Path.GetExtension(f.Path))
            && f.Size <= SourceModel.MaxFileBytes
            && !f.Path.Split('/').SkipLast(1).Any(d => d is "bin" or "obj" or ".git" or ".vs")),
    ];
}
