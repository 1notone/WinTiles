using System.Globalization;

namespace WinTiles.Core;

// Tile sizes in Windows 8.1 units: small = 1×1, medium = 2×2, wide = 4×2, large = 4×4.
public enum TileSize { Small, Medium, Wide, Large }

public enum TileKind { App, Clock, Calendar, Weather, System, Battery, Photos, News, Desktop, Web, Service }

/// <summary>Square is classic Windows 8; Soft and Round use squircle corners.</summary>
public enum TileShape { Square, Soft, Round }

/// <summary>Flat is the classic Metro look; Glass is translucent with a sheen.</summary>
public enum TileFinish { Flat, Glass }

/// <summary>How much tiles lift off the background.</summary>
public enum TileShadow { None, Subtle, Medium, Strong }

/// <summary>What fills the Start screen behind the tiles.</summary>
public enum BackgroundStyle { Color, Media, Live }

/// <summary>Animated wallpapers drawn by WinTiles itself, tinted with the background color.</summary>
public enum LiveWallpaper { Aurora, Ribbons, Bokeh, Squares }

public static class LookInfo
{
    /// <summary>Opacity, blur radius and downward offset of the drop shadow (same as MacTiles).</summary>
    public static (double Opacity, double Radius, double Y) Parameters(this TileShadow shadow) => shadow switch
    {
        TileShadow.Subtle => (0.25, 4, 2),
        TileShadow.Medium => (0.40, 10, 5),
        TileShadow.Strong => (0.55, 20, 10),
        _ => (0, 0, 0),
    };

    public static string Title(this BackgroundStyle style) => style switch
    {
        BackgroundStyle.Media => "Picture or Video",
        _ => style.ToString(),
    };
}

public static class TileSizeInfo
{
    public static (int W, int H) Units(this TileSize size) => size switch
    {
        TileSize.Small => (1, 1),
        TileSize.Medium => (2, 2),
        TileSize.Wide => (4, 2),
        _ => (4, 4),
    };

    public static string Title(this TileSize size) => size.ToString();
}

public static class TileKindInfo
{
    public static IReadOnlyList<TileKind> LiveKinds { get; } =
        Enum.GetValues<TileKind>().Where(k => k is not (TileKind.App or TileKind.Web or TileKind.Service)).ToArray();

    public static string Title(this TileKind kind) => kind switch
    {
        TileKind.Web => "Web Link",
        _ => kind.ToString(),
    };

    public static TileSize DefaultSize(this TileKind kind) => kind switch
    {
        TileKind.Clock or TileKind.Weather or TileKind.Photos or TileKind.News or TileKind.Desktop => TileSize.Wide,
        TileKind.Battery => TileSize.Small,
        _ => TileSize.Medium,
    };

    public static string DefaultColor(this TileKind kind) => kind switch
    {
        TileKind.App => "#2D89EF",
        TileKind.Clock => "#2B5797",
        TileKind.Calendar => "#603CBA",
        TileKind.Weather => "#2D89EF",
        TileKind.System => "#00A300",
        TileKind.Battery => "#1E7145",
        TileKind.Photos => "#00ABA9",
        TileKind.News => "#B91D47",
        TileKind.Web => "#2D89EF",
        TileKind.Service => "#2B5797",
        _ => "#1D1D1D",
    };

    /// <summary>Sizes that make sense for this tile.</summary>
    public static IReadOnlyList<TileSize> Sizes(this TileKind kind) => kind switch
    {
        TileKind.Battery => new[] { TileSize.Small, TileSize.Medium },
        TileKind.Photos or TileKind.News or TileKind.Desktop => new[] { TileSize.Medium, TileSize.Wide, TileSize.Large },
        _ => Enum.GetValues<TileSize>(),
    };
}

public static class TileShapeInfo
{
    /// <summary>Corner radius at 100% tile size.</summary>
    public static double CornerRadius(this TileShape shape) => shape switch
    {
        TileShape.Soft => 12,
        TileShape.Round => 20,
        _ => 0,
    };

    /// <summary>Space between tiles at 100%; round tiles get more room so corners don't crowd.</summary>
    public static double Gap(this TileShape shape) => shape == TileShape.Round ? 12 : 8;

    /// <summary>How far tile labels sit from the bottom-left corner, clear of the curve.</summary>
    public static (double Leading, double Bottom) LabelInset(this TileShape shape) => shape switch
    {
        TileShape.Soft => (10, 7),
        TileShape.Round => (14, 9),
        _ => (8, 6),
    };
}

/// <summary>One tile. AppPath is a Start-menu app ID (shell:AppsFolder parsing name) or a file path.</summary>
public sealed record Tile(Guid Id, TileKind Kind, string? AppPath, TileSize Size, string? Color)
{
    /// <summary>Web tiles: the address.</summary>
    public string? Url { get; init; }          // web tiles: the address; news tiles: their feed (null = Settings' default)
    /// <summary>Web and service tiles: the name shown on the tile (null = automatic).</summary>
    public string? Title { get; init; }
    /// <summary>Service tiles: which integration (Settings → Integrations).</summary>
    public Guid? IntegrationId { get; init; }
    /// <summary>Service tiles: what to show.</summary>
    public ServiceWidget? Widget { get; init; }
    /// <summary>Service tiles: a Home Assistant entity (light.kitchen) or a Beszel system ID.</summary>
    public string? Entity { get; init; }

