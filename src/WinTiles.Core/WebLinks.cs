using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinTiles.Core;

/// <summary>Web addresses for web tiles. Same rules as MacTiles.</summary>
public static class WebLink
{
    private static readonly string[] OpaqueSchemes = { "mailto:", "tel:", "sms:", "ms-settings:", "callto:" };

    /// <summary>
    /// "microsoft.com", "https://github.com/x", "localhost:3000/docs", "mailto:me@x.com" → a URI to open.
    /// Bare addresses get https://. Returns null for things that aren't addresses.
    /// </summary>
    public static Uri? Normalize(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return null;
        var lower = s.ToLowerInvariant();
        if (OpaqueSchemes.Any(lower.StartsWith))
            return Uri.TryCreate(s, UriKind.Absolute, out var opaque) ? opaque : null;

        var explicitScheme = Regex.IsMatch(s, "^[A-Za-z][A-Za-z0-9+.-]*://");
        var candidate = explicitScheme ? s : "https://" + s;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is "http" or "https" && !IsPlausibleHost(uri.Host)) return null;
        return uri;
    }

    private static bool IsPlausibleHost(string host)
    {
        var h = host.ToLowerInvariant().Trim('[', ']');
        if (h == "localhost") return true;
        if (Regex.IsMatch(h, @"^\d{1,3}(\.\d{1,3}){3}$")) return true;   // IPv4
        if (h.Contains(':')) return true;                                 // IPv6
        var parts = h.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0) && parts[^1].Length >= 2;
    }

    /// <summary>"www.microsoft.com" → "microsoft.com"; the label when there's no name.</summary>
    public static string DisplayHost(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Host)) return uri.OriginalString;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    public static bool IsFetchable(Uri uri) => uri.Scheme is "http" or "https";

    public static Uri? Of(Tile tile) => tile.Url is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null;
}

/// <summary>An icon a page declares, with the size it claims (0 = unknown).</summary>
public sealed record IconCandidate(Uri Url, int Size);

/// <summary>Reads a page's title and the icons it offers. Pure, so it's testable.</summary>
public static class WebPage
{
    public static string? Title(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var t = Regex.Replace(m.Groups[1].Value, "<[^>]+>", "");
        t = DecodeEntities(t);
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length == 0) return null;
        return t.Length > 60 ? t[..59] + "…" : t;
    }

    /// <summary>
    /// Icons declared in the page — apple-touch-icon, &lt;link rel="icon"&gt; — best (largest) first,
    /// plus the web app manifest's address.
    /// </summary>
    public static (List<IconCandidate> Icons, Uri? Manifest) IconCandidates(string html, Uri baseUri)
    {
        var found = new List<(IconCandidate Candidate, int Rank)>();
        Uri? manifest = null;
        foreach (Match tag in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attrs = Attributes(tag.Value);
            if (!attrs.TryGetValue("rel", out var rel) || !attrs.TryGetValue("href", out var href) || href.Length == 0) continue;
            if (!Uri.TryCreate(baseUri, href, out var url)) continue;
            rel = rel.ToLowerInvariant();
            var rels = rel.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rels.Contains("manifest")) { manifest ??= url; continue; }
            var declared = ParseSizes(attrs.GetValueOrDefault("sizes"));
            if (rel.Contains("apple-touch-icon")) found.Add((new IconCandidate(url, declared > 0 ? declared : 180), 0));
            else if (rels.Contains("icon")) found.Add((new IconCandidate(url, declared > 0 ? declared : 32), 1));
        }
        var sorted = found.OrderByDescending(f => f.Candidate.Size).ThenBy(f => f.Rank).Select(f => f.Candidate);
        return (Dedupe(sorted), manifest);
    }

    /// <summary>Icons in a web app manifest (often 192 and 512 px — the sharpest a site offers).</summary>
    public static List<IconCandidate> ManifestIcons(string json, Uri baseUri)
    {
        var list = new List<IconCandidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Array) return list;
            foreach (var icon in icons.EnumerateArray())
            {
                if (icon.ValueKind != JsonValueKind.Object || !icon.TryGetProperty("src", out var src) || src.ValueKind != JsonValueKind.String) continue;
                if (!Uri.TryCreate(baseUri, src.GetString(), out var url)) continue;
                var purpose = (icon.TryGetProperty("purpose", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : "any")!.ToLowerInvariant();
                if (!purpose.Split(' ').Contains("any") && purpose.Contains("monochrome")) continue;
                var sizes = icon.TryGetProperty("sizes", out var sz) && sz.ValueKind == JsonValueKind.String ? sz.GetString() : null;
                list.Add(new IconCandidate(url, ParseSizes(sizes)));
            }
        }
        catch (JsonException) { }
        return list.OrderByDescending(c => c.Size).ToList();
    }

    /// <summary>Where sites usually keep icons when they don't declare them.</summary>
    public static List<IconCandidate> ConventionalIcons(Uri page)
    {
        var root = new Uri(page, "/");
        return new()
        {
            new(new Uri(root, "apple-touch-icon.png"), 180),
            new(new Uri(root, "apple-touch-icon-precomposed.png"), 180),
            new(new Uri(root, "favicon.ico"), 32),
        };
    }

    /// <summary>Last resort: Google's public favicon service (it receives only the site's domain).</summary>
    public static IconCandidate FallbackIcon(string host) =>
        new(new Uri("https://www.google.com/s2/favicons?domain=" + Uri.EscapeDataString(host) + "&sz=256"), 64);

    /// <summary>"16x16 32x32" → 32; "any" → 512 (a vector icon).</summary>
    public static int ParseSizes(string? sizes)
    {
        var s = (sizes ?? "").ToLowerInvariant();
        if (s.Contains("any")) return 512;
        return s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.Split('x')[0], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
    }

    public static List<IconCandidate> Dedupe(IEnumerable<IconCandidate> list)
    {
        var seen = new HashSet<string>();
        return list.Where(c => seen.Add(c.Url.AbsoluteUri)).ToList();
    }

    public static Dictionary<string, string> Attributes(string tag)
    {
        var result = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(tag, @"([A-Za-z][\w:-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))"))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            result[name] = DecodeEntities(value);
        }
        return result;
    }

    public static string DecodeEntities(string s) => WebUtility.HtmlDecode(s);
}
