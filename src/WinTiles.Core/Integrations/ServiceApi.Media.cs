namespace WinTiles.Core;

public static partial class ServiceApi
{
    // MARK: - Plex

    private static Dictionary<string, string> PlexHeaders(ServiceClient c) => new()
    {
        ["X-Plex-Token"] = c.Secret,
        ["X-Plex-Client-Identifier"] = "WinTiles",
        ["X-Plex-Product"] = "WinTiles",
    };

    private static Uri? PlexImage(ServiceClient c, string? path) =>
        string.IsNullOrEmpty(path) || c.Integration.BaseUri is not { } b ? null
            : new Uri(Integration.Base(b) + path + "?X-Plex-Token=" + Fmt.Query(c.Secret));

    public static async Task<ServiceSnapshot> PlexNowPlaying(ServiceClient c)
    {
        var j = (await c.Send("/status/sessions", headers: PlexHeaders(c))).Json();
        return ParsePlexSessions(j, p => PlexImage(c, p));
    }

    public static ServiceSnapshot ParsePlexSessions(J j, Func<string?, Uri?>? image = null)
    {
        var s = new ServiceSnapshot();
        var i = 0;
        foreach (var m in j["MediaContainer"]["Metadata"].Array)
        {
            var duration = m.N("duration");
            var offset = m.N("viewOffset");
            var user = m["User"]["title"].Str ?? "";
            var player = m["Player"]["title"].Str ?? m["Player"]["product"].Str ?? "";
            var paused = m["Player"]["state"].Str == "paused";
            s.Items.Add(new ServiceItem(m["sessionKey"].Str ?? (i++).ToString(), PlexTitle(m))
            {
                Subtitle = Join(new[] { user, player }) + (paused ? " · Paused" : ""),
                Progress = duration > 0 ? offset / duration : null,
                Image = image?.Invoke(m["grandparentThumb"].Str ?? m["thumb"].Str),
                IsActive = !paused,
            });
        }
        s.Headline = s.Items.Count.ToString();
        s.Caption = s.Items.Count == 1 ? "stream" : "streams";
        s.EmptyText = "Nothing playing";
        return s;
    }

    public static string PlexTitle(J m)
    {
        var title = m["title"].Str ?? "Untitled";
        switch (m["type"].Str)
        {
            case "episode":
                var show = m["grandparentTitle"].Str ?? "";
                if (m["parentIndex"].Int is { } season && m["index"].Int is { } episode) return $"{show} S{season}E{episode} · {title}";
                return show.Length == 0 ? title : $"{show} · {title}";
            case "season":
                return Join(new[] { m["parentTitle"].Str, title });
            case "track":
                return Join(new[] { m["grandparentTitle"].Str, title }, " — ");
            default:
                return m["year"].Int is { } year ? $"{title} ({year})" : title;
        }
    }

    public static async Task<ServiceSnapshot> PlexRecent(ServiceClient c)
    {
        var j = (await c.Send("/library/recentlyAdded?X-Plex-Container-Start=0&X-Plex-Container-Size=12", headers: PlexHeaders(c))).Json();
        var s = new ServiceSnapshot();
        var i = 0;
        foreach (var m in j["MediaContainer"]["Metadata"].Array)
        {
            var added = m["addedAt"].Num is { } t ? DateTimeOffset.FromUnixTimeSeconds((long)t) : (DateTimeOffset?)null;
            s.Items.Add(new ServiceItem(m["ratingKey"].Str ?? (i++).ToString(), PlexTitle(m))
            {
                Subtitle = added is { } a ? "Added " + Fmt.When(a, withTime: false) : "",
                Image = PlexImage(c, m["parentThumb"].Str ?? m["thumb"].Str),
            });
        }
        s.Headline = s.Items.Count.ToString();
        s.Caption = "recently added";
        s.EmptyText = "Nothing new";
        return s;
    }

    // MARK: - Jellyfin & Emby

    private static string JellyPrefix(ServiceClient c) => c.Integration.Kind == ServiceKind.Emby ? "/emby" : "";

