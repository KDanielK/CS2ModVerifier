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
