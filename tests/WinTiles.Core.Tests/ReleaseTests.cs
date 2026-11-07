using WinTiles.Core;

namespace WinTiles.Core.Tests;

// Ported from MacTiles 1.12 (hardening) and 1.13 (several News tiles, weather by ZIP code).

public class HardeningTests
{
    [Theory]
    [InlineData("localhost")] [InlineData("nas")] [InlineData("192.168.1.10")] [InlineData("10.0.0.5")]
    [InlineData("172.20.1.1")] [InlineData("127.0.0.1")] [InlineData("100.101.102.103")] [InlineData("169.254.3.4")]
    [InlineData("plex.local")] [InlineData("pihole.lan")] [InlineData("router.home.arpa")] [InlineData("box.internal")]
    [InlineData("pc.tail1234.ts.net")] [InlineData("fd12:3456::1")] [InlineData("::1")] [InlineData("fe80::1")]
    public void LocalHostsAreRecognised(string host) => Assert.True(Integration.IsLocalHost(host));

    [Theory]
    [InlineData("example.com")] [InlineData("8.8.8.8")] [InlineData("172.32.0.1")] [InlineData("100.128.0.1")]
    [InlineData("192.169.0.1")] [InlineData("2001:db8::1")] [InlineData("plex.mydomain.net")]
    public void InternetHostsAreNotLocal(string host) => Assert.False(Integration.IsLocalHost(host));

    [Fact]
    public void PlainHttpToTheInternetIsFlagged()
    {
        Integration With(string url) => new() { Kind = ServiceKind.Sonarr, Url = url };
        Assert.True(With("http://sonarr.example.com").SendsSecretInsecurely);
        Assert.True(With("sonarr.example.com:8989").SendsSecretInsecurely);
        Assert.False(With("https://sonarr.example.com").SendsSecretInsecurely);
        Assert.False(With("http://192.168.1.10:8989").SendsSecretInsecurely);
        Assert.False(With("nas.lan:8989").SendsSecretInsecurely);
    }

    [Fact]
    public void SelfSignedTrustIsLimitedToTheServicesOwnHost()
    {
        var i = new Integration { Kind = ServiceKind.Proxmox, Url = "https://pve.lan:8006", AllowSelfSigned = true };
        Assert.True(ServiceClient.TrustsSelfSigned(i, "pve.lan"));
        Assert.True(ServiceClient.TrustsSelfSigned(i, "PVE.lan"));
        Assert.False(ServiceClient.TrustsSelfSigned(i, "image.tmdb.org"));
        i.AllowSelfSigned = false;
        Assert.False(ServiceClient.TrustsSelfSigned(i, "pve.lan"));
    }

    [Fact]
    public void RedirectToAnotherHostDropsCredentials()
    {
        var away = ServiceClient.Redirect(new Uri("http://sonarr.lan:8989/api/v3/queue"), new Uri("https://elsewhere.example/x"), "GET", 302);
        Assert.False(away.KeepHeaders);
        var same = ServiceClient.Redirect(new Uri("http://sonarr.lan/api"), new Uri("https://sonarr.lan/api"), "GET", 301);
        Assert.True(same.KeepHeaders);
        var post = ServiceClient.Redirect(new Uri("http://ha.lan/a"), new Uri("http://ha.lan/b"), "POST", 302);
        Assert.Equal("GET", post.Method);
        var kept = ServiceClient.Redirect(new Uri("http://ha.lan/a"), new Uri("http://ha.lan/b"), "POST", 307);
        Assert.Equal("POST", kept.Method);
        Assert.True(kept.KeepBody);
    }

    [Fact]
    public void PathSegmentsCantChangeTheRequest()
    {
        Assert.Equal("light.kitchen", Fmt.Segment("light.kitchen"));
        var escaped = Fmt.Segment("../config?x=1#y");
        Assert.DoesNotContain("/", escaped);
        Assert.DoesNotContain("?", escaped);
        Assert.DoesNotContain("#", escaped);
        Assert.Equal("a%20b", Fmt.Segment("a b"));
    }

    [Fact]
    public void DockerRequestsAreChecked()
    {
        Assert.True(ServiceClient.IsSafeDockerRequest("GET", "/containers/json?all=1"));
        Assert.True(ServiceClient.IsSafeDockerRequest("POST", "/containers/abc/restart"));
        Assert.False(ServiceClient.IsSafeDockerRequest("DELETE", "/containers/abc"));
        Assert.False(ServiceClient.IsSafeDockerRequest("GET", "/x HTTP/1.0\r\nHost: y"));
        Assert.False(ServiceClient.IsSafeDockerRequest("GET", "containers"));
    }

