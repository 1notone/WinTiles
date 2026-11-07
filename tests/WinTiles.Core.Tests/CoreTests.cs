using WinTiles.Core;

namespace WinTiles.Core.Tests;

public class PackerTests
{
    private static List<Tile> Tiles(params TileSize[] sizes) => sizes.Select(s => Tile.App(@"C:\X.exe", s)).ToList();
    private static List<(int, int)> Positions(Packer.Result r) => r.Items.Select(i => (i.X, i.Y)).ToList();

    [Fact]
    public void MediumTilesFillABandLeftToRightTopToBottom()
    {
        var r = Packer.Pack(Tiles(TileSize.Medium, TileSize.Medium, TileSize.Medium), 4);
        Assert.Equal(new[] { (0, 0), (2, 0), (0, 2) }, Positions(r));
        Assert.Equal(4, r.Width);
    }

    [Fact]
    public void SmallTilesBackFillBesideWideTile()
    {
        var r = Packer.Pack(Tiles(TileSize.Wide, TileSize.Small, TileSize.Small, TileSize.Small, TileSize.Small), 4);
        Assert.Equal(new[] { (0, 0), (0, 2), (1, 2), (2, 2), (3, 2) }, Positions(r));
    }

    [Fact]
    public void FullBandSpillsIntoNextBand()
    {
        var r = Packer.Pack(Tiles(Enumerable.Repeat(TileSize.Medium, 5).ToArray()), 4);
        Assert.Equal((4, 0), Positions(r).Last());
        Assert.Equal(6, r.Width);
    }

    [Fact]
    public void LargeTileTakesWholeBandWhenRowsAreFour()
    {
        var r = Packer.Pack(Tiles(TileSize.Large, TileSize.Medium), 4);
        Assert.Equal(new[] { (0, 0), (4, 0) }, Positions(r));
    }

    [Fact]
    public void EmptyGroupStillHasWidth() => Assert.Equal(2, Packer.Pack(new List<Tile>(), 6).Width);
}

public class MetricsTests
{
    [Fact]
    public void FollowShapeAndScale()
    {
        var square = new Metrics(1440, 900, 1, TileShape.Square);
        var round = new Metrics(1440, 900, 1, TileShape.Round);
        Assert.Equal(0, square.CornerRadius);
        Assert.Equal(8, square.Gap);
        Assert.Equal(20, round.CornerRadius);
        Assert.Equal(12, round.Gap);
        Assert.Equal(18, new Metrics(1440, 900, 1.5, TileShape.Soft).CornerRadius);
    }

    [Fact]
    public void RowsAreEvenAndClamped()
    {
        Assert.Equal(4, new Metrics(1000, 300, 1).Rows);
        Assert.True(new Metrics(1440, 900, 1).Rows % 2 == 0);
        Assert.True(new Metrics(4000, 4000, 0.8).Rows <= 12);
    }

    [Fact]
    public void CornersNeverSwallowASmallTile()
    {
        var m = new Metrics(1440, 900, 0.8, TileShape.Round);
        Assert.True(m.CornerRadius * 2 < m.Tile);
    }
}

public class SearchTests
{
    private static readonly List<AppInfo> Apps = new[] { "Visual Studio Code", "Microsoft Edge", "Calculator", "Calendar", "Media Player" }
        .Select(n => new AppInfo(n.Replace(" ", "") + "!App", n)).ToList();

    private static List<string> Names(string q) => AppSearch.Rank(q, Apps).Select(a => a.Name).ToList();

    [Fact] public void EmptyQueryReturnsNothing() => Assert.Empty(Names("   "));
    [Fact] public void ExactMatchComesFirst() => Assert.Equal("Calendar", Names("calendar")[0]);
    [Fact] public void PrefixMatchesPreferShorterNames() => Assert.Equal(new[] { "Calendar", "Calculator" }, Names("cal"));
    [Fact] public void WordPrefixMatches() => Assert.Equal(new[] { "Microsoft Edge" }, Names("edge"));
    [Fact] public void InitialsMatchAsSubsequence() => Assert.Equal(new[] { "Visual Studio Code" }, Names("vsc"));
    [Fact] public void CaseInsensitive() => Assert.Equal(new[] { "Media Player" }, Names("MEDIA"));

