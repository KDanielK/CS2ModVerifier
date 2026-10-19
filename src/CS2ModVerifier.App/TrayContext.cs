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
    private readonly SynchronizationContext ui;
    private readonly IgnoreStore ignores = IgnoreStore.Load();
    private readonly System.Windows.Forms.Timer retryTimer = new();
    private MainForm? window;
    private bool initialCheckDone;
    private DateTimeOffset? retryAt;

    public TrayContext(bool openWindow)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        KnownLibraries.UseLocalFile(KnownLibraries.DefaultLocalPath());
        checks = new CheckService(ModLocator.DefaultGameDataRoot());

        var menu = new ContextMenuStrip();
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
        tray.BalloonTipClicked += (_, _) => ShowWindow();

        checks.Checked += (previous, result) => Post(() => OnChecked(previous, result));
        checks.Removed += path =>
        {
            Log.Write($"Removed (folder gone): {path}");
            Post(() => window?.RemoveRow(path));
        };
        checks.PendingChanged += () => Post(() => window?.RefreshPending());
        checks.BusyChanged += busy => Post(() =>
        {
            tray.Text = busy ? "CS2 Mod Verifier: checking mods..." : "CS2 Mod Verifier";
            window?.SetBusy(busy);
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
        watcher.Dispose();
        tray.Visible = false;
        tray.Dispose();
        window?.Dispose();
        base.ExitThreadCore();
    }

    private async Task InitialCheckAsync()
    {
        await checks.CheckAllAsync();
        initialCheckDone = true;

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
        Notify(openHigh > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info, summary + " " + Disclaimer.Short);
    }

    private void OnChecked(ModCheckResult? previous, ModCheckResult result)
    {
        Log.Write($"Checked {result.Scan.Mod.Path}: source {result.SourceStatus?.ToString() ?? "-"}, files {result.Scan.Manifest.Status}, findings {result.Scan.Findings.Count}"
            + string.Concat(result.Sources.Where(s => s.Status == SourceStatus.Error).Select(s => $" | {s.Message}")));
        window?.Upsert(result);

        if (result.Sources.Select(s => s.RetryAfter).Where(r => r is not null).Min() is { } reset)
        {
            ScheduleRetry(reset);
        }

        // The first full check ends with one summary instead of a notification per mod.
        if (!initialCheckDone || Texts.Change(previous, result, ignores) is not { } message)
        {
            return;
        }

        Notify(ignores.Summarize(result).Highest == Core.Severity.High ? ToolTipIcon.Warning : ToolTipIcon.Info, message);
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

    private void Notify(ToolTipIcon icon, string text) =>
        tray.ShowBalloonTip(10_000, "CS2 Mod Verifier", text, icon);

    private void ShowWindow()
    {
        if (window is null || window.IsDisposed)
        {
            window = new MainForm(checks, ignores);
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

    private void Post(Action action) => ui.Post(_ => action(), null);
}
