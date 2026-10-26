namespace CS2ModVerifier.Core;

public static class RiskRules
{
    /// <param name="CallsOnly">Count only calls to members of the types (member references), not mere mentions of a type.</param>
    private sealed record Rule(
        Severity Severity,
        string Category,
        string[] TypePrefixes,
        string[] ExactTypeNames,
        (string Type, string[] Members)[] Members,
        string? MessageSuffix,
        bool CallsOnly = false);

    // Severity reflects what a feature means for a player (reviewed 2026-10-19):
    // High: ways to send data out or to run code no check can see. Medium: usually harmless, worth knowing.
    // Low: used by almost every mod (settings files, reflection). Combinations are flagged separately below.
    private static readonly Rule[] Rules =
    [
        // Only classes that can open a connection, and only when the code calls them. Harmony, for example,
        // names HttpWebRequest just as a reference point inside .NET, without ever calling it.
        new(Severity.High, "Network",
            [
                "System.Net.Http.", "System.Net.Sockets.", "System.Net.WebSockets.", "System.Net.Mail.",
                "System.Net.HttpWebRequest", "System.Net.WebRequest", "System.Net.FtpWebRequest", "System.Net.WebClient",
                "System.Net.HttpListener", "UnityEngine.Networking.UnityWebRequest",
            ],
            [], [], null, CallsOnly: true),
        new(Severity.Info, "NetworkTypes", ["System.Net.", "UnityEngine.Networking."], [], [],
            "Names networking types without calling a connection API."),
        // Starting another program, as opposed to reading the game's own process (memory use, loaded modules).
        new(Severity.Medium, "Process", [], ["System.Diagnostics.ProcessStartInfo"], [("System.Diagnostics.Process", ["Start"])], null),
        new(Severity.Info, "ProcessInfo", ["System.Diagnostics.Process"], [], [],
            "Reads information about a process, such as memory use; does not start one."),
        new(Severity.High, "DynamicCode",
            ["System.Runtime.Loader.AssemblyLoadContext"],
            // Only Emit types that create code. OpCodes, OpCode and Label are just data in Harmony transpilers.
            [
                "System.Reflection.Emit.AssemblyBuilder", "System.Reflection.Emit.ModuleBuilder",
                "System.Reflection.Emit.TypeBuilder", "System.Reflection.Emit.MethodBuilder",
                "System.Reflection.Emit.DynamicMethod", "System.Reflection.Emit.ILGenerator",
            ],
            // Load(byte[]) loads code from memory, the classic way to hide a payload; LoadFrom/LoadFile load other files.
            [("System.Reflection.Assembly", ["Load(Byte[])", "Load(Byte[],Byte[])", "LoadFrom", "LoadFile", "UnsafeLoadFrom"]), ("System.AppDomain", ["Load"])],
            null),
        // Load("Name") only finds an assembly by name, usually another installed mod (for compatibility).
        new(Severity.Medium, "AssemblyByName", [], [],
            [("System.Reflection.Assembly", ["Load(String)", "Load(AssemblyName)"])],
            "Looks up another assembly by name, usually an installed mod."),
        new(Severity.High, "NativeInterop",
            ["System.Runtime.InteropServices.NativeLibrary"],
            [],
            [("System.Runtime.InteropServices.Marshal", ["GetDelegateForFunctionPointer"])],
            null),
        new(Severity.Medium, "Registry", ["Microsoft.Win32.Registry", "Microsoft.Win32.RegistryKey"], [], [], null),
        new(Severity.Low, "FileSystem",
            [],
            ["System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileStream"],
            [],
            "Paths cannot be determined statically."),
        new(Severity.Low, "UserFolders",
            [],
            [],
            [("System.Environment", ["GetFolderPath", "GetEnvironmentVariable", "ExpandEnvironmentVariables"])],
            null),
        new(Severity.Low, "Reflection",
            [],
            [],
            [
                ("System.Reflection.MethodBase", ["Invoke"]),
                ("System.Reflection.MethodInfo", ["Invoke"]),
                ("System.Type", ["GetType", "InvokeMember"]),
                ("System.Activator", ["CreateInstance"]),
            ],
            null),
    ];

    // Text that matters mainly when code looks up APIs by name (reflection) to avoid a direct reference.
    // Kept short and generic: words like wallet names would make this tool itself look like a stealer to antivirus software.
    private static readonly string[] SuspiciousSubstrings =
    [
        "http://", "https://", "kernel32", "ntdll", "cmd.exe", "powershell", ".exe",
        "System.Diagnostics.Process", "Assembly.Load",
    ];

