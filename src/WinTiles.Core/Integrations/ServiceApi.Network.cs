namespace WinTiles.Core;

public static partial class ServiceApi
{
    // MARK: - Pi-hole (v6, with a fallback for v5)

    public static async Task<ServiceSnapshot> Pihole(ServiceClient c)
    {
        if (c.Value("pihole.v5") is null)
        {
            try
            {
                var summary = await PiholeV6(c, "/api/stats/summary");
                J blocking;
                try { blocking = await PiholeV6(c, "/api/dns/blocking"); } catch (ServiceException) { blocking = default; }
                return ParsePiholeSummary(summary, blocking);
            }
            catch (ServiceException e) when (e.Status == 404)
            {
                c.Set("pihole.v5", "1");
            }
        }
        var auth = c.Secret.Length == 0 ? "" : "&auth=" + Fmt.Query(c.Secret);
        var j = (await c.Send("/admin/api.php?summaryRaw" + auth)).Json();
        if (j.IsArray) throw ServiceException.Unauthorized();     // v5 answers [] when the token is wrong
        return ParsePiholeV5(j);
    }

    private static async Task<J> PiholeV6(ServiceClient c, string path, string method = "GET", object? json = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var headers = new Dictionary<string, string>();
            if (c.Value("pihole.sid") is { Length: > 0 } sid) headers["X-FTL-SID"] = sid;
            var r = await c.Send(path, method, headers, json);
            if (r.Status == 401 && attempt == 0)
            {
                await PiholeLogin(c);
                continue;
            }
            return r.Json();
        }
        throw ServiceException.Unauthorized();
    }

    /// <summary>Pi-hole limits sign-in sessions, so one is kept per integration and reused.</summary>
    private static async Task PiholeLogin(ServiceClient c)
    {
        var r = await c.Send("/api/auth", "POST", json: new Dictionary<string, object> { ["password"] = c.Secret });
        if (r.Status == 404) throw ServiceException.Http(404);
        if (r.Status == 429) throw new ServiceException("Pi-hole has too many sessions open. Try again in a few minutes.");
        var j = J.Parse(r.Text);
        if (j["session"]["valid"].Bool != true) throw ServiceException.Unauthorized();
        c.Set("pihole.sid", j["session"]["sid"].Str ?? "");
    }

    public static ServiceSnapshot ParsePiholeSummary(J j, J blocking)
    {
        var total = j["queries"].N("total");
        var blocked = j["queries"].N("blocked");
        var computed = total > 0 ? blocked / total * 100 : 0;
        var percent = j["queries"]["percent_blocked"].Num ?? computed;
        var s = new ServiceSnapshot
        {
            Headline = Fmt.F(percent, "0.0") + "%",
            Caption = "blocked today",
            Stats =
            {
                new("Queries", Fmt.Count(total)),
                new("Blocked", Fmt.Count(blocked)),
                new("Clients", j["clients"].W("active").ToString()),
                new("Blocklist", Fmt.Count(j["gravity"].N("domains_being_blocked"))),
            },
            Gauges = { new("Blocked", percent / 100, Fmt.Count(blocked)) },
        };
        if (blocking["blocking"].Str is { } state) s.IsOn = state == "enabled";
        return s;
    }

    public static ServiceSnapshot ParsePiholeV5(J j)
    {
        var total = j.N("dns_queries_today");
        var blocked = j.N("ads_blocked_today");
        var computed = total > 0 ? blocked / total * 100 : 0;
        var percent = j["ads_percentage_today"].Num ?? computed;
        var s = new ServiceSnapshot
        {
            Headline = Fmt.F(percent, "0.0") + "%",
            Caption = "blocked today",
            Stats =
            {
                new("Queries", Fmt.Count(total)),
                new("Blocked", Fmt.Count(blocked)),
                new("Clients", j.W("unique_clients").ToString()),
                new("Blocklist", Fmt.Count(j.N("domains_being_blocked"))),
            },
            Gauges = { new("Blocked", percent / 100, Fmt.Count(blocked)) },
        };
        if (j["status"].Str is { } status) s.IsOn = status == "enabled";
        return s;
    }

    private static async Task PiholeBlocking(ServiceClient c, bool on, int? seconds)
    {
        if (c.Value("pihole.v5") is not null)
        {
            var query = on ? "enable" : "disable" + (seconds is { } n ? "=" + n : "");
            (await c.Send("/admin/api.php?" + query + "&auth=" + Fmt.Query(c.Secret))).Check();
            return;
        }
        var body = new Dictionary<string, object?> { ["blocking"] = on, ["timer"] = !on && seconds is { } secs ? secs : null };
        await PiholeV6(c, "/api/dns/blocking", "POST", body);
    }

    // MARK: - AdGuard Home

    public static async Task<ServiceSnapshot> AdGuard(ServiceClient c)
    {
        var stats = (await c.Send("/control/stats", headers: c.BasicAuth())).Json();
        J status;
        try { status = (await c.Send("/control/status", headers: c.BasicAuth())).Json(); } catch (ServiceException) { status = default; }
        return ParseAdGuard(stats, status);
    }

    public static ServiceSnapshot ParseAdGuard(J j, J status)
    {
        var total = j.N("num_dns_queries");
        var filtered = j.N("num_blocked_filtering");
        var safeBrowsing = j.N("num_replaced_safebrowsing");
        var parental = j.N("num_replaced_parental");
        var blocked = filtered + safeBrowsing + parental;
        var fraction = total > 0 ? blocked / total : 0;
        var ms = j.N("avg_processing_time") * 1000;
        return new ServiceSnapshot
        {
            Headline = Fmt.Percent(fraction, 1),
            Caption = "blocked",
            Stats =
            {
                new("Queries", Fmt.Count(total)),
                new("Blocked", Fmt.Count(blocked)),
                new("Avg time", Fmt.F(ms, "0") + " ms"),
            },
            Gauges = { new("Blocked", fraction, Fmt.Count(blocked)) },
            IsOn = status["protection_enabled"].Bool,
        };
    }

    private static async Task AdGuardProtection(ServiceClient c, bool on, int? seconds)
    {
        var body = new Dictionary<string, object> { ["enabled"] = on };
        if (!on && seconds is { } s) body["duration"] = s * 1000;
        var r = await c.Send("/control/protection", "POST", c.BasicAuth(), body);
        if (r.Status is 404 or 405)
        {
            // Older AdGuard Home versions.
            (await c.Send("/control/dns_config", "POST", c.BasicAuth(), new Dictionary<string, object> { ["protection_enabled"] = on })).Check();
            return;
        }
        r.Check();
    }

    // MARK: - Home Assistant

    private static Dictionary<string, string> HaHeaders(ServiceClient c) => new() { ["Authorization"] = "Bearer " + c.Secret };

    public static async Task<List<PickableEntity>> HaEntities(ServiceClient c)
    {
        var j = (await c.Send("/api/states", headers: HaHeaders(c))).Json();
        return j.Array
            .Where(e => e["entity_id"].Str is not null)
            .Select(e => new PickableEntity(e["entity_id"].Str!, e["attributes"]["friendly_name"].Str ?? e["entity_id"].Str!, e["state"].Str ?? ""))
            .OrderBy(e => HomeAssistant.Rank(e.Domain))
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static async Task<ServiceSnapshot> HaEntity(ServiceClient c, string id)
    {
        var r = await c.Send("/api/states/" + Fmt.Segment(id), headers: HaHeaders(c));
        if (r.Status == 404) throw new ServiceException($"Home Assistant has no entity {id}.");
        return HomeAssistant.Parse(r.Json());
    }

    private static async Task HaActivate(ServiceClient c, string id)
    {
        var domain = HomeAssistant.Domain(id);
        var state = "";
        if (domain == "lock")
        {
            try { state = (await c.Send("/api/states/" + Fmt.Segment(id), headers: HaHeaders(c))).Json()["state"].Str ?? ""; }
            catch (ServiceException) { }
        }
        if (HomeAssistant.Service(domain, state) is not { } service)
            throw new ServiceException($"{id} can't be switched from a tile.");
        (await c.Send($"/api/services/{service.Domain}/{service.Name}", "POST", HaHeaders(c),
            new Dictionary<string, object> { ["entity_id"] = id })).Check();
    }

    // MARK: - UniFi Network

    private static async Task UnifiLogin(ServiceClient c)
    {
        var body = new Dictionary<string, object> { ["username"] = c.Integration.Username, ["password"] = c.Secret, ["remember"] = true };
        // UniFi OS consoles (UDM, UCG, Cloud Key Gen2+).
        var os = await c.Send("/api/auth/login", "POST", json: body);
        if (os.Ok)
        {
            c.Set("unifi.prefix", "/proxy/network");
            c.Set("unifi.csrf", os.Headers.GetValueOrDefault("x-csrf-token"));
            return;
        }
        if (os.Status is 401 or 403) throw ServiceException.Unauthorized();
        // The classic Network Application (usually port 8443).
        var classic = await c.Send("/api/login", "POST", json: body);
        if (classic.Ok)
        {
            c.Set("unifi.prefix", "");
            return;
        }
        if (classic.Status is 400 or 401 or 403) throw ServiceException.Unauthorized();
        throw ServiceException.Http(classic.Status);
    }

    public static async Task<ServiceSnapshot> Unifi(ServiceClient c)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (c.Value("unifi.prefix") is null) await UnifiLogin(c);
            var headers = new Dictionary<string, string>();
            if (c.Value("unifi.csrf") is { } csrf) headers["X-CSRF-Token"] = csrf;
            var r = await c.Send((c.Value("unifi.prefix") ?? "") + "/api/s/default/stat/health", headers: headers);
            if (r.Status is 401 or 403 && attempt == 0)
            {
                c.Set("unifi.prefix", null);
                continue;
            }
            return ParseUniFiHealth(r.Json());
        }
        throw ServiceException.Unauthorized();
    }

    public static ServiceSnapshot ParseUniFiHealth(J j)
    {
        var sub = new Dictionary<string, J>();
        foreach (var item in j["data"].Array)
            if (item["subsystem"].Str is { } name) sub[name] = item;
        var wlan = sub.GetValueOrDefault("wlan");
        var lan = sub.GetValueOrDefault("lan");
        var wan = sub.GetValueOrDefault("wan");
        var www = sub.GetValueOrDefault("www");
        var wifi = wlan.W("num_user") + wlan.W("num_guest");
        var wired = lan.W("num_user") + lan.W("num_guest");
        var devices = wlan.W("num_ap") + lan.W("num_sw") + wan.W("num_gw");
        var s = new ServiceSnapshot
        {
            Headline = (wifi + wired).ToString(),
            Caption = "clients",
            Stats = { new("Wi-Fi", wifi.ToString()), new("Wired", wired.ToString()), new("Devices", devices.ToString()) },
        };
        if (www["latency"].Num is { } latency) s.Stats.Add(new("Latency", Fmt.F(latency, "0") + " ms"));
        if (www["xput_down"].Num is { } down && www["xput_up"].Num is { } up && down > 0)
            s.Stats.Add(new("Speed", $"↓{Fmt.Speed(down)} ↑{Fmt.Speed(up)}"));
        if (wan["wan_ip"].Str is { } ip) s.Stats.Add(new("WAN IP", ip));
        var gw = wan["gw_system-stats"];
        if (gw["cpu"].Num is { } cpu) s.Gauges.Add(new("Gateway CPU", cpu / 100, Fmt.Percent(cpu / 100)));
        if (gw["mem"].Num is { } mem) s.Gauges.Add(new("Gateway memory", mem / 100, Fmt.Percent(mem / 100)));
        if ((www["status"].Str ?? wan["status"].Str) is { } status) s.IsOn = status == "ok";
        return s;
    }

    // MARK: - OPNsense

    /// <summary>
    /// API key and secret as HTTP Basic auth. Newer OPNsense uses snake_case commands (system_time);
    /// older releases used camelCase (systemTime), so both are tried. Each part is optional, so a key
    /// without every privilege still shows what it can see.
    /// </summary>
    public static async Task<ServiceSnapshot> Opnsense(ServiceClient c)
    {
        var auth = c.BasicAuth();
        ServiceException? firstError = null;
        async Task<J> Get(string snake, string? camel = null)
        {
            try
            {
                var r = await c.Send("/api/" + snake, headers: auth);
                if (r.Status == 404 && camel is not null) return (await c.Send("/api/" + camel, headers: auth)).Json();
                return r.Json();
            }
            catch (ServiceException e)
            {
                if (firstError is null || e.IsUnauthorized) firstError = e;
                return default;
            }
        }

        var traffic = new List<ServiceTraffic>();
        var history = new List<double>();
        if (ParseOpnsenseCounters(await Get("diagnostics/traffic/interface", "diagnostics/traffic/_interface"), Now()) is { } sample)
        {
            var now = sample;
            var previous = c.TrafficSample();
            // First look (or a long gap): take a second reading a moment later so speeds show right away.
            if (previous is null || now.Time - previous.Time > 120)
            {
                previous = now;
                await Task.Delay(1500);
                if (ParseOpnsenseCounters(await Get("diagnostics/traffic/interface", "diagnostics/traffic/_interface"), Now()) is { } later) now = later;
            }
            traffic = OpnsenseRates(previous, now);
            c.SetTrafficSample(now);
            var wan = traffic.FirstOrDefault(t => t.Id == "wan") ?? traffic.FirstOrDefault();
            if (wan is not null) history = c.Record(wan.Down);
        }

        var time = await Get("diagnostics/system/system_time", "diagnostics/system/systemTime");
        var resources = await Get("diagnostics/system/system_resources", "diagnostics/system/systemResources");
        var info = await Get("diagnostics/system/system_information", "diagnostics/system/systemInformation");
        var states = await Get("diagnostics/firewall/pf_states", "diagnostics/firewall/pfStates");
        var gateways = await Get("routes/gateway/status");
        if (traffic.Count == 0 && !time.Exists && !resources.Exists && !info.Exists && !states.Exists && !gateways.Exists)
            throw firstError ?? new ServiceException("OPNsense didn't return any data.");
        return ParseOpnsense(time, resources, info, states, gateways, traffic, history);
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// Interface byte counters from diagnostics/traffic/interface. Field names have varied between
    /// releases, so several spellings are accepted.
    /// </summary>
    public static TrafficSample? ParseOpnsenseCounters(J j, double receivedAt)
    {
        var list = j["interfaces"].Exists ? j["interfaces"].Dict : j.Dict;
        var counters = new Dictionary<string, TrafficCounter>();
        static double? Number(J e, params string[] keys)
        {
            foreach (var k in keys) if (e[k].Num is { } v) return v;
            return null;
        }
        foreach (var (id, e) in list)
        {
            if (Number(e, "inbytes", "ibytes", "bytes received", "bytes_received", "rx_bytes", "in_bytes") is not { } inBytes ||
                Number(e, "outbytes", "obytes", "bytes transmitted", "bytes_transmitted", "tx_bytes", "out_bytes") is not { } outBytes) continue;
            var device = e["device"].Str ?? "";
            if (device.StartsWith("lo") || id.Equals("lo0", StringComparison.OrdinalIgnoreCase)) continue;     // loopback
            var name = e["name"].Str ?? e["descr"].Str ?? e["description"].Str ?? id.ToUpperInvariant();
            counters[id] = new TrafficCounter(name, inBytes, outBytes);
        }
        if (counters.Count == 0) return null;
        return new TrafficSample(j["time"].Num ?? receivedAt, counters);
    }

    /// <summary>Speeds between two readings. WAN first, then the busiest interfaces.</summary>
    public static List<ServiceTraffic> OpnsenseRates(TrafficSample a, TrafficSample b)
    {
        var seconds = b.Time - a.Time;
        if (seconds <= 0.2) return new();
        return b.Counters
            .Where(kv => a.Counters.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var before = a.Counters[kv.Key];
                // Counters reset when an interface restarts; don't show a negative speed.
                return new ServiceTraffic(kv.Key, kv.Value.Name,
                    Math.Max(0, kv.Value.InBytes - before.InBytes) / seconds,
                    Math.Max(0, kv.Value.OutBytes - before.OutBytes) / seconds);
            })
            .OrderBy(t => t.Id == "wan" ? 0 : 1)
            .ThenByDescending(t => t.Down + t.Up)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static bool OpnsenseGatewayOnline(J g)
    {
        var status = (g["status"].Str ?? "").ToLowerInvariant();
        var translated = (g["status_translated"].Str ?? "").ToLowerInvariant();
        if (translated.Length > 0) return translated == "online";
        return status is "none" or "online";
    }

    public static ServiceSnapshot ParseOpnsense(J time, J resources, J info, J states, J gateways,
                                                List<ServiceTraffic>? traffic = null, List<double>? history = null)
    {
        traffic ??= new();
        history ??= new();
        var s = new ServiceSnapshot { Traffic = traffic };
        var gws = gateways["items"].Array.Where(g => g["name"].Exists).ToList();
        var online = gws.Where(OpnsenseGatewayOnline).ToList();
        var host = info["name"].Str ?? "OPNsense";
        if (traffic.FirstOrDefault() is { } main)
        {
            // The headline is live WAN download speed; upload goes in the caption.
            s.Headline = Fmt.Bitrate(main.Down);
            s.Caption = $"↓ {main.Name} · ↑ {Fmt.Bitrate(main.Up)}";
            var peak = history.DefaultIfEmpty(0).Max();
            if (history.Count > 1 && peak > 0) s.Series = history.Select(v => v / peak).ToList();
        }
        else if (gws.Count == 0)
        {
            s.Headline = "Online";
            s.Caption = host;
        }
        else
        {
            s.Headline = $"{online.Count}/{gws.Count}";
            s.Caption = gws.Count == 1 ? "gateway online" : "gateways online";
        }
        if (gws.Count > 0)
        {
            s.IsOn = online.Count == gws.Count;
            if (traffic.Count > 0) s.Stats.Add(new("Gateways", $"{online.Count}/{gws.Count} online"));
        }
        var memory = resources["memory"];
        if (memory["used"].Num is { } used && memory["total"].Num is { } total && total > 0)
            s.Gauges.Add(new("Memory", used / total, $"{Fmt.Bytes(used)} of {Fmt.Bytes(total)}"));
        if (states["current"].Num is { } current && states["limit"].Num is { } limit && limit > 0)
            s.Gauges.Add(new("Firewall states", current / limit, $"{Fmt.Count(current)} of {Fmt.Count(limit)}"));
        if (time["uptime"].Str is { } uptime) s.Stats.Add(new("Uptime", uptime));
        if (time["loadavg"].Str is { } load) s.Stats.Add(new("Load", load.Replace(",", "")));
        if (info["versions"][0].Str is { } version) s.Stats.Add(new("Version", version.Replace("OPNsense ", "")));
        if (gws.Count > 0 || traffic.Count > 0) s.Stats.Add(new("Host", host));
        foreach (var g in gws)
        {
            var name = g["name"].Str ?? "Gateway";
            var loss = g["loss"].Str is { } l && l != "~" ? l + " loss" : null;
            var parts = new[] { g["status_translated"].Str, g["delay"].Str, loss }.Where(p => !string.IsNullOrEmpty(p) && p != "~");
            s.Items.Add(new ServiceItem(name, name) { Subtitle = string.Join(" · ", parts), IsActive = OpnsenseGatewayOnline(g) });
        }
        return s;
    }
}