    [Theory]
    [InlineData("https://www.bbc.co.uk/news/1", true)]
    [InlineData("  http://example.com/a  ", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void NewsLinksMustBeWebAddresses(string link, bool kept) => Assert.Equal(kept, FeedParser.SafeLink(link) is not null);

    [Fact]
    public void HandEditedSettingsAreClampedToTheSliders()
    {
        var high = LayoutJson.Parse("""{"settings": {"tileScale": 9, "wallpaperDim": 1}, "groups": []}""").Settings;
        Assert.Equal(1.5, high.TileScale);
        Assert.Equal(0.7, high.WallpaperDim);
        Assert.Equal(0.8, LayoutJson.Parse("""{"settings": {"tileScale": 0}, "groups": []}""").Settings.TileScale);
    }
}

public class NewsTilesTests
{
    [Fact]
    public void EachNewsTileCanHaveItsOwnFeed()
    {
        const string def = "https://feeds.example.com/world.xml";
        var plain = Tile.Create(TileKind.News);
        var tech = Tile.Create(TileKind.News) with { Url = "https://example.com/tech.xml", Title = "Tech" };
        var techAgain = Tile.Create(TileKind.News) with { Url = "https://example.com/tech.xml" };
        var groups = new List<TileGroup>
        {
            new(Guid.NewGuid(), "", new[] { plain, tech, Tile.Create(TileKind.Clock) }),
            new(Guid.NewGuid(), "", new[] { techAgain }),
        };
        Assert.Equal(def, LayoutOps.NewsFeedOf(plain, def));
        Assert.Equal("https://example.com/tech.xml", LayoutOps.NewsFeedOf(tech, def));
        Assert.Equal(new[] { def, "https://example.com/tech.xml" }, LayoutOps.NewsFeeds(groups, def));
    }

    [Fact]
    public void NewsTileFeedRoundTrips()
    {
        var file = new LayoutFile();
        file.Groups.Add(new TileGroup(Guid.NewGuid(), "", new[] { Tile.Create(TileKind.News) with { Url = "https://example.com/tech.xml", Title = "Tech" } }));
        var back = LayoutJson.Parse(LayoutJson.Write(file)).Groups[0].Tiles[0];
        Assert.Equal("https://example.com/tech.xml", back.Url);
        Assert.Equal("Tech", back.Title);
        var old = LayoutJson.Parse("""{"groups": [{"tiles": [{"kind": "news", "size": "wide"}]}]}""").Groups[0].Tiles[0];
        Assert.Null(old.Url);
    }

    [Fact]
    public void FeedDiscoveryFromAWebPage()
    {
        const string html = """
            <html><head>
            <link rel="stylesheet" href="/style.css">
            <link rel="alternate" type="application/rss+xml" title="All" href="/rss/index.xml">
            <link rel='alternate' type='application/atom+xml' href='https://example.com/atom'>
            <link rel="alternate" hreflang="fr" href="/fr/">
            <link rel="alternate" type="application/rss+xml" href="file:///C:/secret.xml">
            </head></html>
            """;
        var feeds = FeedParser.DiscoverFeeds(html, new Uri("https://www.example.com/news/"));
        Assert.Equal(new[] { "https://www.example.com/rss/index.xml", "https://example.com/atom" }, feeds.Select(f => f.AbsoluteUri));
    }

    [Fact]
    public void NonFeedsGiveNoHeadlines() => Assert.Empty(FeedParser.TryParse("<html><body>Not a feed</body></html>").Items);

    [Fact]
    public void PopularFeedsAreWebAddresses()
    {
        Assert.NotEmpty(NewsFeeds.Popular);
        foreach (var (name, url) in NewsFeeds.Popular)
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var u) && WebLink.IsFetchable(u), name);
        Assert.Equal(NewsFeeds.DefaultFeed, new AppSettings().NewsFeed);
    }
}

public class PostalCodeTests
{
    [Theory]
    [InlineData("60601", true)] [InlineData("60601-1234", true)] [InlineData("SW1A 1AA", true)] [InlineData("10115", true)]
    [InlineData("M5V 3L9", true)] [InlineData("Lisbon", false)] [InlineData("New York", false)] [InlineData("", false)]
    [InlineData("12", false)] [InlineData("Paris 75008 France long", false)]
    public void WhatLooksLikeAPostalCode(string text, bool expected) => Assert.Equal(expected, WeatherApi.LooksLikePostalCode(text));

    [Fact]
    public void Normalizing()
    {
        Assert.Equal("60601", WeatherApi.NormalizePostalCode(" 60601-1234 ", "US"));
        Assert.Equal("SW1A 1AA", WeatherApi.NormalizePostalCode("sw1a   1aa", "GB"));
        Assert.Equal("10115", WeatherApi.NormalizePostalCode("10115", null));
    }

    [Fact]
    public void PicksThePlaceThatListsTheCode()
    {
        const string json = """
            {"results": [
              {"name": "Elsewhere", "latitude": 1, "longitude": 2, "country": "United States", "postcodes": ["60999"]},
              {"name": "Chicago", "latitude": 41.88, "longitude": -87.62, "admin1": "Illinois", "country": "United States",
               "postcodes": ["60601", "60602"]}
            ]}
            """;
        var place = WeatherApi.ParsePostalCode(json, "60601");
        Assert.Equal("Chicago", place?.Name);
        Assert.Equal("Chicago, Illinois, United States", place?.Label);
        Assert.Equal("London", WeatherApi.ParsePostalCode("""{"results": [{"name": "London", "latitude": 51.5, "longitude": -0.12, "postcodes": ["SW1A1AA"]}]}""", "SW1A 1AA")?.Name);
        Assert.Null(WeatherApi.ParsePostalCode("{}", "00000"));
    }

    [Fact]
    public void PostalCodeUrlIncludesTheCountry()
    {
        var url = WeatherApi.PostalCodeUrl("60601", "us").AbsoluteUri;
        Assert.Contains("name=60601", url);
        Assert.Contains("countryCode=US", url);
    }

    [Fact]
    public void PostalCodeIsSaved()
    {
        var file = new LayoutFile();
        file.Settings.WeatherPostalCode = "60601";
        Assert.Equal("60601", LayoutJson.Parse(LayoutJson.Write(file)).Settings.WeatherPostalCode);
    }
}
