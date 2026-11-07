using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace WinTiles.Core;

/// <summary>
/// One connection to one service. Keeps cookies and sign-in sessions between polls, and can accept
/// the self-signed certificates homelab servers usually have (only when the integration allows it).
/// </summary>
public sealed class ServiceClient : IDisposable
{
    public sealed record Response(int Status, byte[] Bytes, Dictionary<string, string> Headers)
    {
        public bool Ok => Status is >= 200 and < 300;
        public string Text => Encoding.UTF8.GetString(Bytes);

        public void Check()
        {
            if (Status is 401 or 403) throw ServiceException.Unauthorized();
            if (!Ok) throw ServiceException.Http(Status);
        }

        public J Json()
        {
            Check();
            return J.Parse(Text);
        }
    }

    public Integration Integration { get; }
    public string Secret { get; }
    /// <summary>Identifies the settings this client was made with; a change makes a new client.</summary>
    public string Fingerprint { get; }
    /// <summary>ComfyUI's live progress feed, for ComfyUI connections only.</summary>
    public ComfyMonitor? Comfy { get; }

    private readonly HttpClient http;
    private HttpClient? dockerPipe;
    private readonly object gate = new();
    private readonly Dictionary<string, string> state = new();
    private TrafficSample? traffic;
    private readonly List<double> history = new();

