namespace WinTiles.Core;

/// <summary>Where a dragged or pinned tile should go.</summary>
public abstract record TileDestination
{
    public sealed record Before(Guid TileId) : TileDestination;
    public sealed record EndOf(Guid GroupId) : TileDestination;
    public sealed record NewGroup : TileDestination;
    public sealed record LastGroup : TileDestination;
}

/// <summary>Pure layout edits. Each returns a new list and never mutates its input.</summary>
public static class LayoutOps
{
    public static (int G, int T)? Locate(IReadOnlyList<TileGroup> groups, Guid tileId)
    {
        for (var g = 0; g < groups.Count; g++)
            for (var t = 0; t < groups[g].Tiles.Count; t++)
                if (groups[g].Tiles[t].Id == tileId) return (g, t);
        return null;
    }

    public static Tile? Find(IReadOnlyList<TileGroup> groups, Guid tileId) =>
        Locate(groups, tileId) is { } p ? groups[p.G].Tiles[p.T] : null;

    /// <summary>A News tile's feed: its own, or Settings' default for tiles that don't have one.</summary>
    public static string NewsFeedOf(Tile tile, string defaultFeed)
    {
        var own = (tile.Url ?? "").Trim();
        return own.Length == 0 ? defaultFeed.Trim() : own;
    }

    /// <summary>The distinct feeds the News tiles on Start are showing.</summary>
    public static List<string> NewsFeeds(IReadOnlyList<TileGroup> groups, string defaultFeed) =>
        groups.SelectMany(g => g.Tiles).Where(t => t.Kind == TileKind.News)
              .Select(t => NewsFeedOf(t, defaultFeed)).Where(f => f.Length > 0).Distinct().ToList();

    public static bool IsPinned(IReadOnlyList<TileGroup> groups, string path) =>
        groups.Any(g => g.Tiles.Any(t => string.Equals(t.AppPath, path, StringComparison.OrdinalIgnoreCase)));

    public static List<TileGroup> Update(IReadOnlyList<TileGroup> groups, Guid tileId, Func<Tile, Tile> change)
    {
        var g = Copy(groups);
        if (Locate(g, tileId) is { } p)
        {
            var tiles = g[p.G].Tiles.ToList();
            tiles[p.T] = change(tiles[p.T]);
            g[p.G] = g[p.G].WithTiles(tiles);
        }
        return g;
    }

    public static List<TileGroup> Remove(IReadOnlyList<TileGroup> groups, Guid tileId)
    {
        var g = Copy(groups);
        if (Locate(g, tileId) is { } p)
            g[p.G] = g[p.G].WithTiles(g[p.G].Tiles.Where(t => t.Id != tileId));
        return Pruned(g);
    }

    public static List<TileGroup> Move(IReadOnlyList<TileGroup> groups, Guid tileId, TileDestination dest)
    {
        if (dest is TileDestination.Before b && b.TileId == tileId) return Copy(groups);
        var g = Copy(groups);
        if (Locate(g, tileId) is not { } src) return g;
        var tile = g[src.G].Tiles[src.T];
        g[src.G] = g[src.G].WithTiles(g[src.G].Tiles.Where(t => t.Id != tileId));
        Insert(tile, dest, g, src);
        return Pruned(g);
    }

    public static List<TileGroup> Pin(IReadOnlyList<TileGroup> groups, Tile tile, TileDestination dest)
    {
        var g = Copy(groups);
        Insert(tile, dest, g, null);
        return Pruned(g);
    }

    /// <summary>
    /// Moves a group into another group's position. Dragging right lands after the target,
    /// dragging left lands before it, so passing over a neighbour swaps the two.
    /// </summary>
    public static List<TileGroup> MovingGroup(IReadOnlyList<TileGroup> groups, Guid id, Guid target)
    {
        var g = Copy(groups);
        if (id == target) return g;
        var from = g.FindIndex(x => x.Id == id);
        var to = g.FindIndex(x => x.Id == target);
        if (from < 0 || to < 0) return g;
        var group = g[from];
        g.RemoveAt(from);
        g.Insert(to, group);
        return g;
    }

    public static List<TileGroup> Rename(IReadOnlyList<TileGroup> groups, Guid groupId, string name) =>
        groups.Select(x => x.Id == groupId ? x with { Name = name.Trim() } : x).ToList();

    public static List<TileGroup> RemoveGroup(IReadOnlyList<TileGroup> groups, Guid groupId) =>
        Pruned(groups.Where(x => x.Id != groupId).ToList());

