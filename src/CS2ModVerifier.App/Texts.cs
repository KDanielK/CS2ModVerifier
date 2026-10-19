using System.Text;
using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.App;

/// <summary>Player-facing wording. Precise about what each check shows; never "safe" or "verified".</summary>
internal static class Texts
{
    public static string Source(ModCheckResult result) => result.SourceStatus switch
    {
        null => result.Scan.HasCode ? "Not compared" : "No code (asset mod)",
        SourceStatus.Matches => "Matches source",
        SourceStatus.Differences => "Differs from source",
        SourceStatus.NoSourceLinked => "No source linked",
        _ => "Could not compare",
    };

    public static string Files(ModCheckResult result) => result.Scan.Manifest.Status switch
    {
        ManifestStatus.AllMatch => "As delivered",
        ManifestStatus.Mismatch => "Changed after download",
        ManifestStatus.NoManifest => "No Paradox file list",
        _ => "Could not check",
    };

    public static string Version(ModCheckResult result) =>
        result.Page?.UserModVersion ?? (result.Scan.Mod.Version is { } v ? $"v{v}" : string.Empty);

    /// <summary>A notification line for a mod that is new or changed, or null when nothing worth telling changed.</summary>
    public static string? Change(ModCheckResult? previous, ModCheckResult result, IgnoreStore ignores)
    {
        if (!result.Scan.HasCode)
        {
            return null;
        }

        var name = $"{result.DisplayName} {Version(result)}".Trim();
        var open = IgnoreStore.ImportantFindings(result).Where(f => !ignores.IsIgnored(result.Scan.Mod, f)).ToList();
        var problems = new List<string>();
        if (open.Any(f => f.Category is "SourceMismatch" or "SignatureDiffers"))
        {
            problems.Add("contains code not found in its GitHub source");
        }

        if (open.Any(f => f.Category == "Manifest"))
        {
            problems.Add("files were changed after download");
        }

        var newHigh = NewHighCategories(previous, open);
        if (newHigh.Count > 0)
        {
            problems.Add("new high findings: " + string.Join(", ", newHigh));
        }

        if (problems.Count > 0)
        {
            return $"{name}: {string.Join("; ", problems)}.";
        }

        // Unchanged results need no notification; a new or updated mod gets a short confirmation.
        if (previous is not null && previous.Scan.Files.SequenceEqual(result.Scan.Files))
        {
            return null;
        }

        return result.SourceStatus == SourceStatus.Matches
            ? $"{name} was checked: its code matches the GitHub source."
            : $"{name} was checked: {Source(result).ToLowerInvariant()}.";
    }

    public static string Details(ModCheckResult result, IgnoreStore ignores)
    {
        string Mark(Finding finding) => ignores.IsIgnored(result.Scan.Mod, finding) ? " (ignored)" : string.Empty;

        var text = new StringBuilder();
        var mod = result.Scan.Mod;
        text.AppendLine($"{result.DisplayName} {Version(result)}".Trim());
        text.AppendLine(mod.Source == ModSource.Paradox ? $"Paradox Mods {mod.Id}, version {mod.Version}" : $"Local mod {mod.Id}");
        if (result.Page?.Author is { } author)
        {
            text.AppendLine($"Author: {author}");
        }

        text.AppendLine($"Folder: {mod.Path}");
        text.AppendLine();

        if (!result.Scan.HasCode)
        {
            text.AppendLine("This mod contains no code (assets only), so there is nothing to compare.");
            return text.ToString();
        }

        text.AppendLine("SOURCE COMPARISON");
        if (result.Sources.Count == 0)
        {
            text.AppendLine("  Not compared: no code file of this mod's own was found.");
        }

        foreach (var source in result.Sources)
        {
            text.AppendLine($"  {source.DllFile}: {source.Message}");
            if (source.Comparison is { } c)
            {
                text.AppendLine($"  Types matched: {c.MatchedTypeCount} of {c.DllTypeCount}. Methods matched: {c.MatchedMethodCount} of {c.DllMethodCount}. Generated code listed separately: {c.GeneratedSkipped.Count}.");
            }

            foreach (var note in source.Notes.Concat(source.Comparison?.SourceWarnings ?? []))
            {
                text.AppendLine($"  Note: {note}");
            }

            foreach (var finding in source.Comparison?.Findings.Where(f => f.Severity >= Severity.Medium) ?? [])
            {
                text.AppendLine($"  [{finding.Severity}] {finding.Message}{Mark(finding with { File = finding.File ?? source.DllFile })}");
            }
        }

        var traces = Traces.All(result);
        if (traces.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("RISKY CALLS AND THE PUBLISHED SOURCE");
            foreach (var located in traces)
            {
                text.AppendLine($"  {(located.Trace.InSource ? "IN SOURCE    " : "NOT IN SOURCE")}  {Traces.Describe(located)}");
            }

            text.AppendLine();
            text.AppendLine("  " + (traces.All(t => t.Trace.InSource) ? Explain.InSourceMeaning : Explain.NotInSourceMeaning));
        }

        text.AppendLine();
        text.AppendLine("PARADOX FILE LIST");
        text.AppendLine($"  {Files(result)}.");
        foreach (var issue in result.Scan.Manifest.Issues)
        {
            text.AppendLine($"  [{issue.Severity}] {issue.Message}{Mark(issue)}");
        }

        text.AppendLine();
        text.AppendLine("FILES AND CODE REFERENCES");
        var findings = result.Scan.Findings.Where(f => f.Category != "Manifest").ToList();
        if (findings.Count == 0)
        {
            text.AppendLine("  Nothing found by these checks.");
        }

        foreach (var finding in findings)
        {
            var file = finding.File is null ? string.Empty : $" ({finding.File})";
            text.AppendLine($"  [{finding.Severity}] {finding.Category}: {finding.Message}{file}{Mark(finding)}");
        }

        text.AppendLine();
        text.AppendLine(Disclaimer.Short);
        return text.ToString();
    }

    /// <summary>Categories of open High findings that the previous check of this mod did not have.</summary>
    private static List<string> NewHighCategories(ModCheckResult? previous, IReadOnlyList<Finding> open)
    {
        var before = previous is null
            ? []
            : IgnoreStore.ImportantFindings(previous).Where(f => f.Severity == Severity.High).Select(f => f.Category + "|" + f.File).ToHashSet();
        return [.. open
            .Where(f => f.Severity == Severity.High && !before.Contains(f.Category + "|" + f.File))
            .Select(f => f.Category)
            .Distinct()];
    }
}
