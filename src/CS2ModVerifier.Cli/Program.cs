using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

return Run(args);

static int Run(string[] args)
{
    var command = "scan";
    var index = 0;
    if (args.Length > 0 && !args[0].StartsWith('-'))
    {
        command = args[0];
        index = 1;
    }

    return command switch
    {
        "scan" => Scan(args, index),
        "compare" => Compare(args, index),
        "verify" => Verify(args, index),
        _ => UnknownCommand(command),
    };
}

static int Verify(string[] args, int index)
{
    string? root = null, modPath = null;
    var json = false;
    var useStore = true;
    while (index < args.Length)
    {
        switch (args[index])
        {
            case "--json": json = true; index++; break;
            case "--no-cache": useStore = false; index++; break;
            case "--root" or "--mod" when index + 1 < args.Length:
                if (args[index] == "--root") root = args[index + 1]; else modPath = args[index + 1];
                index += 2;
                break;
            default:
                Console.Error.WriteLine($"Unknown or incomplete option: {args[index]}");
                Console.Error.WriteLine("Usage: verify [--root <gameDataRoot>] [--mod <modFolder>] [--json] [--no-cache]");
                return 2;
        }
    }

    root ??= ModLocator.DefaultGameDataRoot();
    KnownLibraries.UseLocalFile(KnownLibraries.DefaultLocalPath());
    var pages = PdxModsCache.Load(PdxModsCache.DefaultPath(root));
    var store = useStore ? VerificationStore.Open(VerificationStore.DefaultPath()) : null;
    // Asset mods are skipped (performance); a mod named with --mod is always checked.
    var installed = ModLocator.FindMods(root);
    var mods = modPath is not null ? [ModFolderFromPath(modPath)] : CodeMods.Only(installed, pages);
    var skipped = modPath is null ? installed.Count - mods.Count : 0;

    var results = new List<ModCheckResult>();
    foreach (var mod in mods)
    {
        results.Add(ModChecker.CheckAsync(mod, pages, store, compareSource: true).GetAwaiter().GetResult());
    }

    if (json)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(results, options));
        return 0;
    }

    foreach (var result in results.Where(r => r.Scan.HasCode))
    {
        var version = result.Page?.UserModVersion is { } v ? $" {v}" : string.Empty;
        Console.WriteLine($"{result.DisplayName}{version}  [{result.Scan.Mod.Id} v{result.Scan.Mod.Version}]");
        Console.WriteLine($"  Paradox file list: {result.Scan.Manifest.Status}");
        foreach (var source in result.Sources)
        {
            Console.WriteLine($"  Source ({source.DllFile}): {source.Status}. {source.Message}");
            foreach (var note in source.Notes.Concat(source.Comparison?.SourceWarnings ?? []))
            {
                Console.WriteLine($"    Note: {note}");
            }

            foreach (var finding in source.Comparison?.Findings.Where(f => f.Severity >= Severity.Medium).Take(5) ?? [])
            {
                Console.WriteLine($"    [{finding.Severity}] {finding.Message}");
            }

            foreach (var trace in source.Comparison?.CallTraces ?? [])
            {
                var where = trace.InSource ? $"in the source at {trace.File}, line {trace.Line}" : $"NOT in the source: {trace.Reason}";
                Console.WriteLine($"    {(trace.InSource ? "+" : "!")} {trace.Api} in {trace.Method}: {where}");
            }
        }

        var counts = string.Join(", ", new[] { Severity.High, Severity.Medium }.Select(s => $"{result.Scan.Findings.Count(f => f.Severity == s)} {s}"));
        Console.WriteLine($"  Code references and files: {counts}");
        Console.WriteLine();
    }

    Console.WriteLine($"Checked {results.Count(r => r.Scan.HasCode)} code mod(s); skipped {skipped} asset mod(s).");
    Console.WriteLine();
    PrintDisclaimer();
    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}. Use 'scan' or 'compare'.");
    return 2;
}

