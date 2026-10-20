using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Remembers source comparison results per tool version, DLL hash and repository, so an unchanged mod is not
/// compared again (GitHub allows 60 anonymous requests per hour). Stored in the user's local app data.
/// Results from another tool version are not reused: after an update, improved comparison rules apply to every mod.
/// </summary>
public sealed class VerificationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The tool version that produced a stored result ("0.1.7"); part of every key.</summary>
    public static readonly string ToolVersion = typeof(VerificationStore).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly string path;
    private readonly Dictionary<string, SourceVerification> results;
    private readonly Lock gate = new();

    private VerificationStore(string path, Dictionary<string, SourceVerification> results)
    {
        this.path = path;
        this.results = results;
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2ModVerifier", "source-results.json");

    /// <summary>Opens the store, dropping results from other tool versions (they are compared again when needed).</summary>
    public static VerificationStore Open(string path)
    {
        var results = new Dictionary<string, SourceVerification>(StringComparer.Ordinal);
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, SourceVerification>>(File.ReadAllText(path), Options) ?? [];
            foreach (var (key, verification) in stored.Where(e => e.Key.StartsWith(ToolVersion + "|", StringComparison.Ordinal)))
            {
                results[key] = verification;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing stored yet, or unreadable: compare again.
        }

        return new VerificationStore(path, results);
    }

    public static string Key(string dllSha256, string? repository) => ToolVersion + "|" + dllSha256 + "|" + (repository ?? "-");

    public bool TryGet(string key, out SourceVerification verification)
    {
        lock (gate)
        {
            return results.TryGetValue(key, out verification!);
        }
    }

    /// <summary>Stores final results only. Errors and "waiting for GitHub" are retried next time.</summary>
    public void Save(string key, SourceVerification verification)
    {
        if (verification.Status is SourceStatus.Error or SourceStatus.NoSourceLinked or SourceStatus.RateLimited)
        {
            return;
        }

        lock (gate)
        {
            results[key] = verification;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(results, Options));
            File.Move(temp, path, overwrite: true);
        }
    }
}
