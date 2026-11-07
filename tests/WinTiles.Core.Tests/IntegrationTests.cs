using WinTiles.Core;

namespace WinTiles.Core.Tests;

public class LayoutCompatibilityTests
{
    [Fact]
    public void NewSettingsAndTilesRoundTrip()
    {
        var file = new LayoutFile();
        file.Settings.BackgroundStyle = BackgroundStyle.Live;
        file.Settings.LiveWallpaper = LiveWallpaper.Bokeh;
        file.Settings.WallpaperDim = 0.4;
        file.Settings.TileFinish = TileFinish.Glass;
        file.Settings.GlassTint = 0.6;
        file.Settings.TileShadow = TileShadow.Medium;
        file.Settings.ShowRunningIndicator = false;
        file.Settings.HotCornerToggles = true;
        var pihole = Integration.Create(ServiceKind.Pihole);
        pihole.Url = "http://pi.hole";
        file.Settings.Integrations.Add(pihole);
        file.Settings.Integrations.Add(new Integration { Kind = ServiceKind.HomeAssistant, Name = "Home", Url = "http://ha:8123" });
        var web = Tile.WebLink("https://github.com/", "Code");
        var service = Tile.ForService(pihole.Id, ServiceWidget.DnsSummary);
        var entity = Tile.ForService(file.Settings.Integrations[1].Id, ServiceWidget.Entity, "light.kitchen");
        file.Groups.Add(TileGroup.Create("Lab", new[] { web, service, entity }));

        var json = LayoutJson.Write(file);
        Assert.Contains("\"integrationID\"", json);
        Assert.Contains("\"homeAssistant\"", json);
        Assert.Contains("\"dnsSummary\"", json);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);

        var back = LayoutJson.Parse(json);
        Assert.Equal(BackgroundStyle.Live, back.Settings.BackgroundStyle);
        Assert.Equal(LiveWallpaper.Bokeh, back.Settings.LiveWallpaper);
        Assert.Equal(TileFinish.Glass, back.Settings.TileFinish);
        Assert.Equal(TileShadow.Medium, back.Settings.TileShadow);
        Assert.False(back.Settings.ShowRunningIndicator);
        Assert.True(back.Settings.HotCornerToggles);
        Assert.Equal(2, back.Settings.Integrations.Count);
        Assert.Equal(ServiceKind.HomeAssistant, back.Settings.Integrations[1].Kind);
        var tiles = back.Groups[0].Tiles;
        Assert.Equal(web, tiles[0]);
        Assert.Equal(service, tiles[1]);
        Assert.Equal("light.kitchen", tiles[2].Entity);
        Assert.Equal(ServiceWidget.Entity.Sizes(), tiles[2].Sizes);
    }

    [Fact]
    public void ReadsMacTilesLayoutsAndSkipsWhatItCantUse()
    {
        const string json = """
        {"version": 1,
         "settings": {"background": "#25115A", "backgroundStyle": "desktop", "tileFinish": "glass",
                      "integrations": [{"id": "6B29FC40-CA47-1067-B31D-00DD010662DA", "kind": "someFutureThing"},
                                       {"id": "6B29FC40-CA47-1067-B31D-00DD010662DB", "kind": "proxmox", "url": "https://pve:8006", "username": "root@pam!mt"}]},
         "groups": [{"name": "Lab", "tiles": [
            {"kind": "service"},
            {"kind": "web"},
            {"kind": "web", "url": "https://apple.com", "title": "Apple"},
            {"kind": "service", "integrationID": "6B29FC40-CA47-1067-B31D-00DD010662DB", "widget": "resources"},
            {"kind": "clock"}]}]}
        """;
        var file = LayoutJson.Parse(json);
        Assert.Equal(BackgroundStyle.Color, file.Settings.BackgroundStyle);        // "desktop" was removed
        Assert.Equal(TileFinish.Glass, file.Settings.TileFinish);
        var proxmox = Assert.Single(file.Settings.Integrations);
        Assert.True(proxmox.AllowSelfSigned, "Proxmox defaults to allowing its self-signed certificate");
        Assert.Equal(new[] { TileKind.Web, TileKind.Service, TileKind.Clock }, file.Groups[0].Tiles.Select(t => t.Kind));
        Assert.False(TileKindInfo.LiveKinds.Contains(TileKind.Web));
        Assert.False(TileKindInfo.LiveKinds.Contains(TileKind.Service));
    }

    [Fact]
    public void ShadowLevelsMatchMacTiles()
    {
        Assert.Equal((0.0, 0.0, 0.0), TileShadow.None.Parameters());
        Assert.Equal((0.40, 10.0, 5.0), TileShadow.Medium.Parameters());
    }
}