    [Theory]
    [InlineData("Éclair", "E")]
    [InlineData("7-Zip", "#")]
    [InlineData("edge", "E")]
    public void LetterHeadings(string name, string letter) => Assert.Equal(letter, AppSearch.Letter(name));
}

public class TypeToSearchTests
{
    private static TypeToSearch.Action Text(string t, string q = "") => TypeToSearch.ForText(t, q);
    private static TypeToSearch.Action Key(TypeToSearch.Key k, string q, bool ctrl = false, bool shift = false, bool alt = false) =>
        TypeToSearch.ForKey(k, ctrl, shift, alt, q);

    [Fact]
    public void LettersAppend()
    {
        Assert.Equal(new TypeToSearch.Action(TypeToSearch.Kind.Append, "a"), Text("a"));
        Assert.Equal(new TypeToSearch.Action(TypeToSearch.Kind.Append, "é"), Text("é", "caf"));
    }

    [Fact]
    public void ControlCharactersAreIgnored()
    {
        Assert.Equal(TypeToSearch.Kind.Ignore, Text("\b").Kind);
        Assert.Equal(TypeToSearch.Kind.Ignore, Text("\r").Kind);
        Assert.Equal(TypeToSearch.Kind.Ignore, Text("\u001b").Kind);
    }

    [Fact]
    public void LeadingSpaceIgnoredButInnerSpaceKept()
    {
        Assert.Equal(TypeToSearch.Kind.Ignore, Text(" ").Kind);
        Assert.Equal(TypeToSearch.Kind.Append, Text(" ", "visual").Kind);
    }

    [Fact]
    public void BackspaceVariants()
    {
        Assert.Equal(TypeToSearch.Kind.Ignore, Key(TypeToSearch.Key.Backspace, "").Kind);
        Assert.Equal(TypeToSearch.Kind.DeleteBackward, Key(TypeToSearch.Key.Backspace, "abc").Kind);
        Assert.Equal(TypeToSearch.Kind.DeleteWord, Key(TypeToSearch.Key.Backspace, "abc", ctrl: true).Kind);
        Assert.Equal(TypeToSearch.Kind.Clear, Key(TypeToSearch.Key.Backspace, "abc", ctrl: true, shift: true).Kind);
    }

    [Fact]
    public void EnterSubmitsOnlyWithAQuery()
    {
        Assert.Equal(TypeToSearch.Kind.Ignore, Key(TypeToSearch.Key.Enter, "").Kind);
        Assert.Equal(TypeToSearch.Kind.Submit, Key(TypeToSearch.Key.Enter, "edg").Kind);
        Assert.Equal(TypeToSearch.Kind.Ignore, Key(TypeToSearch.Key.Enter, "edg", ctrl: true).Kind);
    }

    [Fact]
    public void Apply()
    {
        Assert.Equal("edge", TypeToSearch.Apply(new(TypeToSearch.Kind.Append, "e"), "edg"));
        Assert.Equal("ed", TypeToSearch.Apply(new(TypeToSearch.Kind.DeleteBackward), "edg"));
        Assert.Equal("visual ", TypeToSearch.Apply(new(TypeToSearch.Kind.DeleteWord), "visual studio "));
        Assert.Equal("", TypeToSearch.Apply(new(TypeToSearch.Kind.DeleteWord), "visual"));
        Assert.Equal("", TypeToSearch.Apply(new(TypeToSearch.Kind.Clear), "anything"));
        Assert.Equal("hi", TypeToSearch.Apply(new(TypeToSearch.Kind.DeleteBackward), "hi😀"));
    }
}

