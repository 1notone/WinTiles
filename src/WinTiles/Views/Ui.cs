using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

/// <summary>Small helpers for building the code-only WPF UI.</summary>
internal static class Ui
{
    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");

    private static readonly Dictionary<string, SolidColorBrush> brushes = new(StringComparer.OrdinalIgnoreCase);

    public static Color ColorOf(string hex)
    {
        var (r, g, b) = Palette.ToRgb(hex);
        return Color.FromRgb(r, g, b);
    }

    public static SolidColorBrush Brush(string hex)
    {
        if (brushes.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush(ColorOf(hex));
        b.Freeze();
        brushes[hex] = b;
        return b;
    }

    public static SolidColorBrush White(double opacity)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), 255, 255, 255));
        b.Freeze();
        return b;
    }

    public static TextBlock Text(string text, double size, FontWeight? weight = null, double opacity = 1)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = Brushes.White,
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    public static readonly FontWeight Light = FontWeights.Light;
    public static readonly FontWeight UltraLight = FontWeights.ExtraLight;
    public static readonly FontWeight Semibold = FontWeights.SemiBold;

    public static void Animate(IAnimatable el, DependencyProperty prop, double to, double ms, double delayMs = 0, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        el.BeginAnimation(prop, a);
    }

    /// <summary>Stops any animation on the property and sets it directly.</summary>
    public static void Set(UIElement el, DependencyProperty prop, double value)
    {
        el.BeginAnimation(prop, null);
        el.SetValue(prop, value);
    }

    public static void AnimateFrom(IAnimatable target, DependencyProperty prop, double from, double to, double ms, double delayMs = 0, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        target.BeginAnimation(prop, a);
    }

    public static MenuItem Item(string header, Action action, bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    public static MenuItem Submenu(string header, IEnumerable<object> items)
    {
        var m = new MenuItem { Header = header };
        foreach (var i in items) m.Items.Add(i);
        return m;
    }

    /// <summary>Squircle ("continuous corner") outline like Apple's, or a plain rectangle when r = 0.</summary>
    public static Geometry Squircle(double w, double h, double r)
    {
        if (r <= 0 || w <= 0 || h <= 0) return new RectangleGeometry(new Rect(0, 0, Math.Max(w, 0), Math.Max(h, 0)));
        var c = Math.Min(1.28 * r, Math.Min(w / 2, h / 2));     // the curve starts further from the corner than a circle would
        var k = 0.55 * c;
        var f = new PathFigure { StartPoint = new Point(c, 0), IsClosed = true, IsFilled = true };
        f.Segments.Add(new LineSegment(new Point(w - c, 0), true));
        f.Segments.Add(new BezierSegment(new Point(w - c + k, 0), new Point(w, c - k), new Point(w, c), true));
        f.Segments.Add(new LineSegment(new Point(w, h - c), true));
        f.Segments.Add(new BezierSegment(new Point(w, h - c + k), new Point(w - c + k, h), new Point(w - c, h), true));
        f.Segments.Add(new LineSegment(new Point(c, h), true));
        f.Segments.Add(new BezierSegment(new Point(c - k, h), new Point(0, h - c + k), new Point(0, h - c), true));
        f.Segments.Add(new LineSegment(new Point(0, c), true));
        f.Segments.Add(new BezierSegment(new Point(0, c - k), new Point(c - k, 0), new Point(c, 0), true));
        var g = new PathGeometry(new[] { f });
        g.Freeze();
        return g;
    }

    /// <summary>A circled arrow, like the Windows 8.1 "All apps" button.</summary>
    public static FrameworkElement CircleArrow(bool up, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Ellipse { Width = 22, Height = 22, Stroke = Brushes.White, StrokeThickness = 1, Margin = new Thickness(1) });
        var arrow = new Path
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse(up ? "M 12,17 L 12,7 M 7.5,11.5 L 12,7 L 16.5,11.5" : "M 12,7 L 12,17 M 7.5,12.5 L 12,17 L 16.5,12.5"),
        };
        canvas.Children.Add(arrow);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }
}