static int Compare(string[] args, int index)
{
    string? dllPath = null, sourcePath = null, repo = null, gitRef = null;
    var json = false;
    var verbose = false;

    while (index < args.Length)
    {
        var option = args[index];
        if (option is "--json" or "--verbose")
        {
            json |= option == "--json";
            verbose |= option == "--verbose";
            index++;
            continue;
        }

        if (option is not ("--dll" or "--source" or "--repo" or "--ref"))
        {
            Console.Error.WriteLine($"Unknown option: {option}");
            return 2;
        }

        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Missing value for {option}");
            return 2;
        }

        var value = args[index + 1];
        index += 2;
        switch (option)
        {
            case "--dll": dllPath = value; break;
            case "--source": sourcePath = value; break;
            case "--repo": repo = value; break;
            case "--ref": gitRef = value; break;
        }
    }

    var repoParts = repo?.Split('/');
    if (dllPath is null || (sourcePath is null) == (repo is null) || (repo is not null && (gitRef is null || repoParts!.Length != 2)))
    {
        Console.Error.WriteLine("Usage: compare --dll <path> (--source <folder-or-.zip> | --repo <owner/name> --ref <tag-or-commit>) [--json] [--verbose]");
        return 2;
    }

    ComparisonResult result;
    try
    {
        var dll = DllModel.Read(dllPath);
        SourceModel source;
        string description;
        if (repo is not null)
        {
            var zip = GitHubSource.DownloadZipballAsync(repoParts![0], repoParts[1], gitRef!).GetAwaiter().GetResult();
            source = SourceModel.FromZip(zip);
            description = $"github.com/{repo} at {gitRef}";
        }
        else if (File.Exists(sourcePath) && sourcePath!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            source = SourceModel.FromZip(File.ReadAllBytes(sourcePath));
            description = Path.GetFileName(sourcePath);
        }
        else
        {
            source = SourceModel.FromDirectory(sourcePath!);
            description = Path.GetFullPath(sourcePath!);
        }

        result = SourceComparer.Compare(dll, source, description);
    }
    catch (Exception e) when (e is IOException or InvalidDataException or BadImageFormatException or HttpRequestException
        or InvalidOperationException or ArgumentException or UnauthorizedAccessException or TaskCanceledException)
    {
        Console.Error.WriteLine($"Error: {e.Message}");
        return 1;
    }

    if (json)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(result, options));
        return 0;
    }

    var version = result.InformationalVersion ?? result.Version;
    Console.WriteLine($"{result.AssemblyName ?? result.DllFile} {version} ({result.DllFile})");
    Console.WriteLine($"  Source: {result.SourceDescription}");
    Console.WriteLine($"  {result.Summary}");
    Console.WriteLine($"  Types: matched {result.MatchedTypeCount} of {result.DllTypeCount}. "
        + $"Methods: matched {result.MatchedMethodCount} of {result.DllMethodCount} ({result.ImplicitAcceptedCount} accepted as compiler-implied). "
        + $"Generated code skipped: {result.GeneratedSkipped.Count}.");

    foreach (var warning in result.SourceWarnings)
    {
        Console.WriteLine($"  Warning: {warning}");
    }

    foreach (var finding in result.Findings.Where(f => verbose || f.Severity >= Severity.Medium))
    {
        Console.WriteLine($"    [{finding.Severity}] {finding.Category}: {finding.Message}");
    }

    if (verbose)
    {
        Console.WriteLine("  Accepted as compiler-implied:");
        foreach (var item in result.ImplicitAccepted)
        {
            Console.WriteLine($"    {item}");
        }

        Console.WriteLine("  Skipped as generated code:");
        foreach (var item in result.GeneratedSkipped)
        {
            Console.WriteLine($"    {item}");
        }
    }

    Console.WriteLine();
    PrintDisclaimer();
    return 0;
}