public class LayoutJsonTests
{
    [Fact]
    public void LenientReadingKeepsGoodTilesAndFillsDefaults()
    {
        const string json = """
        {
          "groups": [
            { "name": "Mine", "tiles": [
              { "kind": "clock" },
              { "kind": "hologram", "size": "medium" },
              { "kind": "app" },
              { "kind": "app", "appPath": "MSEdge", "size": "wide", "color": "#00a300" }
            ]}
          ]
        }
        """;
        var file = LayoutJson.Parse(json);
        var tiles = file.Groups.Single().Tiles;
        Assert.Equal(new[] { TileKind.Clock, TileKind.App }, tiles.Select(t => t.Kind));
        Assert.Equal(TileKind.Clock.DefaultSize(), tiles[0].Size);
        Assert.Equal(TileSize.Wide, tiles[1].Size);
        Assert.Equal("#00A300", tiles[1].Color);
        Assert.True(file.Settings.KeepOnDesktop);
        Assert.Equal(TileShape.Square, file.Settings.TileShape);
        Assert.Equal(HotKeyPreset.DefaultId, file.Settings.HotKey);
    }

    [Fact]
    public void ReadsAMacTilesLayout()
    {
        // MacTiles writes the same schema; Mac-only values fall back sensibly.
        const string json = """
        { "version": 1,
          "settings": { "background": "#003E00", "tileShape": "round", "hotKey": "opt-space", "showInDock": true, "tileScale": 1.2 },
          "groups": [ { "id": "6F9619FF-8B86-D011-B42D-00CF4FC964FF", "name": "Today", "tiles": [ { "id": "7F9619FF-8B86-D011-B42D-00CF4FC964FF", "kind": "weather", "size": "wide" } ] } ] }
        """;
        var file = LayoutJson.Parse(json);
        Assert.Equal("#003E00", file.Settings.Background);
        Assert.Equal(TileShape.Round, file.Settings.TileShape);
        Assert.Equal(HotKeyPreset.DefaultId, file.Settings.HotKey);
        Assert.Equal(1.2, file.Settings.TileScale);
        Assert.Equal(Guid.Parse("6F9619FF-8B86-D011-B42D-00CF4FC964FF"), file.Groups[0].Id);
    }

    [Fact]
    public void RoundTrip()
    {
        var original = new LayoutFile
        {
            Settings = new AppSettings { Background = "#123456", TileShape = TileShape.Soft, WindowsKey = true, CalendarFeed = "https://example.com/cal.ics", WeatherLat = 41.88, WeatherLon = -87.63, WeatherCity = "Chicago" },
            Groups = { TileGroup.Create("A", new[] { Tile.Create(TileKind.Weather), Tile.App("MSEdge", TileSize.Small) with { Color = "#FF0097" } }) },
        };
        var back = LayoutJson.Parse(LayoutJson.Write(original));
        Assert.Equal(original.Groups[0].Id, back.Groups[0].Id);
        Assert.Equal(original.Groups[0].Tiles, back.Groups[0].Tiles);
        Assert.Equal("#123456", back.Settings.Background);
        Assert.Equal(TileShape.Soft, back.Settings.TileShape);
        Assert.True(back.Settings.WindowsKey);
        Assert.Equal("https://example.com/cal.ics", back.Settings.CalendarFeed);
        Assert.Equal(41.88, back.Settings.WeatherLat);
        Assert.Equal("Chicago", back.Settings.WeatherCity);
    }

    [Fact]
    public void NoGroupsIsAnError() => Assert.ThrowsAny<Exception>(() => LayoutJson.Parse("{\"settings\":{}}"));
}

public class LayoutOpsTests
{
    private static readonly Tile A = Tile.App("a"), B = Tile.App("b"), C = Tile.App("c");

    private static List<TileGroup> Groups(params string[] names) =>
        names.Select(n => TileGroup.Create(n, new[] { Tile.Create(TileKind.Clock) })).ToList();

    private static List<string> Names(IEnumerable<TileGroup> g) => g.Select(x => x.Name).ToList();

    [Fact]
    public void DraggingRightLandsAfterTarget()
    {
        var g = Groups("A", "B", "C", "D");
        Assert.Equal(new[] { "B", "A", "C", "D" }, Names(LayoutOps.MovingGroup(g, g[0].Id, g[1].Id)));
        Assert.Equal(new[] { "B", "C", "D", "A" }, Names(LayoutOps.MovingGroup(g, g[0].Id, g[3].Id)));
    }

