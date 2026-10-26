using System.Text.RegularExpressions;

namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Which C# files of a repository belong to the project that builds a given DLL, the way the compiler sees it:
/// files in the project's folder (SDK default), minus subfolders that are projects of their own and anything
/// removed with &lt;Compile Remove&gt;, plus files added with &lt;Compile Include&gt; or a shared project (.projitems).
/// Test projects with stand-in classes of the same name are therefore left out.
/// </summary>
public sealed partial class ProjectScope
{
    private readonly string directory;
    private readonly string[] otherProjectDirectories;
    private readonly Regex[] removed;
    private readonly Regex[] added;

    private ProjectScope(string projectPath, string directory, string[] otherProjectDirectories, Regex[] removed, Regex[] added)
    {
        ProjectPath = projectPath;
        this.directory = directory;
        this.otherProjectDirectories = otherProjectDirectories;
        this.removed = removed;
        this.added = added;
    }

    /// <summary>The project file, relative to the repository ("ParkingLot.csproj", "Code/Mod/Mod.csproj").</summary>
    public string ProjectPath { get; }

    [GeneratedRegex(@"<AssemblyName>\s*([^<$\s]+)\s*</AssemblyName>", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyNamePattern();

    [GeneratedRegex(@"<Compile\b[^>]*?\bRemove\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CompileRemovePattern();

    [GeneratedRegex(@"<Compile\b[^>]*?\bInclude\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CompileIncludePattern();

    [GeneratedRegex(@"<Import\b[^>]*?\bProject\s*=\s*""([^""]+\.projitems)""", RegexOptions.IgnoreCase)]
    private static partial Regex SharedProjectPattern();

    /// <summary>
    /// The project whose assembly name (or, without one, file name) is <paramref name="assemblyName"/>; the shortest path
    /// wins when several match. Null when no project file builds that assembly.
    /// </summary>
    public static ProjectScope? Find(IReadOnlyList<(string Path, string Text)> projectFiles, string assemblyName)
    {
        var projects = projectFiles.Where(p => p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var match = projects
            .Where(p => string.Equals(AssemblyName(p), assemblyName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Path.Length)
            .Cast<(string Path, string Text)?>()
            .FirstOrDefault();
        if (match is not { } project)
        {
            return null;
        }

        var dir = DirectoryOf(project.Path);
        var others = projects
            .Select(p => DirectoryOf(p.Path))
            .Where(d => d != dir && IsUnder(d, dir))
            .Distinct()
            .ToArray();

        // Patterns are relative to the project folder; paths that leave the repository are skipped (reported elsewhere).
        var removed = Values(CompileRemovePattern(), project.Text).Select(v => Combine(dir, v)).OfType<string>().Select(Glob).ToArray();
        var included = Values(CompileIncludePattern(), project.Text).Select(v => Combine(dir, v)).OfType<string>();
        var shared = Values(SharedProjectPattern(), project.Text).Select(v => Combine(dir, v)).OfType<string>()
            .Select(projitems => (DirectoryOf(projitems) is { Length: > 0 } folder ? folder + "/" : string.Empty) + "**/*.cs");
        var added = included.Concat(shared).Select(Glob).ToArray();
        return new ProjectScope(project.Path, dir, others, removed, added);
    }

    /// <summary>Whether a repository file (relative path with '/') is compiled into the project.</summary>
    public bool Includes(string path)
    {
        if (added.Any(r => r.IsMatch(path)))
        {
            return true;
        }

        return IsUnder(DirectoryOf(path), directory)
            && !otherProjectDirectories.Any(other => IsUnder(DirectoryOf(path), other))
            && !removed.Any(r => r.IsMatch(path));
    }

    private static string AssemblyName((string Path, string Text) project) =>
        AssemblyNamePattern().Match(project.Text) is { Success: true } match
            ? match.Groups[1].Value
            : Path.GetFileNameWithoutExtension(project.Path);

    private static IEnumerable<string> Values(Regex pattern, string text) =>
        pattern.Matches(text)
            .SelectMany(m => m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(v => !v.Contains('$') && !v.Contains('@'));

    /// <summary>A repository-relative path pattern ("Tests/**/*.cs") as a regex over repository paths.</summary>
    private static Regex Glob(string pattern)
    {
        var regex = Regex.Escape(pattern)
            .Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new Regex("^" + regex + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Joins a relative path to a folder and resolves "..", or null when it leaves the repository.</summary>
    private static string? Combine(string directory, string relative)
    {
        var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
        foreach (var part in relative.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static bool IsUnder(string path, string directory) =>
        directory.Length == 0
        || string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
}
