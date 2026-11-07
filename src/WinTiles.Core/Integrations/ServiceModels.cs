namespace WinTiles.Core;

// The homelab apps WinTiles can talk to directly — the services Homarr has widgets for, reached
// with each service's own API (Homarr itself isn't needed). Names and JSON values match MacTiles,
// so a layout.json moves between the two.

public enum ServiceKind
{
    // Media
    Plex, Jellyfin, Emby, Sonarr, Radarr, Overseerr,
    // Network & home
    Pihole, Adguard, HomeAssistant, Unifi, Opnsense,
    // Servers
    Proxmox, Truenas, Unraid, Glances, Docker, Beszel,
    // AI
    Comfyui,
}

public enum ServiceCategory { Media, Network, Servers, AI }

/// <summary>What a service tile shows.</summary>
public enum ServiceWidget
{
    NowPlaying, RecentlyAdded, Upcoming, Requests, DnsSummary, Entity, Network, Firewall, Resources, Containers,
    Systems, Generation,
}

public static class ServiceKindInfo
{
    public static ServiceCategory Category(this ServiceKind k) => k switch
    {
        ServiceKind.Plex or ServiceKind.Jellyfin or ServiceKind.Emby or ServiceKind.Sonarr or ServiceKind.Radarr or ServiceKind.Overseerr => ServiceCategory.Media,
        ServiceKind.Pihole or ServiceKind.Adguard or ServiceKind.HomeAssistant or ServiceKind.Unifi or ServiceKind.Opnsense => ServiceCategory.Network,
        ServiceKind.Comfyui => ServiceCategory.AI,
        _ => ServiceCategory.Servers,
    };

    public static string Title(this ServiceCategory c) => c switch
    {
        ServiceCategory.Network => "Network & Home",
        _ => c.ToString(),
    };

    public static IReadOnlyList<ServiceKind> InCategory(ServiceCategory c) =>
        Enum.GetValues<ServiceKind>().Where(k => k.Category() == c).ToArray();

    public static string Title(this ServiceKind k) => k switch
    {
        ServiceKind.Overseerr => "Overseerr / Jellyseerr",
        ServiceKind.Pihole => "Pi-hole",
        ServiceKind.Adguard => "AdGuard Home",
        ServiceKind.HomeAssistant => "Home Assistant",
        ServiceKind.Unifi => "UniFi Network",
        ServiceKind.Opnsense => "OPNsense",
        ServiceKind.Proxmox => "Proxmox VE",
        ServiceKind.Truenas => "TrueNAS",
        ServiceKind.Comfyui => "ComfyUI",
        _ => k.ToString(),
    };

    /// <summary>A monochrome symbol (drawn with the emoji font, in white).</summary>
    public static string Symbol(this ServiceKind k) => k switch
    {
        ServiceKind.Plex or ServiceKind.Jellyfin or ServiceKind.Emby => "🎬",
        ServiceKind.Sonarr => "📺",
        ServiceKind.Radarr => "🎞",
        ServiceKind.Overseerr => "📥",
        ServiceKind.Pihole or ServiceKind.Adguard => "🛡",
        ServiceKind.HomeAssistant => "🏠",
        ServiceKind.Unifi => "📶",
        ServiceKind.Opnsense => "🧱",
        ServiceKind.Proxmox => "🖥",
        ServiceKind.Truenas or ServiceKind.Unraid => "🗄",
        ServiceKind.Glances => "📊",
        ServiceKind.Docker => "📦",
        ServiceKind.Beszel => "📈",
        ServiceKind.Comfyui => "✨",
        _ => "🖥",
    };

    public static string DefaultColor(this ServiceKind k) => k switch
    {
        ServiceKind.Plex => "#C1840B",
        ServiceKind.Jellyfin => "#603CBA",
        ServiceKind.Emby => "#00A300",
        ServiceKind.Sonarr => "#00ABA9",
        ServiceKind.Radarr => "#E3A21A",
        ServiceKind.Overseerr => "#2B5797",
        ServiceKind.Pihole => "#B91D47",
        ServiceKind.Adguard => "#1E7145",
        ServiceKind.HomeAssistant => "#2D89EF",
        ServiceKind.Unifi => "#2B5797",
        ServiceKind.Opnsense => "#DA532C",
        ServiceKind.Proxmox => "#DA532C",
        ServiceKind.Truenas => "#2D89EF",
        ServiceKind.Unraid => "#EE1111",
        ServiceKind.Glances => "#00A300",
        ServiceKind.Docker => "#1D72B8",
        ServiceKind.Beszel => "#00ABA9",
        ServiceKind.Comfyui => "#603CBA",
        _ => "#2B5797",
    };