    public static Tile Create(TileKind kind, string? appPath = null, TileSize? size = null, string? color = null) =>
        new(Guid.NewGuid(), kind, appPath, size ?? kind.DefaultSize(), color);

    public static Tile App(string path, TileSize size = TileSize.Medium) => Create(TileKind.App, path, size);

    public static Tile WebLink(string url, string? title = null, TileSize size = TileSize.Medium) =>
        Create(TileKind.Web, size: size) with { Url = url, Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim() };

    public static Tile ForService(Guid integration, ServiceWidget widget, string? entity = null, string? title = null) =>
        Create(TileKind.Service, size: widget.DefaultSize()) with { IntegrationId = integration, Widget = widget, Entity = entity, Title = title };

    /// <summary>Sizes the Resize menu offers.</summary>
    public IReadOnlyList<TileSize> Sizes => Kind == TileKind.Service && Widget is { } w ? w.Sizes() : Kind.Sizes();
}

public sealed record TileGroup(Guid Id, string Name, IReadOnlyList<Tile> Tiles)
{
    public static TileGroup Create(string name, IEnumerable<Tile> tiles) => new(Guid.NewGuid(), name, tiles.ToList());

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Unnamed group" : Name;

    public TileGroup WithTiles(IEnumerable<Tile> tiles) => this with { Tiles = tiles.ToList() };
}

public sealed record HotKeyPreset(string Id, string Label, uint Modifiers, uint VirtualKey)
{
    // MOD_ALT 0x1, MOD_CONTROL 0x2, MOD_SHIFT 0x4, MOD_WIN 0x8; VK_SPACE 0x20, VK_F13 0x7C
    public static IReadOnlyList<HotKeyPreset> All { get; } = new[]
    {
        new HotKeyPreset("ctrl-alt-space", "Ctrl + Alt + Space", 0x2 | 0x1, 0x20),
        new HotKeyPreset("alt-space", "Alt + Space", 0x1, 0x20),
        new HotKeyPreset("shift-alt-space", "Shift + Alt + Space", 0x4 | 0x1, 0x20),
        new HotKeyPreset("ctrl-space", "Ctrl + Space", 0x2, 0x20),
        new HotKeyPreset("f13", "F13", 0, 0x7C),
        new HotKeyPreset("none", "None", 0, 0),
    };

    public const string DefaultId = "ctrl-alt-space";

    public static HotKeyPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}

/// <summary>Everything the person can change in Settings. Stored in layout.json.</summary>
public sealed class AppSettings
{
    public string UserName { get; set; } = Environment.UserName;
    public string Background { get; set; } = "#25115A";
    public double TileScale { get; set; } = 1.0;
    public bool IconColoredTiles { get; set; }
    public TileShape TileShape { get; set; } = TileShape.Square;

    public bool KeepOnDesktop { get; set; } = true;     // Start stays behind your apps, Windows 8 style
    public string HotKey { get; set; } = HotKeyPreset.DefaultId;
    public bool WindowsKey { get; set; }                 // the Windows key opens WinTiles instead of the Start menu
    public bool HotCorner { get; set; }
    public bool HotCornerToggles { get; set; }           // the corner also puts Start away when it's already open

    public string? WeatherCity { get; set; }
    public double? WeatherLat { get; set; }
    public double? WeatherLon { get; set; }
    public string? WeatherPostalCode { get; set; }       // set when the place was chosen by ZIP / postal code
    public bool Fahrenheit { get; set; } = !RegionUsesMetric();
    public string NewsFeed { get; set; } = NewsFeeds.DefaultFeed;   // News tiles without a feed of their own use this one
    public string PhotosFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public string? CalendarFeed { get; set; }            // an iCalendar (.ics) URL, e.g. Outlook or Google "secret address"

    public BackgroundStyle BackgroundStyle { get; set; } = BackgroundStyle.Color;
    public string? WallpaperFile { get; set; }           // a file in %APPDATA%\WinTiles\Wallpapers (or an absolute path)
    public LiveWallpaper LiveWallpaper { get; set; } = LiveWallpaper.Aurora;
    public double WallpaperDim { get; set; } = 0.25;     // darkens pictures and videos so tiles stay readable
    public TileFinish TileFinish { get; set; } = TileFinish.Flat;
    public double GlassTint { get; set; } = 0.45;        // how much of the tile color the glass carries
    public TileShadow TileShadow { get; set; } = TileShadow.None;
    public bool ShowRunningIndicator { get; set; } = true;
    /// <summary>Homelab services for service tiles. Their secrets are in Windows Credential Manager.</summary>
    public List<Integration> Integrations { get; set; } = new();

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Integrations = Integrations.ToList();
        return copy;
    }

    private static bool RegionUsesMetric()
    {
        try { return RegionInfo.CurrentRegion.IsMetric; } catch { return true; }
    }
}

public sealed class LayoutFile
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<TileGroup> Groups { get; set; } = new();
}