public class WebLinkTests
{
    [Theory]
    [InlineData("github.com", "https://github.com/")]
    [InlineData("https://github.com/x", "https://github.com/x")]
    [InlineData("localhost:3000/docs", "https://localhost:3000/docs")]
    [InlineData("192.168.1.10", "https://192.168.1.10/")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, WebLink.Normalize(input)!.AbsoluteUri);

    [Theory]
    [InlineData("")]
    [InlineData("not a link")]
    [InlineData("hello")]
    public void RejectsNonAddresses(string input) => Assert.Null(WebLink.Normalize(input));

    [Fact]
    public void KeepsOpaqueSchemes() => Assert.Equal("mailto", WebLink.Normalize("mailto:me@example.com")!.Scheme);

    [Fact]
    public void DisplayHostDropsWww() => Assert.Equal("microsoft.com", WebLink.DisplayHost(new Uri("https://www.microsoft.com/en-us")));

    [Fact]
    public void ReadsTitleAndIconsBestFirst()
    {
        const string html = """
        <html><head><title> Example &amp; Co
        </title>
        <link rel="icon" href="/favicon-32.png" sizes="32x32">
        <link rel='apple-touch-icon' href='/touch.png'>
        <link rel="manifest" href="/site.webmanifest">
        <link rel="icon" href="https://cdn.example.com/big.png" sizes="192x192"></head></html>
        """;
        Assert.Equal("Example & Co", WebPage.Title(html));
        var (icons, manifest) = WebPage.IconCandidates(html, new Uri("https://example.com/page"));
        Assert.Equal(new[] { "https://cdn.example.com/big.png", "https://example.com/touch.png", "https://example.com/favicon-32.png" },
                     icons.Select(i => i.Url.AbsoluteUri));
        Assert.Equal("https://example.com/site.webmanifest", manifest!.AbsoluteUri);
    }

    [Fact]
    public void ReadsManifestIconsSkippingMonochrome()
    {
        const string json = """{"icons": [{"src": "a.png", "sizes": "192x192"}, {"src": "b.png", "sizes": "512x512"}, {"src": "m.png", "sizes": "512x512", "purpose": "monochrome"}]}""";
        var icons = WebPage.ManifestIcons(json, new Uri("https://example.com/app/manifest.json"));
        Assert.Equal(new[] { "https://example.com/app/b.png", "https://example.com/app/a.png" }, icons.Select(i => i.Url.AbsoluteUri));
        Assert.Equal(512, WebPage.ParseSizes("any"));
        Assert.Equal(32, WebPage.ParseSizes("16x16 32x32"));
    }
}

public class ServiceModelTests
{
    [Fact]
    public void EveryServiceHasWidgetsAndSensibleFields()
    {
        foreach (var kind in Enum.GetValues<ServiceKind>())
        {
            Assert.NotEmpty(kind.Widgets());
            if (kind.SecretRequired()) Assert.NotNull(kind.SecretLabel());
            if (kind.UsernameRequired()) Assert.NotNull(kind.UsernameLabel());
            Assert.False(string.IsNullOrEmpty(kind.Help()));
        }
        foreach (var widget in Enum.GetValues<ServiceWidget>())
        {
            Assert.Contains(widget.DefaultSize(), widget.Sizes());
            Assert.True(widget.Interval() > 0);
        }
        Assert.Equal(Enum.GetValues<ServiceKind>().Length, Enum.GetValues<ServiceCategory>().Sum(c => ServiceKindInfo.InCategory(c).Count));
    }