static int Scan(string[] args, int index)
{
    string? root = null;
    string? modPath = null;
    var json = false;
    var verbose = false;

    while (index < args.Length)
    {
        switch (args[index])
        {
            case "--json":
                json = true;
                index++;
                break;
            case "--verbose":
                verbose = true;
                index++;
                break;
            case "--root":
                if (index + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for --root");
                    return 2;
                }

                root = args[index + 1];
                index += 2;
                break;
            case "--mod":
                if (index + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Missing value for --mod");
                    return 2;
                }

                modPath = args[index + 1];
                index += 2;
                break;
            default:
                Console.Error.WriteLine($"Unknown option: {args[index]}");
                return 2;
        }
    }

    var mods = new List<ModFolder>();
    if (modPath is not null)
    {
        mods.Add(ModFolderFromPath(modPath));
    }
    else
    {
        // Asset mods are skipped (performance); a mod named with --mod is always checked.
        var gameRoot = root ?? ModLocator.DefaultGameDataRoot();
        mods.AddRange(CodeMods.Only(ModLocator.FindMods(gameRoot), PdxModsCache.Load(PdxModsCache.DefaultPath(gameRoot))));
    }

    var reports = new List<ModReport>();
    foreach (var mod in mods)
    {
        reports.Add(ModScanner.Scan(mod));
    }

    if (json)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        Console.WriteLine(JsonSerializer.Serialize(reports, options));
        return 0;
    }

    PrintText(reports, verbose);
    return 0;
}

static ModFolder ModFolderFromPath(string path)
{
    var fullPath = Path.GetFullPath(path);
    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
    var match = Regex.Match(name, @"^(\d+)_(\d+)$");
    if (match.Success)
    {
        return new ModFolder(ModSource.Paradox, match.Groups[1].Value, match.Groups[2].Value, fullPath);
    }

    return new ModFolder(ModSource.Local, name, null, fullPath);
}

static void PrintText(List<ModReport> reports, bool verbose)
{
    for (var i = 0; i < reports.Count; i++)
    {
        var report = reports[i];
        var name = DisplayName(report);
        var header = report.Mod.Source == ModSource.Paradox
            ? $"[Paradox {report.Mod.Id} v{report.Mod.Version}] {name}"
            : $"[Local {report.Mod.Id}] {name}";
        Console.WriteLine(header);

        if (report.HasCode)
        {
            Console.WriteLine($"  Manifest: {report.Manifest.Status}");
            var high = report.Findings.Count(f => f.Severity == Severity.High);
            var medium = report.Findings.Count(f => f.Severity == Severity.Medium);
            var low = report.Findings.Count(f => f.Severity == Severity.Low);
            var info = report.Findings.Count(f => f.Severity == Severity.Info);
            Console.WriteLine($"  Findings: {high} High, {medium} Medium, {low} Low, {info} Info");
        }
        else
        {
            Console.WriteLine("  No code (asset mod)");
        }

        foreach (var finding in report.Findings)
        {
            if (!verbose && finding.Severity < Severity.Medium)
            {
                continue;
            }

            var filePart = finding.File is null ? string.Empty : $" ({finding.File})";
            Console.WriteLine($"    [{finding.Severity}] {finding.Category}: {finding.Message}{filePart}");
        }

        if (i < reports.Count - 1)
        {
            Console.WriteLine();
        }
    }

    var totalFindings = reports.Sum(r => r.Findings.Count);
    Console.WriteLine();
    Console.WriteLine($"Scanned {reports.Count} mod(s), {totalFindings} finding(s).");
    Console.WriteLine();
    PrintDisclaimer();
}

static void PrintDisclaimer()
{
    Console.WriteLine(Disclaimer.ChecksTitle.ToUpperInvariant());
    Console.WriteLine(Disclaimer.Checks);
    Console.WriteLine();
    Console.WriteLine(Disclaimer.Title.ToUpperInvariant());
    Console.WriteLine(Disclaimer.Text);
}

static string DisplayName(ModReport report)
{
    if (report.Assemblies.Count == 0)
    {
        return Path.GetFileName(report.Mod.Path);
    }

    var fileNames = report.Files
        .Select(f => f.RelativePath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var assembly in report.Assemblies)
    {
        var baseName = ManagedBaseName(assembly.RelativePath);
        if (baseName is not null && fileNames.Contains($"{baseName}_win_x86_64.dll"))
        {
            return assembly.Name ?? baseName;
        }
    }

    var topLevel = report.Assemblies
        .Where(a => !a.RelativePath.Contains('/') && a.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (topLevel.Count == 1)
    {
        return topLevel[0].Name ?? ManagedBaseName(topLevel[0].RelativePath) ?? Path.GetFileName(report.Mod.Path);
    }

    return report.Assemblies[0].Name ?? Path.GetFileName(report.Mod.Path);
}

static string? ManagedBaseName(string relativePath)
{
    if (!relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return relativePath[..^".dll".Length];
}
