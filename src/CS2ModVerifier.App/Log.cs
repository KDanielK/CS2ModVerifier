namespace CS2ModVerifier.App;

/// <summary>A small local log in %LOCALAPPDATA%\CS2ModVerifier\app.log, for troubleshooting. Never sent anywhere.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "app.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }
}
