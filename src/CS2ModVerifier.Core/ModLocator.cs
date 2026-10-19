using System.Text.RegularExpressions;

namespace CS2ModVerifier.Core;

public static partial class ModLocator
{
    public static string DefaultGameDataRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "AppData", "LocalLow", "Colossal Order", "Cities Skylines II");
    }

    public static IReadOnlyList<ModFolder> FindMods(string gameDataRoot)
    {
        var result = new List<ModFolder>();
        if (string.IsNullOrWhiteSpace(gameDataRoot) || !Directory.Exists(gameDataRoot))
        {
            return result;
        }

        var pdxRoot = Path.Combine(gameDataRoot, ".cache", "Mods", "pdx_mods");
        if (Directory.Exists(pdxRoot))
        {
            foreach (var dir in SafeDirectories(pdxRoot))
            {
                var name = Path.GetFileName(dir);
                var match = PdxFolderRegex().Match(name);
                if (match.Success)
                {
                    result.Add(new ModFolder(ModSource.Paradox, match.Groups[1].Value, match.Groups[2].Value, dir));
                }
            }
        }

        var localRoot = Path.Combine(gameDataRoot, "Mods");
        if (Directory.Exists(localRoot))
        {
            foreach (var dir in SafeDirectories(localRoot))
            {
                result.Add(new ModFolder(ModSource.Local, Path.GetFileName(dir), null, dir));
            }
        }

        return result
            .OrderBy(m => m.Source)
            .ThenBy(m => ParseId(m.Id))
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static long ParseId(string id) => long.TryParse(id, out var n) ? n : long.MaxValue;

    [GeneratedRegex(@"^(\d+)_(\d+)$")]
    private static partial Regex PdxFolderRegex();
}
