using System.Text.Json;

namespace CS2ModVerifier.App;

/// <summary>Per-user settings in %LOCALAPPDATA%\CS2ModVerifier\settings.json.</summary>
/// <param name="NotifiedUpdate">The newer release the user was last notified about, so each one is announced once.</param>
internal sealed record AppSettings(bool DisclaimerAccepted = false, string? NotifiedUpdate = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2ModVerifier");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