/// <summary>Simple white weather icons drawn as vectors (no font dependencies).</summary>
internal static class WeatherGlyph
{
    public static FrameworkElement Create(WeatherIcon icon, bool isDay, double size)
    {
        var c = new Canvas { Width = 32, Height = 32 };
        var sunColor = Ui.Brush("#FFC40D");

        void Sun(double cx, double cy, double r, bool rays)
        {
            var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = sunColor };
            Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
            c.Children.Add(e);
            if (!rays) return;
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                c.Children.Add(new Line
                {
                    X1 = cx + Math.Cos(a) * (r + 2.5), Y1 = cy + Math.Sin(a) * (r + 2.5),
                    X2 = cx + Math.Cos(a) * (r + 5.5), Y2 = cy + Math.Sin(a) * (r + 5.5),
                    Stroke = sunColor, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
            }
        }

        void Moon(double cx, double cy, double r)
        {
            var g = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), r, r),
                new EllipseGeometry(new Point(cx + r * 0.6, cy - r * 0.4), r * 0.85, r * 0.85));
            c.Children.Add(new Path { Data = g, Fill = Ui.Brush("#F2F2F2") });
        }

        void Cloud(double dx, double dy, double scale, double opacity = 1)
        {
            var path = new Path
            {
                Data = Geometry.Parse("M 7,22 A 5,5 0 0 1 7.5,12 A 7,7 0 0 1 21,10.5 A 5.5,5.5 0 0 1 25,22 Z"),
                Fill = Ui.White(opacity),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(dx, dy) } },
            };
            c.Children.Add(path);
        }

        void Drops(int count, bool slanted, string color = "#FFFFFF", double thickness = 2)
        {
            for (var i = 0; i < count; i++)
            {
                var x = 9 + i * 6;
                c.Children.Add(new Line
                {
                    X1 = x, Y1 = 25, X2 = slanted ? x - 2 : x, Y2 = 30,
                    Stroke = Ui.Brush(color), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
            }
        }

        switch (icon)
        {
            case WeatherIcon.Clear:
                if (isDay) Sun(16, 16, 7, true); else Moon(16, 16, 10);
                break;
            case WeatherIcon.PartlyCloudy:
                if (isDay) Sun(12, 11, 6, false); else Moon(12, 11, 7);
                Cloud(4, 5, 0.9);
                break;
            case WeatherIcon.Cloudy:
                Cloud(-3, -2, 0.75, 0.6);
                Cloud(2, 3, 0.95);
                break;
            case WeatherIcon.Fog:
                Cloud(2, -2, 0.9);
                for (var i = 0; i < 2; i++)
                    c.Children.Add(new Line { X1 = 6, X2 = 26, Y1 = 25 + i * 4, Y2 = 25 + i * 4, Stroke = Brushes.White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                break;
            case WeatherIcon.Drizzle:
                Cloud(2, -3, 0.9);
                Drops(3, false, "#9ED8FF", 1.5);
                break;
            case WeatherIcon.Rain:
            case WeatherIcon.Showers:
                Cloud(2, -3, 0.9);
                Drops(icon == WeatherIcon.Showers ? 4 : 3, true, "#9ED8FF");
                break;
            case WeatherIcon.Snow:
                Cloud(2, -3, 0.9);
                for (var i = 0; i < 3; i++)
                {
                    var e = new Ellipse { Width = 3.5, Height = 3.5, Fill = Brushes.White };
                    Canvas.SetLeft(e, 8 + i * 6.5); Canvas.SetTop(e, 25 + (i % 2) * 2.5);
                    c.Children.Add(e);
                }
                break;
            case WeatherIcon.Thunder:
                Cloud(2, -3, 0.9);
                c.Children.Add(new Path { Data = Geometry.Parse("M 17,20 L 12,27 L 16,27 L 14,32 L 21,24 L 17,24 L 19,20 Z"), Fill = sunColor });
                break;
        }
        return new Viewbox { Width = size, Height = size, Child = c };
    }
}
