namespace CS2ModVerifier.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One instance per user: a second start just exits.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\CS2ModVerifier", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();

        // Code after "await" must continue on the UI thread. Windows Forms only installs this context once a
        // window exists, which is not the case when the app starts without the disclaimer (e.g. autostart).
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.ThreadException += (_, e) => Log.Write($"Error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write($"Background error: {e.Exception}");
        Log.Write("Started.");

        var settings = AppSettings.Load();
        var firstRun = !settings.DisclaimerAccepted;
        if (firstRun)
        {
            using var disclaimer = new DisclaimerForm();
            if (disclaimer.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            settings = settings with { DisclaimerAccepted = true };
            settings.Save();
        }

        // On the first run the window opens right away, so the user sees the check happen.
        Application.Run(new TrayContext(openWindow: firstRun));
    }
}