    [Theory]
    [InlineData("192.168.1.10:8989/", "http://192.168.1.10:8989/")]
    [InlineData(" https://pve.local:8006// ", "https://pve.local:8006/")]
    [InlineData("tcp://nas.local:2375", "http://nas.local:2375/")]
    public void NormalizesAddresses(string input, string expected) => Assert.Equal(expected, Integration.Normalize(input)!.AbsoluteUri);

    [Fact]
    public void RejectsBadAddresses()
    {
        Assert.Null(Integration.Normalize(""));
        Assert.Null(Integration.Normalize("not an address"));
    }

    [Fact]
    public void WebPagesForPlexAndPihole()
    {
        Assert.Equal("http://plex:32400/web", new Integration { Kind = ServiceKind.Plex, Url = "plex:32400" }.WebUri!.AbsoluteUri);
        Assert.Equal("http://pi.hole/admin", new Integration { Kind = ServiceKind.Pihole, Url = "pi.hole" }.WebUri!.AbsoluteUri);
    }

    [Fact]
    public void SnapshotSignatureChangesWithContent()
    {
        var a = new ServiceSnapshot { Headline = "1" };
        var b = a.Copy();
        Assert.Equal(a.Signature(), b.Signature());
        b.Items.Add(new ServiceItem("x", "Movie"));
        Assert.NotEqual(a.Signature(), b.Signature());
    }

    [Fact]
    public void JsonIsForgiving()
    {
        var j = J.Parse("""{"n": "42", "b": 1, "s": 7, "t": true, "z": null}""");
        Assert.Equal(42, j["n"].Int);
        Assert.True(j["b"].Bool);
        Assert.Equal("7", j["s"].Str);
        Assert.Null(j["t"].Str);
        Assert.False(j["z"].Exists);
        Assert.False(j["missing"]["deeper"].Exists);
        Assert.Empty(j["missing"].Array);
    }

    [Fact]
    public void Formatting()
    {
        Assert.Equal("950", Fmt.Count(950));
        Assert.Equal("1.5k", Fmt.Count(1_500));
        Assert.Equal("25k", Fmt.Count(25_000));
        Assert.Equal("2.5M", Fmt.Count(2_500_000));
        Assert.Equal("1d 1h", Fmt.Duration(90_000));
        Assert.Equal("1h 5m", Fmt.Duration(3_900));
        Assert.Equal("12%", Fmt.Percent(0.123));
        Assert.Equal("1.5 Gbps", Fmt.Speed(1_500));
        Assert.Equal("800 kb/s", Fmt.Bitrate(100_000));
        Assert.Equal("2.0 Mb/s", Fmt.Bitrate(250_000));
        Assert.Equal("48 Mb/s", Fmt.Bitrate(6_000_000));
        Assert.Equal("16 GB", Fmt.Bytes(16.0 * 1024 * 1024 * 1024));
        Assert.Equal("a%20b%26c%3Dd", Fmt.Query("a b&c=d"));
        Assert.NotNull(Fmt.IsoDate("2030-01-02T03:04:05.123Z"));
    }
}

public class ServiceParserTests
{
    private static J Json(string text) => J.Parse(text);

    [Fact]
    public void PiholeV6()
    {
        var s = ServiceApi.ParsePiholeSummary(
            Json("""{"queries": {"total": 25000, "blocked": 3100, "percent_blocked": 12.4}, "clients": {"active": 14}, "gravity": {"domains_being_blocked": 123456}}"""),
            Json("""{"blocking": "disabled", "timer": 120}"""));
        Assert.Equal("12.4%", s.Headline);
        Assert.Equal("25k", s.Stats[0].Value);
        Assert.Equal("14", s.Stats[2].Value);
        Assert.False(s.IsOn);
    }