    /// <summary>
    /// The rule a single call falls under ("System.Diagnostics.Process::Start", or "native:module!Name" for P/Invoke),
    /// for tracing risky calls back to the source. Only Medium and High rules count; null otherwise.
    /// </summary>
    public static (string Category, Severity Severity)? ClassifyCall(string call)
    {
        if (call.StartsWith("native:", StringComparison.Ordinal))
        {
            return ("NativeInterop", Severity.High);
        }

        if (call == "System.Reflection.Assembly::Load")
        {
            return ("DynamicCode", Severity.High); // Overload could not be read: assume code from memory.
        }

        var separator = call.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var type = call[..separator];
        var member = call[(separator + 2)..];
        foreach (var rule in Rules.Where(r => r.Severity >= Severity.Medium).OrderByDescending(r => r.Severity))
        {
            if (rule.TypePrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal))
                || rule.ExactTypeNames.Contains(type)
                || rule.Members.Any(m => m.Type == type && m.Members.Contains(member)))
            {
                return (rule.Category, rule.Severity);
            }
        }

        return null;
    }

    /// <summary>Links that are expected in a mod's text and not suspicious: Paradox Mods pages.</summary>
    public static readonly IReadOnlyList<string> DefaultTrustedLinks = ["https://mods.paradoxplaza.com/"];

    /// <param name="trustedLinks">
    /// Link prefixes that are not suspicious for this mod, such as its own GitHub repository ("https://github.com/owner/repo")
    /// and Paradox Mods; null uses <see cref="DefaultTrustedLinks"/>. A trusted link that also contains other suspicious text still counts.
    /// </param>
    public static IReadOnlyList<Finding> Evaluate(AssemblyReport report, IReadOnlyList<string>? trustedLinks = null)
    {
        var findings = new List<Finding>();

        foreach (var rule in Rules)
        {
            var finding = EvaluateRule(report, rule);
            if (finding is not null)
            {
                findings.Add(finding);
            }
        }

        bool Has(string category) => findings.Any(f => f.Category == category);

        // The weaker Info variants only add something when the stronger finding is absent.
        findings.RemoveAll(f => (f.Category == "NetworkTypes" && Has("Network")) || (f.Category == "ProcessInfo" && Has("Process")));

        // Suspicious text is supporting evidence: Medium only when the code can also look up or load code by name.
        // Assembly.Load whose overload could not be read: assume the risky one (code from memory).
        if (report.MemberReferences.Contains("System.Reflection.Assembly::Load")
            && !report.MemberReferences.Any(m => m.StartsWith("System.Reflection.Assembly::Load(", StringComparison.Ordinal))
            && !Has("DynamicCode"))
        {
            findings.Add(new Finding(Severity.High, "DynamicCode",
                "References System.Reflection.Assembly::Load (which overload could not be read)", report.RelativePath));
        }

        var suspicious = MatchSuspiciousStrings(report, trustedLinks ?? DefaultTrustedLinks);
        if (suspicious is not null)
        {
            findings.Add(Has("Reflection") || Has("DynamicCode") ? suspicious with { Severity = Severity.Medium } : suspicious);
        }

        // Reading files and connecting to the internet in one DLL is what data-stealing code needs.
        if (Has("Network") && (Has("FileSystem") || Has("UserFolders")))
        {
            findings.Add(new Finding(Severity.High, "NetworkWithFileAccess",
                "Can both read files and connect to the internet", report.RelativePath));
        }

        if (report.HasModuleInitializer)
        {
            findings.Add(new Finding(Severity.Info, "ModuleInitializer",
                "Defines a module initializer (code runs as soon as the DLL is loaded)", report.RelativePath));
        }

        if (report.EmbeddedResources.Count > 0)
        {
            findings.Add(new Finding(Severity.Info, "EmbeddedResources",
                $"Embedded resources: {string.Join(", ", report.EmbeddedResources.Take(5))}", report.RelativePath));
        }

        return findings;
    }

    private static Finding? EvaluateRule(AssemblyReport report, Rule rule)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in rule.CallsOnly ? [] : report.TypeReferences)
        {
            foreach (var prefix in rule.TypePrefixes)
            {
                if (type.StartsWith(prefix, StringComparison.Ordinal))
                {
                    matches.Add(type);
                }
            }

            foreach (var exact in rule.ExactTypeNames)
            {
                if (type == exact)
                {
                    matches.Add(type);
                }
            }
        }

        foreach (var member in report.MemberReferences)
        {
            var separator = member.IndexOf("::", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var type = member[..separator];
            var memberName = member[(separator + 2)..];

            foreach (var prefix in rule.TypePrefixes)
            {
                if (type.StartsWith(prefix, StringComparison.Ordinal))
                {
                    matches.Add(type);
                }
            }

            foreach (var exact in rule.ExactTypeNames)
            {
                if (type == exact)
                {
                    matches.Add(type);
                }
            }

            foreach (var (patternType, patternMembers) in rule.Members)
            {
                if (type == patternType && patternMembers.Contains(memberName))
                {
                    matches.Add(member);
                }
            }
        }

        if (rule.Category == "NativeInterop")
        {
            foreach (var pInvoke in report.PInvokes)
            {
                matches.Add(pInvoke);
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var listed = matches.OrderBy(x => x, StringComparer.Ordinal).Take(5);
        var message = $"References {string.Join(", ", listed)}";
        if (rule.MessageSuffix is not null)
        {
            message += $". {rule.MessageSuffix}";
        }

        return new Finding(rule.Severity, rule.Category, message, report.RelativePath);
    }

    private static Finding? MatchSuspiciousStrings(AssemblyReport report, IReadOnlyList<string> trustedLinks)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in report.UserStrings)
        {
            var hits = SuspiciousSubstrings.Where(s => value.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            // A link to the mod's own repository or to Paradox Mods is expected, unless it also contains other suspicious text.
            if (hits.All(h => h is "http://" or "https://") && trustedLinks.Any(prefix => IsUnderLink(value, prefix)))
            {
                continue;
            }

            matches.Add(Truncate(value));
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var listed = matches.OrderBy(x => x, StringComparer.Ordinal).Take(5);
        return new Finding(Severity.Info, "SuspiciousStrings",
            $"User strings: {string.Join(" | ", listed)}", report.RelativePath);
    }

    /// <summary>"https://github.com/a/b" covers "https://github.com/a/b/issues", but not "https://github.com/a/bc".</summary>
    private static bool IsUnderLink(string value, string prefix) =>
        prefix.EndsWith('/')
            ? value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            : value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(prefix + "?", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(prefix + "#", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value)
    {
        const int max = 80;
        return value.Length <= max ? value : value[..max] + "...";
    }
}
