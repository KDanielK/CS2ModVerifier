using System.Text;
using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.App;

/// <summary>
/// Hover texts in plain language for players. They say what was found and what it usually means,
/// never that something is safe or malicious.
/// </summary>
internal static class Explain
{
    public static string Finding(Finding finding, bool ignored, IReadOnlyList<Traces.Located> traces)
    {
        var text = new StringBuilder();
        text.AppendLine(SeverityMeaning(finding.Severity));
        text.AppendLine();
        text.AppendLine(Category(finding.Category));
        if (finding.File is { } file)
        {
            text.AppendLine();
            text.AppendLine($"Found in: {file}");
        }

        if (traces.Count > 0)
        {
            text.AppendLine();
            foreach (var located in traces)
            {
                text.AppendLine("• " + Traces.Describe(located));
            }

            text.AppendLine();
            text.AppendLine(Traces.AllInSource(traces) == true ? InSourceMeaning : NotInSourceMeaning);
            if (traces.Any(t => t.Url is not null))
            {
                text.AppendLine("Right-click and choose \"View the code on GitHub\" to read it.");
            }
        }

        text.AppendLine();
        text.Append(ignored
            ? "You chose to ignore this finding. It is still listed, but does not count for the mod. Select it and click Restore finding to count it again."
            : "If you have looked into this and are fine with it, select it and click Ignore finding.");
        return Wrap(text.ToString());
    }

    public static string Cell(ModCheckResult result, Rollup rollup, int column) => Wrap(column switch
    {
        0 => Summary(result, rollup),
        1 => result.Page?.UserModVersion is { } v
            ? $"Version {v} as named by the author on Paradox Mods (Paradox's own version number: {result.Scan.Mod.Version})."
            : "The mod's version.",
        2 => Source(result),
        3 => Files(result.Scan.Manifest.Status),
        _ => rollup.Highest is null
            ? "None of the checks found anything above low severity, or you ignored what they found. That does not mean the mod is safe: it means these checks did not find anything to look into."
            : $"The most serious open finding is {rollup.Highest.Value.ToString().ToLowerInvariant()}. Click the arrow next to the mod's name to see each finding. Ignored findings stay listed but do not count here.",
    });

    /// <summary>What "the call is in the source" does and does not tell the player.</summary>
    public const string InSourceMeaning =
        "IN SOURCE: this call is in the mod's published source, at the file and line shown. That means anyone can read what it does there, "
        + "and the installed mod does nothing here that the source hides. It does NOT mean the code is harmless or that anyone has reviewed it: "
        + "an author can publish harmful code openly too. Its value is that you, or someone you trust, can check it.";

    public const string NotInSourceMeaning =
        "NOT IN SOURCE: the installed mod makes this call, but the published source of that method does not contain it. "
        + "Nobody can read what this code does. This is the strongest warning sign this tool has, and what a tampered upload looks like.";

    public static string Waiting(bool checking) => checking
        ? "Checking this mod now: reading its files, comparing them with the Paradox file list and its GitHub source."
        : "This mod is waiting to be checked.";

    private static string Summary(ModCheckResult result, Rollup rollup)
    {
        var parts = new List<string> { $"{result.DisplayName}{(result.Page?.Author is { } a ? $" by {a}" : string.Empty)}." };
        parts.Add(result.SourceStatus switch
        {
            SourceStatus.Matches => "Its code was found in its published source on GitHub.",
            SourceStatus.Differences => "Some of its code is not in its published source on GitHub.",
            SourceStatus.NoSourceLinked => "It has no published source to compare with.",
            SourceStatus.Error => "Its source comparison could not be finished yet.",
            SourceStatus.RateLimited => "Its source comparison is waiting for GitHub's hourly limit to reset.",
            _ => "Its source was not compared.",
        });
        parts.Add(rollup.Highest is null ? "No open findings." : $"Most serious open finding: {rollup.Highest.Value.ToString().ToLowerInvariant()}.");
        parts.Add("Click the arrow to see the findings, or select the mod for all details below.");
        return string.Join(" ", parts);
    }

    private static string Source(ModCheckResult result)
    {
        var source = result.Sources.FirstOrDefault();
        return result.SourceStatus switch
        {
            SourceStatus.Matches =>
                $"Every part of this installed mod was found in its published source code on GitHub ({source?.Repository}, {source?.HowFound}). "
                + "Anyone can read that code. This shows the mod was built from that code; it does not mean someone has reviewed the code.",
            SourceStatus.Differences =>
                $"Some code in this installed mod is not in its published source code ({source?.Repository}). Nobody can read that part by looking at the source. "
                + "This can happen innocently, for example when an author builds from changes not uploaded yet, or uses a library kept elsewhere. "
                + "It is also what a tampered upload looks like. Click the arrow to see which parts.",
            SourceStatus.NoSourceLinked =>
                "The mod's Paradox Mods page has no GitHub link, so there is no published source to compare with. "
                + "That is common and not a problem in itself, but the code cannot be checked against anything.",
            SourceStatus.Error =>
                $"The comparison could not be finished: {source?.Message} It is tried again on the next check.",
            SourceStatus.RateLimited =>
                "GitHub allows 60 requests per hour without an account, and this check has used them up. Nothing is wrong with the mod. "
                + $"The comparison continues automatically after {source?.RetryAfter?.ToLocalTime():HH:mm}. Mods that were compared before are remembered, so this mainly happens on the first run.",
            _ => result.Scan.HasCode ? "No code file of this mod's own was found to compare." : "This mod has no code.",
        };
    }