    [Fact]
    public void DraggingLeftLandsBeforeTarget()
    {
        var g = Groups("A", "B", "C", "D");
        Assert.Equal(new[] { "A", "D", "B", "C" }, Names(LayoutOps.MovingGroup(g, g[3].Id, g[1].Id)));
    }

    [Fact]
    public void MovingOntoItselfOrUnknownChangesNothing()
    {
        var g = Groups("A", "B");
        Assert.Equal(new[] { "A", "B" }, Names(LayoutOps.MovingGroup(g, g[1].Id, g[1].Id)));
        Assert.Equal(new[] { "A", "B" }, Names(LayoutOps.MovingGroup(g, Guid.NewGuid(), g[1].Id)));
    }

    [Fact]
    public void MovingATileForwardSwapsNeighbours()
    {
        var g = new List<TileGroup> { TileGroup.Create("G", new[] { A, B, C }) };
        var moved = LayoutOps.Move(g, A.Id, new TileDestination.Before(B.Id));
        Assert.Equal(new[] { B.Id, A.Id, C.Id }, moved[0].Tiles.Select(t => t.Id));
        var back = LayoutOps.Move(moved, A.Id, new TileDestination.Before(B.Id));
        Assert.Equal(new[] { A.Id, B.Id, C.Id }, back[0].Tiles.Select(t => t.Id));
    }

    [Fact]
    public void EmptyGroupsArePrunedButOneAlwaysRemains()
    {
        var g = new List<TileGroup> { TileGroup.Create("G", new[] { A }), TileGroup.Create("H", new[] { B }) };
        var moved = LayoutOps.Move(g, A.Id, new TileDestination.EndOf(g[1].Id));
        Assert.Single(moved);
        Assert.Single(LayoutOps.Remove(LayoutOps.Remove(g, A.Id), B.Id));
    }

    [Fact]
    public void NewGroupAndInputIsNotMutated()
    {
        var g = new List<TileGroup> { TileGroup.Create("G", new[] { A, B }) };
        var moved = LayoutOps.Move(g, B.Id, new TileDestination.NewGroup());
        Assert.Equal(2, moved.Count);
        Assert.Equal(2, g[0].Tiles.Count);
    }

    [Fact]
    public void PinIsCaseInsensitiveForPaths()
    {
        var g = new List<TileGroup> { TileGroup.Create("G", new[] { Tile.App(@"C:\Apps\Thing.exe") }) };
        Assert.True(LayoutOps.IsPinned(g, @"c:\apps\thing.EXE"));
    }

    [Fact]
    public void DefaultLayoutOnlyPinsInstalledApps()
    {
        var groups = LayoutOps.DefaultGroups(id => id == LayoutOps.WellKnownApps.Edge);
        var apps = groups.SelectMany(g => g.Tiles).Where(t => t.Kind == TileKind.App).ToList();
        Assert.Single(apps);
        Assert.Equal(LayoutOps.WellKnownApps.Edge, apps[0].AppPath);
        Assert.Contains(groups.SelectMany(g => g.Tiles), t => t.Kind == TileKind.Clock);
    }
}

public class PaletteTests
{
    [Fact]
    public void NormalizeHex()
    {
        Assert.Equal("#25115A", Palette.NormalizeHex("25115a"));
        Assert.Equal("#25115A", Palette.NormalizeHex(" #25115A "));
        Assert.Null(Palette.NormalizeHex("#25115"));
        Assert.Null(Palette.NormalizeHex("purple"));
    }

    [Fact]
    public void TileColorIsStableAndMatchesMacTiles() => Assert.Equal(5, Palette.StableIndex("/Applications/Safari.app", 14));

    [Theory]
    [InlineData("#25115A")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#7F3FBF")]
    [InlineData("#00A300")]
    public void HsvRoundTrip(string hex)
    {
        var (h, s, v) = Palette.HexToHsv(hex);
        Assert.Equal(hex, Palette.HsvToHex(h, s, v));
    }

    [Fact]
    public void IconTint()
    {
        Assert.Null(Palette.IconTint(new byte[16]));                                  // fully transparent
        var red = Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 4).SelectMany(x => x).ToArray();
        var tint = Palette.IconTint(red)!;
        var (r, g, b) = Palette.ToRgb(tint);
        Assert.True(r > g && r > b);
        Assert.InRange(Palette.HexToHsv(tint).V, 0.38, 0.73);
    }