    private static Dictionary<string, string> JellyHeaders(ServiceClient c)
    {
        var h = new Dictionary<string, string> { ["X-Emby-Token"] = c.Secret };
        if (c.Integration.Kind == ServiceKind.Jellyfin) h["Authorization"] = $"MediaBrowser Token=\"{c.Secret}\"";
        return h;
    }

    private static Uri? JellyImage(ServiceClient c, string? id) =>
        id is null || c.Integration.BaseUri is not { } b ? null
            : new Uri(Integration.Base(b) + JellyPrefix(c) + $"/Items/{Fmt.Segment(id)}/Images/Primary?maxHeight=360&api_key=" + Fmt.Query(c.Secret));

    public static async Task<ServiceSnapshot> JellyNowPlaying(ServiceClient c)
    {
        var j = (await c.Send(JellyPrefix(c) + "/Sessions?activeWithinSeconds=960", headers: JellyHeaders(c))).Json();
        return ParseJellySessions(j, id => JellyImage(c, id));
    }

    public static ServiceSnapshot ParseJellySessions(J j, Func<string?, Uri?>? image = null)
    {
        var s = new ServiceSnapshot();
        var i = 0;
        foreach (var session in j.Array.Where(x => x["NowPlayingItem"].Exists))
        {
            var item = session["NowPlayingItem"];
            var runtime = item.N("RunTimeTicks");
            var position = session["PlayState"].N("PositionTicks");
            var paused = session["PlayState"]["IsPaused"].Bool ?? false;
            var user = session["UserName"].Str ?? "";
            var device = session["DeviceName"].Str ?? session["Client"].Str ?? "";
            s.Items.Add(new ServiceItem(session["Id"].Str ?? (i++).ToString(), JellyTitle(item))
            {
                Subtitle = Join(new[] { user, device }) + (paused ? " · Paused" : ""),
                Progress = runtime > 0 ? position / runtime : null,
                Image = image?.Invoke(item["SeriesId"].Str ?? item["Id"].Str),
                IsActive = !paused,
            });
        }
        s.Headline = s.Items.Count.ToString();
        s.Caption = s.Items.Count == 1 ? "stream" : "streams";
        s.EmptyText = "Nothing playing";
        return s;
    }

    public static string JellyTitle(J item)
    {
        var name = item["Name"].Str ?? "Untitled";
        if (item["Type"].Str == "Episode")
        {
            var series = item["SeriesName"].Str ?? "";
            if (item["ParentIndexNumber"].Int is { } season && item["IndexNumber"].Int is { } episode) return $"{series} S{season}E{episode} · {name}";
            return series.Length == 0 ? name : $"{series} · {name}";
        }
        return item["ProductionYear"].Int is { } year ? $"{name} ({year})" : name;
    }

    public static async Task<ServiceSnapshot> JellyRecent(ServiceClient c)
    {
        var path = JellyPrefix(c) + "/Items?SortBy=DateCreated&SortOrder=Descending&Recursive=true&IncludeItemTypes=Movie,Episode&Limit=12&Fields=DateCreated";
        var j = (await c.Send(path, headers: JellyHeaders(c))).Json();
        var s = new ServiceSnapshot();
        var i = 0;
        foreach (var item in j["Items"].Array)
        {
            var added = Fmt.IsoDate(item["DateCreated"].Str);
            s.Items.Add(new ServiceItem(item["Id"].Str ?? (i++).ToString(), JellyTitle(item))
            {
                Subtitle = added is { } a ? "Added " + Fmt.When(a, withTime: false) : "",
                Image = JellyImage(c, item["SeriesId"].Str ?? item["Id"].Str),
            });
        }
        s.Headline = s.Items.Count.ToString();
        s.Caption = "recently added";
        s.EmptyText = "Nothing new";
        return s;
    }

    // MARK: - Sonarr & Radarr

    private static Dictionary<string, string> ArrHeaders(ServiceClient c) => new() { ["X-Api-Key"] = c.Secret };

