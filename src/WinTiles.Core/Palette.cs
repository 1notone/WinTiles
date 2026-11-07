using System.Globalization;
using System.Text;

namespace WinTiles.Core;

public sealed record NamedColor(string Name, string Hex);

/// <summary>The Windows 8 "Metro" palette and color helpers.</summary>
public static class Palette
{
    public static IReadOnlyList<NamedColor> Named { get; } = new[]
    {
        new NamedColor("Blue", "#2D89EF"),
        new NamedColor("Cobalt", "#2B5797"),
        new NamedColor("Teal", "#00ABA9"),
        new NamedColor("Emerald", "#00A300"),
        new NamedColor("Forest", "#1E7145"),
        new NamedColor("Lime", "#99B433"),
        new NamedColor("Plum", "#7E3878"),
        new NamedColor("Violet", "#603CBA"),
        new NamedColor("Magenta", "#FF0097"),
        new NamedColor("Red", "#EE1111"),
        new NamedColor("Crimson", "#B91D47"),
        new NamedColor("Amber", "#E3A21A"),
        new NamedColor("Orange", "#DA532C"),
        new NamedColor("Yellow", "#FFC40D"),
    };

    public static IReadOnlyList<string> Accents { get; } = Named.Select(n => n.Hex).ToArray();

    /// <summary>Start-screen backgrounds.</summary>
    public static IReadOnlyList<string> Backgrounds { get; } = new[]
    {
        "#25115A", "#1F0068", "#2D004E", "#4E0038", "#4E0000",
        "#3B1F00", "#003E00", "#004050", "#002050", "#1D1D1D",
    };

    /// <summary>Stable across launches (unlike GetHashCode), so a tile keeps its color. Same as MacTiles.</summary>
    public static int StableIndex(string s, int modulo)
    {
        ulong h = 5381;
        foreach (var b in Encoding.UTF8.GetBytes(s)) h = unchecked(h * 33 + b);
        return (int)(h % (ulong)Math.Max(modulo, 1));
    }

    public static string AutoColor(string key) => Accents[StableIndex(key, Accents.Count)];

    /// <summary>"25115a", "#25115A", " #25115a " → "#25115A"; anything else → null.</summary>
    public static string? NormalizeHex(string input)
    {
        var s = input.Trim().ToUpperInvariant();
        if (s.StartsWith('#')) s = s[1..];
        return s.Length == 6 && s.All(Uri.IsHexDigit) ? "#" + s : null;
    }

    public static (byte R, byte G, byte B) ToRgb(string hex)
    {
        var s = NormalizeHex(hex) ?? "#333333";
        var v = int.Parse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static string ToHex(double r, double g, double b) =>
        ToHex(Channel(r), Channel(g), Channel(b));

    private static byte Channel(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);

    // MARK: HSV (for the color wheel)

    /// <summary>h in [0,360), s and v in [0,1] → r, g, b in [0,1].</summary>
    public static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }

    public static (double H, double S, double V) RgbToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static (double H, double S, double V) HexToHsv(string hex)
    {
        var (r, g, b) = ToRgb(hex);
        return RgbToHsv(r / 255.0, g / 255.0, b / 255.0);
    }

    public static string HsvToHex(double h, double s, double v)
    {
        var (r, g, b) = HsvToRgb(h, s, v);
        return ToHex(r, g, b);
    }

    /// <summary>
    /// Derives a flat, Metro-friendly tile color from an app icon's pixels
    /// (32-bit BGRA, premultiplied alpha). Returns null for fully transparent icons.
    /// </summary>
    public static string? IconTint(ReadOnlySpan<byte> bgra)
    {
        double r = 0, g = 0, b = 0, a = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; a += bgra[i + 3];
        }
        if (a <= 0) return null;
        var (h, s, v) = RgbToHsv(Math.Min(1, r / a), Math.Min(1, g / a), Math.Min(1, b / a));
        return HsvToHex(h, Math.Min(1, s * 1.3 + 0.1), Math.Clamp(v * 0.85, 0.38, 0.72));
    }
}
