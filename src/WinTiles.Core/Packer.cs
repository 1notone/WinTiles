namespace WinTiles.Core;

/// <summary>
/// Packs tiles the way Windows 8.1 does: each group is a run of 4-unit-wide columns
/// ("bands"); tiles fill a band top-to-bottom, left-to-right, back-filling gaps with small
/// tiles, and spill into the next band when it's full.
/// </summary>
public static class Packer
{
    public const int BandWidth = 4;

    public readonly record struct Placed(Tile Tile, int X, int Y);

    public sealed record Result(IReadOnlyList<Placed> Items, int Width);

    public static Result Pack(IReadOnlyList<Tile> tiles, int rows)
    {
        rows = Math.Max(rows, 4);
        var items = new List<Placed>();
        var band = 0;
        var grid = new bool[rows, BandWidth];
        var right = 0;

        foreach (var tile in tiles)
        {
            var (uw, uh) = tile.Size.Units();
            var w = Math.Min(uw, BandWidth);
            var h = Math.Min(uh, rows);
            (int X, int Y)? spot = null;
            while (spot is null)
            {
                for (var y = 0; y <= rows - h && spot is null; y++)
                    for (var x = 0; x <= BandWidth - w; x++)
                        if (IsFree(grid, x, y, w, h)) { spot = (x, y); break; }
                if (spot is null)
                {
                    band++;
                    grid = new bool[rows, BandWidth];
                }
            }
            var (sx, sy) = spot.Value;
            for (var yy = sy; yy < sy + h; yy++)
                for (var xx = sx; xx < sx + w; xx++)
                    grid[yy, xx] = true;
            var gx = band * BandWidth + sx;
            items.Add(new Placed(tile, gx, sy));
            right = Math.Max(right, gx + w);
        }
        return new Result(items, Math.Max(right, 2));
    }

    private static bool IsFree(bool[,] g, int x, int y, int w, int h)
    {
        for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
                if (g[yy, xx]) return false;
        return true;
    }
}

/// <summary>Grid geometry. One unit = a small tile; medium is 2×2 units, wide 4×2, large 4×4.</summary>
public readonly record struct Metrics
{
    public const double HeaderHeight = 120;
    public const double FooterHeight = 64;
    public const double GroupTitleHeight = 36;

    public double Tile { get; }
    public double Gap { get; }
    public double Step => Tile + Gap;
    public int Rows { get; }
    public double LeftMargin { get; }
    public double GroupSpacing { get; }
    public double CornerRadius { get; }
    public TileShape Shape { get; }

    public Metrics(double width, double height, double scale, TileShape shape = TileShape.Square)
    {
        var s = Math.Clamp(scale, 0.7, 1.6);
        Tile = Math.Round(62 * s);
        Gap = Math.Round(shape.Gap() * s);
        var available = height - HeaderHeight - FooterHeight - GroupTitleHeight - 16;
        var r = (int)Math.Floor((available + Gap) / (Tile + Gap));
        r = Math.Clamp(r, 4, 12);
        if (r % 2 == 1) r -= 1;          // keep medium tiles aligned
        Rows = r;
        LeftMargin = Math.Clamp(width * 0.06, 40, 116);
        GroupSpacing = Math.Round(52 * s);
        CornerRadius = shape.CornerRadius() * s;
        Shape = shape;
    }

    public double Span(int units) => units * Step - Gap;

    public (double W, double H) Frame(TileSize size)
    {
        var (w, h) = size.Units();
        return (Span(w), Span(h));
    }
}
