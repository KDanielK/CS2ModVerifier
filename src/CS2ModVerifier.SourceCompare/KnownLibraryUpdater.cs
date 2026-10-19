using System.IO.Compression;
using System.Security.Cryptography;
using CS2ModVerifier.Core;

namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Learns official builds of a library version that the built-in list does not have yet, from the library's
/// own GitHub releases (the repository is fixed in the tool's data, never searched). Release zips are read in
/// memory and only hashed: nothing is extracted, loaded or run.
/// </summary>
public static class KnownLibraryUpdater
{
    private const long MaxEntryBytes = 50L * 1024 * 1024;

    /// <summary>Looks the version up once; afterwards it is either known or remembered as missing.</summary>
    public static async Task LearnVersionAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var source = KnownLibraries.Sources.FirstOrDefault(s => s.Name == name);
        if (source is null || KnownLibraries.IsKnownVersion(name, version) || KnownLibraries.IsMissingVersion(name, version))
        {
            return;
        }

        var parts = source.Repository.Split('/');
        var wanted = KnownLibraries.Normalize(version);
        var releases = await GitHubSource.ListReleasesAsync(parts[0], parts[1], cancellationToken);

        var builds = new List<KnownLibrary>();
        foreach (var release in releases.Where(r => KnownLibraries.Normalize(TagVersion(r.Tag)) == wanted))
        {
            // "Ref" zips hold reference assemblies for compiling, not code that runs.
            foreach (var asset in release.Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !a.Name.StartsWith("Harmony-Ref", StringComparison.OrdinalIgnoreCase)))
            {
                var zip = await GitHubSource.DownloadReleaseAssetAsync(parts[0], parts[1], asset.Id, cancellationToken);
                var variant = asset.Name.Contains("Thin", StringComparison.OrdinalIgnoreCase) ? "thin" : "fat";
                builds.AddRange(HashEntries(zip, source, release, variant));
            }
        }

        if (builds.Count > 0)
        {
            KnownLibraries.Learn(builds);
        }
        else
        {
            KnownLibraries.LearnMissing(name, version);
        }
    }

    /// <summary>"v2.4.3" → "2.4.3", "v3.0.0-rc.3" → "3.0.0".</summary>
    public static string TagVersion(string tag)
    {
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        return dash < 0 ? text : text[..dash];
    }

    private static IEnumerable<KnownLibrary> HashEntries(byte[] zip, LibrarySource source, GitHubSource.Release release, string variant)
    {
        using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        var result = new List<KnownLibrary>();
        foreach (var entry in archive.Entries.Where(e => string.Equals(e.Name, source.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            if (entry.Length > MaxEntryBytes)
            {
                continue;
            }

            using var stream = entry.Open();
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var build = Path.GetFileName(Path.GetDirectoryName(entry.FullName.Replace('/', Path.DirectorySeparatorChar))) ?? "?";
            result.Add(new KnownLibrary(source.Name, source.FileName, TagVersion(release.Tag), $"{build} ({variant})", hash, release.Url));
        }

        return result;
    }
}
