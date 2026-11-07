using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WinTiles.Core;

public static partial class ServiceApi
{
    // MARK: - Proxmox VE

    public static async Task<ServiceSnapshot> Proxmox(ServiceClient c)
    {
        var auth = new Dictionary<string, string> { ["Authorization"] = $"PVEAPIToken={c.Integration.Username}={c.Secret}" };
        return ParseProxmox((await c.Send("/api2/json/cluster/resources", headers: auth)).Json());
    }

    public static ServiceSnapshot ParseProxmox(J j)
    {
        var s = new ServiceSnapshot();
        var all = j["data"].Array.ToList();
        var nodes = all.Where(r => r["type"].Str == "node").ToList();
        var guests = all.Where(r => r["type"].Str is "qemu" or "lxc").ToList();
        var seenStorage = new HashSet<string>();
        // Shared storage is listed once per node; count it once.
        var storage = all.Where(r => r["type"].Str == "storage" && r["status"].Str != "unknown" &&
                                     seenStorage.Add(r["shared"].Int == 1 ? r["storage"].Str ?? "" : r["id"].Str ?? "")).ToList();
        var online = nodes.Where(n => n["status"].Str == "online").ToList();
        var cores = online.Sum(n => n.N("maxcpu"));
        var busy = online.Sum(n => n.N("cpu") * n.N("maxcpu"));
        var mem = online.Sum(n => n.N("mem"));
        var maxMem = online.Sum(n => n.N("maxmem"));
        var disk = storage.Sum(r => r.N("disk"));
        var maxDisk = storage.Sum(r => r.N("maxdisk"));
        var running = guests.Count(g => g["status"].Str == "running");

        s.Headline = $"{running}/{guests.Count}";
        s.Caption = "guests running";
        if (cores > 0) s.Gauges.Add(new("CPU", busy / cores, Fmt.Percent(busy / cores)));
        if (maxMem > 0) s.Gauges.Add(new("Memory", mem / maxMem, $"{Fmt.Bytes(mem)} of {Fmt.Bytes(maxMem)}"));
        if (maxDisk > 0) s.Gauges.Add(new("Storage", disk / maxDisk, $"{Fmt.Bytes(disk)} of {Fmt.Bytes(maxDisk)}"));
        s.Stats.Add(new("Nodes", $"{online.Count}/{nodes.Count} online"));
        if (online.Count > 0) s.Stats.Add(new("Uptime", Fmt.Duration(online.Max(n => n.N("uptime")))));
        s.Items = guests
            .OrderBy(g => g["status"].Str == "running" ? 0 : 1)
            .ThenBy(g => g["name"].Str ?? "", StringComparer.Ordinal)
            .Take(12)
            .Select(g =>
            {
                var isRunning = g["status"].Str == "running";
                var vmid = g.W("vmid");
                return new ServiceItem(g["id"].Str ?? Guid.NewGuid().ToString(), g["name"].Str ?? $"VM {vmid}")
                {
                    Subtitle = isRunning
                        ? $"{(g["type"].Str == "lxc" ? "CT" : "VM")} {vmid} · CPU {Fmt.Percent(g.N("cpu"))}"
                        : TitleCase(g["status"].Str ?? "stopped"),
                    IsActive = isRunning,
                };
            })
            .ToList();
        s.IsOn = online.Count > 0;
        return s;
    }

    // MARK: - TrueNAS

    public static async Task<ServiceSnapshot> TrueNas(ServiceClient c)
    {
        var auth = new Dictionary<string, string> { ["Authorization"] = "Bearer " + c.Secret };
        if (c.Value("truenas.rpc") is null)
        {
            var info = await c.Send("/api/v2.0/system/info", headers: auth);
            if (info.Status is not (404 or 410))
            {
                var pools = (await c.Send("/api/v2.0/pool", headers: auth)).Json();
                return ParseTrueNas(info.Json(), pools);
            }
            c.Set("truenas.rpc", "1");      // REST is gone on newer TrueNAS; use JSON-RPC.
        }
        var results = await TrueNasRpc(c, ("system.info", Array.Empty<object>()), ("pool.query", Array.Empty<object>()));
        return ParseTrueNas(results[0], results[1]);
    }