    private static string Files(ManifestStatus status) => status switch
    {
        ManifestStatus.AllMatch =>
            "The files are exactly the ones Paradox Mods delivered, checked by their digital fingerprints (SHA-256). Nothing changed them after download. "
            + "This does not show whether the upload itself was genuine.",
        ManifestStatus.Mismatch =>
            "Some files differ from what Paradox Mods delivered, or were added afterwards. Something on this PC changed the mod after download.",
        ManifestStatus.NoManifest =>
            "There is no Paradox file list for this mod, so this cannot be checked. Local mods never have one.",
        _ => "The Paradox file list could not be read.",
    };

    private static string SeverityMeaning(Severity severity) => severity switch
    {
        Severity.High => "HIGH: worth looking into. Not proof of anything bad, but something you would want to understand.",
        Severity.Medium => "MEDIUM: usually harmless, but worth knowing.",
        Severity.Low => "LOW: common in normal mods.",
        _ => "INFO: for your information.",
    };

    private static string Category(string category) => category switch
    {
        "SourceMismatch" =>
            "This part of the installed mod is not in its published source code on GitHub, so nobody can read what it does there. "
            + "It can happen innocently (the author built from code not uploaded yet, or used a library kept elsewhere), but it is also exactly what a tampered upload looks like.",
        "CallNotInSource" =>
            "The installed mod makes a risky call (such as starting a program or connecting to the internet) in a method whose published source does not contain that call. "
            + "Nobody can read what this code does.",
        "NoSourceLinked" =>
            "The author has not linked any source code on the mod's Paradox Mods page. Nobody can compare the installed code with readable source, so you have to trust the author completely. "
            + "Many honest mods have no public source; if you know and trust the author, you can ignore this.",
        "SignatureDiffers" =>
            "A function in the installed mod exists in the source, but with different inputs. Often a harmless difference between versions; sometimes a sign the mod was built from other code than the published one.",
        "Manifest" =>
            "This file is different from what Paradox Mods delivered, or was added after download. Something on this PC changed the mod.",
        "UnexpectedNative" =>
            "A compiled library that is not one of the standard files every code mod has. This tool cannot look inside it. The 2024 Traffic malware hid in exactly such an extra file.",
        "Executable" => "A program or script file inside a mod. Mods normally do not need these.",
        "DisguisedFile" => "A program file with a misleading name or file extension.",
        "UnreadablePE" => "A file that looks like a program but could not be read. It may be damaged, or made to confuse inspection tools.",
        "Assembly" => "A code file that could not be read. It may be damaged, or made to confuse inspection tools.",
        "Network" =>
            "The mod's code can connect to the internet. Some mods do this to check for updates or show news. It is also how stolen data would be sent away.",
        "NetworkWithFileAccess" =>
            "The same code can read files on your PC and connect to the internet. Downloading updates needs this too, but it is also exactly what data-stealing malware needs. Worth knowing why the mod does this.",
        "DynamicCode" =>
            "The mod can create or load code while the game runs. Such code is not in the files this tool checked, so no check can see it. Patching libraries such as Harmony use this legitimately to change the game.",
        "NativeInterop" =>
            "The mod calls Windows functions directly, outside .NET. That code runs with full access and cannot be inspected here.",
        "Process" => "The mod can start other programs. Usually this is opening a web page or a folder.",
        "Registry" => "The mod can read or change Windows registry settings. Usually for its own settings; it is also where programs set themselves to start with Windows.",
        "FileSystem" => "The mod can read and write files. Almost every mod does this to save settings or logs.",
        "UserFolders" => "The mod looks up personal folders such as Documents or AppData. Usually to find where to save its settings.",
        "Reflection" => "The mod looks up parts of the game's code by name. Very common in mods; it can also be used to hide which features the code uses.",
        "SuspiciousStrings" =>
            "Text in the mod such as web addresses or program names, usually links to the author's pages. It matters more when the mod also looks up or loads code by name, because then such text can hide what is really used.",
        "ExtraAssembly" =>
            "An extra code library bundled with the mod, such as Harmony. Common and usually harmless; it should only appear or change when the author adds it.",
        "KnownLibrary" =>
            "The mod bundles a well-known library, and the file is byte-for-byte identical to that library's official release on GitHub. "
            + "The library's abilities (listed in the details below) are powerful, which is why many mods use it. Ignore this once you are fine with it; "
            + "a different version or build will show up again.",
        "UnofficialLibrary" =>
            "This file is named like a well-known library, but it is not identical to any official build of the version it claims, "
            + "or that version was never officially released. It may have been modified. Take this seriously.",
        "UnlistedLibrary" =>
            "This file is named like a well-known library, and its version could not be checked against the library's official releases yet, "
            + "usually because GitHub could not be reached. It is checked again automatically.",
        "NetworkTypes" =>
            "The code names networking types but never calls anything that opens a connection. Usually harmless.",
        "ProcessInfo" =>
            "The code reads information about a running program, usually the game itself, such as memory use. It does not start any program.",
        "ModuleInitializer" => "Code that runs as soon as the game loads the mod.",
        "EmbeddedResources" => "Files stored inside the mod's code file, usually translations or icons.",
        _ => category,
    };

    /// <summary>Breaks long text into lines, because tooltips do not wrap by themselves.</summary>
    private static string Wrap(string text, int width = 90)
    {
        var result = new StringBuilder();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > width)
                {
                    result.AppendLine(line.ToString());
                    line.Clear();
                }

                line.Append(line.Length == 0 ? word : " " + word);
            }

            result.AppendLine(line.ToString());
        }

        return result.ToString().TrimEnd();
    }
}