    [Fact]
    public void AdGuard()
    {
        var s = ServiceApi.ParseAdGuard(
            Json("""{"num_dns_queries": 1000, "num_blocked_filtering": 150, "num_replaced_safebrowsing": 40, "num_replaced_parental": 10, "avg_processing_time": 0.012}"""),
            Json("""{"protection_enabled": true}"""));
        Assert.Equal("20.0%", s.Headline);
        Assert.Equal("12 ms", s.Stats[2].Value);
        Assert.True(s.IsOn);
    }

    [Fact]
    public void PlexSessions()
    {
        var j = Json("""
        {"MediaContainer": {"Metadata": [
          {"sessionKey": "7", "type": "episode", "title": "Pilot", "grandparentTitle": "Severance", "parentIndex": 1, "index": 1,
           "duration": 1000, "viewOffset": 250, "User": {"title": "alex"}, "Player": {"title": "Apple TV", "state": "playing"}, "grandparentThumb": "/thumb/1"},
          {"sessionKey": "8", "type": "movie", "title": "Arrival", "year": 2016, "duration": 0, "Player": {"product": "Plex Web", "state": "paused"}}]}}
        """);
        var s = ServiceApi.ParsePlexSessions(j, p => p is null ? null : new Uri("http://plex" + p));
        Assert.Equal("2", s.Headline);
        Assert.Equal("Severance S1E1 · Pilot", s.Items[0].Title);
        Assert.Equal("alex · Apple TV", s.Items[0].Subtitle);
        Assert.Equal(0.25, s.Items[0].Progress!.Value, 3);
        Assert.Equal("http://plex/thumb/1", s.Items[0].Image!.AbsoluteUri);
        Assert.Equal("Arrival (2016)", s.Items[1].Title);
        Assert.EndsWith("Paused", s.Items[1].Subtitle);
        Assert.Null(s.Items[1].Progress);
    }

    [Fact]
    public void JellyfinSkipsIdleClients()
    {
        var j = Json("""
        [{"Id": "a", "UserName": "sam", "DeviceName": "iPad", "PlayState": {"PositionTicks": 50, "IsPaused": false},
          "NowPlayingItem": {"Id": "i1", "SeriesId": "s1", "Type": "Episode", "Name": "Rain", "SeriesName": "Dark", "ParentIndexNumber": 2, "IndexNumber": 3, "RunTimeTicks": 100}},
         {"Id": "b", "UserName": "idle", "DeviceName": "TV"}]
        """);
        var s = ServiceApi.ParseJellySessions(j);
        Assert.Single(s.Items);
        Assert.Equal("Dark S2E3 · Rain", s.Items[0].Title);
        Assert.Equal("stream", s.Caption);
    }