    /// <summary>TrueNAS 25.04+ JSON-RPC 2.0 over a WebSocket at /api/current.</summary>
    private static async Task<List<J>> TrueNasRpc(ServiceClient c, params (string Method, object[] Params)[] calls)
    {
        using var socket = c.NewWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await socket.ConnectAsync(c.WebSocketUri("/api/current"), timeout.Token);
            async Task<J> Call(int id, string method, object[] parameters)
            {
                var message = JsonSerializer.Serialize(new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[256 * 1024];
                while (true)
                {
                    var text = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        if (r.MessageType == WebSocketMessageType.Close) throw new ServiceException("TrueNAS closed the connection.");
                        text.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage);
                    var j = J.Parse(Encoding.UTF8.GetString(text.ToArray()));
                    if (j["id"].Int != id) continue;     // skip notifications
                    if (j["error"].Exists) throw new ServiceException(j["error"]["message"].Str ?? "TrueNAS returned an error.");
                    return j["result"];
                }
            }
            var login = await Call(1, "auth.login_with_api_key", new object[] { c.Secret });
            if (login.Bool != true) throw ServiceException.Unauthorized();
            var results = new List<J>();
            for (var i = 0; i < calls.Length; i++) results.Add(await Call(i + 2, calls[i].Method, calls[i].Params));
            return results;
        }
        catch (OperationCanceledException) { throw new ServiceException("Timed out. Is the service running?"); }
        catch (WebSocketException ex) { throw ServiceClient.Explain(ex, c.Integration.AllowSelfSigned); }
    }

    public static ServiceSnapshot ParseTrueNas(J info, J pools)
    {
        var s = new ServiceSnapshot();
        var list = pools.Array.ToList();
        var healthy = list.Count(p => p["healthy"].Bool ?? p["status"].Str == "ONLINE");
        s.Headline = list.Count == 0 ? "—" : healthy == list.Count ? "Healthy" : "Degraded";
        s.Caption = info["hostname"].Str ?? "TrueNAS";
        var cores = info.N("cores");
        if (cores > 0 && info["loadavg"][0].Num is { } load) s.Gauges.Add(new("Load", load / cores, Fmt.F(load, "0.00")));
        foreach (var p in list.Take(3))
        {
            var size = p.N("size");
            var used = p.N("allocated");
            if (size > 0) s.Gauges.Add(new(p["name"].Str ?? "Pool", used / size, $"{Fmt.Bytes(used)} of {Fmt.Bytes(size)}"));
        }
        if (info["uptime_seconds"].Num is { } up) s.Stats.Add(new("Uptime", Fmt.Duration(up)));
        if (info["version"].Str is { } version) s.Stats.Add(new("Version", version.Replace("TrueNAS-", "")));
        if (info["physmem"].Num is { } physmem) s.Stats.Add(new("Memory", Fmt.Bytes(physmem)));
        s.Items = list.Select(p => new ServiceItem(p["name"].Str ?? Guid.NewGuid().ToString(), p["name"].Str ?? "Pool")
        {
            Subtitle = p["status"].Str ?? "",
            IsActive = p["healthy"].Bool ?? false,
        }).ToList();
        s.IsOn = list.Count == 0 ? null : healthy == list.Count;
        return s;
    }

    // MARK: - Unraid (GraphQL API)

    private static async Task<J> UnraidQuery(ServiceClient c, string query)
    {
        var j = (await c.Send("/graphql", "POST", new Dictionary<string, string> { ["x-api-key"] = c.Secret },
            new Dictionary<string, object> { ["query"] = query })).Json();
        if (!j["data"].Exists && j["errors"][0]["message"].Str is { } message) throw new ServiceException(message);
        return j["data"];
    }

    private static async Task<J> Optional(Func<Task<J>> fetch)
    {
        try { return await fetch(); } catch (ServiceException) { return default; }
    }

    public static async Task<ServiceSnapshot> Unraid(ServiceClient c)
    {
        var info = await UnraidQuery(c, "{ info { os { hostname uptime } cpu { brand cores threads } } }");
        var array = await Optional(() => UnraidQuery(c, "{ array { state capacity { kilobytes { free used total } } } }"));
        var metrics = await Optional(() => UnraidQuery(c, "{ metrics { cpu { percentTotal } memory { percentTotal total used } } }"));
        var containers = await Optional(() => UnraidContainerList(c));
        return ParseUnraid(info, array, metrics, containers);
    }

    private static async Task<J> UnraidContainerList(ServiceClient c)
    {
        var first = await Optional(() => UnraidQuery(c, "{ dockerContainers { id names state status image } }"));
        if (first["dockerContainers"].IsArray) return first["dockerContainers"];
        return (await UnraidQuery(c, "{ docker { containers { id names state status image } } }"))["docker"]["containers"];
    }

    public static ServiceSnapshot ParseUnraid(J info, J array, J metrics, J containers, DateTimeOffset? now = null)
    {
        var s = new ServiceSnapshot();
        var state = array["array"]["state"].Str ?? "";
        s.Headline = state.Length == 0 ? "Online" : TitleCase(state);
        s.Caption = info["info"]["os"]["hostname"].Str ?? "Unraid";
        if (metrics["metrics"]["cpu"]["percentTotal"].Num is { } cpu) s.Gauges.Add(new("CPU", cpu / 100, Fmt.Percent(cpu / 100)));
        if (metrics["metrics"]["memory"]["percentTotal"].Num is { } mem) s.Gauges.Add(new("Memory", mem / 100, Fmt.Percent(mem / 100)));
        var kb = array["array"]["capacity"]["kilobytes"];
        if (kb["used"].Num is { } used && kb["total"].Num is { } total && total > 0)
            s.Gauges.Add(new("Array", used / total, $"{Fmt.Bytes(used * 1024)} of {Fmt.Bytes(total * 1024)}"));
        var list = containers.Array.ToList();
        if (list.Count > 0)
        {
            var running = list.Count(x => (x["state"].Str ?? "").Equals("running", StringComparison.OrdinalIgnoreCase));
            s.Stats.Add(new("Containers", $"{running}/{list.Count} running"));
        }
        if (Fmt.IsoDate(info["info"]["os"]["uptime"].Str) is { } boot)
            s.Stats.Add(new("Uptime", Fmt.Duration(((now ?? DateTimeOffset.UtcNow) - boot).TotalSeconds)));
        if (info["info"]["cpu"]["brand"].Str is { } brand) s.Stats.Add(new("CPU", brand));
        s.IsOn = state.Length == 0 ? null : state.Equals("STARTED", StringComparison.OrdinalIgnoreCase);
        return s;
    }

    public static async Task<ServiceSnapshot> UnraidContainers(ServiceClient c) =>
        ParseContainers(await UnraidContainerList(c), "id", "names", "state", "status");

    // MARK: - Glances

    public static async Task<ServiceSnapshot> Glances(ServiceClient c)
    {
        var headers = c.BasicAuth();
        var version = c.Value("glances.api") ?? "4";
        var cpu = await c.Send($"/api/{version}/cpu", headers: headers);
        if (cpu.Status == 404 && version == "4")
        {
            version = "3";
            cpu = await c.Send("/api/3/cpu", headers: headers);
        }
        c.Set("glances.api", version);
        var mem = (await c.Send($"/api/{version}/mem", headers: headers)).Json();
        async Task<J> Part(string name) => await Optional(async () => (await c.Send($"/api/{version}/{name}", headers: headers)).Json());
        return ParseGlances(cpu.Json(), mem, await Part("fs"), await Part("load"), await Part("uptime"), await Part("system"));
    }

    public static ServiceSnapshot ParseGlances(J cpu, J mem, J fs, J load, J uptime, J system)
    {
        var s = new ServiceSnapshot();
        var cpuPercent = cpu.N("total");
        s.Headline = Fmt.Percent(cpuPercent / 100);
        s.Caption = system["hostname"].Str is { } host ? "CPU · " + host : "CPU";
        s.Gauges.Add(new("CPU", cpuPercent / 100, Fmt.Percent(cpuPercent / 100)));
        if (mem["percent"].Num is { } percent)
        {
            var detail = mem["used"].Num is { } u && mem["total"].Num is { } t ? $"{Fmt.Bytes(u)} of {Fmt.Bytes(t)}" : Fmt.Percent(percent / 100);
            s.Gauges.Add(new("Memory", percent / 100, detail));
        }
        var disks = fs.Array.Where(d => d.N("size") > 0).ToList();
        var main = disks.FirstOrDefault(d => d["mnt_point"].Str == "/");
        if (!main.Exists && disks.Count > 0) main = disks.MaxBy(d => d.N("size"));
        if (main.Exists && main["percent"].Num is { } diskPercent)
            s.Gauges.Add(new($"Disk {main["mnt_point"].Str}", diskPercent / 100, $"{Fmt.Bytes(main.N("used"))} of {Fmt.Bytes(main.N("size"))}"));
        if (load["min1"].Num is { } min1)
            s.Stats.Add(new("Load", string.Join(" ", new[] { min1, load.N("min5"), load.N("min15") }.Select(v => Fmt.F(v, "0.00")))));
        if (uptime.Str is { } up) s.Stats.Add(new("Uptime", up.Split('.')[0]));
        s.Items = disks.Take(8).Select(d => new ServiceItem(d["mnt_point"].Str ?? Guid.NewGuid().ToString(), d["mnt_point"].Str ?? "Disk")
        {
            Subtitle = $"{Fmt.Percent(d.N("percent") / 100)} of {Fmt.Bytes(d.N("size"))}",
            Progress = d.N("percent") / 100,
        }).ToList();
        return s;
    }

    // MARK: - Docker

    public static async Task<ServiceSnapshot> Docker(ServiceClient c) =>
        ParseContainers((await c.Docker("GET", "/containers/json?all=1")).Json(), "Id", "Names", "State", "Status");

    private static async Task DockerAction(ServiceClient c, string id, string verb)
    {
        if (verb is not ("start" or "stop" or "restart")) return;
        var r = await c.Docker("POST", $"/containers/{Fmt.Segment(id)}/{verb}");
        if (r.Status == 304) return;      // already in that state
        r.Check();
    }

    public static ServiceSnapshot ParseContainers(J j, string idKey, string namesKey, string stateKey, string statusKey)
    {
        var list = j.Array.Select(x =>
        {
            var names = x[namesKey].Array.Select(n => n.Str).Where(n => n is not null).ToList();
            var name = (names.FirstOrDefault() ?? x[namesKey].Str ?? new string((x[idKey].Str ?? "").Take(12).ToArray())).Trim('/');
            var state = (x[stateKey].Str ?? "").ToLowerInvariant();
            return new ServiceItem(x[idKey].Str ?? name, name)
            {
                Subtitle = x[statusKey].Str ?? TitleCase(state),
                IsActive = state == "running",
            };
        })
        .OrderBy(i => i.IsActive ? 0 : 1)
        .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
        var running = list.Count(i => i.IsActive);
        return new ServiceSnapshot
        {
            Headline = running.ToString(),
            Caption = $"of {list.Count} running",
            Items = list,
            Stats = { new("Running", running.ToString()), new("Stopped", (list.Count - running).ToString()) },
            EmptyText = "No containers",
        };
    }

    // MARK: - Beszel (a PocketBase app)

    /// <summary>Signs in as a regular user, or a superuser (PocketBase 0.23+), or an admin (older PocketBase).</summary>
    private static async Task<string> BeszelLogin(ServiceClient c)
    {
        var body = new Dictionary<string, object> { ["identity"] = c.Integration.Username, ["password"] = c.Secret };
        var lastStatus = 0;
        foreach (var path in new[] { "/api/collections/users/auth-with-password", "/api/collections/_superusers/auth-with-password", "/api/admins/auth-with-password" })
        {
            var r = await c.Send(path, "POST", json: body);
            if (r.Ok)
            {
                string? token = null;
                try { token = J.Parse(r.Text)["token"].Str; } catch (ServiceException) { }
                if (token is not null)
                {
                    c.Set("beszel.token", token);
                    return token;
                }
            }
            lastStatus = r.Status;
        }
        if (lastStatus == 404) throw ServiceException.Http(404);
        throw ServiceException.Unauthorized();
    }

    private static async Task<J> BeszelGet(ServiceClient c, string path)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = c.Value("beszel.token") ?? await BeszelLogin(c);
            var r = await c.Send(path, headers: new Dictionary<string, string> { ["Authorization"] = token });
            if (r.Status is 401 or 403 && attempt == 0)
            {
                c.Set("beszel.token", null);     // expired: sign in again
                continue;
            }
            return r.Json();
        }
        throw ServiceException.Unauthorized();
    }

    public static async Task<ServiceSnapshot> Beszel(ServiceClient c, string? system) =>
        ParseBeszel(await BeszelGet(c, "/api/collections/systems/records?perPage=200&sort=name"), system);

    private static async Task<List<PickableEntity>> BeszelSystems(ServiceClient c)
    {
        var j = await BeszelGet(c, "/api/collections/systems/records?perPage=200&sort=name");
        return j["items"].Array.Where(s => s["id"].Str is not null)
            .Select(s => new PickableEntity(s["id"].Str!, s["name"].Str ?? s["id"].Str!, s["status"].Str ?? ""))
            .ToList();
    }

    /// <summary>
    /// Beszel keeps each system's latest readings in "info", with short keys: cpu, mp (memory %),
    /// dp (disk %), dt (top temperature), b (network MB/s), u (uptime s), g (GPU %), la (load averages),
    /// h (hostname), v (agent version).
    /// </summary>
    public static ServiceSnapshot ParseBeszel(J j, string? system)
    {
        var all = j["items"].Array.ToList();
        if (!string.IsNullOrEmpty(system))
        {
            var one = all.FirstOrDefault(x => x["id"].Str == system);
            return one.Exists ? BeszelSystem(one) : new ServiceSnapshot { Headline = "—", Caption = "System not found" };
        }
        var s = new ServiceSnapshot();
        var up = all.Where(x => x["status"].Str == "up").ToList();
        var watched = all.Where(x => x["status"].Str != "paused").ToList();
        s.Headline = $"{up.Count}/{watched.Count}";
        s.Caption = "systems up";
        double? Average(string key)
        {
            var values = up.Select(x => x["info"][key].Num).Where(v => v is not null).Select(v => v!.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }
        if (Average("cpu") is { } cpu) s.Gauges.Add(new("Average CPU", cpu / 100, Fmt.Percent(cpu / 100)));
        if (Average("mp") is { } mem) s.Gauges.Add(new("Average memory", mem / 100, Fmt.Percent(mem / 100)));
        if (Average("dp") is { } disk) s.Gauges.Add(new("Average disk", disk / 100, Fmt.Percent(disk / 100)));
        if (up.Count > 0)
        {
            var busiest = up.MaxBy(x => x["info"].N("cpu"));
            if (busiest["name"].Str is { } name) s.Stats.Add(new("Busiest", $"{name} · {Fmt.Percent(busiest["info"].N("cpu") / 100)}"));
        }
        var down = watched.Count - up.Count;
        if (down > 0) s.Stats.Add(new("Down", down.ToString()));
        s.Items = all.Select(x =>
        {
            var info = x["info"];
            var status = x["status"].Str ?? "";
            var isUp = status == "up";
            var subtitle = isUp
                ? $"CPU {Fmt.Percent(info.N("cpu") / 100)} · Mem {Fmt.Percent(info.N("mp") / 100)} · Disk {Fmt.Percent(info.N("dp") / 100)}"
                : status.Length == 0 ? "Unknown" : TitleCase(status);
            return new ServiceItem(x["id"].Str ?? Guid.NewGuid().ToString(), x["name"].Str ?? "System")
            {
                Subtitle = subtitle,
                Progress = isUp ? info.N("cpu") / 100 : null,
                IsActive = isUp,
            };
        })
        .OrderBy(i => i.IsActive ? 1 : 0)                // systems that aren't up come first
        .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
        s.IsOn = watched.Count == 0 ? null : down == 0;
        s.EmptyText = "No systems yet";
        return s;
    }

    /// <summary>One system in full.</summary>
    public static ServiceSnapshot BeszelSystem(J sys)
    {
        var s = new ServiceSnapshot();
        var info = sys["info"];
        var status = sys["status"].Str ?? "";
        var name = sys["name"].Str ?? "System";
        s.Name = name;
        s.IsOn = status == "paused" ? null : status == "up";
        if (status != "up")
        {
            s.Headline = status.Length == 0 ? "—" : TitleCase(status);
            s.Caption = name;
            return s;
        }
        var cpu = info.N("cpu");
        s.Headline = Fmt.Percent(cpu / 100);
        s.Caption = "CPU";
        s.Gauges.Add(new("CPU", cpu / 100, Fmt.Percent(cpu / 100)));
        if (info["mp"].Num is { } mem) s.Gauges.Add(new("Memory", mem / 100, Fmt.Percent(mem / 100)));
        if (info["dp"].Num is { } disk) s.Gauges.Add(new("Disk", disk / 100, Fmt.Percent(disk / 100)));
        if (info["g"].Num is { } gpu) s.Gauges.Add(new("GPU", gpu / 100, Fmt.Percent(gpu / 100)));
        if (info["dt"].Num is { } temp) s.Stats.Add(new("Temp", Fmt.F(temp, "0") + "°C"));
        if (info["b"].Num is { } net) s.Stats.Add(new("Network", Fmt.Bitrate(net * 1_048_576)));
        if (info["u"].Num is { } uptime) s.Stats.Add(new("Uptime", Fmt.Duration(uptime)));
        var loads = info["la"].Array.Select(x => x.Num).Where(v => v is not null).Select(v => Fmt.F(v!.Value, "0.00")).ToList();
        if (loads.Count > 0) s.Stats.Add(new("Load", string.Join(" ", loads)));
        if (info["h"].Str is { } host) s.Stats.Add(new("Host", host));
        if (info["v"].Str is { } version) s.Stats.Add(new("Agent", version));
        return s;
    }

    // MARK: - ComfyUI

    public static async Task<ServiceSnapshot> ComfyUi(ServiceClient c)
    {
        c.Comfy?.EnsureRunning(c);
        var stats = (await c.Send("/system_stats")).Json();
        var queue = await Optional(async () => (await c.Send("/queue")).Json());
        // History holds whole workflows, so it's read less often than the rest.
        List<ServiceItem> recent;
        var lastRead = double.TryParse(c.Value("comfy.historyAt"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : 0;
        var marker = $"{queue["queue_running"].Array.Count()}|{queue["queue_pending"].Array.Count()}";
        J history = default;
        if (Now() - lastRead > 15 || c.Value("comfy.queueMarker") != marker)
            history = await Optional(async () => (await c.Send("/history?max_items=8")).Json());
        if (history.Exists)
        {
            recent = ParseComfyHistory(history, c.Integration.BaseUri, DateTimeOffset.Now);
            c.Set("comfy.historyAt", Now().ToString(System.Globalization.CultureInfo.InvariantCulture));
            c.Set("comfy.queueMarker", marker);
            c.Set("comfy.recent", JsonSerializer.Serialize(recent.Select(i => new[] { i.Id, i.Title, i.Subtitle, i.Image?.AbsoluteUri ?? "", i.IsActive ? "1" : "0" })));
        }
        else recent = DecodeItems(c.Value("comfy.recent"));
        return ParseComfy(stats, queue, c.Comfy?.Snapshot() ?? new ComfyMonitor.Live(), recent);
    }

    private static List<ServiceItem> DecodeItems(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new();
        try
        {
            var rows = JsonSerializer.Deserialize<string[][]>(text) ?? Array.Empty<string[]>();
            return rows.Where(r => r.Length == 5).Select(r => new ServiceItem(r[0], r[1])
            {
                Subtitle = r[2],
                Image = r[3].Length > 0 && Uri.TryCreate(r[3], UriKind.Absolute, out var u) ? u : null,
                IsActive = r[4] == "1",
            }).ToList();
        }
        catch (JsonException) { return new(); }
    }

    /// <summary>"cuda:0 NVIDIA GeForce RTX 4090 : cudaMallocAsync" → "NVIDIA GeForce RTX 4090"</summary>
    public static string ComfyDeviceName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var name = raw;
        var colon = name.IndexOf(" : ", StringComparison.Ordinal);
        if (colon >= 0) name = name[..colon];
        var space = name.IndexOf(' ');
        if (space > 0 && name[..space].Contains(':')) name = name[(space + 1)..];
        return name.Trim();
    }

    public static ServiceSnapshot ParseComfy(J stats, J queue, ComfyMonitor.Live live, List<ServiceItem> recent)
    {
        var s = new ServiceSnapshot();
        var runningJobs = queue["queue_running"].Array.ToList();
        var pending = queue["queue_pending"].Array.Count();
        var isRunning = runningJobs.Count > 0 || live.PromptId is not null;
        double? fraction = live.Max > 0 ? Math.Min(live.Value / live.Max, 1) : null;
        s.Busy = isRunning;
        if (isRunning)
        {
            s.Headline = fraction is { } f ? Fmt.Percent(f) : "Running";
            var parts = new List<string> { live.Max > 0 ? $"step {(int)live.Value}/{(int)live.Max}" : "generating" };
            if (pending > 0) parts.Add($"{pending} queued");
            s.Caption = string.Join(" · ", parts);
            s.Progress = fraction;
        }
        else
        {
            s.Headline = "Idle";
            s.Caption = pending > 0 ? $"{pending} queued" : "queue empty";
        }

        // Resources: Crystools (if installed) for live load; ComfyUI itself for memory.
        if (live.Gpu is { } gpu) s.Gauges.Add(new("GPU", gpu / 100, Fmt.Percent(gpu / 100)));
        var device = stats["devices"][0];
        var vramTotal = device.N("vram_total");
        if (vramTotal > 0)
        {
            var used = vramTotal - device.N("vram_free");
            s.Gauges.Add(new("VRAM", used / vramTotal, $"{Fmt.Bytes(used)} of {Fmt.Bytes(vramTotal)}"));
        }
        else if (live.VramPercent is { } vram) s.Gauges.Add(new("VRAM", vram / 100, Fmt.Percent(vram / 100)));
        var ramTotal = stats["system"].N("ram_total");
        if (ramTotal > 0)
        {
            var used = ramTotal - stats["system"].N("ram_free");
            s.Gauges.Add(new("RAM", used / ramTotal, $"{Fmt.Bytes(used)} of {Fmt.Bytes(ramTotal)}"));
        }
        if (live.Cpu is { } cpu) s.Gauges.Add(new("CPU", cpu / 100, Fmt.Percent(cpu / 100)));

        s.Stats.Add(new("Queue", $"{runningJobs.Count} running · {pending} waiting"));
        if (live.GpuTemperature is { } temp) s.Stats.Add(new("GPU temp", Fmt.F(temp, "0") + "°C"));
        var gpuName = ComfyDeviceName(device["name"].Str);
        if (gpuName.Length > 0) s.Stats.Add(new("Device", gpuName));
        if (stats["system"]["comfyui_version"].Str is { } version) s.Stats.Add(new("Version", version));

        var items = recent.ToList();
        if (isRunning)
        {
            var id = live.PromptId ?? (runningJobs.Count > 0 ? runningJobs[0][1].Str : null) ?? "running";
            items.RemoveAll(i => i.Id == id);
            items.Insert(0, new ServiceItem(id, "Generating…") { Subtitle = s.Caption, Progress = fraction, IsActive = true });
        }
        s.Items = items;
        s.EmptyText = "No images yet";
        return s;
    }

    /// <summary>Recent runs, newest first, each with its first output image.</summary>
    public static List<ServiceItem> ParseComfyHistory(J j, Uri? baseUri, DateTimeOffset now)
    {
        var runs = new List<(ServiceItem Item, double Order)>();
        foreach (var (id, run) in j.Dict)
        {
            var messages = run["status"]["messages"].Array.ToList();
            double? Time(string kind) => messages.FirstOrDefault(m => m[0].Str == kind)[1]["timestamp"].Num;
            var started = Time("execution_start");
            var ended = Time("execution_success") ?? Time("execution_error") ?? Time("execution_interrupted");
            var failed = run["status"]["status_str"].Str == "error" || Time("execution_error") is not null;
            Uri? image = null;
            foreach (var (_, output) in run["outputs"].Dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                foreach (var picture in output["images"].Array)
                {
                    if (picture["type"].Str == "temp" || baseUri is null || picture["filename"].Str is not { } file) continue;
                    var sub = picture["subfolder"].Str ?? "";
                    var type = picture["type"].Str ?? "output";
                    image = new Uri($"{Integration.Base(baseUri)}/view?filename={Fmt.Query(file)}&subfolder={Fmt.Query(sub)}&type={Fmt.Query(type)}");
                    break;
                }
                if (image is not null) break;
            }
            var parts = new List<string> { failed ? "Failed" : "Done" };
            if (ended is { } e) parts.Add(Fmt.When(DateTimeOffset.FromUnixTimeMilliseconds((long)e), now));
            if (started is { } st && ended is { } en && en >= st) parts.Add(Fmt.F((en - st) / 1000, "0") + "s");
            var item = new ServiceItem(id, failed ? "Failed run" : "Image")
            {
                Subtitle = string.Join(" · ", parts),
                Image = image,
                IsActive = !failed,
            };
            runs.Add((item, ended ?? started ?? run["prompt"][0].Num ?? 0));
        }
        return runs.OrderByDescending(r => r.Order).Take(8).Select(r => r.Item).ToList();
    }
}
