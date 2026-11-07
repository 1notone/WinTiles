using System.Text;
using System.Text.Json;

namespace WinTiles.Core;

/// <summary>
/// Reads and writes layout.json. Same schema as MacTiles, so a layout can be copied between
/// them. Reading is lenient: missing keys fall back to defaults and tiles of unknown kinds
/// are skipped instead of failing the whole file.
/// </summary>
public static class LayoutJson
{
    public static LayoutFile Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("groups", out var groupsEl) || groupsEl.ValueKind != JsonValueKind.Array)
            throw new FormatException("layout.json has no \"groups\" list");

        var file = new LayoutFile
        {
            Version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 1,
            Settings = root.TryGetProperty("settings", out var s) ? ParseSettings(s) : new AppSettings(),
        };
        foreach (var g in groupsEl.EnumerateArray())
            if (ParseGroup(g) is { } group) file.Groups.Add(group);
        return file;
    }

    public static AppSettings ParseSettings(JsonElement e)
    {
        var d = new AppSettings();
        if (e.ValueKind != JsonValueKind.Object) return d;
        return new AppSettings
        {
            UserName = Str(e, "userName") ?? d.UserName,
            Background = Palette.NormalizeHex(Str(e, "background") ?? "") ?? d.Background,
            TileScale = Math.Clamp(Num(e, "tileScale") ?? d.TileScale, 0.8, 1.5),      // Settings' slider range
            IconColoredTiles = Bool(e, "iconColoredTiles") ?? d.IconColoredTiles,
            TileShape = EnumOf<TileShape>(e, "tileShape") ?? d.TileShape,
            KeepOnDesktop = Bool(e, "keepOnDesktop") ?? d.KeepOnDesktop,
            HotKey = HotKeyPreset.Find(Str(e, "hotKey")).Id,
            WindowsKey = Bool(e, "windowsKey") ?? d.WindowsKey,
            HotCorner = Bool(e, "hotCorner") ?? d.HotCorner,
            HotCornerToggles = Bool(e, "hotCornerToggles") ?? d.HotCornerToggles,
            BackgroundStyle = EnumOf<BackgroundStyle>(e, "backgroundStyle") ?? d.BackgroundStyle,
            WallpaperFile = Str(e, "wallpaperFile"),
            LiveWallpaper = EnumOf<LiveWallpaper>(e, "liveWallpaper") ?? d.LiveWallpaper,
            WallpaperDim = Math.Clamp(Num(e, "wallpaperDim") ?? d.WallpaperDim, 0, 0.7),
            TileFinish = EnumOf<TileFinish>(e, "tileFinish") ?? d.TileFinish,
            GlassTint = Math.Clamp(Num(e, "glassTint") ?? d.GlassTint, 0.1, 0.9),
            TileShadow = EnumOf<TileShadow>(e, "tileShadow") ?? d.TileShadow,
            ShowRunningIndicator = Bool(e, "showRunningIndicator") ?? d.ShowRunningIndicator,
            Integrations = ParseIntegrations(e),
            WeatherCity = Str(e, "weatherCity"),
            WeatherLat = Num(e, "weatherLat"),
            WeatherLon = Num(e, "weatherLon"),
            WeatherPostalCode = Str(e, "weatherPostalCode"),
            Fahrenheit = Bool(e, "fahrenheit") ?? d.Fahrenheit,
            NewsFeed = Str(e, "newsFeed") ?? d.NewsFeed,
            PhotosFolder = Str(e, "photosFolder") ?? d.PhotosFolder,
            CalendarFeed = Str(e, "calendarFeed"),
        };
    }

    /// <summary>Services from a newer version (unknown kinds) are skipped, not fatal.</summary>
    private static List<Integration> ParseIntegrations(JsonElement e)
    {
        var list = new List<Integration>();
        if (!e.TryGetProperty("integrations", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var i in arr.EnumerateArray())
        {
            if (i.ValueKind != JsonValueKind.Object || EnumOf<ServiceKind>(i, "kind") is not { } kind) continue;
            list.Add(new Integration
            {
                Id = GuidOf(i),
                Kind = kind,
                Name = Str(i, "name") ?? "",
                Url = Str(i, "url") ?? "",
                Username = Str(i, "username") ?? "",
                AllowSelfSigned = Bool(i, "allowSelfSigned") ?? kind.UsuallySelfSigned(),
            });
        }
        return list;
    }

    private static TileGroup? ParseGroup(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object) return null;
        var tiles = new List<Tile>();
        if (g.TryGetProperty("tiles", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tEl.EnumerateArray())
                if (ParseTile(t) is { } tile) tiles.Add(tile);
        return new TileGroup(GuidOf(g), Str(g, "name") ?? "", tiles);
    }

    private static Tile? ParseTile(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return null;
        if (EnumOf<TileKind>(t, "kind") is not { } kind) return null;   // unknown kind (e.g. from a newer version)
        var path = Str(t, "appPath");
        if (kind == TileKind.App && string.IsNullOrWhiteSpace(path)) return null;
        var size = EnumOf<TileSize>(t, "size") ?? kind.DefaultSize();
        var color = Str(t, "color") is { } c ? Palette.NormalizeHex(c) : null;
        var tile = new Tile(GuidOf(t), kind, path, size, color)
        {
            Url = Str(t, "url"),
            Title = Str(t, "title"),
            IntegrationId = Str(t, "integrationID") is { } iid && Guid.TryParse(iid, out var g) ? g : null,
            Widget = EnumOf<ServiceWidget>(t, "widget"),
            Entity = Str(t, "entity"),
        };
        if (kind == TileKind.Web && string.IsNullOrWhiteSpace(tile.Url)) return null;
        if (kind == TileKind.Service && (tile.IntegrationId is null || tile.Widget is null)) return null;
        return tile;
    }

    // MARK: writing

    public static string Write(LayoutFile file)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", file.Version);

            var s = file.Settings;
            w.WriteStartObject("settings");
            w.WriteString("userName", s.UserName);
            w.WriteString("background", s.Background);
            w.WriteNumber("tileScale", Math.Round(s.TileScale, 3));
            w.WriteBoolean("iconColoredTiles", s.IconColoredTiles);
            w.WriteString("tileShape", Camel(s.TileShape));
            w.WriteBoolean("keepOnDesktop", s.KeepOnDesktop);
            w.WriteString("hotKey", s.HotKey);
            w.WriteBoolean("windowsKey", s.WindowsKey);
            w.WriteBoolean("hotCorner", s.HotCorner);
            w.WriteBoolean("hotCornerToggles", s.HotCornerToggles);
            if (s.WeatherCity is not null) w.WriteString("weatherCity", s.WeatherCity);
            if (s.WeatherLat is { } lat) w.WriteNumber("weatherLat", lat);
            if (s.WeatherLon is { } lon) w.WriteNumber("weatherLon", lon);
            if (s.WeatherPostalCode is not null) w.WriteString("weatherPostalCode", s.WeatherPostalCode);
            w.WriteBoolean("fahrenheit", s.Fahrenheit);
            w.WriteString("newsFeed", s.NewsFeed);
            w.WriteString("photosFolder", s.PhotosFolder);
            if (!string.IsNullOrWhiteSpace(s.CalendarFeed)) w.WriteString("calendarFeed", s.CalendarFeed);
            w.WriteString("backgroundStyle", Camel(s.BackgroundStyle));
            if (s.WallpaperFile is not null) w.WriteString("wallpaperFile", s.WallpaperFile);
            w.WriteString("liveWallpaper", Camel(s.LiveWallpaper));
            w.WriteNumber("wallpaperDim", Math.Round(s.WallpaperDim, 3));
            w.WriteString("tileFinish", Camel(s.TileFinish));
            w.WriteNumber("glassTint", Math.Round(s.GlassTint, 3));
            w.WriteString("tileShadow", Camel(s.TileShadow));
            w.WriteBoolean("showRunningIndicator", s.ShowRunningIndicator);
            w.WriteStartArray("integrations");
            foreach (var i in s.Integrations)
            {
                w.WriteStartObject();
                w.WriteString("id", i.Id.ToString().ToUpperInvariant());
                w.WriteString("kind", Camel(i.Kind));
                w.WriteString("name", i.Name);
                w.WriteString("url", i.Url);
                w.WriteString("username", i.Username);
                w.WriteBoolean("allowSelfSigned", i.AllowSelfSigned);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("groups");
            foreach (var g in file.Groups)
            {
                w.WriteStartObject();
                w.WriteString("id", g.Id.ToString().ToUpperInvariant());
                w.WriteString("name", g.Name);
                w.WriteStartArray("tiles");
                foreach (var t in g.Tiles)
                {
                    w.WriteStartObject();
                    w.WriteString("id", t.Id.ToString().ToUpperInvariant());
                    w.WriteString("kind", Camel(t.Kind));
                    if (t.AppPath is not null) w.WriteString("appPath", t.AppPath);
                    w.WriteString("size", Camel(t.Size));
                    if (t.Color is not null) w.WriteString("color", t.Color);
                    if (t.Url is not null) w.WriteString("url", t.Url);
                    if (t.Title is not null) w.WriteString("title", t.Title);
                    if (t.IntegrationId is { } iid) w.WriteString("integrationID", iid.ToString().ToUpperInvariant());
                    if (t.Widget is { } widget) w.WriteString("widget", Camel(widget));
                    if (t.Entity is not null) w.WriteString("entity", t.Entity);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // MARK: helpers

    private static string Camel<T>(T value) where T : struct, System.Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    private static T? EnumOf<T>(JsonElement e, string key) where T : struct, System.Enum =>
        Str(e, key) is { } s && System.Enum.TryParse<T>(s, ignoreCase: true, out var v) && System.Enum.IsDefined(v) && !int.TryParse(s, out _) ? v : null;

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool? Bool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static Guid GuidOf(JsonElement e) =>
        Str(e, "id") is { } s && Guid.TryParse(s, out var id) ? id : Guid.NewGuid();
}
