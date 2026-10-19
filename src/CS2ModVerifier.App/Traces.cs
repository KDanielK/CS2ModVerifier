using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.App;

/// <summary>Connects a scan finding (e.g. "Process" in ParkingControl.dll) to the traced calls behind it.</summary>
internal static class Traces
{
    public sealed record Located(CallTrace Trace, string? Url);

    /// <summary>The traced calls for this finding's kind of risk in this DLL, with a GitHub link to each line found.</summary>
    public static IReadOnlyList<Located> For(ModCheckResult result, Finding finding)
    {
        var located = new List<Located>();
        foreach (var source in result.Sources.Where(s => string.Equals(s.DllFile, finding.File, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var trace in source.Comparison?.CallTraces ?? [])
            {
                if (trace.Category == finding.Category || (finding.Category == "CallNotInSource" && !trace.InSource))
                {
                    located.Add(new Located(trace, Url(source, trace)));
                }
            }
        }

        return located;
    }

    /// <summary>All traced calls of the mod, for the details pane.</summary>
    public static IReadOnlyList<Located> All(ModCheckResult result) =>
        [.. result.Sources.SelectMany(s => (s.Comparison?.CallTraces ?? []).Select(t => new Located(t, Url(s, t))))];

    /// <summary>null: no traces; true: every traced call is in the source; false: at least one is not.</summary>
    public static bool? AllInSource(IReadOnlyList<Located> traces) => traces.Count == 0 ? null : traces.All(t => t.Trace.InSource);

    public static string Describe(Located located) => located.Trace.InSource
        ? $"{located.Trace.Api} in {ShortMethod(located.Trace.Method)}: in the published source at {located.Trace.File}, line {located.Trace.Line}"
        : $"{located.Trace.Api} in {ShortMethod(located.Trace.Method)}: NOT in the published source ({located.Trace.Reason})";

    private static string ShortMethod(string method)
    {
        var separator = method.IndexOf("::", StringComparison.Ordinal);
        var type = method[..separator];
        return type[(type.LastIndexOf('.') + 1)..] + "." + method[(separator + 2)..];
    }

    /// <summary>A link to the exact line on GitHub, at the compared commit. Only github.com links are ever built.</summary>
    private static string? Url(SourceVerification source, CallTrace trace) =>
        source.Repository is { } repo && source.ComparedCommit is { } commit && trace.File is { } file && trace.Line is { } line
            ? $"https://github.com/{repo}/blob/{commit}/{string.Join('/', file.Split('/').Select(Uri.EscapeDataString))}#L{line}"
            : null;
}
