using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// Owns every live-tile data provider. Polling only runs while Start can be seen,
/// so WinTiles idles at ~0% CPU when it's hidden or covered.
/// </summary>
public sealed class LiveHub
{
    private readonly LayoutStore store;
    public WeatherProvider Weather { get; } = new();
    /// <summary>One provider per feed, shared by News tiles showing the same feed.</summary>
    private readonly Dictionary<string, NewsProvider> newsByFeed = new();
    /// <summary>Any feed got new headlines (News tiles look up their own provider).</summary>
    public event Action? NewsChanged;
    public SystemProvider Stats { get; } = new();
    public BatteryProvider Battery { get; } = new();
    public CalendarProvider Calendar { get; } = new();
    public PhotosProvider Photos { get; } = new();
    public DesktopProvider Desktop { get; } = new();
    private bool active;

    internal static readonly HttpClient Http = CreateClient();

    public LiveHub(LayoutStore store) { this.store = store; }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WinTiles (+https://github.com/1notone/WinTiles)");
        return c;
    }

    /// <summary>The headlines for one News tile.</summary>
    public NewsProvider News(Tile tile) => NewsFor(store.NewsFeedOf(tile));

    private NewsProvider NewsFor(string feed)
    {
        if (!newsByFeed.TryGetValue(feed, out var provider))
        {
            provider = new NewsProvider();
            provider.Changed += () => NewsChanged?.Invoke();
            newsByFeed[feed] = provider;
        }
        return provider;
    }

    /// <summary>Network-backed tiles refresh when stale (weather every 15 min, news and calendar every 30).</summary>
    public void RefreshStale()
    {
        var s = store.Settings;
        if (store.Has(TileKind.Weather)) Weather.Refresh(s);
        var feeds = store.NewsFeeds;
        foreach (var feed in feeds) NewsFor(feed).Refresh(feed);
        foreach (var gone in newsByFeed.Keys.Where(f => !feeds.Contains(f)).ToList()) newsByFeed.Remove(gone);
        if (store.Has(TileKind.Calendar)) Calendar.Refresh(s.CalendarFeed);
    }

    /// <summary>A News tile's feed or Settings' default feed changed: load the new one now.</summary>
    public void NewsFeedsChanged()
    {
        RefreshStale();
        NewsChanged?.Invoke();
    }

    public void SetActive(bool on)
    {
        active = on;
        if (on)
        {
            RefreshStale();
            if (store.Has(TileKind.System)) Stats.Sample();
            if (store.Has(TileKind.Battery)) Battery.Sample();
            if (store.Has(TileKind.Photos)) Photos.Start(store.Settings.PhotosFolder);
            if (store.Has(TileKind.Desktop)) Desktop.Refresh();
        }
        else Photos.Stop();
    }

    private int seconds;

    /// <summary>Called once a second while live tiles are visible.</summary>
    public void Tick(DateTime now)
    {
        if (!active) return;
        seconds++;
        if (seconds % 2 == 0 && store.Has(TileKind.System)) Stats.Sample();
        if (seconds % 30 == 0 && store.Has(TileKind.Battery)) Battery.Sample();
        // Several News tiles take turns rather than all changing headline at once.
        var feeds = store.NewsFeeds;
        for (var i = 0; i < feeds.Count; i++)
            if ((seconds + (i % 4) * 2) % 8 == 0) NewsFor(feeds[i]).Advance();
        if (seconds % 9 == 0 && store.Has(TileKind.Photos)) Photos.Advance();
        if (seconds % 60 == 0) { RefreshStale(); if (store.Has(TileKind.Desktop)) Desktop.Refresh(); }
    }
}

// MARK: - Weather

public sealed class WeatherProvider
{
    public WeatherNow? Now { get; private set; }
    public string Status { get; private set; } = "Choose a place in Settings";
    public event Action? Changed;
    private DateTime fetchedAt = DateTime.MinValue;
    private string key = "";
    private bool loading;

    public async void Refresh(AppSettings s, bool force = false)
    {
        if (s.WeatherLat is not { } lat || s.WeatherLon is not { } lon)
        {
            Now = null;
            Status = "Choose a place in Settings";
            Changed?.Invoke();
            return;
        }
        var newKey = $"{lat},{lon},{s.Fahrenheit}";
        if (!force && newKey == key && (DateTime.UtcNow - fetchedAt).TotalMinutes < 15) return;
        if (loading) return;
        loading = true;
        if (newKey != key) { Status = "Loading…"; Changed?.Invoke(); }
        try
        {
            var json = await LiveHub.Http.GetStringAsync(WeatherApi.ForecastUrl(lat, lon, s.Fahrenheit));
            Now = WeatherApi.ParseForecast(json);
            key = newKey;
            fetchedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            if (key != newKey) Now = null;
            Status = "Weather unavailable";
            Log.Write("Weather: " + ex.Message);
        }
        finally { loading = false; }
        Changed?.Invoke();
    }

