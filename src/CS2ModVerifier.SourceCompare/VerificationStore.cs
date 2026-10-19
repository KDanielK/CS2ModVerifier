using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Remembers source comparison results per DLL hash and repository, so an unchanged mod is not
/// compared again (GitHub allows 60 anonymous requests per hour). Stored in the user's local app data.
/// </summary>
public sealed class VerificationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

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

    public static VerificationStore Open(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, SourceVerification>>(File.ReadAllText(path), Options);
            return new VerificationStore(path, stored ?? new Dictionary<string, SourceVerification>(StringComparer.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new VerificationStore(path, new Dictionary<string, SourceVerification>(StringComparer.Ordinal));
        }
    }

    // The "v2" prefix drops results stored before call tracing existed, so they are compared again.
    public static string Key(string dllSha256, string? repository) => "v2|" + dllSha256 + "|" + (repository ?? "-");

    public bool TryGet(string key, out SourceVerification verification)
    {
        lock (gate)
        {
            return results.TryGetValue(key, out verification!);
        }
    }

    /// <summary>Stores final results only. Errors (network, rate limit) are retried next time.</summary>
    public void Save(string key, SourceVerification verification)
    {
        if (verification.Status is SourceStatus.Error or SourceStatus.NoSourceLinked)
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
