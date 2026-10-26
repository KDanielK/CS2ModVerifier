using CS2ModVerifier.Core;

namespace CS2ModVerifier.SourceCompare;

public enum Verdict
{
    Matches,
    Differences,
    NotComparable,
}

public sealed record ComparisonResult(
    string DllFile,
    string? AssemblyName,
    string? Version,
    string? InformationalVersion,
    string SourceDescription,
    Verdict Verdict,
    string Summary,
    int DllTypeCount,
    int MatchedTypeCount,
    int DllMethodCount,
    int MatchedMethodCount,
    int ImplicitAcceptedCount,
    IReadOnlyList<string> ImplicitAccepted,
    IReadOnlyList<string> GeneratedSkipped,
    IReadOnlyList<string> SourceWarnings,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>Each risky call in the DLL, and whether its method's published source contains it.</summary>
    public IReadOnlyList<CallTrace> CallTraces { get; init; } = [];
}

/// <summary>
/// Checks that every type and method signature in a DLL also appears in the source.
/// Proves structure only: it does not compare method bodies.
/// </summary>
public static class SourceComparer
{
    public const string MatchesSentence = "All types and method signatures in this DLL were found in the source.";
    public const string DifferencesSentence = "Some code in this DLL was not found in the source.";
    public const string NotComparableSentence = "The source contains no C# types, so nothing could be compared.";

    // Members the compiler synthesizes for records.
    private static readonly HashSet<string> RecordMembers = new(StringComparer.Ordinal)
    {
        "get_EqualityContract", "ToString", "PrintMembers", "GetHashCode", "Equals", "op_Equality", "op_Inequality", "Deconstruct",
    };

