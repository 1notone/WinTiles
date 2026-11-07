namespace WinTiles.Core;

/// <summary>
/// Talks to each service's own API and turns the answers into <see cref="ServiceSnapshot"/>s.
/// The Parse… methods are pure, so tests can feed them sample responses. Ported from MacTiles.
/// </summary>
public static partial class ServiceApi
{
    public static Task<ServiceSnapshot> Fetch(ServiceClient c, ServiceWidget widget, string? entity) =>
        (c.Integration.Kind, widget) switch
        {
            (ServiceKind.Plex, ServiceWidget.NowPlaying) => PlexNowPlaying(c),
            (ServiceKind.Plex, _) => PlexRecent(c),
            (ServiceKind.Jellyfin or ServiceKind.Emby, ServiceWidget.NowPlaying) => JellyNowPlaying(c),
            (ServiceKind.Jellyfin or ServiceKind.Emby, _) => JellyRecent(c),
            (ServiceKind.Sonarr, _) => Sonarr(c),
            (ServiceKind.Radarr, _) => Radarr(c),
            (ServiceKind.Overseerr, _) => Overseerr(c),
            (ServiceKind.Pihole, _) => Pihole(c),
            (ServiceKind.Adguard, _) => AdGuard(c),
            (ServiceKind.HomeAssistant, _) => string.IsNullOrEmpty(entity)
                ? throw new ServiceException("Choose an entity for this tile.")
                : HaEntity(c, entity),
            (ServiceKind.Unifi, _) => Unifi(c),
            (ServiceKind.Opnsense, _) => Opnsense(c),
            (ServiceKind.Proxmox, _) => Proxmox(c),
            (ServiceKind.Truenas, _) => TrueNas(c),
            (ServiceKind.Unraid, ServiceWidget.Containers) => UnraidContainers(c),
            (ServiceKind.Unraid, _) => Unraid(c),
            (ServiceKind.Glances, _) => Glances(c),
            (ServiceKind.Docker, _) => Docker(c),
            (ServiceKind.Beszel, _) => Beszel(c, entity),
            (ServiceKind.Comfyui, _) => ComfyUi(c),
            _ => throw new ServiceException("This service isn't supported yet."),
        };

    public static async Task Perform(ServiceClient c, ServiceAction action)
    {
        switch (c.Integration.Kind, action)
        {
            case (ServiceKind.Pihole, ServiceAction.PauseBlocking p): await PiholeBlocking(c, false, p.Seconds); break;
            case (ServiceKind.Pihole, ServiceAction.ResumeBlocking): await PiholeBlocking(c, true, null); break;
            case (ServiceKind.Adguard, ServiceAction.PauseBlocking p): await AdGuardProtection(c, false, p.Seconds); break;
            case (ServiceKind.Adguard, ServiceAction.ResumeBlocking): await AdGuardProtection(c, true, null); break;
            case (ServiceKind.HomeAssistant, ServiceAction.ToggleEntity t): await HaActivate(c, t.EntityId); break;
            case (ServiceKind.Docker, ServiceAction.Container k): await DockerAction(c, k.Id, k.Verb); break;
            case (ServiceKind.Comfyui, ServiceAction.Interrupt):
                (await c.Send("/interrupt", "POST", json: new Dictionary<string, object>())).Check();
                break;
            case (ServiceKind.Comfyui, ServiceAction.ClearQueue):
                (await c.Send("/queue", "POST", json: new Dictionary<string, object> { ["clear"] = true })).Check();
                break;
            default:
                throw new ServiceException($"That isn't supported for {c.Integration.Kind.Title()}.");
        }
    }

    /// <summary>For the Test button: tries the first widget and reports what it found.</summary>
    public static async Task<string> Test(ServiceClient c)
    {
        switch (c.Integration.Kind)
        {
            case ServiceKind.HomeAssistant:
                return $"Connected — {(await HaEntities(c)).Count} entities.";
            case ServiceKind.Docker:
                return $"Connected — {(await Docker(c)).Items.Count} containers.";
            case ServiceKind.Beszel:
                var b = await Beszel(c, null);
                return $"Connected — {b.Headline} {b.Caption}.";
            case ServiceKind.Comfyui:
                var stats = (await c.Send("/system_stats")).Json();
                var version = stats["system"]["comfyui_version"].Str is { } v ? " " + v : "";
                var gpu = ComfyDeviceName(stats["devices"][0]["name"].Str);
                return "Connected — ComfyUI" + version + (gpu.Length == 0 ? "." : $" on {gpu}.");
            default:
                var snap = await Fetch(c, c.Integration.Kind.Widgets()[0], null);
                var summary = string.Join(" ", new[] { snap.Headline, snap.Caption }.Where(x => x.Length > 0));
                return summary.Length == 0 ? "Connected." : $"Connected — {summary}.";
        }
    }

    /// <summary>Home Assistant entities, or Beszel systems, for the tile editor.</summary>
    public static Task<List<PickableEntity>> Entities(ServiceClient c) =>
        c.Integration.Kind == ServiceKind.Beszel ? BeszelSystems(c) : HaEntities(c);

    private static string Join(IEnumerable<string?> parts, string separator = " · ") =>
        string.Join(separator, parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string TitleCase(string s) =>
        s.Length == 0 ? s : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
}
