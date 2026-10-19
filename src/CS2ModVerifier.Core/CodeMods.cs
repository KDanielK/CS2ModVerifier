namespace CS2ModVerifier.Core;

/// <summary>
/// Which installed mods are checked. Asset mods (buildings, maps, props) are skipped entirely for performance:
/// they can be large, and the checks are about code. Deciding needs no file reads, only the mod's Paradox Mods
/// page (from the game's local cache) and a listing of its main folder.
/// </summary>
public static class CodeMods
{
    /// <summary>
    /// A code mod: its Paradox Mods page tags it "Code Mod", or its main folder contains a .dll file.
    /// Code hidden in an asset mod's subfolders or under another extension is not found (a documented limitation).
    /// </summary>
    public static bool IsCodeMod(ModFolder mod, IReadOnlyDictionary<string, PdxModInfo> pages)
    {
        if (pages.TryGetValue(PdxModsCache.Key(mod.Id, mod.Version ?? string.Empty), out var page) && page.IsCodeMod)
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(mod.Path, "*.dll").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The code mods among the given mods, in the same order.</summary>
    public static IReadOnlyList<ModFolder> Only(IEnumerable<ModFolder> mods, IReadOnlyDictionary<string, PdxModInfo> pages) =>
        [.. mods.Where(m => IsCodeMod(m, pages))];
}
