using System.Text.RegularExpressions;

namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Names that the C# compiler or the CS2 toolchain generate, which therefore never appear in source.
/// Hand-written IL can use the same names, so everything these rules skip is listed in the report.
/// </summary>
public static class GeneratedCodeRules
{
    public sealed record Rule(string Id, string Description, Func<string, bool> Matches);

    public static readonly IReadOnlyList<Rule> Rules =
    [
        new("compiler", "C# compiler names with '<' or '>' (lambdas, closures, async and iterator state machines, local functions, anonymous types)",
            name => name.Contains('<') || name.Contains('>')),
        new("unity-codegen", "Unity Entities code generation (__TypeHandle, __AssignQueries, OnCreateForCompiler, __codegen__)",
            name => name.StartsWith("__", StringComparison.Ordinal)
                || name == "OnCreateForCompiler"
                || name.Contains("__codegen__", StringComparison.Ordinal)),
        new("burst", "Burst compiler names with '$' ($BurstDirectCall, $BurstManaged)",
            name => name.Contains('$')),
    ];

    /// <summary>A whole type the CS2 toolchain injects into a mod DLL, with the exact methods it may contain.</summary>
    public sealed record ToolchainType(string Id, string Description, Func<string, bool> MatchesKey, IReadOnlySet<string> AllowedMethods);

    /// <summary>
    /// Types the toolchain adds that never appear in source. Only used when the source does not declare the type,
    /// and a type is only accepted when every one of its methods is on the list; anything extra is reported.
    /// Seen in Bottlenecks, Traffic, ParkingControl and BetterBulldozer (2026-10-19).
    /// </summary>
    public static readonly IReadOnlyList<ToolchainType> KnownToolchainTypes =
    [
        new("unity-type-registry", "Unity Entities type registry",
            key => key == "Unity.Entities.CodeGeneratedRegistry.AssemblyTypeRegistry",
            new HashSet<string>(StringComparer.Ordinal)
            {
                ".cctor()",
                "Equals(Object,Object,Int32)",
                "Equals(Object,Void*,Int32)",
                "BoxedGetHashCode(Object,Int32)",
                "ConstructComponentFromBuffer(Void*,Int32)",
                "GetSystemAttributes(Type)",
                "SetSharedStaticTypeIndices(Int32*,Int32)",
            }),
        new("unity-type-registry-data", "Unity Entities type registry constant data (no methods)",
            key => key.StartsWith("Unity.Entities.CodeGeneratedRegistry.AssemblyTypeRegistry+ConstantData", StringComparison.Ordinal),
            new HashSet<string>()),
        new("unity-type-handle", "Unity Entities component type handles of a system (no methods)",
            key => key.EndsWith("+TypeHandle", StringComparison.Ordinal),
            new HashSet<string>()),
    ];

    /// <summary>
    /// Code that a source generator adds for a pattern written in a hand-written type, with the exact methods it generates.
    /// <paramref name="KeyPattern"/> captures the hand-written type as "owner"; the owner's source must use every identifier in
    /// <paramref name="RequiredIdentifiers"/> (in one method body), so the generated code is only accepted where its pattern is used.
    /// </summary>
    public sealed record SourceGeneratedType(string Id, string Description, Regex KeyPattern, IReadOnlySet<string> AllowedMethods, string[] RequiredIdentifiers);

    /// <summary>
    /// Unity Entities generates a struct "IFE_&lt;hash&gt;_&lt;n&gt;" (idiomatic foreach), with nested Enumerator, ResolvedChunk and
    /// TypeHandle types, for each <c>foreach (... in SystemAPI.Query&lt;...&gt;())</c> loop in a system. Seen 2026-10-20.
    /// </summary>
    public static readonly IReadOnlyList<SourceGeneratedType> SourceGeneratedTypes =
    [
        new("unity-idiomatic-foreach", "Unity Entities code for a SystemAPI.Query foreach loop",
            new Regex(@"^(?<owner>.+?)\+IFE_\d+_\d+$", RegexOptions.Compiled),
            new HashSet<string>(StringComparer.Ordinal) { "Query(EntityQuery,TypeHandle,SystemState&)", "CompleteDependencies(SystemState&)" },
            ["SystemAPI", "Query"]),
        new("unity-idiomatic-foreach", "Unity Entities enumerator for a SystemAPI.Query foreach loop",
            new Regex(@"^(?<owner>.+?)\+IFE_\d+_\d+\+Enumerator$", RegexOptions.Compiled),
            new HashSet<string>(StringComparer.Ordinal)
            {
                ".ctor(EntityQuery,TypeHandle,SystemState&)", "Dispose()", "MoveNext()", "get_Current()", "GetEnumerator()", "Reset()",
            },
            ["SystemAPI", "Query"]),
        new("unity-idiomatic-foreach", "Unity Entities chunk access for a SystemAPI.Query foreach loop",
            new Regex(@"^(?<owner>.+?)\+IFE_\d+_\d+\+ResolvedChunk$", RegexOptions.Compiled),
            new HashSet<string>(StringComparer.Ordinal) { "Get(Int32)" },
            ["SystemAPI", "Query"]),
        new("unity-idiomatic-foreach", "Unity Entities type handles for a SystemAPI.Query foreach loop",
            new Regex(@"^(?<owner>.+?)\+IFE_\d+_\d+\+TypeHandle$", RegexOptions.Compiled),
            new HashSet<string>(StringComparer.Ordinal) { ".ctor(SystemState&)", "Update(SystemState&)", "Resolve(ArchetypeChunk)" },
            ["SystemAPI", "Query"]),
    ];

    /// <summary>
    /// The rule id when a DLL type is source-generated code for a pattern its hand-written owner uses: the name matches, every
    /// method is on the list, and the owner exists in the source with a method body that uses the pattern. Otherwise null.
    /// </summary>
    public static string? MatchSourceGenerated(string typeKey, IReadOnlyCollection<string> methods, SourceModel source)
    {
        foreach (var rule in SourceGeneratedTypes)
        {
            var match = rule.KeyPattern.Match(typeKey);
            if (!match.Success || !methods.All(rule.AllowedMethods.Contains))
            {
                continue;
            }

            if (source.Types.TryGetValue(match.Groups["owner"].Value, out var owner)
                && owner.Bodies.Values.SelectMany(b => b).Any(body => rule.RequiredIdentifiers.All(body.Identifiers.ContainsKey)))
            {
                return rule.Id;
            }
        }

        return null;
    }

    /// <summary>Returns the id of the first matching rule, or null when the name looks hand-written.</summary>
    public static string? Match(string name)
    {
        foreach (var rule in Rules)
        {
            if (rule.Matches(name))
            {
                return rule.Id;
            }
        }

        return null;
    }
}
