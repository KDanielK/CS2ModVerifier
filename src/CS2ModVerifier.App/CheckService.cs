using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.App;

/// <summary>A code mod waiting to be checked, or being checked right now.</summary>
internal sealed record PendingMod(ModFolder Mod, string DisplayName, string Version, bool Checking);

/// <summary>Runs mod checks in the background, one at a time, and keeps the latest result per mod folder.</summary>
internal sealed class CheckService(string gameDataRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly VerificationStore store = VerificationStore.Open(VerificationStore.DefaultPath());
    private readonly Dictionary<string, ModCheckResult> results = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingMod> pending = new(StringComparer.OrdinalIgnoreCase);

    public string GameDataRoot { get; } = gameDataRoot;

    /// <summary>Raised on any thread when mods are queued, start being checked, or finish.</summary>
    public event Action? PendingChanged;

    public IReadOnlyList<PendingMod> Pending
    {
        get
        {
            lock (pending)
            {
                return [.. pending.Values];
            }
        }
    }

    /// <summary>Raised on a background thread after a mod was checked (previous result, new result).</summary>
    public event Action<ModCheckResult?, ModCheckResult>? Checked;

    /// <summary>Raised on a background thread when a mod folder disappeared (uninstalled or replaced by a new version).</summary>
    public event Action<string>? Removed;

    public event Action<bool>? BusyChanged;

    /// <summary>When the last check (of all mods or one) finished.</summary>
    public DateTimeOffset? LastCheckFinished { get; private set; }

    public IReadOnlyList<ModCheckResult> Results
    {
        get
        {
            lock (results)
            {
                return [.. results.Values];
            }
        }
    }

    public Task CheckAllAsync()
    {
        // Asset mods are skipped entirely (performance). Queue right away, so the window can list every code mod before it is checked.
        var pages = LoadPages();
        var mods = CodeMods.Only(ModLocator.FindMods(GameDataRoot), pages);
        Enqueue(mods, pages);

        return RunAsync(async () =>
        {
            var present = mods.Select(m => m.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in Results.Select(r => r.Scan.Mod.Path).Where(p => !present.Contains(p)).ToList())
            {
                Forget(gone);
            }

            foreach (var mod in mods)
            {
                await CheckOneAsync(mod, pages);
            }
        });
    }

    /// <summary>Checks the mod in this folder again, or forgets it when the folder is gone.</summary>
    public Task CheckFolderAsync(string modPath)
    {
        var pages = LoadPages();
        var mod = CodeMods.Only(ModLocator.FindMods(GameDataRoot), pages)
            .FirstOrDefault(m => string.Equals(m.Path, modPath, StringComparison.OrdinalIgnoreCase));
        if (mod is not null)
        {
            Enqueue([mod], pages);
        }

        return RunAsync(async () =>
        {
            // Gone, or an asset mod (skipped): drop it from the list.
            if (mod is null)
            {
                Forget(modPath);
                return;
            }

            await CheckOneAsync(mod, pages);
        });
    }

    private void Enqueue(IEnumerable<ModFolder> mods, IReadOnlyDictionary<string, PdxModInfo> pages)
    {
        lock (pending)
        {
            foreach (var mod in mods)
            {
                pages.TryGetValue(PdxModsCache.Key(mod.Id, mod.Version ?? string.Empty), out var page);
                pending.TryAdd(mod.Path, new PendingMod(mod, page?.DisplayName ?? Path.GetFileName(mod.Path),
                    page?.UserModVersion ?? (mod.Version is { } v ? $"v{v}" : string.Empty), Checking: false));
            }
        }

        PendingChanged?.Invoke();
    }

    private void SetPending(string modPath, bool? checking)
    {
        lock (pending)
        {
            if (checking is null)
            {
                pending.Remove(modPath);
            }
            else if (pending.TryGetValue(modPath, out var item))
            {
                pending[modPath] = item with { Checking = checking.Value };
            }
        }

        PendingChanged?.Invoke();
    }

    private async Task CheckOneAsync(ModFolder mod, IReadOnlyDictionary<string, PdxModInfo> pages)
    {
        SetPending(mod.Path, checking: true);
        ModCheckResult result;
        try
        {
            result = await ModChecker.CheckAsync(mod, pages, store, compareSource: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetPending(mod.Path, checking: null);
            return; // The game is probably still writing the folder; the watcher checks it again when it settles.
        }

        ModCheckResult? previous;
        lock (results)
        {
            // An update arrives as a new "<id>_<version>" folder: compare with the previous version of the same mod.
            if (!results.TryGetValue(mod.Path, out previous))
            {
                previous = results.Values.FirstOrDefault(r => r.Scan.Mod.Source == mod.Source && r.Scan.Mod.Id == mod.Id);
            }

            results[mod.Path] = result;
        }

        Checked?.Invoke(previous, result);
        SetPending(mod.Path, checking: null);
    }

    private void Forget(string modPath)
    {
        bool removed;
        lock (results)
        {
            removed = results.Remove(modPath);
        }

        SetPending(modPath, checking: null);

        if (removed)
        {
            Removed?.Invoke(modPath);
        }
    }

    private IReadOnlyDictionary<string, PdxModInfo> LoadPages() => PdxModsCache.Load(PdxModsCache.DefaultPath(GameDataRoot));

    private async Task RunAsync(Func<Task> work)
    {
        await gate.WaitAsync();
        BusyChanged?.Invoke(true);
        try
        {
            await Task.Run(work);
        }
        finally
        {
            LastCheckFinished = DateTimeOffset.Now;
            BusyChanged?.Invoke(false);
            gate.Release();
        }
    }
}
