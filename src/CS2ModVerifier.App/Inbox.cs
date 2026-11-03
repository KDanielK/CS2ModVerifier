using System.Text.Json;

namespace CS2ModVerifier.App;

/// <summary>One unread notification. <paramref name="ModPath"/> or <paramref name="Url"/> says what clicking it opens.</summary>
internal sealed record InboxItem(DateTimeOffset Time, string Text, string? ModPath, string? Url);

/// <summary>
/// Notifications the player has not seen yet, shown in the tray until read: Windows notifications disappear,
/// and may be hidden by "Do not disturb". Kept across restarts in %LOCALAPPDATA%\CS2ModVerifier\unread.json.
/// </summary>
internal sealed class Inbox
{
    private const int MaxItems = 20;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "unread.json");

    private readonly List<InboxItem> items;

    private Inbox(List<InboxItem> items) => this.items = items;

    /// <summary>Raised on the UI thread when items are added or read.</summary>
    public event Action? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<InboxItem> Items => items;

    public static Inbox Load()
    {
        try
        {
            return new Inbox(JsonSerializer.Deserialize<List<InboxItem>>(File.ReadAllText(FilePath), Options) ?? []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Inbox([]);
        }
    }

    public void Add(string text, string? modPath = null, string? url = null)
    {
        // A newer notification about the same mod replaces the older one.
        if (modPath is not null)
        {
            items.RemoveAll(i => string.Equals(i.ModPath, modPath, StringComparison.OrdinalIgnoreCase));
        }

        items.Insert(0, new InboxItem(DateTimeOffset.Now, text, modPath, url));
        if (items.Count > MaxItems)
        {
            items.RemoveRange(MaxItems, items.Count - MaxItems);
        }

        Commit();
    }

    public bool HasUnread(string modPath) =>
        items.Any(i => string.Equals(i.ModPath, modPath, StringComparison.OrdinalIgnoreCase));

    public void MarkRead(InboxItem item)
    {
        if (items.Remove(item))
        {
            Commit();
        }
    }

    public void MarkRead(string modPath)
    {
        if (items.RemoveAll(i => string.Equals(i.ModPath, modPath, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Commit();
        }
    }

    /// <summary>The update notification is read once its release page was opened, whichever way it was opened.</summary>
    public void MarkAllUpdatesRead()
    {
        if (items.RemoveAll(i => i.Url is not null) > 0)
        {
            Commit();
        }
    }

    public void MarkAllRead()
    {
        if (items.Count > 0)
        {
            items.Clear();
            Commit();
        }
    }

    private void Commit()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(items, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not save unread notifications: {e.Message}");
        }

        Changed?.Invoke();
    }
}