    [Fact]
    public void HotKeyPresetsAreUnique()
    {
        Assert.Equal(HotKeyPreset.All.Count, HotKeyPreset.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal(HotKeyPreset.DefaultId, HotKeyPreset.Find("nonsense").Id);
    }
}

public class FeedTests
{
    [Fact]
    public void Rss()
    {
        var (title, items) = FeedParser.Parse("""
            <rss><channel><title>World</title>
              <item><title>First</title><link>https://example.com/1</link></item>
              <item><title><![CDATA[Second & more]]></title><link>https://example.com/2</link></item>
            </channel></rss>
            """);
        Assert.Equal("World", title);
        Assert.Equal(new[] { "First", "Second & more" }, items.Select(i => i.Title));
        Assert.Equal("https://example.com/1", items[0].Link?.AbsoluteUri);
    }

    [Fact]
    public void Atom()
    {
        var (title, items) = FeedParser.Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Blog</title>
              <entry><title>Post</title><link rel="alternate" href="https://example.com/post"/></entry>
            </feed>
            """);
        Assert.Equal("Blog", title);
        Assert.Equal("https://example.com/post", items.Single().Link?.AbsoluteUri);
    }

    [Fact]
    public void DoctypeIsIgnoredNotFatal()
    {
        var (_, items) = FeedParser.Parse("<!DOCTYPE rss><rss><channel><title>T</title><item><title>X</title></item></channel></rss>");
        Assert.Single(items);
    }
}

public class WeatherTests
{
    [Fact]
    public void IconsAndDescriptions()
    {
        Assert.Equal(WeatherIcon.Clear, WeatherApi.Icon(0));
        Assert.Equal(WeatherIcon.Rain, WeatherApi.Icon(63));
        Assert.Equal(WeatherIcon.Snow, WeatherApi.Icon(86));
        Assert.Equal("Thunderstorms", WeatherApi.Describe(95));
    }

    [Fact]
    public void ParsesForecast()
    {
        var w = WeatherApi.ParseForecast("""
            {"current":{"temperature_2m":20.4,"apparent_temperature":19,"weather_code":2,"is_day":1},
             "daily":{"time":["2026-10-31","2026-11-01"],"weather_code":[2,61],"temperature_2m_max":[22,18],"temperature_2m_min":[12,9]}}
            """);
        Assert.Equal(20.4, w.Temp);
        Assert.True(w.IsDay);
        Assert.Equal(2, w.Daily.Count);
        Assert.Equal(new DateOnly(2026, 11, 1), w.Daily[1].Date);
    }

    [Fact]
    public void ForecastUrlUsesInvariantNumbers()
    {
        var url = WeatherApi.ForecastUrl(41.88, -87.63, true).ToString();
        Assert.Contains("latitude=41.88", url);
        Assert.Contains("temperature_unit=fahrenheit", url);
    }
}

public class IcsTests
{
    private const string Feed = "BEGIN:VCALENDAR\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Stand-up\r\nDTSTART:20261031T150000Z\r\nDTEND:20261031T151500Z\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Long\\, folded\r\n  title\r\nDTSTART;TZID=America/Chicago:20261101T090000\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20261102\r\nEND:VEVENT\r\n" +
        "BEGIN:VEVENT\r\nSUMMARY:Cancelled\r\nSTATUS:CANCELLED\r\nDTSTART:20261101T100000\r\nEND:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    [Fact]
    public void ParsesEventsUnfoldsAndUnescapes()
    {
        var events = IcsParser.Parse(Feed);
        Assert.Equal(3, events.Count);
        Assert.Equal("Long, folded title", events[1].Title);
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0), events[1].Start);
        Assert.True(events[2].AllDay);
    }

    [Fact]
    public void UpcomingSkipsAllDayAndPast()
    {
        var events = IcsParser.Parse(Feed);
        var upcoming = IcsParser.Upcoming(events, new DateTime(2026, 11, 1, 8, 0, 0));
        Assert.Equal(new[] { "Long, folded title" }, upcoming.Select(e => e.Title));
    }
}