    public static string Placeholder(this ServiceKind k) => k switch
    {
        ServiceKind.Plex => "http://192.168.1.10:32400",
        ServiceKind.Jellyfin or ServiceKind.Emby => "http://192.168.1.10:8096",
        ServiceKind.Sonarr => "http://192.168.1.10:8989",
        ServiceKind.Radarr => "http://192.168.1.10:7878",
        ServiceKind.Overseerr => "http://192.168.1.10:5055",
        ServiceKind.Pihole => "http://pi.hole",
        ServiceKind.Adguard => "http://192.168.1.2:3000",
        ServiceKind.HomeAssistant => "http://homeassistant.local:8123",
        ServiceKind.Unifi or ServiceKind.Opnsense => "https://192.168.1.1",
        ServiceKind.Proxmox => "https://192.168.1.20:8006",
        ServiceKind.Truenas => "https://truenas.local",
        ServiceKind.Unraid => "http://tower.local",
        ServiceKind.Glances => "http://192.168.1.10:61208",
        ServiceKind.Docker => "Leave empty for this PC's Docker",
        ServiceKind.Beszel => "http://192.168.1.10:8090",
        ServiceKind.Comfyui => "http://192.168.1.10:8188",
        _ => "http://",
    };

    /// <summary>Docker can run on this PC with no address at all.</summary>
    public static bool AddressOptional(this ServiceKind k) => k == ServiceKind.Docker;

    public static string? UsernameLabel(this ServiceKind k) => k switch
    {
        ServiceKind.Adguard or ServiceKind.Unifi or ServiceKind.Glances => "Username",
        ServiceKind.Proxmox => "API token ID",
        ServiceKind.Opnsense => "API key",
        ServiceKind.Beszel => "Email",
        _ => null,
    };

    /// <summary>Services where the username field must be filled in.</summary>
    public static bool UsernameRequired(this ServiceKind k) =>
        k is ServiceKind.Proxmox or ServiceKind.Adguard or ServiceKind.Unifi or ServiceKind.Opnsense or ServiceKind.Beszel;

    public static string UsernamePrompt(this ServiceKind k) => k switch
    {
        ServiceKind.Proxmox => "root@pam!wintiles",
        ServiceKind.Glances => "Only if Glances asks for a password",
        ServiceKind.Opnsense => "The key from the downloaded apikey.txt",
        ServiceKind.Beszel => "you@example.com",
        _ => "admin",
    };

    /// <summary>The name of the secret (kept in Credential Manager), or null if the service has none.</summary>
    public static string? SecretLabel(this ServiceKind k) => k switch
    {
        ServiceKind.Plex => "X-Plex-Token",
        ServiceKind.Jellyfin or ServiceKind.Emby or ServiceKind.Sonarr or ServiceKind.Radarr or ServiceKind.Overseerr
            or ServiceKind.Truenas or ServiceKind.Unraid => "API key",
        ServiceKind.Pihole or ServiceKind.Adguard or ServiceKind.Unifi or ServiceKind.Glances or ServiceKind.Beszel => "Password",
        ServiceKind.HomeAssistant => "Long-lived access token",
        ServiceKind.Proxmox => "Token secret",
        ServiceKind.Opnsense => "API secret",
        _ => null,     // Docker, ComfyUI
    };

    public static bool SecretRequired(this ServiceKind k) =>
        k is not (ServiceKind.Pihole or ServiceKind.Glances or ServiceKind.Docker or ServiceKind.Comfyui);

