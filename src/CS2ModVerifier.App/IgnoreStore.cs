using System.Text.Json;
using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.App;

/// <summary>
/// Findings the user chose to ignore, in %LOCALAPPDATA%\CS2ModVerifier\ignored.json.
/// A key holds the mod (not its version), the check, the file and the exact message. So an ignore
/// survives mod updates, but a finding whose text changes (for example a new API) shows up again.
/// </summary>
internal sealed class IgnoreStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "ignored.json");

    private readonly Dictionary<string, DateTimeOffset> ignored;

    private IgnoreStore(Dictionary<string, DateTimeOffset> ignored) => this.ignored = ignored;

    public static IgnoreStore Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(FilePath), Options);
            return new IgnoreStore(stored ?? []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new IgnoreStore([]);
        }
    }

    public static string Key(ModFolder mod, Finding finding) =>
        string.Join("|", mod.Source, mod.Id, finding.Category, finding.File ?? string.Empty, finding.Message);

    public bool IsIgnored(ModFolder mod, Finding finding) => ignored.ContainsKey(Key(mod, finding));

    public void SetIgnored(ModFolder mod, Finding finding, bool ignore)
    {
        var key = Key(mod, finding);
        if (ignore)
        {
            ignored[key] = DateTimeOffset.Now;
        }
        else
        {
            ignored.Remove(key);
        }

        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(ignored, Options));
    }

    /// <summary>
    /// The findings shown under a mod in the list: High and Medium from the source comparison,
    /// the Paradox file list and the file and code-reference checks.
    /// </summary>
    public static IReadOnlyList<Finding> ImportantFindings(ModCheckResult result) =>
    [
        .. result.Sources
            .SelectMany(s => s.Comparison?.Findings.Select(f => f with { File = f.File ?? s.DllFile }) ?? [])
            // No published source means the code cannot be checked against anything: High (decided 2026-10-19).
            .Concat(result.Sources
                .Where(s => s.Status == SourceStatus.NoSourceLinked)
                .Select(s => new Finding(Severity.High, "NoSourceLinked", "The mod's Paradox Mods page has no GitHub link, so its code cannot be compared with any published source", s.DllFile)))
            .Concat(result.Scan.Findings)
            .Where(f => f.Severity >= Severity.Medium)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category, StringComparer.Ordinal),
    ];

    /// <summary>The highest severity among findings that are not ignored, and the counts.</summary>
    public Rollup Summarize(ModCheckResult result)
    {
        var findings = ImportantFindings(result);
        var active = findings.Where(f => !IsIgnored(result.Scan.Mod, f)).ToList();
        return new Rollup(
            active.Count == 0 ? null : active.Max(f => f.Severity),
            active.Count(f => f.Severity == Severity.High),
            active.Count(f => f.Severity == Severity.Medium),
            findings.Count - active.Count);
    }
}

internal sealed record Rollup(Severity? Highest, int High, int Medium, int Ignored)
{
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if (High > 0)
            {
                parts.Add($"{High} high");
            }

            if (Medium > 0)
            {
                parts.Add($"{Medium} medium");
            }

            var text = parts.Count == 0 ? "No open findings" : string.Join(", ", parts);
            return Ignored > 0 ? $"{text} ({Ignored} ignored)" : text;
        }
    }
}
