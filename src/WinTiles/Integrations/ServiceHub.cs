using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// Polls your services for the service tiles on Start. Like the other live tiles, it only runs
/// while Start can be seen, and each widget keeps its own pace (ComfyUI every 2 s, now playing
/// every 15 s, upcoming episodes every 5 min). Everything here runs on the UI thread; the network
/// calls themselves are awaited.
/// </summary>
public sealed class ServiceHub
{
    public sealed record Status(ServiceSnapshot? Snapshot, string? Error, bool Loading);

    private readonly LayoutStore store;
    private readonly Dictionary<string, Status> status = new();
    private readonly Dictionary<string, DateTime> fetchedAt = new();
    private readonly HashSet<string> inFlight = new();
    private readonly Dictionary<Guid, ServiceClient> clients = new();
    private readonly Dictionary<Uri, ImageSource> posters = new();
    private readonly HashSet<Uri> posterLoading = new();
    private readonly DispatcherTimer timer;

    /// <summary>Raised with the key of a widget whose data (or posters) changed.</summary>
    public event Action<string>? Changed;

    public bool Active { get; private set; }

    public ServiceHub(LayoutStore store)
    {
        this.store = store;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
    }

    /// <summary>One entry per distinct thing to fetch; two tiles showing the same widget share it.</summary>
    public static string Key(Tile t) => $"{t.IntegrationId}|{t.Widget}|{t.Entity}";

    public Integration? IntegrationFor(Tile t) =>
        t.IntegrationId is { } id ? store.Settings.Integrations.FirstOrDefault(i => i.Id == id) : null;

    public Status? StatusFor(Tile t) => status.TryGetValue(Key(t), out var s) ? s : null;

    public ImageSource? Poster(Uri? url) => url is not null && posters.TryGetValue(url, out var p) ? p : null;

    // MARK: polling

    public void SetActive(bool on)
    {
        Active = on;
        if (on) { timer.Start(); Tick(); }
        else
        {
            timer.Stop();
            // Close ComfyUI's live feed now rather than when it idles out.
            foreach (var c in clients.Values) c.Comfy?.Stop();
        }
    }

