using System.Net.WebSockets;
using System.Text;

namespace WinTiles.Core;

/// <summary>
/// Listens to ComfyUI's WebSocket for step-by-step progress (and Crystools' monitor, if installed)
/// between polls. It disconnects by itself about 90 seconds after Start stops asking.
/// </summary>
public sealed class ComfyMonitor
{
    public sealed record Live
    {
        public string? PromptId { get; init; }
        public double Value { get; init; }
        public double Max { get; init; }
        public int? QueueRemaining { get; init; }
        // From the Crystools custom node, when installed.
        public double? Cpu { get; init; }
        public double? Gpu { get; init; }
        public double? GpuTemperature { get; init; }
        public double? VramPercent { get; init; }
        public double? RamPercent { get; init; }
        public DateTimeOffset? MonitorAt { get; init; }
    }

    private readonly object gate = new();
    private Live live = new();
    private ClientWebSocket? socket;
    private DateTimeOffset wantedAt = DateTimeOffset.UtcNow;

    public Live Snapshot()
    {
        lock (gate)
        {
            // Crystools figures go stale if the node stops sending.
            if (live.MonitorAt is { } at && (DateTimeOffset.UtcNow - at).TotalSeconds > 15)
                return live with { Cpu = null, Gpu = null, GpuTemperature = null, VramPercent = null, RamPercent = null };
            return live;
        }
    }

    /// <summary>Opens the feed if it isn't open. Called on every poll.</summary>
    public void EnsureRunning(ServiceClient client)
    {
        ClientWebSocket ws;
        lock (gate)
        {
            wantedAt = DateTimeOffset.UtcNow;
            if (socket is { State: WebSocketState.Open or WebSocketState.Connecting }) return;
            ws = client.NewWebSocket();
            socket = ws;
        }
        Uri uri;
        try { uri = client.WebSocketUri("/ws?clientId=wintiles-" + Guid.NewGuid()); }
        catch (ServiceException) { return; }
        _ = Task.Run(() => Listen(ws, uri));
    }

    public void Stop()
    {
        ClientWebSocket? ws;
        lock (gate) { ws = socket; socket = null; }
        try { ws?.Abort(); ws?.Dispose(); } catch { }
    }

    private async Task Listen(ClientWebSocket ws, Uri uri)
    {
        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
            _ = Task.Run(() => Watchdog(ws));
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                // Binary frames are image previews; only text frames carry events.
                if (result.MessageType == WebSocketMessageType.Text) Handle(Encoding.UTF8.GetString(message.ToArray()));
                message.SetLength(0);
            }
        }
        catch (Exception) { }
        finally
        {
            lock (gate) { if (socket == ws) socket = null; }
            try { ws.Abort(); ws.Dispose(); } catch { }
        }
    }

    /// <summary>Closes the feed once Start has stopped asking for a while.</summary>
    private async Task Watchdog(ClientWebSocket ws)
    {
        while (ws.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            bool idle;
            lock (gate) idle = (DateTimeOffset.UtcNow - wantedAt).TotalSeconds > 90 || socket != ws;
            if (idle) { try { ws.Abort(); } catch { } return; }
        }
    }

    public void Handle(string text)
    {
        J j;
        try { j = J.Parse(text); } catch (ServiceException) { return; }
        if (j["type"].Str is not { } type) return;
        lock (gate) live = Apply(type, j["data"], live, DateTimeOffset.UtcNow);
    }

    /// <summary>Updates the live state with one message. Pure, for tests.</summary>
    public static Live Apply(string type, J d, Live state, DateTimeOffset now)
    {
        switch (type)
        {
            case "status":
                return state with { QueueRemaining = d["status"]["exec_info"]["queue_remaining"].Int };
            case "execution_start":
                return state with { PromptId = d["prompt_id"].Str, Value = 0, Max = 0 };
            case "progress":
                return state with { PromptId = d["prompt_id"].Str ?? state.PromptId, Value = d["value"].Num ?? 0, Max = d["max"].Num ?? 0 };
            case "executing":
                if (!d["node"].Exists && d["prompt_id"].Str == state.PromptId)
                    return state with { PromptId = null, Value = 0, Max = 0 };     // node null: the run finished
                return d["prompt_id"].Str is { } id ? state with { PromptId = id } : state;
            case "execution_success":
            case "execution_error":
            case "execution_interrupted":
                var pid = d["prompt_id"].Str;
                return pid is null || pid == state.PromptId ? state with { PromptId = null, Value = 0, Max = 0 } : state;
            case "crystools.monitor":
                var gpu = d["gpus"][0];
                return state with
                {
                    Cpu = d["cpu_utilization"].Num,
                    RamPercent = d["ram_used_percent"].Num,
                    Gpu = gpu["gpu_utilization"].Num,
                    GpuTemperature = gpu["gpu_temperature"].Num,
                    VramPercent = gpu["vram_used_percent"].Num,
                    MonitorAt = now,
                };
            default:
                return state;
        }
    }
}
