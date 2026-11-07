using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// Owns the Start layout and settings, persisted to %APPDATA%\WinTiles\layout.json
/// (human-readable, safe to hand-edit, same format as MacTiles).
/// </summary>
public sealed class LayoutStore
{
    public const string TileFormat = "WinTiles.Tile";
    public const string GroupFormat = "WinTiles.Group";

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTiles");
    public static string FilePath { get; } = Path.Combine(Directory, "layout.json");

    public IReadOnlyList<TileGroup> Groups { get; private set; } = Array.Empty<TileGroup>();
    public AppSettings Settings { get; private set; } = new();

    /// <summary>A tile, or a whole group, currently being dragged.</summary>
    public Guid? DraggingTile { get; set; }
    public Guid? DraggingGroup { get; set; }

    public event Action? LayoutChanged;
    /// <summary>Raised with a copy of the settings before the change.</summary>
    public event Action<AppSettings>? SettingsChanged;

    private readonly DispatcherTimer saveTimer;
    private DateTime lastLiveMove = DateTime.MinValue;
    private (Guid Id, Guid Target, DateTime At)? lastGroupSwap;

    public LayoutStore()
    {
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
    }

    // MARK: persistence

    /// <param name="isInstalled">Used to build the default layout on first run.</param>
    public void Load(Func<string, bool> isInstalled)
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var file = LayoutJson.Parse(File.ReadAllText(FilePath));
                Settings = file.Settings;
                Groups = file.Groups.Count > 0 ? file.Groups : LayoutOps.DefaultGroups(isInstalled);
                return;
            }
            catch (Exception ex)
            {
                var backup = Path.Combine(Directory, $"layout.unreadable-{DateTimeOffset.Now.ToUnixTimeSeconds()}.json");
                try { File.Move(FilePath, backup); } catch { }
                Log.Write($"layout.json could not be read ({ex.Message}). Saved it as {backup} and restored defaults.");
            }
        }
        Settings = new AppSettings();
        Groups = LayoutOps.DefaultGroups(isInstalled);
        SaveNow();
    }

    public void SaveNow()
    {
        saveTimer.Stop();
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = LayoutJson.Write(new LayoutFile { Settings = Settings, Groups = Groups.ToList() });
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't save layout: " + ex.Message);
        }
    }

    private void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    // MARK: changes

    public void SetGroups(IReadOnlyList<TileGroup> groups)
    {
        Groups = groups;
        ScheduleSave();
        LayoutChanged?.Invoke();
    }

    public void ChangeSettings(Action<AppSettings> change)
    {
        var before = Settings.Clone();
        change(Settings);
        ScheduleSave();
        SettingsChanged?.Invoke(before);
    }

    public void ResetLayout(Func<string, bool> isInstalled) => SetGroups(LayoutOps.DefaultGroups(isInstalled));

    public bool Has(TileKind kind) => Groups.Any(g => g.Tiles.Any(t => t.Kind == kind));
    public bool IsPinned(string path) => LayoutOps.IsPinned(Groups, path);

    public void Update(Guid id, Func<Tile, Tile> change) => SetGroups(LayoutOps.Update(Groups, id, change));
    public void Remove(Guid id) => SetGroups(LayoutOps.Remove(Groups, id));
    public void Move(Guid id, TileDestination dest) => SetGroups(LayoutOps.Move(Groups, id, dest));

    public bool Pin(string path, TileDestination? dest = null)
    {
        if (IsPinned(path)) return false;
        SetGroups(LayoutOps.Pin(Groups, Tile.App(path), dest ?? new TileDestination.LastGroup()));
        return true;
    }

    public void Unpin(string path)
    {
        var tile = Groups.SelectMany(g => g.Tiles).FirstOrDefault(t => string.Equals(t.AppPath, path, StringComparison.OrdinalIgnoreCase));
        if (tile is not null) Remove(tile.Id);
    }

    /// <summary>Adds a web or service tile (by default to the end of the last group).</summary>
    public void AddTile(Tile tile, TileDestination? dest = null) =>
        SetGroups(LayoutOps.Pin(Groups, tile, dest ?? new TileDestination.LastGroup()));

    /// <summary>Service tiles showing this integration.</summary>
    public IReadOnlyList<Tile> ServiceTiles(Guid integration) =>
        Groups.SelectMany(g => g.Tiles).Where(t => t.Kind == TileKind.Service && t.IntegrationId == integration).ToList();

    /// <summary>Removes an integration, unpins its tiles, and deletes its secret from Credential Manager.</summary>
    public void RemoveIntegration(Guid id)
    {
        ChangeSettings(s => s.Integrations = s.Integrations.Where(i => i.Id != id).ToList());
        var groups = Groups.Select(g => g.WithTiles(g.Tiles.Where(t => !(t.Kind == TileKind.Service && t.IntegrationId == id)))).ToList();
        SetGroups(LayoutOps.RemoveGroup(groups, Guid.Empty));      // drops groups left empty
        IntegrationSecrets.Remove(id);
    }

    public void AddLive(TileKind kind) => AddToFirstGroup(Tile.Create(kind));

    /// <summary>A News tile with its own feed; you can have as many as you like.</summary>
    public void AddNews(string feed, string? title) =>
        AddToFirstGroup(Tile.Create(TileKind.News) with { Url = feed, Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() });

    /// <summary>A News tile's feed: its own, or Settings' default.</summary>
    public string NewsFeedOf(Tile tile) => LayoutOps.NewsFeedOf(tile, Settings.NewsFeed);

    /// <summary>The distinct feeds the News tiles on Start are showing.</summary>
    public IReadOnlyList<string> NewsFeeds => LayoutOps.NewsFeeds(Groups, Settings.NewsFeed);

    private void AddToFirstGroup(Tile tile)
    {
        var dest = Groups.Count > 0 ? new TileDestination.EndOf(Groups[0].Id) : (TileDestination)new TileDestination.LastGroup();
        SetGroups(LayoutOps.Pin(Groups, tile, dest));
    }

    public void Rename(Guid groupId, string name) => SetGroups(LayoutOps.Rename(Groups, groupId, name));
    public void RemoveGroup(Guid groupId) => SetGroups(LayoutOps.RemoveGroup(Groups, groupId));
    public void MoveGroup(Guid id, Guid target) => SetGroups(LayoutOps.MovingGroup(Groups, id, target));

    public void ShiftGroup(Guid id, int offset)
    {
        var list = Groups.ToList();
        var i = list.FindIndex(g => g.Id == id);
        var j = i + offset;
        if (i < 0 || j < 0 || j >= list.Count) return;
        MoveGroup(id, list[j].Id);
    }

    /// <summary>Reorders while dragging over other tiles, Windows 8 style. Debounced to avoid jitter.</summary>
    public void LiveMove(Guid id, Guid beforeTile)
    {
        var now = DateTime.UtcNow;
        if (id == beforeTile || (now - lastLiveMove).TotalSeconds < 0.18) return;
        lastLiveMove = now;
        Move(id, new TileDestination.Before(beforeTile));
    }

    /// <summary>Reorders groups while dragging, without swapping straight back after a swap with a wider group.</summary>
    public void LiveMoveGroup(Guid id, Guid target)
    {
        var now = DateTime.UtcNow;
        if (id == target || (now - lastLiveMove).TotalSeconds < 0.18) return;
        if (lastGroupSwap is { } last && last.Id == id && last.Target == target && (now - last.At).TotalSeconds < 0.7) return;
        lastGroupSwap = (id, target, now);
        lastLiveMove = now;
        MoveGroup(id, target);
    }

    /// <summary>Handles drops of tiles (reordering), whole groups, apps from File Explorer (pinning) and links from a browser (web tiles).</summary>
    public bool AcceptDrop(IDataObject data, TileDestination dest)
    {
        if (DraggingTile is null && DraggingGroup is null && DroppedLink(data) is { } link)
        {
            AddTile(Tile.WebLink(link.Url.AbsoluteUri, link.Title), dest);
            WebLinkDropped?.Invoke(link.Url);
            return true;
        }

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var pinned = false;
            foreach (var file in files.Where(AppLauncher.IsPinnableFile))
                pinned |= Pin(file, dest);
            return pinned;
        }

        if (DraggingGroup is { } group)
        {
            DraggingGroup = null;
            // Live reordering has already moved it; dropping after the last group moves it to the end.
            if (dest is TileDestination.NewGroup && Groups.Count > 0 && Groups[^1].Id != group)
                MoveGroup(group, Groups[^1].Id);
            else
                LayoutChanged?.Invoke();
            return true;
        }

        if (DraggingTile is not { } tile) return false;
        DraggingTile = null;
        if (dest is TileDestination.Before) LayoutChanged?.Invoke();   // live reordering already placed it
        else Move(tile, dest);
        return true;
    }

    public Guid? GroupOf(TileDestination dest) => LayoutOps.GroupOf(Groups, dest);

    /// <summary>Tiles and groups move; files and links from other apps are copied or linked (whichever the source allows).</summary>
    public DragDropEffects DropEffect(DragEventArgs e)
    {
        if (DraggingTile is not null || DraggingGroup is not null) return DragDropEffects.Move;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        return DragDropEffects.Move;
    }

    /// <summary>A web link dropped from a browser (the page or its address bar), so its icon can be fetched.</summary>
    public event Action<Uri>? WebLinkDropped;

    /// <summary>Browsers offer links as URLs and as text; files are handled elsewhere.</summary>
    public static (Uri Url, string? Title)? DroppedLink(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop)) return null;
        string? text = null;
        try
        {
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream w)
                text = System.Text.Encoding.Unicode.GetString(w.ToArray()).TrimEnd('\0');
            else if (data.GetDataPresent(DataFormats.UnicodeText)) text = data.GetData(DataFormats.UnicodeText) as string;
            else if (data.GetDataPresent(DataFormats.Text)) text = data.GetData(DataFormats.Text) as string;
        }
        catch { }
        var first = text?.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
        if (first is null || WebLink.Normalize(first) is not { Scheme: "http" or "https" } url) return null;
        return (url, null);
    }
}

/// <summary>Appends errors to %APPDATA%\WinTiles\wintiles.log.</summary>
public static class Log
{
    public static void Write(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LayoutStore.Directory);
            var path = Path.Combine(LayoutStore.Directory, "wintiles.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}
