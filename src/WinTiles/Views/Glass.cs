using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// The shine on glass tiles: a curved gloss across the top, a little shade pooling at the bottom,
/// a polished rim that catches the light at the top-left, a highlight that follows the pointer,
/// and a quick shimmer on hover. (WPF can't blur what's behind a window, so the "frost" is a
/// translucent tint over the wallpaper rather than a real blur.)
/// </summary>
public sealed class GlassOverlay : Grid
{
    private readonly Rectangle gloss = new() { IsHitTestVisible = false };
    private readonly Rectangle shade = new() { IsHitTestVisible = false };
    private readonly Rectangle glint = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Rectangle shimmer = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Path rim = new() { IsHitTestVisible = false, StrokeThickness = 1.2 };
    private readonly Path innerRim = new() { IsHitTestVisible = false, StrokeThickness = 1, Opacity = 0.35 };
    private readonly RadialGradientBrush glintBrush = new()
    {
        GradientStops = { new GradientStop(Color.FromArgb(70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
        RadiusX = 0.6,
        RadiusY = 0.6,
    };
    private readonly TranslateTransform shimmerShift = new();
    private double width;

    public GlassOverlay()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;

        gloss.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(92, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(28, 255, 255, 255), 0.42),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55),
            },
        };
        shade.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.55),
            EndPoint = new Point(0.5, 1),
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(46, 0, 0, 0), 1) },
        };
        rim.Stroke = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(170, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.45),
                new GradientStop(Color.FromArgb(20, 255, 255, 255), 1),
            },
        };
        innerRim.Stroke = Ui.White(0.6);
        glint.Fill = glintBrush;
        shimmer.Fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.3),
            EndPoint = new Point(1, 0.7),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35),
                new GradientStop(Color.FromArgb(60, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65),
            },
        };
        shimmer.RenderTransform = shimmerShift;

        Children.Add(shade);
        Children.Add(gloss);
        Children.Add(glint);
        Children.Add(shimmer);
        Children.Add(innerRim);
        Children.Add(rim);
    }

    public void Configure(double w, double h, Geometry shape, double cornerRadius)
    {
        width = w;
        Width = w;
        Height = h;
        Clip = shape;
        rim.Data = shape;
        // A second, inner edge for thickness.
        innerRim.Data = Ui.Squircle(Math.Max(w - 4, 0), Math.Max(h - 4, 0), Math.Max(cornerRadius - 2, 0));
        innerRim.Margin = new Thickness(2);
        innerRim.HorizontalAlignment = HorizontalAlignment.Left;
        innerRim.VerticalAlignment = VerticalAlignment.Top;
    }

    /// <summary>The pane's tint: the tile color, clearer at the top and deeper toward the bottom.</summary>
    public static Brush TintBrush(string hex, double tint)
    {
        var c = Ui.ColorOf(hex);
        byte A(double a) => (byte)Math.Round(255 * Math.Clamp(a, 0, 1));
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(A(tint * 0.75), c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(A(Math.Min(tint * 1.2, 0.95)), c.R, c.G, c.B), 1),
            },
        };
        b.Freeze();
        return b;
    }

    public void Pointer(Point p)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        glintBrush.Center = glintBrush.GradientOrigin = new Point(p.X / ActualWidth, p.Y / ActualHeight);
    }

    public void Hover(bool on)
    {
        Ui.Animate(glint, OpacityProperty, on ? 1 : 0, 200);
        if (!on || !SystemParameters.ClientAreaAnimation || Visibility != Visibility.Visible) return;
        shimmer.Opacity = 1;
        var sweep = new DoubleAnimation(-width, width, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        sweep.Completed += (_, _) => shimmer.Opacity = 0;
        shimmerShift.BeginAnimation(TranslateTransform.XProperty, sweep);
    }
}

/// <summary>"Quit Notepad" and "Force Quit Notepad…" for a running app's right-click menu.</summary>
public static class QuitMenu
{
    public static IEnumerable<object> Items(Launcher launcher, string appId, string name)
    {
        yield return Ui.Item($"Quit {name}", () => launcher.Running.Quit(appId));
        if (!launcher.Running.CanForceQuit(appId)) yield break;
        yield return Ui.Item($"Force Quit {name}…", () =>
        {
            var answer = MessageBox.Show($"Any unsaved changes in {name} will be lost. Use this only if the app isn't responding.",
                $"Force quit {name}?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) launcher.Running.ForceQuit(appId);
        });
    }
}
