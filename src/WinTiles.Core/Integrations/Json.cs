using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinTiles.Core;

/// <summary>A forgiving view into decoded JSON: missing keys give empty values instead of throwing.</summary>
public readonly struct J
{
    public readonly JsonNode? Node;

    public J(JsonNode? node) { Node = node; }

    public static J Parse(string text)
    {
        try
        {
            return new J(JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }));
        }
        catch (JsonException)
        {
            throw new ServiceException("The service didn't send the data WinTiles expected.");
        }
    }

    public J this[string key] => new(Node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null);
    public J this[int index] => new(Node is JsonArray a && index >= 0 && index < a.Count ? a[index] : null);

    /// <summary>False for missing keys and JSON null.</summary>
    public bool Exists => Node is not null;
    public bool IsArray => Node is JsonArray;
    public IEnumerable<J> Array => Node is JsonArray a ? a.Select(x => new J(x)).ToList() : Enumerable.Empty<J>();
    public IEnumerable<KeyValuePair<string, J>> Dict =>
        Node is JsonObject o ? o.Select(kv => new KeyValuePair<string, J>(kv.Key, new J(kv.Value))).ToList() : Enumerable.Empty<KeyValuePair<string, J>>();

    private JsonValueKind Kind => Node is JsonValue v ? v.GetValueKind() : JsonValueKind.Undefined;

    /// <summary>Strings, and numbers as text; not booleans.</summary>
    public string? Str => Kind switch
    {
        JsonValueKind.String => Node!.GetValue<string>(),
        JsonValueKind.Number => Node!.ToJsonString(),
        _ => null,
    };

    public double? Num => Kind switch
    {
        JsonValueKind.Number => Node!.GetValue<double>(),
        JsonValueKind.String => double.TryParse(Node!.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    public int? Int => Num is double d ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : null;

    public bool? Bool => Kind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => Node!.GetValue<double>() != 0,
        JsonValueKind.String => Node!.GetValue<string>().ToLowerInvariant() is "true" or "1" or "yes" or "on" or "enabled",
        _ => null,
    };

    /// <summary>A number under <paramref name="key"/>, or 0.</summary>
    public double N(string key) => this[key].Num ?? 0;
    /// <summary>A whole number under <paramref name="key"/>, or 0.</summary>
    public int W(string key) => this[key].Int ?? 0;
}

/// <summary>Number and date formatting for tiles. Invariant, so the same everywhere (like MacTiles).</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// One path segment ("light.kitchen", a container ID) made safe to put between slashes:
    /// "/", "?", "#", "%" and spaces are escaped so a value can't change the request.
    /// </summary>
    public static string Segment(string value) => Uri.EscapeDataString(value);

    public static string Count(double n)
    {
        if (n >= 1_000_000) return (n / 1_000_000).ToString("0.0", Inv) + "M";
        if (n >= 10_000) return (n / 1_000).ToString("0", Inv) + "k";
        if (n >= 1_000) return (n / 1_000).ToString("0.0", Inv) + "k";
        return ((long)n).ToString(Inv);
    }

    public static string Percent(double fraction, int digits = 0) =>
        (Math.Clamp(fraction, 0, 1) * 100).ToString(digits == 0 ? "0" : "0." + new string('0', digits), Inv) + "%";

    /// <summary>Binary units: "512 MB", "9.8 GB", "16 GB".</summary>
    public static string Bytes(double n)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB", "PB" };
        var v = Math.Max(n, 0);
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        if (i == 0) return ((long)v).ToString(Inv) + " bytes";
        return v.ToString(v < 10 ? "0.#" : "0", Inv) + " " + units[i];
    }

    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(seconds, 0);
        long d = s / 86_400, h = s % 86_400 / 3_600, m = s % 3_600 / 60;
        if (d > 0) return $"{d}d {h}h";
        if (h > 0) return $"{h}h {m}m";
        return $"{m}m";
    }

    public static string Speed(double mbps) => mbps >= 1000
        ? (mbps / 1000).ToString("0.0", Inv) + " Gbps"
        : mbps.ToString("0", Inv) + " Mbps";

    /// <summary>Bytes per second as a network speed: "820 kb/s", "48 Mb/s", "1.2 Gb/s".</summary>
    public static string Bitrate(double bytesPerSecond)
    {
        var bits = Math.Max(bytesPerSecond, 0) * 8;
        if (bits >= 1e9) return (bits / 1e9).ToString("0.0", Inv) + " Gb/s";
        if (bits >= 1e7) return (bits / 1e6).ToString("0", Inv) + " Mb/s";
        if (bits >= 1e6) return (bits / 1e6).ToString("0.0", Inv) + " Mb/s";
        if (bits >= 1e3) return (bits / 1e3).ToString("0", Inv) + " kb/s";
        return bits.ToString("0", Inv) + " b/s";
    }

    /// <summary>"Today 8:00 PM", "Tomorrow", "Fri 14" — in local time.</summary>
    public static string When(DateTimeOffset date, DateTimeOffset? now = null, bool withTime = true)
    {
        var local = date.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today) return withTime ? "Today " + time : "Today";
        if (local.Date == today.AddDays(1)) return withTime ? "Tomorrow " + time : "Tomorrow";
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        return local.ToString("ddd d", CultureInfo.CurrentCulture);
    }

    public static DateTimeOffset? IsoDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    public static string Iso(DateTimeOffset date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv);

    /// <summary>For query strings: encodes everything except unreserved characters.</summary>
    public static string Query(string s) => Uri.EscapeDataString(s);

    public static string F(double v, string format) => v.ToString(format, Inv);
}
