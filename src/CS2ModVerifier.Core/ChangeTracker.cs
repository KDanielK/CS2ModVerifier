using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CS2ModVerifier.Core;

/// <summary>What changed for a mod since it was last recorded.</summary>
/// <param name="IsNew">The mod was not recorded before.</param>
/// <param name="IsUpdated">The mod's files differ from the recorded ones.</param>
/// <param name="NewFindingKeys">Findings that were not recorded before (see <see cref="ChangeTracker.FindingKey"/>).</param>
public sealed record ModChange(bool IsNew, bool IsUpdated, IReadOnlySet<string> NewFindingKeys)
{
    public bool IsAnything => IsNew || IsUpdated || NewFindingKeys.Count > 0;
}

/// <summary>
/// Remembers each mod's files and findings between checks and app starts, so the player is only told about real
/// changes: the same result on the next check, or after a restart, is not news. Stored in the user's local app data.
/// </summary>
public sealed class ChangeTracker
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string path;
    private readonly Dictionary<string, ModState> mods;
    private bool dirty;

    private ChangeTracker(string path, Dictionary<string, ModState> mods)
    {
        this.path = path;
        this.mods = mods;
    }

    private sealed record ModState(string Fingerprint, HashSet<string> Findings);

    /// <summary>Nothing recorded yet: the app's first check on this PC.</summary>
    public bool IsEmpty => mods.Count == 0;

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2ModVerifier", "known-state.json");

    public static ChangeTracker Open(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, ModState>>(File.ReadAllText(path), Options);
            return new ChangeTracker(path, stored ?? []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ChangeTracker(path, []);
        }
    }

    /// <summary>
    /// A finding's identity for "is this new": kind and file, not the wording, so a reworded message in a new
    /// app version is not reported as a change.
    /// </summary>
    public static string FindingKey(Finding finding) => finding.Category + "|" + (finding.File ?? string.Empty);

    /// <summary>A fingerprint of a mod's files: their paths and SHA-256 hashes.</summary>
    public static string Fingerprint(IEnumerable<ModFile> files)
    {
        var text = string.Join("\n", files.Select(f => f.RelativePath + "|" + f.Sha256Hex).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Compares a mod's current state with the recorded one, and records the current state.</summary>
    /// <param name="modKey">The mod's identity across versions, e.g. "Paradox|12345".</param>
    /// <param name="folder">
    /// The mod's folder name. Each folder is recorded on its own: two versions can be on disk at once, and comparing
    /// them with each other would report an update at every check. A new folder of a known mod is an update.
    /// </param>
    /// <param name="comparisonIncomplete">
    /// The source comparison did not finish (network, GitHub's limit). Its findings are then missing, not gone,
    /// so recorded findings are kept; otherwise they would count as new when the comparison succeeds. Not after an
    /// update: the old findings were about the old files, and what the comparison then finds must be reported.
    /// </param>
    public ModChange Update(string modKey, string folder, string fingerprint, IEnumerable<string> findingKeys, bool comparisonIncomplete)
    {
        var current = findingKeys.ToHashSet(StringComparer.Ordinal);
        var key = modKey + "|" + folder;
        if (!mods.TryGetValue(key, out var known))
        {
            var isKnownMod = mods.Keys.Any(k => k.StartsWith(modKey + "|", StringComparison.Ordinal));
            mods[key] = new ModState(fingerprint, current);
            dirty = true;
            return new ModChange(!isKnownMod, isKnownMod, current);
        }

        var isUpdated = known.Fingerprint != fingerprint;
        var newKeys = current.Where(k => !known.Findings.Contains(k)).ToHashSet(StringComparer.Ordinal);
        var recorded = comparisonIncomplete && !isUpdated ? known.Findings.Union(current).ToHashSet(StringComparer.Ordinal) : current;
        if (isUpdated || !recorded.SetEquals(known.Findings))
        {
            mods[key] = new ModState(fingerprint, recorded);
            dirty = true;
        }

        return new ModChange(false, isUpdated, newKeys);
    }

    /// <summary>Writes the recorded state if it changed. A failed write only means the next start may repeat a notification.</summary>
    public void Save()
    {
        if (!dirty)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(mods, Options));
            File.Move(temp, path, overwrite: true);
            dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Try again after the next change.
        }
    }
}