    public static string Help(this ServiceKind k) => k switch
    {
        ServiceKind.Plex => "Find your token by viewing the XML of any item in Plex Web (it's the X-Plex-Token in the address).",
        ServiceKind.Jellyfin => "Dashboard → API Keys → +.",
        ServiceKind.Emby => "Settings → Advanced → API Keys → New API Key.",
        ServiceKind.Sonarr or ServiceKind.Radarr => "Settings → General → Security → API Key.",
        ServiceKind.Overseerr => "Settings → General → API Key. Works with Jellyseerr too.",
        ServiceKind.Pihole => "Your web interface password, or an app password (Settings → Web interface / API). For Pi-hole 5, use the API token. Leave empty if you don't use a password.",
        ServiceKind.Adguard => "The account you sign in to AdGuard Home with.",
        ServiceKind.HomeAssistant => "Your profile → Security → Long-lived access tokens → Create token.",
        ServiceKind.Unifi => "A local account on your console or controller (a read-only local admin is enough).",
        ServiceKind.Opnsense => "System → Access → Users → edit a user → API keys → +. The downloaded apikey.txt holds the key and secret. The user needs the Dashboard, Diagnostics and Traffic Graph privileges (or \"All pages\").",
        ServiceKind.Proxmox => "Datacenter → Permissions → API Tokens. Give it the PVEAuditor role, or uncheck Privilege Separation.",
        ServiceKind.Truenas => "Your account menu (top right) → API Keys → Add.",
        ServiceKind.Unraid => "Settings → Management Access → API Keys (Unraid 7.2 or the Unraid Connect plugin).",
        ServiceKind.Glances => "Run Glances in web server mode: glances -w. The REST API is on port 61208.",
        ServiceKind.Docker => "Uses Docker Desktop on this PC (its \\\\.\\pipe\\docker_engine pipe). For another machine, enter its Docker API address, like http://nas.local:2375.",
        ServiceKind.Beszel => "The email and password you sign in to Beszel with — a regular user (sees the systems shared with it) or a superuser.",
        ServiceKind.Comfyui => "No key needed: WinTiles uses ComfyUI's own API. To reach it from another machine, start ComfyUI with --listen. Install the ComfyUI-Crystools custom node to add GPU load, GPU temperature and CPU.",
        _ => "",
    };

    /// <summary>Self-signed certificates are the norm for these.</summary>
    public static bool UsuallySelfSigned(this ServiceKind k) =>
        k is ServiceKind.Unifi or ServiceKind.Opnsense or ServiceKind.Proxmox or ServiceKind.Truenas;

    public static IReadOnlyList<ServiceWidget> Widgets(this ServiceKind k) => k switch
    {
        ServiceKind.Plex or ServiceKind.Jellyfin or ServiceKind.Emby => new[] { ServiceWidget.NowPlaying, ServiceWidget.RecentlyAdded },
        ServiceKind.Sonarr or ServiceKind.Radarr => new[] { ServiceWidget.Upcoming },
        ServiceKind.Overseerr => new[] { ServiceWidget.Requests },
        ServiceKind.Pihole or ServiceKind.Adguard => new[] { ServiceWidget.DnsSummary },
        ServiceKind.HomeAssistant => new[] { ServiceWidget.Entity },
        ServiceKind.Unifi => new[] { ServiceWidget.Network },
        ServiceKind.Opnsense => new[] { ServiceWidget.Firewall },
        ServiceKind.Unraid => new[] { ServiceWidget.Resources, ServiceWidget.Containers },
        ServiceKind.Docker => new[] { ServiceWidget.Containers },
        ServiceKind.Beszel => new[] { ServiceWidget.Systems },
        ServiceKind.Comfyui => new[] { ServiceWidget.Generation },
        _ => new[] { ServiceWidget.Resources },
    };

    /// <summary>Home Assistant tiles show an entity; Beszel tiles can show one system.</summary>
    public static bool PicksEntity(this ServiceKind k) => k is ServiceKind.HomeAssistant or ServiceKind.Beszel;
}

public static class ServiceWidgetInfo
{
    public static string Title(this ServiceWidget w) => w switch
    {
        ServiceWidget.NowPlaying => "Now Playing",
        ServiceWidget.RecentlyAdded => "Recently Added",
        ServiceWidget.DnsSummary => "Blocking Stats",
        ServiceWidget.Network => "Network Health",
        ServiceWidget.Firewall => "Firewall Health",
        ServiceWidget.Resources => "System Health",
        _ => w.ToString(),
    };

    public static string Detail(this ServiceWidget w) => w switch
    {
        ServiceWidget.NowPlaying => "Who's watching what, right now.",
        ServiceWidget.RecentlyAdded => "The newest movies and episodes, with posters.",
        ServiceWidget.Upcoming => "What's airing or releasing over the next two weeks, and the download queue.",
        ServiceWidget.Requests => "Pending and recent requests.",
        ServiceWidget.DnsSummary => "Queries and blocked today. Pause blocking from the tile's menu.",
        ServiceWidget.Entity => "One light, switch, sensor, script or automation. Click to toggle or run.",
        ServiceWidget.Network => "Clients, devices and internet speed.",
        ServiceWidget.Firewall => "Live traffic per interface, gateways (latency and packet loss), firewall states, memory, load and uptime.",
        ServiceWidget.Resources => "CPU, memory, storage and uptime.",
        ServiceWidget.Containers => "Running containers. Start, stop or restart from the tile's menu.",
        ServiceWidget.Systems => "Every system's CPU, memory and disk — or pick one system for its full stats.",
        ServiceWidget.Generation => "Live job progress, the queue, GPU / VRAM / RAM use and your latest images. Interrupt or clear the queue from the tile's menu.",
        _ => "",
    };