    private static Uri? Poster(J images)
    {
        var all = images.Array.ToList();
        var pick = all.FirstOrDefault(x => x["coverType"].Str == "poster");
        if (!pick.Exists) pick = all.FirstOrDefault();
        var url = pick["remoteUrl"].Str ?? pick["url"].Str;
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;
    }

    private static async Task<int?> TotalRecords(ServiceClient c, string path)
    {
        try { return (await c.Send(path, headers: ArrHeaders(c))).Json()["totalRecords"].Int; }
        catch (ServiceException) { return null; }
    }

    public static async Task<ServiceSnapshot> Sonarr(ServiceClient c)
    {
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Date, now.Offset);
        var end = start.AddDays(14);
        var path = $"/api/v3/calendar?start={Fmt.Query(Fmt.Iso(start))}&end={Fmt.Query(Fmt.Iso(end))}&includeSeries=true&unmonitored=false";
        var s = ParseSonarrCalendar((await c.Send(path, headers: ArrHeaders(c))).Json(), now);
        if (await TotalRecords(c, "/api/v3/queue?pageSize=1") is { } queue) s.Stats.Add(new("Queue", queue.ToString()));
        if (await TotalRecords(c, "/api/v3/wanted/missing?pageSize=1") is { } missing) s.Stats.Add(new("Missing", missing.ToString()));
        return s;
    }

    public static ServiceSnapshot ParseSonarrCalendar(J j, DateTimeOffset now)
    {
        var s = new ServiceSnapshot();
        var episodes = j.Array.OrderBy(e => Fmt.IsoDate(e["airDateUtc"].Str) ?? DateTimeOffset.MaxValue).ToList();
        var i = 0;
        foreach (var e in episodes.Take(12))
        {
            var code = $"S{e.W("seasonNumber"):00}E{e.W("episodeNumber"):00}";
            var air = Fmt.IsoDate(e["airDateUtc"].Str);
            var title = e["title"].Str is { } t ? " · " + t : "";
            s.Items.Add(new ServiceItem(e["id"].Str ?? (i++).ToString(), e["series"]["title"].Str ?? "Unknown series")
            {
                Subtitle = Join(new[] { air is { } a ? Fmt.When(a, now) : null, code + title }),
                Image = Poster(e["series"]["images"]),
                IsActive = e["hasFile"].Bool ?? false,
            });
        }
        s.Headline = episodes.Count.ToString();
        s.Caption = episodes.Count == 1 ? "episode in 2 weeks" : "episodes in 2 weeks";
        s.EmptyText = "Nothing airing soon";
        return s;
    }

    public static async Task<ServiceSnapshot> Radarr(ServiceClient c)
    {
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Date, now.Offset);
        var end = start.AddDays(30);
        var path = $"/api/v3/calendar?start={Fmt.Query(Fmt.Iso(start))}&end={Fmt.Query(Fmt.Iso(end))}&unmonitored=false";
        var s = ParseRadarrCalendar((await c.Send(path, headers: ArrHeaders(c))).Json(), start, now);
        if (await TotalRecords(c, "/api/v3/queue?pageSize=1") is { } queue) s.Stats.Add(new("Queue", queue.ToString()));
        if (await TotalRecords(c, "/api/v3/wanted/missing?pageSize=1") is { } missing) s.Stats.Add(new("Missing", missing.ToString()));
        return s;
    }

    /// <summary>A movie's next release in the window: in cinemas, digital or physical.</summary>
    public static (DateTimeOffset Date, string Kind)? RadarrRelease(J m, DateTimeOffset start)
    {
        (string Key, string Label)[] releases = { ("inCinemas", "In cinemas"), ("digitalRelease", "Digital"), ("physicalRelease", "Physical") };
        (DateTimeOffset Date, string Kind)? best = null;
        foreach (var (key, label) in releases)
            if (Fmt.IsoDate(m[key].Str) is { } d && d >= start && (best is null || d < best.Value.Date)) best = (d, label);
        return best;
    }

    public static ServiceSnapshot ParseRadarrCalendar(J j, DateTimeOffset start, DateTimeOffset now)
    {
        var s = new ServiceSnapshot();
        var movies = j.Array
            .Select(m => (Movie: m, Release: RadarrRelease(m, start)))
            .Where(x => x.Release is not null)
            .OrderBy(x => x.Release!.Value.Date)
            .ToList();
        var i = 0;
        foreach (var (m, release) in movies.Take(12))
        {
            var title = m["title"].Str ?? "Untitled";
            if (m["year"].Int is { } year) title += $" ({year})";
            s.Items.Add(new ServiceItem(m["id"].Str ?? (i++).ToString(), title)
            {
                Subtitle = $"{release!.Value.Kind} · {Fmt.When(release.Value.Date, now, withTime: false)}",
                Image = Poster(m["images"]),
                IsActive = m["hasFile"].Bool ?? false,
            });
        }
        s.Headline = movies.Count.ToString();
        s.Caption = movies.Count == 1 ? "release this month" : "releases this month";
        s.EmptyText = "No releases soon";
        return s;
    }

    // MARK: - Overseerr / Jellyseerr

    public static async Task<ServiceSnapshot> Overseerr(ServiceClient c)
    {
        var count = (await c.Send("/api/v1/request/count", headers: ArrHeaders(c))).Json();
        var list = (await c.Send("/api/v1/request?take=8&skip=0&sort=added&filter=all", headers: ArrHeaders(c))).Json();
        var s = ParseOverseerrCount(count);
        var i = 0;
        foreach (var r in list["results"].Array.Take(8))
        {
            var type = r["type"].Str ?? r["media"]["mediaType"].Str ?? "movie";
            var tmdb = r["media"].W("tmdbId");
            var info = await OverseerrTitle(c, type, tmdb);
            s.Items.Add(new ServiceItem(r["id"].Str ?? (i++).ToString(), info.Title)
            {
                Subtitle = Join(new[] { OverseerrStatus(r), r["requestedBy"]["displayName"].Str }),
                Image = info.Poster,
                IsActive = r["media"]["status"].Int == 5,
            });
        }
        return s;
    }

    public static ServiceSnapshot ParseOverseerrCount(J j)
    {
        var pending = j.W("pending");
        return new ServiceSnapshot
        {
            Headline = pending.ToString(),
            Caption = pending == 1 ? "pending request" : "pending requests",
            Stats =
            {
                new("Approved", j.W("approved").ToString()),
                new("Processing", j.W("processing").ToString()),
                new("Available", j.W("available").ToString()),
                new("Total", j.W("total").ToString()),
            },
            EmptyText = "No requests yet",
        };
    }

    public static string OverseerrStatus(J r)
    {
        if (r["media"]["status"].Int == 5) return "Available";
        return r["status"].Int switch
        {
            1 => "Pending",
            2 => r["media"]["status"].Int == 3 ? "Processing" : "Approved",
            3 => "Declined",
            4 => "Failed",
            _ => "",
        };
    }

    private static async Task<(string Title, Uri? Poster)> OverseerrTitle(ServiceClient c, string type, int tmdb)
    {
        var key = $"tmdb.{type}.{tmdb}";
        if (c.Value(key) is { } cached)
        {
            var parts = cached.Split('\n');
            return (parts[0], parts.Length > 1 && Uri.TryCreate(parts[1], UriKind.Absolute, out var u) ? u : null);
        }
        var fallback = type == "tv" ? "TV show" : "Movie";
        if (tmdb <= 0) return (fallback, null);
        J j;
        try { j = (await c.Send(type == "tv" ? $"/api/v1/tv/{tmdb}" : $"/api/v1/movie/{tmdb}", headers: ArrHeaders(c))).Json(); }
        catch (ServiceException) { return (fallback, null); }
        var title = j["title"].Str ?? j["name"].Str ?? "Untitled";
        var date = j["releaseDate"].Str ?? j["firstAirDate"].Str;
        if (date is { Length: >= 4 }) title += $" ({date[..4]})";
        var poster = j["posterPath"].Str is { } p ? "https://image.tmdb.org/t/p/w300" + p : null;
        c.Set(key, title + "\n" + (poster ?? ""));
        return (title, poster is null ? null : new Uri(poster));
    }
}