    /// <summary>Fetches whatever is due.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>();
        foreach (var tile in store.Groups.SelectMany(g => g.Tiles))
        {
            if (tile.Kind != TileKind.Service || tile.Widget is not { } widget) continue;
            var key = Key(tile);
            if (!seen.Add(key)) continue;
            if (fetchedAt.TryGetValue(key, out var last) && (now - last).TotalSeconds < widget.Interval()) continue;
            Fetch(tile);
        }
    }

    public void Refresh(Tile tile)
    {
        fetchedAt.Remove(Key(tile));
        Fetch(tile);
    }

    /// <summary>An integration's settings changed (or it was removed): reconnect and refetch.</summary>
    public void IntegrationChanged(Guid id)
    {
        if (clients.Remove(id, out var old)) old.Dispose();
        var prefix = id + "|";
        foreach (var key in status.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            status.Remove(key);
            fetchedAt.Remove(key);
            Changed?.Invoke(key);
        }
        if (Active) Tick();
    }

    private async void Fetch(Tile tile)
    {
        var key = Key(tile);
        if (inFlight.Contains(key) || tile.Widget is not { } widget) return;
        if (IntegrationFor(tile) is not { } integration)
        {
            Set(key, new Status(null, "This service was removed from Settings → Integrations.", false));
            return;
        }
        var client = ClientFor(integration);
        inFlight.Add(key);
        if (!status.ContainsKey(key)) Set(key, new Status(null, null, true));
        try
        {
            var snapshot = await Task.Run(() => ServiceApi.Fetch(client, widget, tile.Entity));
            var previous = status.GetValueOrDefault(key)?.Snapshot;
            if (previous is null || previous.Signature() != snapshot.Signature() || status[key].Error is not null)
                Set(key, new Status(snapshot, null, false));
            foreach (var url in snapshot.Items.Take(8).Select(i => i.Image).OfType<Uri>()) LoadPoster(url, client);
        }
        catch (Exception ex)
        {
            Set(key, new Status(status.GetValueOrDefault(key)?.Snapshot, Message(ex), false));
        }
        finally
        {
            inFlight.Remove(key);
            fetchedAt[key] = DateTime.UtcNow;
        }
    }

    private void Set(string key, Status s)
    {
        status[key] = s;
        Changed?.Invoke(key);
    }

    public static string Message(Exception ex) => ex switch
    {
        ServiceException s => s.Message,
        AggregateException { InnerException: { } inner } => Message(inner),
        _ => ex.Message,
    };

    private ServiceClient ClientFor(Integration integration)
    {
        var secret = IntegrationSecrets.Get(integration.Id);
        var fingerprint = ServiceClient.MakeFingerprint(integration, secret);
        if (clients.TryGetValue(integration.Id, out var existing) && existing.Fingerprint == fingerprint) return existing;
        existing?.Dispose();
        var client = new ServiceClient(integration, secret);
        clients[integration.Id] = client;
        return client;
    }

    // MARK: posters

    private async void LoadPoster(Uri url, ServiceClient client)
    {
        if (posters.ContainsKey(url) || !posterLoading.Add(url)) return;
        try
        {
            var image = await Task.Run(async () => Decode(await client.Image(url), 480));
            if (image is null) return;
            if (posters.Count > 150) posters.Clear();
            posters[url] = image;
            foreach (var key in status.Where(kv => kv.Value.Snapshot?.Items.Any(i => i.Image == url) == true).Select(kv => kv.Key).ToList())
                Changed?.Invoke(key);
        }
        catch (Exception) { }
        finally { posterLoading.Remove(url); }
    }

    /// <summary>Decodes off the UI thread, downscaled, frozen so any thread can draw it.</summary>
    public static ImageSource? Decode(byte[] data, int maxPixel)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(data);
            image.DecodePixelWidth = maxPixel;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    // MARK: actions

    /// <summary>Runs an action (pause blocking, toggle a light, restart a container), then refreshes the tile.</summary>
    public async void Perform(ServiceAction action, Tile tile)
    {
        if (IntegrationFor(tile) is not { } integration) return;
        var key = Key(tile);
        var client = ClientFor(integration);

        // Show the change right away; the refresh confirms it.
        if (status.GetValueOrDefault(key)?.Snapshot is { } current)
        {
            var s = current.Copy();
            switch (action)
            {
                case ServiceAction.PauseBlocking: s.IsOn = false; break;
                case ServiceAction.ResumeBlocking: s.IsOn = true; break;
                case ServiceAction.ToggleEntity when s.IsOn is { } on: s.IsOn = !on; break;
                case ServiceAction.Interrupt: s.Busy = false; s.Progress = null; s.Headline = "Stopping…"; break;
            }
            Set(key, new Status(s, null, false));
        }

        string? failure = null;
        try { await Task.Run(() => ServiceApi.Perform(client, action)); }
        catch (Exception ex) { failure = Message(ex); }
        await Task.Delay(700);       // give the service a moment to apply it
        if (failure is not null) Set(key, new Status(status.GetValueOrDefault(key)?.Snapshot, failure, false));
        Refresh(tile);
    }

    // MARK: Settings helpers

    /// <summary>For the Test button: connects with the values in the form, not the saved ones.</summary>
    public static async Task<(bool Ok, string Message)> Test(Integration integration, string secret)
    {
        using var client = new ServiceClient(integration, secret);
        try { return (true, await Task.Run(() => ServiceApi.Test(client))); }
        catch (Exception ex) { return (false, Message(ex)); }
    }

    /// <summary>Home Assistant entities or Beszel systems, for the tile editor.</summary>
    public Task<List<PickableEntity>> Entities(Integration integration)
    {
        var client = ClientFor(integration);
        return Task.Run(() => ServiceApi.Entities(client));
    }

    public void Shutdown()
    {
        timer.Stop();
        foreach (var c in clients.Values) c.Dispose();
        clients.Clear();
    }
}