    public static async Task<Place?> Geocode(string query)
    {
        var json = await LiveHub.Http.GetStringAsync(WeatherApi.GeocodeUrl(query));
        return WeatherApi.ParseGeocode(json);
    }

    /// <summary>Looks up a ZIP or postal code in Windows' region.</summary>
    public static async Task<Place?> LookupPostalCode(string code)
    {
        var country = WeatherApi.PostalCountry;
        var cleaned = WeatherApi.NormalizePostalCode(code, country);
        if (cleaned.Length == 0) return null;
        var json = await LiveHub.Http.GetStringAsync(WeatherApi.PostalCodeUrl(cleaned, country));
        return WeatherApi.ParsePostalCode(json, cleaned);
    }
}

// MARK: - News

public sealed class NewsProvider
{
    public IReadOnlyList<Headline> Items { get; private set; } = Array.Empty<Headline>();
    public int Index { get; private set; }
    public string Source { get; private set; } = "";
    public string Status { get; private set; } = "";
    public Headline? Current => Items.Count == 0 ? null : Items[Index % Items.Count];
    public event Action? Changed;
    private DateTime fetchedAt = DateTime.MinValue;
    private DateTime failedAt = DateTime.MinValue;
    private string feed = "";
    private bool loading;

    public async void Refresh(string newFeed)
    {
        var url = newFeed.Trim();
        if (url.Length == 0) { Items = Array.Empty<Headline>(); Source = ""; Changed?.Invoke(); return; }
        if (url == feed && (DateTime.UtcNow - fetchedAt).TotalMinutes < 30) return;
        if ((DateTime.UtcNow - failedAt).TotalMinutes < 5) return;     // a feed that just failed isn't retried every minute
        if (loading) return;
        loading = true;
        try
        {
            var result = await Fetch(url);
            Items = result.Items;
            Source = result.Title;
            Status = "";
            Index = 0;
            feed = url;
            fetchedAt = DateTime.UtcNow;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            failedAt = DateTime.UtcNow;
            if (Items.Count == 0) { Status = "Couldn't load this feed"; Changed?.Invoke(); }
            Log.Write("News: " + ex.Message);
        }
        finally { loading = false; }
    }

    /// <summary>
    /// Reads a feed. Given a web page instead (bbc.com, theverge.com), follows the page's own
    /// link to its feed.
    /// </summary>
    public static async Task<FeedResult> Fetch(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !WebLink.IsFetchable(uri)) throw new ArgumentException("Not a web address");
        var text = await Download(uri);
        var parsed = FeedParser.TryParse(text);
        if (parsed.Items.Count > 0) return new FeedResult(parsed.Title, parsed.Items, url);
        foreach (var candidate in FeedParser.DiscoverFeeds(text.Length > 768_000 ? text[..768_000] : text, uri).Take(2))
        {
            string body;
            try { body = await Download(candidate); } catch { continue; }
            var found = FeedParser.TryParse(body);
            if (found.Items.Count > 0) return new FeedResult(found.Title, found.Items, candidate.AbsoluteUri);
        }
        throw new FormatException("No news feed found at that address");
    }

    private static async Task<string> Download(Uri uri)
    {
        using var response = await LiveHub.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 5_000_000) throw new InvalidDataException("Feed is too large");
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length > 5_000_000) throw new InvalidDataException("Feed is too large");
        return text;
    }

    public void Advance()
    {
        if (Items.Count < 2) return;
        Index = (Index + 1) % Items.Count;
        Changed?.Invoke();
    }
}

// MARK: - CPU & memory

public sealed class SystemProvider
{
    public double Cpu { get; private set; }
    public IReadOnlyList<double> History => history;
    public double MemUsed { get; private set; }
    public double MemTotal { get; private set; }
    public event Action? Changed;
    private readonly List<double> history = new();
    private (ulong Busy, ulong Total)? previous;

    public void Sample()
    {
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes idle time.
            var total = kernel.Value + user.Value;
            var busy = total - idle.Value;
            if (previous is { } p && total > p.Total)
            {
                Cpu = Math.Clamp((double)(busy - p.Busy) / (total - p.Total), 0, 1);
                history.Add(Cpu);
                if (history.Count > 30) history.RemoveRange(0, history.Count - 30);
            }
            previous = (busy, total);
        }
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            MemTotal = mem.ullTotalPhys;
            MemUsed = mem.ullTotalPhys - mem.ullAvailPhys;
        }
        Changed?.Invoke();
    }
}