    public static ComparisonResult Compare(DllModel dll, SourceModel source, string sourceDescription)
    {
        if (source.Project is { } project)
        {
            sourceDescription += $", project {project}";
        }

        var findings = new List<Finding>();
        var generated = new List<string>();
        var implicitAccepted = new List<string>();
        var dllTypes = 0;
        var matchedTypes = 0;
        var dllMethods = 0;
        var matchedMethods = 0;
        var seenSourceTypes = new HashSet<string>(StringComparer.Ordinal);

        if (source.Types.Count == 0)
        {
            return Result(Verdict.NotComparable, NotComparableSentence);
        }

        foreach (var type in dll.Types)
        {
            if (type.GeneratedRule is not null)
            {
                generated.Add($"{type.Key} ({type.GeneratedRule}, {type.Methods.Count} methods)");
                continue;
            }

            var methods = type.Methods.Where(m => m.GeneratedRule is null).Select(m => m.Key[(m.Key.IndexOf("::", StringComparison.Ordinal) + 2)..]).ToList();
            // Code a source generator wrote for a pattern the hand-written type uses (checked before the toolchain list,
            // whose TypeHandle rule only allows method-less types).
            if (!source.Types.ContainsKey(type.Key) && GeneratedCodeRules.MatchSourceGenerated(type.Key, methods, source) is { } generatedBy)
            {
                generated.Add($"{type.Key} ({generatedBy}, {methods.Count} methods, all on the known list)");
                continue;
            }

            var toolchain = source.Types.ContainsKey(type.Key) ? null : GeneratedCodeRules.KnownToolchainTypes.FirstOrDefault(t => t.MatchesKey(type.Key));
            if (toolchain is not null)
            {
                var unexpected = methods.Where(m => !toolchain.AllowedMethods.Contains(m)).ToList();
                if (unexpected.Count == 0)
                {
                    generated.Add($"{type.Key} ({toolchain.Id}, {methods.Count} methods, all on the known list)");
                    continue;
                }

                dllTypes++;
                dllMethods += methods.Count;
                findings.Add(new Finding(Severity.High, "SourceMismatch",
                    $"Toolchain type {type.Key} has methods that the toolchain does not generate: {string.Join(", ", unexpected.Take(10))}", dll.FileName));
                continue;
            }

            dllTypes++;
            if (!source.Types.TryGetValue(type.Key, out var sourceType))
            {
                dllMethods += methods.Count;
                var listed = methods.Count == 0 ? string.Empty : ": " + string.Join(", ", methods.Take(10)) + (methods.Count > 10 ? ", ..." : string.Empty);
                findings.Add(new Finding(Severity.High, "SourceMismatch",
                    $"Type {type.Key} is in the DLL but not in the source ({methods.Count} methods{listed})", dll.FileName));
                continue;
            }

            matchedTypes++;
            seenSourceTypes.Add(type.Key);

            foreach (var method in type.Methods)
            {
                if (method.GeneratedRule is not null)
                {
                    generated.Add($"{method.Key} ({method.GeneratedRule})");
                    continue;
                }

                dllMethods++;
                if (sourceType.MethodKeys.Contains(method.Key))
                {
                    matchedMethods++;
                    continue;
                }

                var reason = ImplicitReason(type, sourceType, method);
                if (reason is not null)
                {
                    matchedMethods++;
                    implicitAccepted.Add($"{method.Key} ({reason})");
                    continue;
                }

                var sameCount = sourceType.MethodsByName.TryGetValue(method.Name, out var candidates)
                    ? candidates.Where(c => c.Parameters.Count == method.Parameters.Count).ToList()
                    : [];

                if (sameCount.Any(c => c.Parameters.Zip(method.Parameters).All(p => TypeNames.ParameterMatches(p.First, p.Second))))
                {
                    matchedMethods++;
                    continue;
                }

                if (sameCount.Count > 0)
                {
                    var similar = sameCount.Take(3).Select(c => c.Key);
                    findings.Add(new Finding(Severity.Medium, "SignatureDiffers",
                        $"Method {method.Key} differs from the source ({string.Join("; ", similar)})", dll.FileName));
                }
                else
                {
                    findings.Add(new Finding(Severity.High, "SourceMismatch",
                        $"Method {method.Key} is in the DLL but not in the source", dll.FileName));
                }
            }
        }

        // Source without a DLL counterpart is normal (#if branches, other projects in the repo): one summary line.
        var notInDll = source.Types.Keys.Where(k => !seenSourceTypes.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (notInDll.Count > 0)
        {
            findings.Add(new Finding(Severity.Info, "NotInDll",
                $"{notInDll.Count} source types are not in this DLL (normal for other projects or #if code), e.g. {string.Join(", ", notInDll.Take(10))}",
                null));
        }

        // A risky call the method's published source does not contain is code nobody can read: the strongest signal.
        var traces = CallTracer.Trace(dll, source);
        foreach (var trace in traces.Where(t => !t.InSource))
        {
            findings.Add(new Finding(Severity.High, "CallNotInSource",
                $"{trace.Api} is called in {trace.Method}, but {trace.Reason}", dll.FileName));
        }

        var differs = findings.Any(f => f.Severity >= Severity.Medium);
        return Result(differs ? Verdict.Differences : Verdict.Matches, differs ? DifferencesSentence : MatchesSentence) with { CallTraces = traces };

        ComparisonResult Result(Verdict verdict, string summary) => new(
            dll.FileName,
            dll.AssemblyName,
            dll.Version,
            dll.InformationalVersion,
            sourceDescription,
            verdict,
            summary,
            dllTypes,
            matchedTypes,
            dllMethods,
            matchedMethods,
            implicitAccepted.Count,
            implicitAccepted,
            generated,
            source.Warnings,
            [.. findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Message, StringComparer.Ordinal)]);
    }

    /// <summary>Why a DLL method without a source declaration is still implied by the source, or null.</summary>
    private static string? ImplicitReason(DllType type, SourceType source, DllMethod method)
    {
        if (type.IsDelegate || source.Kind == SourceTypeKind.Delegate)
        {
            return "delegate member";
        }

        if (method.Name == ".ctor" && method.Parameters.Count == 0 && !source.HasInstanceConstructor && !source.HasPrimaryConstructor && !source.IsStatic)
        {
            return "default constructor";
        }

        if (method.Name == ".cctor" && source.HasStaticInitialization)
        {
            return "static field initializers";
        }

        var isRecord = type.IsRecordLike || source.Kind is SourceTypeKind.Record or SourceTypeKind.RecordStruct;
        if (isRecord)
        {
            if (RecordMembers.Contains(method.Name))
            {
                return "record member";
            }

            // Copy constructor: .ctor(<RecordName>).
            var simpleName = TypeNames.StripArity(type.Key[(Math.Max(type.Key.LastIndexOf('.'), type.Key.LastIndexOf('+')) + 1)..]);
            if (method.Key.EndsWith($"::.ctor({simpleName})", StringComparison.Ordinal))
            {
                return "record copy constructor";
            }
        }

        return null;
    }
}
