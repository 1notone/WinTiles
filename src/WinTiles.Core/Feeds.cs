using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace WinTiles.Core;

// MARK: - News (any RSS 2.0 or Atom feed)

public sealed record Headline(string Title, Uri? Link);

public sealed record FeedResult(string Title, IReadOnlyList<Headline> Items, string FeedUrl);

public static class FeedParser
{
    /// <summary>Like <see cref="Parse"/>, but returns no headlines instead of throwing for something that isn't a feed.</summary>
    public static (string Title, IReadOnlyList<Headline> Items) TryParse(string text, int limit = 15)
    {
        try { return Parse(text, limit); }
        catch { return ("", Array.Empty<Headline>()); }
    }

    /// <summary>
    /// Clicking the News tile opens the headline, so only web links are kept: a feed can't
    /// make the app open a local file or launch a program through a custom link type.
    /// </summary>
    public static Uri? SafeLink(string? link) =>
        Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var u) && WebLink.IsFetchable(u) && !string.IsNullOrEmpty(u.Host) ? u : null;

    /// <summary>Feeds a web page advertises: &lt;link rel="alternate" type="application/rss+xml" href="…"&gt;.</summary>
    public static IReadOnlyList<Uri> DiscoverFeeds(string html, Uri baseUri)
    {
        var found = new List<Uri>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"<link\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var attrs = WebPage.Attributes(m.Value);
            if (!attrs.TryGetValue("rel", out var rel) || !rel.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate")) continue;
            if (!attrs.TryGetValue("type", out var type) || !(type.Contains("rss", StringComparison.OrdinalIgnoreCase) || type.Contains("atom", StringComparison.OrdinalIgnoreCase))) continue;
            if (!attrs.TryGetValue("href", out var href) || !Uri.TryCreate(baseUri, href, out var url) || !WebLink.IsFetchable(url)) continue;
            if (!found.Contains(url)) found.Add(url);
        }
        return found;
    }

    public static (string Title, IReadOnlyList<Headline> Items) Parse(string xml, int limit = 15)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new FormatException("empty feed");

        static bool IsEntry(XElement e) => e.Name.LocalName is "item" or "entry";

        var channelTitle = root.Descendants()
            .Where(e => e.Name.LocalName == "title" && !e.Ancestors().Any(IsEntry))
            .Select(e => e.Value.Trim())
            .FirstOrDefault() ?? "";

        var items = new List<Headline>();
        foreach (var entry in root.Descendants().Where(IsEntry))
        {
            var title = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
            if (string.IsNullOrEmpty(title)) continue;
            string? link = null;
            foreach (var l in entry.Elements().Where(e => e.Name.LocalName == "link"))
            {
                var href = l.Attribute("href")?.Value;                       // Atom
                var rel = l.Attribute("rel")?.Value;
                if (!string.IsNullOrWhiteSpace(href) && (rel is null || rel == "alternate")) { link = href; break; }
                if (!string.IsNullOrWhiteSpace(l.Value)) { link = l.Value.Trim(); break; }   // RSS
            }
            items.Add(new Headline(title, SafeLink(link)));
            if (items.Count >= limit) break;
        }
        return (channelTitle, items);
    }
}

/// <summary>Feeds offered in the News tile panel's "Popular feeds" menu.</summary>
public static class NewsFeeds
{
    public const string DefaultFeed = "https://feeds.bbci.co.uk/news/world/rss.xml";

    public static readonly IReadOnlyList<(string Name, string Url)> Popular = new[]
    {
        ("BBC News – World", DefaultFeed),
        ("BBC News – Technology", "https://feeds.bbci.co.uk/news/technology/rss.xml"),
        ("NPR News", "https://feeds.npr.org/1001/rss.xml"),
        ("The New York Times", "https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml"),
        ("The Verge", "https://www.theverge.com/rss/index.xml"),
        ("Ars Technica", "https://feeds.arstechnica.com/arstechnica/index"),
        ("Hacker News", "https://hnrss.org/frontpage"),
    };
}

// MARK: - Weather (Open-Meteo: free, no API key, no account)