    [Fact]
    public void SonarrSortedByAirDate()
    {
        var j = Json("""
        [{"id": 2, "seasonNumber": 1, "episodeNumber": 2, "title": "Later", "airDateUtc": "2030-01-05T01:00:00Z", "series": {"title": "B"}},
         {"id": 1, "seasonNumber": 3, "episodeNumber": 10, "title": "Sooner", "airDateUtc": "2030-01-02T01:00:00Z", "hasFile": true,
          "series": {"title": "A", "images": [{"coverType": "banner", "remoteUrl": "https://x/banner.jpg"}, {"coverType": "poster", "remoteUrl": "https://x/poster.jpg"}]}}]
        """);
        var s = ServiceApi.ParseSonarrCalendar(j, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        Assert.Equal(new[] { "A", "B" }, s.Items.Select(i => i.Title));
        Assert.Contains("S03E10 · Sooner", s.Items[0].Subtitle);
        Assert.Equal("https://x/poster.jpg", s.Items[0].Image!.AbsoluteUri);
        Assert.True(s.Items[0].IsActive);
    }

    [Fact]
    public void RadarrPicksNextReleaseInWindow()
    {
        var start = DateTimeOffset.Parse("2030-03-01T00:00:00Z");
        var j = Json("""
        [{"id": 1, "title": "Dune", "year": 2030, "inCinemas": "2030-02-01T00:00:00Z", "digitalRelease": "2030-03-20T00:00:00Z"},
         {"id": 2, "title": "Old", "inCinemas": "2020-01-01T00:00:00Z"}]
        """);
        var s = ServiceApi.ParseRadarrCalendar(j, start, start);
        Assert.Single(s.Items);
        Assert.Equal("Dune (2030)", s.Items[0].Title);
        Assert.StartsWith("Digital", s.Items[0].Subtitle);
    }

    [Fact]
    public void OverseerrCountsAndStatus()
    {
        var s = ServiceApi.ParseOverseerrCount(Json("""{"total": 40, "pending": 3, "approved": 30, "processing": 4, "available": 20}"""));
        Assert.Equal("3", s.Headline);
        Assert.Equal("pending requests", s.Caption);
        Assert.Equal("Processing", ServiceApi.OverseerrStatus(Json("""{"status": 2, "media": {"status": 3}}""")));
        Assert.Equal("Available", ServiceApi.OverseerrStatus(Json("""{"status": 2, "media": {"status": 5}}""")));
    }

    [Fact]
    public void UniFiHealth()
    {
        var j = Json("""
        {"data": [
          {"subsystem": "wlan", "num_user": 20, "num_guest": 2, "num_ap": 3},
          {"subsystem": "wan", "status": "ok", "wan_ip": "203.0.113.5", "num_gw": 1, "gw_system-stats": {"cpu": "12.5", "mem": "40"}},
          {"subsystem": "www", "status": "ok", "latency": 9, "xput_down": 940.2, "xput_up": 35.1},
          {"subsystem": "lan", "num_user": 8, "num_sw": 2}]}
        """);
        var s = ServiceApi.ParseUniFiHealth(j);
        Assert.Equal("30", s.Headline);
        Assert.Equal("6", s.Stats.First(x => x.Label == "Devices").Value);
        Assert.Equal("9 ms", s.Stats.First(x => x.Label == "Latency").Value);
        Assert.Equal(0.125, s.Gauges[0].Fraction, 4);
        Assert.True(s.IsOn);
    }

    [Fact]
    public void OpnsenseTrafficAndGateways()
    {
        var first = Json("""
        {"interfaces": {
          "wan": {"name": "WAN", "device": "igb0", "inbytes": "1000000", "outbytes": "500000"},
          "lan": {"name": "LAN", "device": "igb1", "inbytes": 200000, "outbytes": 900000},
          "lo0": {"name": "Loopback", "device": "lo0", "inbytes": 5, "outbytes": 5}},
         "time": 100.0}
        """);
        var second = Json("""
        {"interfaces": {
          "wan": {"name": "WAN", "inbytes": "7000000", "outbytes": "1000000"},
          "lan": {"name": "LAN", "inbytes": 100, "outbytes": 6900000}},
         "time": 102.0}
        """);
        var a = ServiceApi.ParseOpnsenseCounters(first, 0)!;
        var b = ServiceApi.ParseOpnsenseCounters(second, 0)!;
        Assert.Equal(new[] { "lan", "wan" }, a.Counters.Keys.OrderBy(k => k));
        var rates = ServiceApi.OpnsenseRates(a, b);
        Assert.Equal(new[] { "wan", "lan" }, rates.Select(r => r.Id));
        Assert.Equal(3_000_000, rates[0].Down, 1);
        Assert.Equal(0, rates[1].Down);          // the counter went backwards (reset)

        var gateways = Json("""{"items": [{"name": "WAN_DHCP", "status_translated": "Online", "delay": "9.8 ms", "loss": "0.0 %"}, {"name": "WAN2", "status_translated": "Offline", "delay": "~", "loss": "~"}]}""");
        var s = ServiceApi.ParseOpnsense(default, default, default, default, gateways, rates, new List<double> { 1_000_000, 3_000_000 });
        Assert.Equal("24 Mb/s", s.Headline);
        Assert.Equal("↓ WAN · ↑ 2.0 Mb/s", s.Caption);
        Assert.Equal(new[] { 1.0 / 3, 1.0 }, s.Series);
        Assert.False(s.IsOn);
        Assert.Equal("Online · 9.8 ms · 0.0 % loss", s.Items[0].Subtitle);
        Assert.Equal("Offline", s.Items[1].Subtitle);
    }

    [Fact]
    public void ProxmoxCountsSharedStorageOnce()
    {
        var j = Json("""
        {"data": [
          {"type": "node", "status": "online", "cpu": 0.5, "maxcpu": 8, "mem": 8, "maxmem": 32, "uptime": 90000},
          {"type": "node", "status": "online", "cpu": 0.25, "maxcpu": 8, "mem": 8, "maxmem": 32, "uptime": 1000},
          {"type": "qemu", "id": "qemu/100", "vmid": 100, "name": "web", "status": "running", "cpu": 0.1},
          {"type": "lxc", "id": "lxc/101", "vmid": 101, "name": "dns", "status": "running", "cpu": 0.01},
          {"type": "qemu", "id": "qemu/102", "vmid": 102, "name": "spare", "status": "stopped"},
          {"type": "storage", "id": "storage/pve1/nfs", "storage": "nfs", "shared": 1, "status": "available", "disk": 50, "maxdisk": 100},
          {"type": "storage", "id": "storage/pve2/nfs", "storage": "nfs", "shared": 1, "status": "available", "disk": 50, "maxdisk": 100},
          {"type": "storage", "id": "storage/pve1/local", "storage": "local", "shared": 0, "status": "available", "disk": 10, "maxdisk": 100}]}
        """);
        var s = ServiceApi.ParseProxmox(j);
        Assert.Equal("2/3", s.Headline);
        Assert.Equal(0.375, s.Gauges[0].Fraction, 4);
        Assert.Equal(0.3, s.Gauges[2].Fraction, 4);
        Assert.Equal(new[] { "dns", "web", "spare" }, s.Items.Select(i => i.Title));
    }

    [Fact]
    public void TrueNasAndUnraid()
    {
        var t = ServiceApi.ParseTrueNas(
            Json("""{"hostname": "nas", "version": "TrueNAS-SCALE-24.10.2", "uptime_seconds": 200000, "loadavg": [2.0, 1.0, 0.5], "cores": 8}"""),
            Json("""[{"name": "tank", "status": "ONLINE", "healthy": true, "size": 1000, "allocated": 600}, {"name": "fast", "status": "DEGRADED", "healthy": false, "size": 100, "allocated": 10}]"""));
        Assert.Equal("Degraded", t.Headline);
        Assert.Equal(0.25, t.Gauges[0].Fraction, 4);
        Assert.False(t.IsOn);

        var u = ServiceApi.ParseUnraid(
            Json("""{"info": {"os": {"hostname": "tower"}, "cpu": {"brand": "Ryzen 7"}}}"""),
            Json("""{"array": {"state": "STARTED", "capacity": {"kilobytes": {"free": "750", "used": "250", "total": "1000"}}}}"""),
            Json("""{"metrics": {"cpu": {"percentTotal": 12.0}, "memory": {"percentTotal": 50.0}}}"""),
            Json("""[{"id": "1", "names": ["/plex"], "state": "RUNNING"}, {"id": "2", "names": ["/old"], "state": "EXITED"}]"""));
        Assert.Equal("Started", u.Headline);
        Assert.Equal(new[] { "CPU", "Memory", "Array" }, u.Gauges.Select(g => g.Label));
        Assert.Equal("1/2 running", u.Stats[0].Value);
    }

    [Fact]
    public void GlancesAndDocker()
    {
        var g = ServiceApi.ParseGlances(Json("""{"total": 23.7}"""), Json("""{"percent": 61.0, "total": 1000, "used": 610}"""),
            Json("""[{"mnt_point": "/boot", "percent": 30, "size": 500, "used": 150}, {"mnt_point": "/", "percent": 45, "size": 1000, "used": 450}]"""),
            Json("""{"min1": 0.5, "min5": 0.4, "min15": 0.3}"""), Json("\"3 days, 4:05:06\""), Json("""{"hostname": "box"}"""));
        Assert.Equal("24%", g.Headline);
        Assert.Equal(new[] { "CPU", "Memory", "Disk /" }, g.Gauges.Select(x => x.Label));

        var d = ServiceApi.ParseContainers(Json("""[{"Id": "b", "Names": ["/zeta"], "State": "running", "Status": "Up 2 hours"}, {"Id": "a", "Names": ["/alpha"], "State": "exited"}, {"Id": "c", "Names": ["/beta"], "State": "running"}]"""),
            "Id", "Names", "State", "Status");
        Assert.Equal(new[] { "beta", "zeta", "alpha" }, d.Items.Select(i => i.Title));
        Assert.Equal("of 3 running", d.Caption);
    }

    [Fact]
    public void BeszelFleetAndSingleSystem()
    {
        var j = Json("""
        {"items": [
          {"id": "a1", "name": "nas", "status": "up", "info": {"cpu": 10, "mp": 40, "dp": 70, "dt": 51.4, "b": 1.5, "la": [0.5, 0.4, 0.3], "g": 20}},
          {"id": "b2", "name": "pi", "status": "up", "info": {"cpu": 30, "mp": 60, "dp": 30}},
          {"id": "c3", "name": "old", "status": "down", "info": {}},
          {"id": "d4", "name": "lab", "status": "paused", "info": {}}]}
        """);
        var fleet = ServiceApi.ParseBeszel(j, null);
        Assert.Equal("2/3", fleet.Headline);
        Assert.Equal(0.2, fleet.Gauges[0].Fraction, 4);
        Assert.Equal("pi · 30%", fleet.Stats[0].Value);
        Assert.Equal("lab", fleet.Items[0].Title);
        var nas = ServiceApi.ParseBeszel(j, "a1");
        Assert.Equal("10%", nas.Headline);
        Assert.Equal("nas", nas.Name);
        Assert.Equal(new[] { "CPU", "Memory", "Disk", "GPU" }, nas.Gauges.Select(x => x.Label));
        Assert.Equal("13 Mb/s", nas.Stats.First(x => x.Label == "Network").Value);
        Assert.Equal("Down", ServiceApi.ParseBeszel(j, "c3").Headline);
    }

    [Fact]
    public void ComfyIdleRunningAndHistory()
    {
        var stats = Json("""
        {"system": {"ram_total": 68719476736, "ram_free": 51539607552, "comfyui_version": "0.3.60"},
         "devices": [{"name": "cuda:0 NVIDIA GeForce RTX 4090 : cudaMallocAsync", "vram_total": 25769803776, "vram_free": 6442450944}]}
        """);
        var idle = ServiceApi.ParseComfy(stats, Json("""{"queue_running": [], "queue_pending": []}"""), new ComfyMonitor.Live(), new());
        Assert.Equal("Idle", idle.Headline);
        Assert.Equal(new[] { "VRAM", "RAM" }, idle.Gauges.Select(g => g.Label));
        Assert.Equal(0.75, idle.Gauges[0].Fraction, 4);
        Assert.Equal("NVIDIA GeForce RTX 4090", idle.Stats.First(s => s.Label == "Device").Value);

        var live = new ComfyMonitor.Live { PromptId = "p1", Value = 5, Max = 20, Gpu = 97, GpuTemperature = 71, Cpu = 12 };
        var busy = ServiceApi.ParseComfy(stats, Json("""{"queue_running": [[3, "p1", {}, {}, []]], "queue_pending": [[4, "p2"], [5, "p3"]]}"""),
                                         live, new() { new ServiceItem("old", "Image") });
        Assert.Equal("25%", busy.Headline);
        Assert.Equal("step 5/20 · 2 queued", busy.Caption);
        Assert.True(busy.Busy);
        Assert.Equal(new[] { "GPU", "VRAM", "RAM", "CPU" }, busy.Gauges.Select(g => g.Label));
        Assert.Equal(new[] { "p1", "old" }, busy.Items.Select(i => i.Id));

        var history = ServiceApi.ParseComfyHistory(Json("""
        {"aaa": {"prompt": [1, "aaa"], "outputs": {"9": {"images": [{"filename": "ComfyUI_0001_.png", "subfolder": "", "type": "output"}]}},
                 "status": {"status_str": "success", "messages": [["execution_start", {"timestamp": 1000000}], ["execution_success", {"timestamp": 1012000}]]}},
         "bbb": {"prompt": [2, "bbb"], "outputs": {},
                 "status": {"status_str": "error", "messages": [["execution_start", {"timestamp": 2000000}], ["execution_error", {"timestamp": 2001000}]]}}}
        """), new Uri("http://gpu:8188"), DateTimeOffset.FromUnixTimeSeconds(2000));
        Assert.Equal(new[] { "bbb", "aaa" }, history.Select(i => i.Id));
        Assert.Equal("Failed run", history[0].Title);
        Assert.Equal("http://gpu:8188/view?filename=ComfyUI_0001_.png&subfolder=&type=output", history[1].Image!.AbsoluteUri);
        Assert.EndsWith("12s", history[1].Subtitle);
    }

    [Fact]
    public void ComfyLiveMessages()
    {
        var live = new ComfyMonitor.Live();
        var now = DateTimeOffset.UtcNow;
        live = ComfyMonitor.Apply("execution_start", Json("""{"prompt_id": "p1"}"""), live, now);
        live = ComfyMonitor.Apply("progress", Json("""{"value": 7, "max": 30, "prompt_id": "p1"}"""), live, now);
        Assert.Equal("p1", live.PromptId);
        Assert.Equal(7, live.Value);
        live = ComfyMonitor.Apply("executing", Json("""{"node": null, "prompt_id": "p1"}"""), live, now);
        Assert.Null(live.PromptId);
        live = ComfyMonitor.Apply("crystools.monitor", Json("""{"cpu_utilization": 15.5, "gpus": [{"gpu_utilization": 88, "gpu_temperature": 66}]}"""), live, now);
        Assert.Equal(88, live.Gpu);
        Assert.Equal(now, live.MonitorAt);
    }

    [Fact]
    public void HomeAssistantEntities()
    {
        var light = HomeAssistant.Parse(Json("""{"entity_id": "light.kitchen", "state": "on", "attributes": {"friendly_name": "Kitchen", "brightness": 128}}"""));
        Assert.Equal("On", light.Headline);
        Assert.Equal("Kitchen", light.Caption);
        Assert.True(light.IsOn);
        Assert.Equal(128.0 / 255, light.Gauges[0].Fraction, 3);

        var temp = HomeAssistant.Parse(Json("""{"entity_id": "sensor.office", "state": "21.46", "attributes": {"unit_of_measurement": "°C", "device_class": "temperature"}}"""));
        Assert.Equal("21°C", temp.Headline);
        Assert.Null(temp.IsOn);

        Assert.Null(HomeAssistant.Parse(Json("""{"entity_id": "script.movie", "state": "off", "attributes": {}}""")).IsOn);
        Assert.Equal("toggle", HomeAssistant.Service("light")!.Value.Name);
        Assert.Equal("unlock", HomeAssistant.Service("lock", "locked")!.Value.Name);
        Assert.Null(HomeAssistant.Service("sensor"));
    }
}