    public ServiceClient(Integration integration, string secret)
    {
        Integration = integration.Copy();
        Secret = secret;
        Fingerprint = MakeFingerprint(integration, secret);
        Comfy = integration.Kind == ServiceKind.Comfyui ? new ComfyMonitor() : null;
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,     // followed by hand, so keys aren't sent to another server
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (integration.AllowSelfSigned)
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = AcceptSelfSigned };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTiles");
    }

    /// <summary>
    /// "Allow self-signed certificate" applies to the service's own server only — never to
    /// another site it links to (posters) or redirects to.
    /// </summary>
    private bool AcceptSelfSigned(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
                                  System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None || sender is not SslStream stream || TrustsSelfSigned(Integration, stream.TargetHostName);

    public static bool TrustsSelfSigned(Integration integration, string? host) =>
        integration.AllowSelfSigned && integration.BaseUri?.Host is { } own && !string.IsNullOrEmpty(host) &&
        string.Equals(own, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>The request to send after a redirect. Another server doesn't get the service's key, token or session headers.</summary>
    public static (Uri Url, string Method, bool KeepHeaders, bool KeepBody) Redirect(Uri from, Uri to, string method, int status)
    {
        var sameHost = string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase);
        var keepMethod = status is 307 or 308;
        return (to, keepMethod ? method : "GET", sameHost, keepMethod && sameHost);
    }

    public static string MakeFingerprint(Integration i, string secret) =>
        string.Join("|", i.Kind, i.Url, i.Username, i.AllowSelfSigned, secret.GetHashCode());

    public void Dispose()
    {
        Comfy?.Stop();
        http.Dispose();
        dockerPipe?.Dispose();
    }

    // Session state: Pi-hole session IDs, UniFi's CSRF token, cached titles…
    public string? Value(string key) { lock (gate) return state.TryGetValue(key, out var v) ? v : null; }

    public void Set(string key, string? value)
    {
        lock (gate)
        {
            if (value is null) state.Remove(key); else state[key] = value;
        }
    }

    /// <summary>The previous interface counters, to turn byte totals into speeds.</summary>
    public TrafficSample? TrafficSample() { lock (gate) return traffic; }
    public void SetTrafficSample(TrafficSample sample) { lock (gate) traffic = sample; }

    /// <summary>Keeps the last 30 values of a reading (for a sparkline) and returns them.</summary>
    public List<double> Record(double value)
    {
        lock (gate)
        {
            history.Add(value);
            if (history.Count > 30) history.RemoveRange(0, history.Count - 30);
            return history.ToList();
        }
    }

    public Uri Base => Integration.BaseUri ?? throw ServiceException.BadAddress();

    /// <summary>Sends a request to <paramref name="path"/> (which may include a query string) under the service's address.</summary>
    public Task<Response> Send(string path, string method = "GET", IReadOnlyDictionary<string, string>? headers = null,
                               object? json = null, Uri? absolute = null) =>
        SendWith(http, absolute ?? new Uri(Integration.Base(Base) + path), method, headers, json);

    private async Task<Response> SendWith(HttpClient client, Uri url, string method, IReadOnlyDictionary<string, string>? headers, object? json)
    {
        try
        {
            var response = await SendOnce(client, url, method, headers, json).ConfigureAwait(false);
            // Follow up to 5 redirects; one to another server goes without the service's credentials.
            for (var hop = 0; hop < 5 && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location; hop++)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (target.Scheme is not ("http" or "https")) break;
                var next = Redirect(url, target, method, (int)response.StatusCode);
                response.Dispose();
                url = next.Url;
                method = next.Method;
                if (!next.KeepHeaders) headers = null;
                if (!next.KeepBody) json = null;
                response = await SendOnce(client, url, method, headers, json).ConfigureAwait(false);
            }
            using var _ = response;
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var map = new Dictionary<string, string>();
            foreach (var h in response.Headers.Concat(response.Content.Headers))
                map[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
            return new Response((int)response.StatusCode, bytes, map);
        }
        catch (TaskCanceledException)
        {
            throw new ServiceException("Timed out. Is the service running?");
        }
        catch (HttpRequestException ex)
        {
            throw Explain(ex, Integration.AllowSelfSigned);
        }
    }

    private static Task<HttpResponseMessage> SendOnce(HttpClient client, Uri url, string method, IReadOnlyDictionary<string, string>? headers, object? json)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Accept.ParseAdd("application/json");
        if (headers is not null)
            foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        if (json is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        else if (method is "POST" or "PUT")
            request.Content = new ByteArrayContent(Array.Empty<byte>());
        return client.SendAsync(request);
    }

    public static ServiceException Explain(Exception ex, bool selfSignedAllowed)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is ServiceException already) return already;
            if (e is AuthenticationException)
                return new ServiceException(selfSignedAllowed
                    ? "The secure connection failed."
                    : "The server's certificate isn't trusted. Turn on \"Allow self-signed certificate\" if it's yours.");
            if (e is SocketException se)
                return new ServiceException(se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "Couldn't find that server.",
                    SocketError.ConnectionRefused => "Couldn't connect. Check the address and port.",
                    SocketError.TimedOut => "Timed out. Is the service running?",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => "No route to that server.",
                    _ => se.Message,
                });
        }
        return new ServiceException(ex.Message);
    }

    /// <summary>Fetches an image (a poster) through this connection, so self-signed servers work too.</summary>
    public async Task<byte[]> Image(Uri url)
    {
        var r = await Send("", absolute: url).ConfigureAwait(false);
        r.Check();
        return r.Bytes;
    }

    // MARK: Docker on this PC

    /// <summary>
    /// Docker Desktop serves its API on the \\.\pipe\docker_engine named pipe. If the integration
    /// has an address (a remote Docker API), that's used instead.
    /// </summary>
    public Task<Response> Docker(string method, string path)
    {
        if (!string.IsNullOrWhiteSpace(Integration.Url)) return Send(path, method);
        dockerPipe ??= new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { await pipe.ConnectAsync(3000, ct).ConfigureAwait(false); }
                    catch (Exception) { pipe.Dispose(); throw new ServiceException("Docker isn't running on this PC."); }
                    return pipe;
                }
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), ct).ConfigureAwait(false); }
                catch (Exception) { socket.Dispose(); throw new ServiceException("Docker isn't running."); }
                return new NetworkStream(socket, ownsSocket: true);
            },
        }) { Timeout = TimeSpan.FromSeconds(10) };
        return SendDocker(method, path);
    }

    private Task<Response> SendDocker(string method, string path) =>
        IsSafeDockerRequest(method, path)
            ? SendWith(dockerPipe!, new Uri("http://docker" + path), method, null, null)
            : throw new ServiceException("Refused an unusual Docker request.");

    /// <summary>Only GET/POST, and a path that can't smuggle extra lines into the request.</summary>
    public static bool IsSafeDockerRequest(string method, string path) =>
        method is "GET" or "POST" && path.StartsWith('/') && path.IndexOfAny(new[] { ' ', '\r', '\n', '\t' }) < 0;

    // MARK: WebSockets (TrueNAS JSON-RPC, ComfyUI progress)

    public ClientWebSocket NewWebSocket()
    {
        var socket = new ClientWebSocket();
        if (Integration.AllowSelfSigned) socket.Options.RemoteCertificateValidationCallback = AcceptSelfSigned;
        return socket;
    }

    /// <summary>ws:// or wss:// for a path on the service.</summary>
    public Uri WebSocketUri(string pathAndQuery)
    {
        var b = new UriBuilder(Base) { Scheme = Base.Scheme == "https" ? "wss" : "ws" };
        var basePath = b.Path.TrimEnd('/');
        var q = pathAndQuery.IndexOf('?');
        b.Path = basePath + (q >= 0 ? pathAndQuery[..q] : pathAndQuery);
        b.Query = q >= 0 ? pathAndQuery[(q + 1)..] : "";
        return b.Uri;
    }

    /// <summary>Basic auth from the integration's username and secret.</summary>
    public Dictionary<string, string> BasicAuth()
    {
        if (Integration.Username.Length == 0 && Secret.Length == 0) return new();
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Integration.Username}:{Secret}"));
        return new() { ["Authorization"] = "Basic " + token };
    }
}

/// <summary>Byte counters for every interface at one moment.</summary>
public sealed record TrafficSample(double Time, Dictionary<string, TrafficCounter> Counters);
public sealed record TrafficCounter(string Name, double InBytes, double OutBytes);