public enum WeatherIcon { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Showers, Thunder }

public sealed record WeatherDay(DateOnly Date, int Code, double Hi, double Lo);

public sealed record WeatherNow(double Temp, double FeelsLike, int Code, bool IsDay, IReadOnlyList<WeatherDay> Daily);

public sealed record Place(string Name, double Latitude, double Longitude, string? Admin1, string? Country)
{
    public string Label => string.Join(", ", new[] { Name, Admin1, Country }.Where(x => !string.IsNullOrEmpty(x)));
}

public static class WeatherApi
{
    /// <summary>Open-Meteo's data is CC BY 4.0: credit it, with a link, wherever it's shown.</summary>
    public const string Attribution = "Weather data by Open-Meteo.com";
    public const string AttributionUrl = "https://open-meteo.com/";

    public static Uri ForecastUrl(double lat, double lon, bool fahrenheit) => new(
        "https://api.open-meteo.com/v1/forecast" +
        $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
        "&current=temperature_2m,apparent_temperature,weather_code,is_day" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
        "&timezone=auto&forecast_days=4" +
        $"&temperature_unit={(fahrenheit ? "fahrenheit" : "celsius")}");

    public static Uri GeocodeUrl(string query) => new(
        "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" + Uri.EscapeDataString(query));

    public static WeatherNow ParseForecast(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var cur = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        var times = daily.GetProperty("time").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        var codes = daily.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var hi = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var lo = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var n = new[] { times.Length, codes.Length, hi.Length, lo.Length }.Min();
        var days = Enumerable.Range(0, n)
            .Where(i => DateOnly.TryParseExact(times[i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Select(i => new WeatherDay(DateOnly.ParseExact(times[i], "yyyy-MM-dd", CultureInfo.InvariantCulture), codes[i], hi[i], lo[i]))
            .ToList();
        return new WeatherNow(
            cur.GetProperty("temperature_2m").GetDouble(),
            cur.GetProperty("apparent_temperature").GetDouble(),
            cur.GetProperty("weather_code").GetInt32(),
            cur.GetProperty("is_day").GetInt32() == 1,
            days);
    }

    // MARK: ZIP and postal codes

    /// <summary>The country postal codes are looked up in: Windows' region (Settings → Time &amp; language → Language &amp; region).</summary>
    public static string? PostalCountry
    {
        get
        {
            try { var r = RegionInfo.CurrentRegion.TwoLetterISORegionName; return r.Length == 2 ? r : null; }
            catch { return null; }
        }
    }

    /// <summary>"60601", "60601-1234", "SW1A 1AA", "10115" look like postal codes; "Lisbon" doesn't.</summary>
    public static bool LooksLikePostalCode(string s)
    {
        var t = s.Trim();
        return t.Length is >= 3 and <= 10 && t.Any(char.IsDigit) && t.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-');
    }

    /// <summary>Upper-cased, single spaces; a US ZIP+4 becomes its 5-digit ZIP.</summary>
    public static string NormalizePostalCode(string code, string? country)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(code.Trim().ToUpperInvariant(), @"\s+", " ");
        if (string.Equals(country, "US", StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.Match(t, @"^\d{5}") is { Success: true } zip)
            t = zip.Value;
        return t;
    }

    public static Uri PostalCodeUrl(string code, string? country) => new(
        "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + Uri.EscapeDataString(code) +
        (country is { Length: 2 } ? "&countryCode=" + country.ToUpperInvariant() : ""));

    /// <summary>The result that lists this postal code; otherwise the first result.</summary>
    public static Place? ParsePostalCode(string json, string code)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
        static string Squash(string s) => s.ToUpperInvariant().Replace(" ", "");
        var wanted = Squash(code);
        JsonElement? match = null;
        foreach (var r in results.EnumerateArray())
        {
            if (r.TryGetProperty("postcodes", out var pcs) && pcs.ValueKind == JsonValueKind.Array &&
                pcs.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && Squash(p.GetString() ?? "") == wanted))
            {
                match = r;
                break;
            }
        }
        return PlaceOf(match ?? results[0]);
    }

    private static Place PlaceOf(JsonElement r)
    {
        string? Opt(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Place(Opt("name") ?? "", r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                         Opt("admin1"), Opt("country"));
    }

    public static Place? ParseGeocode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        string? Opt(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Place(Opt("name") ?? "", r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                         Opt("admin1"), Opt("country"));
    }

    /// <summary>WMO weather code → icon.</summary>
    public static WeatherIcon Icon(int code) => code switch
    {
        0 => WeatherIcon.Clear,
        1 or 2 => WeatherIcon.PartlyCloudy,
        3 => WeatherIcon.Cloudy,
        45 or 48 => WeatherIcon.Fog,
        >= 51 and <= 57 => WeatherIcon.Drizzle,
        >= 61 and <= 67 => WeatherIcon.Rain,
        (>= 71 and <= 77) or 85 or 86 => WeatherIcon.Snow,
        >= 80 and <= 82 => WeatherIcon.Showers,
        >= 95 and <= 99 => WeatherIcon.Thunder,
        _ => WeatherIcon.Cloudy,
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        >= 51 and <= 55 => "Drizzle",
        56 or 57 or 66 or 67 => "Freezing rain",
        >= 61 and <= 65 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 and <= 99 => "Thunderstorms",
        _ => "—",
    };
}

// MARK: - Calendar (any iCalendar feed: Outlook / Google "secret address", etc.)

public sealed record CalendarEvent(string Title, DateTime Start, DateTime? End, bool AllDay)
{
    public string WhenText(DateTime now)
    {
        var time = Start.ToString("t", CultureInfo.CurrentCulture);
        if (Start.Date == now.Date) return time;
        if (Start.Date == now.Date.AddDays(1)) return "Tomorrow, " + time;
        return Start.ToString("dddd", CultureInfo.CurrentCulture) + ", " + time;
    }
}

public static class IcsParser
{
    public static IReadOnlyList<CalendarEvent> Parse(string ics)
    {
        // Unfold continuation lines (RFC 5545 §3.1).
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }

        var events = new List<CalendarEvent>();
        Dictionary<string, (string Params, string Value)>? cur = null;
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { cur = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line == "END:VEVENT")
            {
                if (cur is not null && cur.TryGetValue("DTSTART", out var ds) && ParseDate(ds.Params, ds.Value) is { } start)
                {
                    var end = cur.TryGetValue("DTEND", out var de) ? ParseDate(de.Params, de.Value)?.When : null;
                    var title = cur.TryGetValue("SUMMARY", out var su) ? Unescape(su.Value) : "Event";
                    var cancelled = cur.TryGetValue("STATUS", out var st) && st.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
                    if (!cancelled) events.Add(new CalendarEvent(title, start.When, end, start.AllDay));
                }
                cur = null;
                continue;
            }
            if (cur is null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            var semi = head.IndexOf(';');
            var name = semi < 0 ? head : head[..semi];
            var prms = semi < 0 ? "" : head[(semi + 1)..];
            cur.TryAdd(name, (prms, line[(colon + 1)..]));
        }
        return events;
    }

    /// <summary>Timed events that haven't ended, in the next few days, soonest first.</summary>
    public static IReadOnlyList<CalendarEvent> Upcoming(IEnumerable<CalendarEvent> events, DateTime now, int days = 3, int limit = 4) =>
        events.Where(e => !e.AllDay && (e.End ?? e.Start) > now && e.Start < now.AddDays(days))
              .OrderBy(e => e.Start)
              .Take(limit)
              .ToList();

    private static (DateTime When, bool AllDay)? ParseDate(string prms, string value)
    {
        value = value.Trim();
        if (prms.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !prms.Contains("DATE-TIME", StringComparison.OrdinalIgnoreCase) || value.Length == 8)
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? (d, true) : null;
        if (value.EndsWith('Z'))
            return DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u)
                ? (u.ToLocalTime(), false) : null;
        // TZID=… or floating: treat as local time (good enough for a glanceable tile).
        return DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var l)
            ? (l, false) : null;
    }

    private static string Unescape(string s) =>
        new StringBuilder(s).Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").ToString().Trim();
}