// MARK: - Battery

public sealed record BatteryState(int Percent, bool Charging, bool OnAC, int? MinutesLeft);

public sealed class BatteryProvider
{
    public BatteryState? State { get; private set; }
    public event Action? Changed;

    public void Sample()
    {
        BatteryState? next = null;
        if (Native.GetSystemPowerStatus(out var s) && (s.BatteryFlag & 128) == 0 && s.BatteryLifePercent <= 100)
        {
            next = new BatteryState(
                s.BatteryLifePercent,
                (s.BatteryFlag & 8) != 0,
                s.ACLineStatus == 1,
                s.BatteryLifeTime > 0 ? s.BatteryLifeTime / 60 : null);
        }
        if (next != State) { State = next; Changed?.Invoke(); }
    }
}

// MARK: - Calendar (any iCalendar feed URL; no account sign-in needed)

public sealed class CalendarProvider
{
    public IReadOnlyList<CalendarEvent> Events { get; private set; } = Array.Empty<CalendarEvent>();
    public event Action? Changed;
    private IReadOnlyList<CalendarEvent> all = Array.Empty<CalendarEvent>();
    private DateTime fetchedAt = DateTime.MinValue;
    private string feed = "";
    private bool loading;

    public async void Refresh(string? newFeed, bool force = false)
    {
        var url = (newFeed ?? "").Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url["webcal://".Length..];
        if (url.Length == 0)
        {
            all = Events = Array.Empty<CalendarEvent>();
            feed = "";
            Changed?.Invoke();
            return;
        }
        if (!force && url == feed && (DateTime.UtcNow - fetchedAt).TotalMinutes < 30) { Recompute(); return; }
        if (loading) return;
        loading = true;
        try
        {
            all = IcsParser.Parse(await LiveHub.Http.GetStringAsync(url));
            feed = url;
            fetchedAt = DateTime.UtcNow;
        }
        catch (Exception ex) { Log.Write("Calendar: " + ex.Message); }
        finally { loading = false; }
        Recompute();
    }

    private void Recompute()
    {
        Events = IcsParser.Upcoming(all, DateTime.Now);
        Changed?.Invoke();
    }
}

// MARK: - Photos (slideshow from a folder; defaults to Pictures)

public sealed class PhotosProvider
{
    public ImageSource? Image { get; private set; }
    public bool IsEmpty { get; private set; }
    public event Action? Changed;
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp", ".jfif" };
    private List<string> files = new();
    private string folder = "";
    private bool running;
    private readonly Random random = new();

    public async void Start(string newFolder)
    {
        running = true;
        if (newFolder == folder && files.Count > 0) { if (Image is null) Advance(); return; }
        folder = newFolder;
        files = await Task.Run(() => List(newFolder));
        IsEmpty = files.Count == 0;
        Changed?.Invoke();
        Advance();
    }

    public void Stop() => running = false;

    public async void Advance()
    {
        if (!running || files.Count == 0) return;
        var path = files[random.Next(files.Count)];
        var image = await Task.Run(() => Thumbnail(path, 1000));
        if (image is null) return;
        Image = image;
        Changed?.Invoke();
    }

    private static List<string> List(string folder)
    {
        var found = new List<string>();
        try
        {
            if (!Directory.Exists(folder)) return found;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var f in Directory.EnumerateFiles(folder, "*", options))
            {
                if (Extensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))) found.Add(f);
                if (found.Count >= 400) break;
            }
        }
        catch (Exception ex) { Log.Write("Photos: " + ex.Message); }
        return found;
    }

    /// <summary>Decodes a downsampled, frozen image off the UI thread, so a 48 MP photo costs ~1 MB.</summary>
    internal static BitmapImage? Thumbnail(string path, int width)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            b.DecodePixelWidth = width;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }
}

// MARK: - Desktop (shows the current wallpaper, like the Windows 8 Desktop tile)

public sealed class DesktopProvider
{
    public ImageSource? Image { get; private set; }
    public event Action? Changed;
    private string? lastPath;
    private DateTime lastWrite;

    public async void Refresh()
    {
        var path = Native.WallpaperPath();
        if (path is null || !File.Exists(path)) return;
        var write = File.GetLastWriteTimeUtc(path);
        if (path == lastPath && write == lastWrite) return;
        lastPath = path;
        lastWrite = write;
        var image = await Task.Run(() => PhotosProvider.Thumbnail(path, 900));
        if (image is null) return;
        Image = image;
        Changed?.Invoke();
    }
}