    public static Guid? GroupOf(IReadOnlyList<TileGroup> groups, TileDestination dest) => dest switch
    {
        TileDestination.Before b => Locate(groups, b.TileId) is { } p ? groups[p.G].Id : null,
        TileDestination.EndOf e => e.GroupId,
        _ => groups.Count > 0 ? groups[^1].Id : null,
    };

    // MARK: helpers

    private static List<TileGroup> Copy(IReadOnlyList<TileGroup> groups) => groups.ToList();

    private static void Insert(Tile tile, TileDestination dest, List<TileGroup> g, (int G, int T)? src)
    {
        switch (dest)
        {
            case TileDestination.Before b when Locate(g, b.TileId) is { } p:
                var index = p.T;
                // Moving forward within a group lands after the target, so a drag swaps neighbours.
                if (src is { } s && s.G == p.G && s.T <= p.T) index += 1;
                var tiles = g[p.G].Tiles.ToList();
                tiles.Insert(Math.Min(index, tiles.Count), tile);
                g[p.G] = g[p.G].WithTiles(tiles);
                break;
            case TileDestination.EndOf e when g.Exists(x => x.Id == e.GroupId):
                var gi = g.FindIndex(x => x.Id == e.GroupId);
                g[gi] = g[gi].WithTiles(g[gi].Tiles.Append(tile));
                break;
            case TileDestination.NewGroup:
                g.Add(TileGroup.Create("", new[] { tile }));
                break;
            default:
                if (g.Count == 0) g.Add(TileGroup.Create("", new[] { tile }));
                else g[^1] = g[^1].WithTiles(g[^1].Tiles.Append(tile));
                break;
        }
    }

    internal static List<TileGroup> Pruned(List<TileGroup> g)
    {
        var kept = g.Where(x => x.Tiles.Count > 0).ToList();
        return kept.Count == 0 ? new List<TileGroup> { TileGroup.Create("", Array.Empty<Tile>()) } : kept;
    }

    // MARK: default layout

    /// <summary>Start-menu app IDs pinned on first run, when installed.</summary>
    public static class WellKnownApps
    {
        public const string Edge = "MSEdge";
        public const string Explorer = "Microsoft.Windows.Explorer";
        public const string Settings = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
        public const string Store = "Microsoft.WindowsStore_8wekyb3d8bbwe!App";
        public const string Photos = "Microsoft.Windows.Photos_8wekyb3d8bbwe!App";
        public const string Outlook = "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows";
        public const string MediaPlayer = "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic";
        public const string Calculator = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        public const string Notepad = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";
        public const string Terminal = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
        public const string Snipping = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App";
        public const string Clock = "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App";
    }

    public static List<TileGroup> DefaultGroups(Func<string, bool> isInstalled)
    {
        Tile? App(string id, TileSize size = TileSize.Medium) => isInstalled(id) ? Tile.App(id, size) : null;

        var today = TileGroup.Create("Today", new[]
        {
            Tile.Create(TileKind.Clock, size: TileSize.Wide),
            Tile.Create(TileKind.Weather, size: TileSize.Wide),
            Tile.Create(TileKind.Calendar),
            Tile.Create(TileKind.System),
            Tile.Create(TileKind.Photos, size: TileSize.Wide),
            Tile.Create(TileKind.News, size: TileSize.Wide),
        });

        var everyday = new[]
        {
            App(WellKnownApps.Edge, TileSize.Wide),
            App(WellKnownApps.Outlook),
            App(WellKnownApps.Photos),
            App(WellKnownApps.MediaPlayer),
            App(WellKnownApps.Store),
            App(WellKnownApps.Notepad, TileSize.Small),
            App(WellKnownApps.Calculator, TileSize.Small),
            App(WellKnownApps.Snipping, TileSize.Small),
            App(WellKnownApps.Clock, TileSize.Small),
            App(WellKnownApps.Settings),
        }.OfType<Tile>();

        var work = new[]
        {
            Tile.Create(TileKind.Desktop, size: TileSize.Wide),
            App(WellKnownApps.Explorer),
            App(WellKnownApps.Terminal),
            Tile.Create(TileKind.Battery),
        }.OfType<Tile>();

        return Pruned(new List<TileGroup>
        {
            today,
            TileGroup.Create("Everyday", everyday),
            TileGroup.Create("Work", work),
        });
    }
}