    public static TileSize DefaultSize(this ServiceWidget w) => w switch
    {
        ServiceWidget.Entity or ServiceWidget.DnsSummary or ServiceWidget.Network or ServiceWidget.Firewall or ServiceWidget.Resources => TileSize.Medium,
        _ => TileSize.Wide,
    };

    public static IReadOnlyList<TileSize> Sizes(this ServiceWidget w) => w switch
    {
        ServiceWidget.Entity => new[] { TileSize.Small, TileSize.Medium, TileSize.Wide },
        ServiceWidget.DnsSummary or ServiceWidget.Systems or ServiceWidget.Generation => new[] { TileSize.Small, TileSize.Medium, TileSize.Wide, TileSize.Large },
        _ => new[] { TileSize.Medium, TileSize.Wide, TileSize.Large },
    };

    /// <summary>How often to ask the service, in seconds, while Start is on screen.</summary>
    public static double Interval(this ServiceWidget w) => w switch
    {
        ServiceWidget.Generation => 2,
        ServiceWidget.Firewall => 10,
        ServiceWidget.NowPlaying or ServiceWidget.Entity or ServiceWidget.Containers => 15,
        ServiceWidget.DnsSummary or ServiceWidget.Network or ServiceWidget.Resources or ServiceWidget.Systems => 20,
        ServiceWidget.Requests or ServiceWidget.RecentlyAdded => 120,
        _ => 300,
    };

    /// <summary>Media and image widgets flip through their items (posters) on medium and wide tiles.</summary>
    public static bool FlipsItems(this ServiceWidget w) =>
        w is ServiceWidget.NowPlaying or ServiceWidget.RecentlyAdded or ServiceWidget.Upcoming or ServiceWidget.Requests or ServiceWidget.Generation;
}

/// <summary>
/// One service connected in Settings → Integrations. Saved in layout.json; its password, token
/// or API key is kept in Windows Credential Manager instead.
/// </summary>
public sealed class Integration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ServiceKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public bool AllowSelfSigned { get; set; }

    public static Integration Create(ServiceKind kind) => new() { Kind = kind, AllowSelfSigned = kind.UsuallySelfSigned() };

    public Integration Copy() => (Integration)MemberwiseClone();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Kind.Title() : Name.Trim();

    public Uri? BaseUri => Normalize(Url);

    /// <summary>
    /// A plain-HTTP address that isn't on your local network: a key or password sent to it
    /// travels unencrypted across the internet.
    /// </summary>
    public bool SendsSecretInsecurely => BaseUri is { Scheme: "http" } u && !IsLocalHost(u.Host);

    /// <summary>
    /// Addresses that stay on your network (or this PC): private IPv4/IPv6 ranges, Tailscale's
    /// 100.64/10, single-label names and the usual home-network suffixes.
    /// </summary>
    public static bool IsLocalHost(string rawHost)
    {
        var host = rawHost.ToLowerInvariant().Trim('[', ']', '.');
        if (host == "localhost" || (!host.Contains('.') && !host.Contains(':'))) return true;
        foreach (var suffix in new[] { ".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".localhost", ".ts.net" })
            if (host.EndsWith(suffix, StringComparison.Ordinal)) return true;
        var parts = host.Split('.');
        if (parts.Length == 4 && parts.All(p => byte.TryParse(p, out _)))
        {
            var a = byte.Parse(parts[0]);
            var b = byte.Parse(parts[1]);
            return a == 10 || a == 127 || (a == 192 && b == 168) || (a == 169 && b == 254) ||
                   (a == 172 && b is >= 16 and <= 31) || (a == 100 && b is >= 64 and <= 127);
        }
        if (host.Contains(':')) return host == "::1" || host.StartsWith("fc") || host.StartsWith("fd") || host.StartsWith("fe80");
        return false;
    }

    /// <summary>Adds http:// if missing and drops trailing slashes. "tcp://" (Docker) becomes http://.</summary>
    public static Uri? Normalize(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Contains(' ')) return null;
        if (s.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) s = "http://" + s[6..];
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s;
        s = s.TrimEnd('/');
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u : null;
    }

    /// <summary>The service's own web page, opened when you click its tile.</summary>
    public Uri? WebUri => BaseUri is not { } b ? null : Kind switch
    {
        ServiceKind.Plex => new Uri(Base(b) + "/web"),
        ServiceKind.Pihole => new Uri(Base(b) + "/admin"),
        _ => b,
    };

    /// <summary>The base address as text, without a trailing slash, for joining paths.</summary>
    public static string Base(Uri u) => u.AbsoluteUri.TrimEnd('/');
}

