using CS2ModVerifier.Core;
using CS2ModVerifier.SourceCompare;

namespace CS2ModVerifier.App;

/// <summary>
/// The tray icon and everything behind it: the first full check, the folder watcher, a periodic rescan,
/// notifications, and the main window on demand.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(30);

    private readonly NotifyIcon tray;
    private readonly CheckService checks;
    private readonly ModWatcher watcher;
    private readonly System.Windows.Forms.Timer rescanTimer;
    // Marshals work to the UI thread. SynchronizationContext.Current was not reliable here: without a window
    // created first (no disclaimer, e.g. autostart) it posted to the thread pool, and updating the list from
    // there crashed the app.
    private readonly Control ui = new();
    private readonly IgnoreStore ignores = IgnoreStore.Load();
    private readonly System.Windows.Forms.Timer retryTimer = new();
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = (int)TimeSpan.FromMinutes(1).TotalMilliseconds };
    private readonly ToolStripMenuItem updateItem = new() { Visible = false, Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
    private readonly Inbox inbox = Inbox.Load();
    private readonly ChangeTracker changes = ChangeTracker.Open(ChangeTracker.DefaultPath());
    private readonly List<(string Text, string ModPath, bool Warning)> pendingChanges = [];
    private readonly List<ToolStripItem> unreadMenuItems = [];
    private readonly ContextMenuStrip menu = new();
    private MainForm? window;
    private bool baselineRun;
    private bool busy;
    private DateTimeOffset? retryAt;
    private string? updateUrl;
    private Action? balloonClick;

    public TrayContext(bool openWindow)
    {
        ui.CreateControl(); // creates the window handle on this (the UI) thread, so BeginInvoke runs here
        KnownLibraries.UseLocalFile(KnownLibraries.DefaultLocalPath());
        checks = new CheckService(ModLocator.DefaultGameDataRoot());

        // Nothing recorded yet: the first check sets the baseline and ends with one summary.
        baselineRun = changes.IsEmpty;

        updateItem.Click += (_, _) => OpenUpdatePage();
        menu.Opening += (_, _) => RebuildUnreadMenu();
        menu.Items.Add(updateItem);
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Check all mods now", null, (_, _) => _ = checks.CheckAllAsync());
        menu.Items.Add("About and disclaimer", null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = Autostart.IsEnabled() };
        startWithWindows.CheckedChanged += (_, _) =>
        {
            Autostart.Set(startWithWindows.Checked);
            Log.Write($"Start with Windows: {(startWithWindows.Checked ? "on" : "off")}");
        };
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "CS2 Mod Verifier",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowWindow();
        tray.BalloonTipClicked += (_, _) => (balloonClick ?? ShowWindow)();
        inbox.Changed += UpdateTrayState;
        UpdateTrayState();

        checks.Checked += (_, result) => Post(() => OnChecked(result));
        checks.Removed += path =>
        {
            Log.Write($"Removed (folder gone): {path}");
            Post(() =>
            {
                inbox.MarkRead(path);
                window?.RemoveRow(path);
            });
        };
        checks.PendingChanged += () => Post(() => window?.RefreshPending());
        checks.BusyChanged += busy => Post(() =>
        {
            this.busy = busy;
            UpdateTrayState();
            window?.SetBusy(busy);
            if (!busy)
            {
                FlushChanges();
            }
        });

        watcher = new ModWatcher(checks.GameDataRoot);
        watcher.ModChanged += path =>
        {
            Log.Write($"Folder changed and settled: {path}");
            _ = checks.CheckFolderAsync(path);
        };
        watcher.RescanNeeded += () =>
        {
            Log.Write("Watcher lost events; checking everything again.");
            _ = checks.CheckAllAsync();
        };

        rescanTimer = new System.Windows.Forms.Timer { Interval = (int)RescanInterval.TotalMilliseconds };
        rescanTimer.Tick += (_, _) => _ = checks.CheckAllAsync();
        rescanTimer.Start();

        // Comparisons that waited for GitHub's hourly limit continue on their own once it resets.
        retryTimer.Tick += (_, _) =>
        {
            retryTimer.Stop();
            retryAt = null;
            Log.Write("GitHub's limit has reset; continuing the comparisons.");
            _ = checks.CheckAllAsync();
        };

        // A newer release of this tool: first check a minute after start, then once a day.
        updateTimer.Tick += (_, _) =>
        {
            updateTimer.Interval = (int)TimeSpan.FromDays(1).TotalMilliseconds;
            _ = CheckForUpdateAsync();
        };
        updateTimer.Start();

        Autostart.KeepPathCurrent();
        _ = InitialCheckAsync();
        if (openWindow)
        {
            ShowWindow();
        }
    }

    protected override void ExitThreadCore()
    {
        rescanTimer.Dispose();
        retryTimer.Dispose();
        updateTimer.Dispose();
        watcher.Dispose();
        tray.Visible = false;
        tray.Dispose();
        window?.Dispose();
        ui.Dispose();
        base.ExitThreadCore();
    }

    private async Task InitialCheckAsync()
    {
        await checks.CheckAllAsync();

        var code = checks.Results.Where(r => r.Scan.HasCode).ToList();
        var matches = code.Count(r => r.SourceStatus == SourceStatus.Matches);
        var differs = code.Count(r => r.SourceStatus == SourceStatus.Differences);
        var openHigh = code.Count(r => ignores.Summarize(r).Highest == Core.Severity.High);
        var waiting = code.Count(r => r.SourceStatus == SourceStatus.RateLimited);
        var summary = $"Checked {code.Count} code mods: {matches} match their GitHub source, {differs} differ from it. {openHigh} have open high findings.";
        if (waiting > 0 && retryAt is { } at)
        {
            summary += $" {waiting} wait for GitHub's hourly limit and are compared after {at.ToLocalTime():HH:mm}.";
        }

        Log.Write("Initial check done. " + summary);

        // Only on the very first run: later starts stay quiet unless something changed.
        if (baselineRun)
        {
            baselineRun = false;
            pendingChanges.Clear();
            Notify(openHigh > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info, summary + " " + Disclaimer.Short);
        }
    }

    private void OnChecked(ModCheckResult result)
    {
        Log.Write($"Checked {result.Scan.Mod.Path}: source {result.SourceStatus?.ToString() ?? "-"}, files {result.Scan.Manifest.Status}, findings {result.Scan.Findings.Count}"
            + string.Concat(result.Sources.Where(s => s.Status == SourceStatus.Error).Select(s => $" | {s.Message}")));
        window?.Upsert(result);

        if (result.Sources.Select(s => s.RetryAfter).Where(r => r is not null).Min() is { } reset)
        {
            ScheduleRetry(reset);
        }

        if (!result.Scan.HasCode)
        {
            return;
        }

        // Compare with what was recorded before; only a real change becomes a notification.
        var mod = result.Scan.Mod;
        var change = changes.Update(
            $"{mod.Source}|{mod.Id}",
            Path.GetFileName(mod.Path),
            ChangeTracker.Fingerprint(result.Scan.Files),
            IgnoreStore.ImportantFindings(result).Select(ChangeTracker.FindingKey),
            comparisonIncomplete: result.Sources.Any(s => s.Status is SourceStatus.RateLimited or SourceStatus.Error));
        if (!baselineRun && Texts.Change(change, result, ignores) is { } message)
        {
            pendingChanges.Add((message, mod.Path, ignores.Summarize(result).Highest == Core.Severity.High));
        }
    }

    /// <summary>After a check: one notification for everything that changed in it, and the changes kept as unread in the tray.</summary>
    private void FlushChanges()
    {
        changes.Save();
        if (baselineRun || pendingChanges.Count == 0)
        {
            return;
        }

        foreach (var (text, modPath, _) in pendingChanges)
        {
            Log.Write($"Change: {text}");
            inbox.Add(text, modPath);
        }

        var icon = pendingChanges.Any(c => c.Warning) ? ToolTipIcon.Warning : ToolTipIcon.Info;
        if (pendingChanges.Count == 1)
        {
            var (text, modPath, _) = pendingChanges[0];
            Notify(icon, text, () => OpenMod(modPath));
        }
        else
        {
            var names = string.Join(", ", checks.Results
                .Where(r => pendingChanges.Any(c => string.Equals(c.ModPath, r.Scan.Mod.Path, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.DisplayName)
                .Take(5));
            var more = pendingChanges.Count > 5 ? $" and {pendingChanges.Count - 5} more" : string.Empty;
            Notify(icon, $"{pendingChanges.Count} mods changed: {names}{more}. Click to see them.", ShowWindow);
        }

        pendingChanges.Clear();
    }

    /// <summary>The dot on the icon and the tooltip show whether anything is unread.</summary>
    private void UpdateTrayState()
    {
        var unread = inbox.Items.Count;
        tray.Icon = unread > 0 ? TrayIcons.Unread : TrayIcons.Normal;
        tray.Text = busy
            ? "CS2 Mod Verifier: checking mods..."
            : unread > 0 ? $"CS2 Mod Verifier: {unread} new" : "CS2 Mod Verifier";
    }

    /// <summary>Puts the unread notifications at the top of the tray menu.</summary>
    private void RebuildUnreadMenu()
    {
        foreach (var item in unreadMenuItems)
        {
            menu.Items.Remove(item);
            item.Dispose();
        }

        unreadMenuItems.Clear();
        if (inbox.Items.Count == 0)
        {
            return;
        }

        const int MaxShown = 8;
        var index = 0;
        foreach (var entry in inbox.Items.Take(MaxShown))
        {
            var text = entry.Text.Length > 80 ? entry.Text[..80] + "..." : entry.Text;
            var item = new ToolStripMenuItem(text.Replace("&", "&&", StringComparison.Ordinal)) { ToolTipText = entry.Text };
            item.Font = new Font(item.Font, FontStyle.Bold);
            item.Click += (_, _) => OpenInboxItem(entry);
            menu.Items.Insert(index++, item);
            unreadMenuItems.Add(item);
        }

        var more = inbox.Items.Count > MaxShown ? $" ({inbox.Items.Count})" : string.Empty;
        var markAll = new ToolStripMenuItem("Mark all as read" + more);
        markAll.Click += (_, _) => inbox.MarkAllRead();
        menu.Items.Insert(index++, markAll);
        unreadMenuItems.Add(markAll);

        var separator = new ToolStripSeparator();
        menu.Items.Insert(index, separator);
        unreadMenuItems.Add(separator);
    }

    private void OpenInboxItem(InboxItem item)
    {
        inbox.MarkRead(item);
        if (item.Url is { } url)
        {
            OpenReleasePage(url);
        }
        else if (item.ModPath is { } path)
        {
            OpenMod(path);
        }
        else
        {
            ShowWindow();
        }
    }

    /// <summary>Opens the window with this mod selected (selecting it marks it as read).</summary>
    private void OpenMod(string modPath)
    {
        ShowWindow();
        window?.SelectMod(modPath);
    }

    /// <summary>Checks everything again 30 seconds after GitHub's limit resets (cached results make that cheap).</summary>
    private void ScheduleRetry(DateTimeOffset reset)
    {
        var at = reset.AddSeconds(30);
        if (retryAt is { } planned && planned <= at)
        {
            return;
        }

        retryAt = at;
        var wait = at - DateTimeOffset.UtcNow;
        retryTimer.Stop();
        retryTimer.Interval = (int)Math.Clamp(wait.TotalMilliseconds, 1_000, int.MaxValue);
        retryTimer.Start();
        Log.Write($"Waiting for GitHub's hourly limit; continuing at {at.ToLocalTime():HH:mm:ss}.");
    }

    /// <summary>Asks GitHub for this tool's latest release; on a newer one, shows the menu item and notifies once per version.</summary>
    private async Task CheckForUpdateAsync()
    {
        var current = typeof(TrayContext).Assembly.GetName().Version ?? new Version(0, 0, 0);
        UpdateCheck.NewRelease? newer;
        try
        {
            newer = await UpdateCheck.FindNewerAsync(current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException
            or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Log.Write($"Update check skipped: {e.Message}");
            return;
        }

        if (newer is null)
        {
            return;
        }

        updateUrl = newer.Url;
        updateItem.Text = $"Update available: {newer.Tag} (open download page)";
        updateItem.Visible = true;

        var settings = AppSettings.Load();
        if (settings.NotifiedUpdate != newer.Tag)
        {
            Log.Write($"Newer release available: {newer.Tag} (running {current.ToString(3)}).");
            var text = $"CS2 Mod Verifier {newer.Tag} is available (you have {current.ToString(3)}). Click to open the download page.";
            inbox.Add(text, url: newer.Url);
            Notify(ToolTipIcon.Info, text, OpenUpdatePage);
            (settings with { NotifiedUpdate = newer.Tag }).Save();
        }
    }

    private void OpenUpdatePage()
    {
        if (updateUrl is { } url)
        {
            OpenReleasePage(url);
        }
    }

    /// <summary>Opens a release page of this tool in the browser. Nothing is downloaded or installed by the app.</summary>
    private void OpenReleasePage(string url)
    {
        if (UpdateCheck.IsReleasePage(url))
        {
            inbox.MarkAllUpdatesRead();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void Notify(ToolTipIcon icon, string text, Action? onClick = null)
    {
        balloonClick = onClick;
        tray.ShowBalloonTip(10_000, "CS2 Mod Verifier", text, icon);
    }

    private void ShowWindow()
    {
        if (window is null || window.IsDisposed)
        {
            window = new MainForm(checks, ignores, inbox);
            window.FormClosed += (_, _) => window = null;
        }

        window.Show();
        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }

        window.Activate();
    }

    private static void ShowAbout()
    {
        using var about = new DisclaimerForm(firstRun: false);
        about.ShowDialog();
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread; dropped once the app is closing.</summary>
    private void Post(Action action)
    {
        try
        {
            if (!ui.IsDisposed)
            {
                ui.BeginInvoke(action);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // The app is shutting down; there is no window left to update.
        }
    }
}
