namespace CS2ModVerifier.App;

/// <summary>
/// Watches the subscribed and local mod folders. Reports a mod folder once it has been quiet for a few
/// seconds (the game writes files over time), and asks for a full rescan when Windows drops events.
/// </summary>
internal sealed class ModWatcher : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);

    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Dictionary<string, System.Threading.Timer> pending = new(StringComparer.OrdinalIgnoreCase);

    public ModWatcher(string gameDataRoot)
    {
        Watch(Path.Combine(gameDataRoot, ".cache", "Mods", "pdx_mods"));
        Watch(Path.Combine(gameDataRoot, "Mods"));
    }

    /// <summary>A mod folder changed and has been quiet since. Raised on a timer thread.</summary>
    public event Action<string>? ModChanged;

    /// <summary>Events were lost (buffer overflow or watcher error): everything should be checked again.</summary>
    public event Action? RescanNeeded;

    public void Dispose()
    {
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }

        lock (pending)
        {
            foreach (var timer in pending.Values)
            {
                timer.Dispose();
            }

            pending.Clear();
        }
    }

    private void Watch(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) => OnChange(root, e.FullPath);
        watcher.Changed += (_, e) => OnChange(root, e.FullPath);
        watcher.Deleted += (_, e) => OnChange(root, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            OnChange(root, e.OldFullPath);
            OnChange(root, e.FullPath);
        };
        watcher.Error += (_, _) => RescanNeeded?.Invoke();
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
    }

    private void OnChange(string root, string path)
    {
        // The mod folder is the first segment below the watched root; skip the game's download area.
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (first.Length == 0 || first == ".." || first.StartsWith('.'))
        {
            return;
        }

        var modPath = Path.Combine(root, first);
        lock (pending)
        {
            if (pending.TryGetValue(modPath, out var timer))
            {
                timer.Change(Quiet, Timeout.InfiniteTimeSpan);
                return;
            }

            pending[modPath] = new System.Threading.Timer(_ => Settled(modPath), null, Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Settled(string modPath)
    {
        lock (pending)
        {
            if (pending.Remove(modPath, out var timer))
            {
                timer.Dispose();
            }
        }

        ModChanged?.Invoke(modPath);
    }
}