// MARK: - What a tile shows

public sealed record ServiceStat(string Label, string Value);
public sealed record ServiceGauge(string Label, double Fraction, string Detail = "");

public sealed record ServiceItem(string Id, string Title)
{
    public string Subtitle { get; init; } = "";
    public double? Progress { get; init; }
    public Uri? Image { get; init; }
    public bool IsActive { get; init; }
}

public sealed record ServiceTraffic(string Id, string Name, double Down, double Up);   // bytes per second

/// <summary>
/// A service-agnostic picture of a widget's data: a headline, stats, gauges and a list. Every
/// service is parsed into this shape, so the tiles can draw any of them.
/// </summary>
public sealed class ServiceSnapshot
{
    public string Headline { get; set; } = "";
    public string Caption { get; set; } = "";
    public List<ServiceStat> Stats { get; set; } = new();
    public List<ServiceGauge> Gauges { get; set; } = new();
    public List<ServiceItem> Items { get; set; } = new();
    /// <summary>On/off for things you can switch: Pi-hole blocking, a light…</summary>
    public bool? IsOn { get; set; }
    /// <summary>A symbol for the current state (Home Assistant entities).</summary>
    public string? Symbol { get; set; }
    public string EmptyText { get; set; } = "";
    /// <summary>Live throughput per interface (OPNsense).</summary>
    public List<ServiceTraffic> Traffic { get; set; } = new();
    /// <summary>Recent values for a sparkline, scaled 0…1.</summary>
    public List<double> Series { get; set; } = new();
    /// <summary>Progress of the job running now (ComfyUI), 0…1.</summary>
    public double? Progress { get; set; }
    /// <summary>Something is running; the tile stays on its live face instead of flipping.</summary>
    public bool Busy { get; set; }
    /// <summary>What the tile is about, when it's one thing (a Beszel system); used as its label.</summary>
    public string? Name { get; set; }

    public ServiceSnapshot Copy() => new()
    {
        Headline = Headline, Caption = Caption, Stats = Stats.ToList(), Gauges = Gauges.ToList(), Items = Items.ToList(),
        IsOn = IsOn, Symbol = Symbol, EmptyText = EmptyText, Traffic = Traffic.ToList(), Series = Series.ToList(),
        Progress = Progress, Busy = Busy, Name = Name,
    };

    /// <summary>Everything that's drawn, as one string; tiles redraw only when it changes.</summary>
    public string Signature() => string.Join("\u001F", new object?[]
    {
        Headline, Caption, IsOn, Symbol, EmptyText, Progress, Busy, Name,
        string.Join("\u001E", Stats), string.Join("\u001E", Gauges), string.Join("\u001E", Items),
        string.Join("\u001E", Traffic), string.Join(",", Series.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))),
    });
}

/// <summary>Something you can do to a service from its tile.</summary>
public abstract record ServiceAction
{
    public sealed record PauseBlocking(int? Seconds) : ServiceAction;    // null = until you turn it back on
    public sealed record ResumeBlocking : ServiceAction;
    public sealed record ToggleEntity(string EntityId) : ServiceAction;
    public sealed record Container(string Id, string Verb) : ServiceAction;   // start, stop, restart
    public sealed record Interrupt : ServiceAction;                       // stop the job running now (ComfyUI)
    public sealed record ClearQueue : ServiceAction;                      // drop jobs waiting to run (ComfyUI)
}

/// <summary>A friendly error for a service tile or the Test button.</summary>
public sealed class ServiceException : Exception
{
    public int? Status { get; }
    public bool IsUnauthorized { get; }

    public ServiceException(string message, int? status = null, bool unauthorized = false) : base(message)
    {
        Status = status;
        IsUnauthorized = unauthorized;
    }

    public static ServiceException BadAddress() => new("Check the address in Settings → Integrations.");
    public static ServiceException Unauthorized() => new("Sign-in failed. Check the key or password.", unauthorized: true);
    public static ServiceException Http(int code) => new(code == 404
        ? "Not found (404). Is this the right address and port?"
        : $"The service answered with error {code}.", code);
}
