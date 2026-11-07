using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

/// <summary>What's inside a tile. Live tiles update on Tick and when their provider changes.</summary>
public abstract class TileContent : Grid
{
    protected readonly Tile Tile;
    protected readonly Size Frame;
    protected readonly Launcher L;
    private readonly List<Action> cleanup = new();

    protected TileContent(Tile tile, Size frame, Launcher launcher)
    {
        Tile = tile;
        Frame = frame;
        L = launcher;
        Width = frame.Width;
        Height = frame.Height;
        ClipToBounds = true;
        TextElement.SetForeground(this, Brushes.White);
    }

    public static TileContent Create(Tile tile, Size frame, Launcher launcher) => tile.Kind switch
    {
        TileKind.App => new AppContent(tile, frame, launcher),
        TileKind.Clock => new ClockContent(tile, frame, launcher),
        TileKind.Calendar => new CalendarContent(tile, frame, launcher),
        TileKind.Weather => new WeatherContent(tile, frame, launcher),
        TileKind.System => new SystemContent(tile, frame, launcher),
        TileKind.Battery => new BatteryContent(tile, frame, launcher),
        TileKind.Photos => new PhotosContent(tile, frame, launcher),
        TileKind.News => new NewsContent(tile, frame, launcher),
        TileKind.Web => new WebTileContent(tile, frame, launcher),
        TileKind.Service => new ServiceTileContent(tile, frame, launcher),
        _ => new DesktopContent(tile, frame, launcher),
    };

    public virtual void Tick(DateTime now) { }

    public void Detach()
    {
        foreach (var c in cleanup) c();
        cleanup.Clear();
    }

    protected bool IsSmall => Tile.Size == TileSize.Small;

    /// <summary>Offsets each tile's flip timing so they don't all turn at once.</summary>
    protected double Phase => Palette.StableIndex(Tile.Id.ToString(), 40) / 10.0;

    /// <summary>Subscribes to a provider's Changed event until the tile goes away.</summary>
    protected void Watch(Action<Action> add, Action<Action> remove, Action handler)
    {
        add(handler);
        cleanup.Add(() => remove(handler));
    }

    /// <summary>The same, for events that carry a value (a web host, a service key).</summary>
    protected void Watch<T>(Action<Action<T>> add, Action<Action<T>> remove, Action<T> handler)
    {
        add(handler);
        cleanup.Add(() => remove(handler));
    }

    protected FrameworkElement Label(string text)
    {
        var (leading, bottom) = L.Store.Settings.TileShape.LabelInset();
        var t = Ui.Text(text, 12);
        t.HorizontalAlignment = HorizontalAlignment.Left;
        t.VerticalAlignment = VerticalAlignment.Bottom;
        t.Margin = new Thickness(leading, 0, leading, bottom);
        t.IsHitTestVisible = false;
        return t;
    }

    protected static StackPanel Stack(Thickness margin, HorizontalAlignment h = HorizontalAlignment.Left, VerticalAlignment v = VerticalAlignment.Bottom) =>
        new() { Margin = margin, HorizontalAlignment = h, VerticalAlignment = v };
}

/// <summary>
/// Cycles a live tile between faces, sliding the next face up from the bottom like Windows 8.
/// Driven by Tick, which only runs while Start can be seen.
/// </summary>
public sealed class FlipFaces : Grid
{
    private readonly List<FrameworkElement> faces = new();
    private readonly double interval, phase;
    private int current = -1;
    public int ActiveCount { get; set; }

    public FlipFaces(double interval, double phase, params FrameworkElement[] faces)
    {
        this.interval = interval;
        this.phase = phase;
        ClipToBounds = true;
        foreach (var f in faces)
        {
            f.RenderTransform = new TranslateTransform();
            f.Visibility = Visibility.Collapsed;
            this.faces.Add(f);
            Children.Add(f);
        }
        ActiveCount = faces.Length;
        Show(0, animate: false);
    }

    public void Tick(DateTime now)
    {
        var n = Math.Min(ActiveCount, faces.Count);
        var index = n < 2 ? 0 : (int)((now.TimeOfDay.TotalSeconds + phase) / interval) % n;
        if (index != current) Show(index, animate: n > 1 && current >= 0 && SystemParameters.ClientAreaAnimation);
    }

    private void Show(int index, bool animate)
    {
        var old = current >= 0 && current < faces.Count ? faces[current] : null;
        var next = faces[index];
        current = index;
        next.Visibility = Visibility.Visible;
        var h = Math.Max(ActualHeight, 60);
        if (!animate || old is null || old == next)
        {
            foreach (var f in faces) if (f != next) f.Visibility = Visibility.Collapsed;
            ((TranslateTransform)next.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
            ((TranslateTransform)next.RenderTransform).Y = 0;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var outAnim = new DoubleAnimation(0, -h, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease };
        outAnim.Completed += (_, _) => { if (faces[current] != old) old.Visibility = Visibility.Collapsed; };
        ((TranslateTransform)old.RenderTransform).BeginAnimation(TranslateTransform.YProperty, outAnim);
        ((TranslateTransform)next.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(h, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
    }
}

// MARK: - App

public sealed class AppContent : TileContent
{
    public AppContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        var path = tile.AppPath ?? "";
        var side = tile.Size switch
        {
            TileSize.Small => frame.Width * 0.62,
            TileSize.Medium => frame.Height * 0.45,
            TileSize.Wide => frame.Height * 0.48,
            _ => frame.Height * 0.38,
        };
        var image = new Image
        {
            Width = side,
            Height = side,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, IsSmall ? 0 : 12),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Children.Add(image);
        if (!IsSmall) Children.Add(Label(l.Catalog.Name(path)));
        l.Catalog.Icon(path, src => image.Source = src);
    }
}

// MARK: - Clock

public sealed class ClockContent : TileContent
{
    private readonly TextBlock time;
    private readonly TextBlock? date;
    private readonly AnalogClock? analog;
    private readonly FlipFaces? flip;

    public ClockContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        if (IsSmall)
        {
            time = Ui.Text("", 17, Ui.Light);
            time.HorizontalAlignment = HorizontalAlignment.Center;
            time.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(time);
            return;
        }
        time = Ui.Text("", tile.Size == TileSize.Medium ? 36 : 56, Ui.UltraLight);
        date = Ui.Text("", 13, opacity: 0.9);
        var digital = Stack(new Thickness(12, 0, 12, 26));
        digital.Children.Add(time);
        digital.Children.Add(date);
        var digitalHost = new Grid();
        digitalHost.Children.Add(digital);

        analog = new AnalogClock { Margin = new Thickness(12, 12, 12, 26) };
        flip = new FlipFaces(9, Phase, digitalHost, analog);
        Children.Add(flip);
        Children.Add(Label("Clock"));
        Tick(DateTime.Now);
    }

    public override void Tick(DateTime now)
    {
        time.Text = now.ToString("t", CultureInfo.CurrentCulture);
        if (date is not null) date.Text = now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        analog?.Set(now);
        flip?.Tick(now);
    }
}

public sealed class AnalogClock : Viewbox
{
    private readonly RotateTransform hour = new(0, 50, 50), minute = new(0, 50, 50), second = new(0, 50, 50);

    public AnalogClock()
    {
        var c = new Canvas { Width = 100, Height = 100 };
        c.Children.Add(new Ellipse { Width = 96, Height = 96, Stroke = Ui.White(0.9), StrokeThickness = 1.5, Margin = new Thickness(2) });
        for (var i = 0; i < 12; i++)
        {
            var a = i / 12.0 * 2 * Math.PI;
            var inner = i % 3 == 0 ? 38 : 42;
            c.Children.Add(new Line
            {
                X1 = 50 + inner * Math.Sin(a), Y1 = 50 - inner * Math.Cos(a),
                X2 = 50 + 46 * Math.Sin(a), Y2 = 50 - 46 * Math.Cos(a),
                Stroke = Ui.White(0.85), StrokeThickness = i % 3 == 0 ? 2 : 1,
            });
        }
        Line Hand(double length, double width, double opacity, RotateTransform rotate) => new()
        {
            X1 = 50, Y1 = 50, X2 = 50, Y2 = 50 - length,
            Stroke = Ui.White(opacity), StrokeThickness = width,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rotate,
        };
        c.Children.Add(Hand(24, 3, 1, hour));
        c.Children.Add(Hand(36, 2, 1, minute));
        c.Children.Add(Hand(41, 1, 0.7, second));
        Child = c;
        Stretch = Stretch.Uniform;
    }

    public void Set(DateTime t)
    {
        var secs = t.Second;
        var mins = t.Minute + secs / 60.0;
        var hrs = t.Hour % 12 + mins / 60.0;
        hour.Angle = hrs * 30;
        minute.Angle = mins * 6;
        second.Angle = secs * 6;
    }
}

// MARK: - Calendar

public sealed class CalendarContent : TileContent
{
    private readonly TextBlock day, weekday;
    private readonly StackPanel? events;
    private readonly FlipFaces? flip;
    private DateTime shownDate;

    public CalendarContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        if (IsSmall)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            day = Ui.Text("", 26, Ui.Light);
            weekday = Ui.Text("", 10);
            day.HorizontalAlignment = weekday.HorizontalAlignment = HorizontalAlignment.Center;
            weekday.Margin = new Thickness(0, -4, 0, 0);
            s.Children.Add(day);
            s.Children.Add(weekday);
            Children.Add(s);
            Tick(DateTime.Now);
            return;
        }
        day = Ui.Text("", tile.Size == TileSize.Medium ? 54 : 66, Ui.UltraLight);
        weekday = Ui.Text("", 14);
        weekday.Margin = new Thickness(0, -8, 0, 0);
        var dateFace = new Grid();
        var st = Stack(new Thickness(12, 8, 10, 26));
        st.Children.Add(day);
        st.Children.Add(weekday);
        dateFace.Children.Add(st);

        events = Stack(new Thickness(10, 10, 10, 26), v: VerticalAlignment.Top);
        var eventsFace = new Grid();
        eventsFace.Children.Add(events);

        flip = new FlipFaces(8, Phase, dateFace, eventsFace);
        Children.Add(flip);
        Children.Add(Label("Calendar"));
        Watch(h => l.Hub.Calendar.Changed += h, h => l.Hub.Calendar.Changed -= h, UpdateEvents);
        UpdateEvents();
        Tick(DateTime.Now);
    }

    private void UpdateEvents()
    {
        if (events is null || flip is null) return;
        events.Children.Clear();
        var list = L.Hub.Calendar.Events;
        foreach (var e in list.Take(Tile.Size == TileSize.Large ? 4 : 1))
        {
            var t = Ui.Text(e.Title, 14, Ui.Semibold);
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxHeight = Tile.Size == TileSize.Medium ? 60 : 40;
            events.Children.Add(t);
            var when = Ui.Text(e.WhenText(DateTime.Now), 11, opacity: 0.85);
            when.Margin = new Thickness(0, 0, 0, 8);
            events.Children.Add(when);
        }
        flip.ActiveCount = list.Count > 0 ? 2 : 1;
    }

    public override void Tick(DateTime now)
    {
        if (now.Date != shownDate)
        {
            shownDate = now.Date;
            day.Text = now.Day.ToString(CultureInfo.CurrentCulture);
            weekday.Text = now.ToString(IsSmall ? "ddd" : "dddd", CultureInfo.CurrentCulture);
        }
        flip?.Tick(now);
    }
}

// MARK: - Weather

public sealed class WeatherContent : TileContent
{
    private FlipFaces? flip;

    public WeatherContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        Watch(h => l.Hub.Weather.Changed += h, h => l.Hub.Weather.Changed -= h, Rebuild);
        Rebuild();
    }

    private string City => L.Store.Settings.WeatherCity ?? "Weather";

    private void Rebuild()
    {
        Children.Clear();
        flip = null;
        if (L.Hub.Weather.Now is not { } w) { Placeholder(); return; }

        switch (Tile.Size)
        {
            case TileSize.Small:
                var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var icon = WeatherGlyph.Create(WeatherApi.Icon(w.Code), w.IsDay, 24);
                var temp = Ui.Text($"{Math.Round(w.Temp)}°", 14, Ui.Light);
                temp.HorizontalAlignment = HorizontalAlignment.Center;
                s.Children.Add(icon);
                s.Children.Add(temp);
                Children.Add(s);
                return;
            case TileSize.Large:
                var g = new Grid();
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition());
                var cur = Current(w);
                var line = new Rectangle { Height = 1, Fill = Ui.White(0.25), Margin = new Thickness(14, 0, 14, 0) };
                var fc = Forecast(w, 3);
                SetRow(line, 1);
                SetRow(fc, 2);
                g.Children.Add(cur);
                g.Children.Add(line);
                g.Children.Add(fc);
                Children.Add(g);
                break;
            default:
                flip = new FlipFaces(7, Phase, Current(w), Forecast(w, Tile.Size == TileSize.Wide ? 3 : 1));
                Children.Add(flip);
                break;
        }
        Children.Add(Label(City));
    }

    private FrameworkElement Current(WeatherNow w)
    {
        var g = new Grid();
        var icon = WeatherApi.Icon(w.Code);
        var hiLo = w.Daily.FirstOrDefault() is { } d ? $"{Math.Round(d.Hi)}° / {Math.Round(d.Lo)}°" : "";
        if (Tile.Size == TileSize.Medium)
        {
            var glyph = WeatherGlyph.Create(icon, w.IsDay, 30);
            glyph.HorizontalAlignment = HorizontalAlignment.Left;
            glyph.VerticalAlignment = VerticalAlignment.Top;
            glyph.Margin = new Thickness(10);
            g.Children.Add(glyph);
            var st = Stack(new Thickness(10, 0, 10, 24));
            st.Children.Add(Ui.Text($"{Math.Round(w.Temp)}°", 38, Ui.UltraLight));
            st.Children.Add(Ui.Text(WeatherApi.Describe(w.Code), 11, opacity: 0.9));
            g.Children.Add(st);
        }
        else
        {
            var st = Stack(new Thickness(14, 0, 14, 24), v: VerticalAlignment.Center);
            st.Children.Add(Ui.Text($"{Math.Round(w.Temp)}°", 50, Ui.UltraLight));
            st.Children.Add(Ui.Text(WeatherApi.Describe(w.Code), 13));
            st.Children.Add(Ui.Text(hiLo, 12, opacity: 0.85));
            g.Children.Add(st);
            var glyph = WeatherGlyph.Create(icon, w.IsDay, 54);
            glyph.HorizontalAlignment = HorizontalAlignment.Right;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.Margin = new Thickness(0, 0, 18, 12);
            g.Children.Add(glyph);
        }
        return g;
    }

    private FrameworkElement Forecast(WeatherNow w, int days)
    {
        var g = new UniformGrid(days) { Margin = new Thickness(0, 0, 0, 18) };
        foreach (var d in w.Daily.Skip(1).Take(days))
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var name = Ui.Text(d.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd", CultureInfo.CurrentCulture), 12, Ui.Semibold);
            var glyph = WeatherGlyph.Create(WeatherApi.Icon(d.Code), true, 26);
            var temps = Ui.Text($"{Math.Round(d.Hi)}° {Math.Round(d.Lo)}°", 12);
            foreach (var e in new FrameworkElement[] { name, glyph, temps })
            {
                e.HorizontalAlignment = HorizontalAlignment.Center;
                e.Margin = new Thickness(0, 2, 0, 2);
                s.Children.Add(e);
            }
            g.Add(s);
        }
        return g;
    }

    private void Placeholder()
    {
        var st = Stack(new Thickness(10), v: VerticalAlignment.Top);
        st.Children.Add(WeatherGlyph.Create(WeatherIcon.PartlyCloudy, true, 30));
        if (!IsSmall)
        {
            var t = Ui.Text(L.Hub.Weather.Status, 11, opacity: 0.9);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 6, 0, 0);
            st.Children.Add(t);
        }
        Children.Add(st);
        if (!IsSmall) Children.Add(Label("Weather"));
    }

    public override void Tick(DateTime now) => flip?.Tick(now);

    /// <summary>A tiny equal-width row (System.Windows.Controls.Primitives.UniformGrid would need extra setup).</summary>
    private sealed class UniformGrid : Grid
    {
        private int next;
        public UniformGrid(int columns)
        {
            for (var i = 0; i < Math.Max(columns, 1); i++) ColumnDefinitions.Add(new ColumnDefinition());
        }
        public void Add(UIElement e) { SetColumn(e, next++); Children.Add(e); }
    }
}

// MARK: - System

public sealed class SystemContent : TileContent
{
    private readonly TextBlock cpu = Ui.Text("", 18, Ui.Light);
    private readonly TextBlock? mem, memOf;
    private readonly ProgressBar? memBar;
    private readonly Canvas? spark;
    private readonly FlipFaces? flip;

    public SystemContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        if (IsSmall)
        {
            var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cap = Ui.Text("CPU", 10, Ui.Semibold, 0.85);
            cap.HorizontalAlignment = cpu.HorizontalAlignment = HorizontalAlignment.Center;
            s.Children.Add(cap);
            s.Children.Add(cpu);
            Children.Add(s);
        }
        else
        {
            cpu.FontSize = tile.Size == TileSize.Medium ? 34 : 46;
            cpu.FontWeight = Ui.UltraLight;
            var cpuFace = Stack(new Thickness(10, 10, 10, 26), v: VerticalAlignment.Top);
            cpuFace.Children.Add(Ui.Text("CPU", 12, Ui.Semibold, 0.85));
            cpuFace.Children.Add(cpu);
            spark = new Canvas { Height = tile.Size == TileSize.Large ? 120 : 30, Width = frame.Width - 20, Margin = new Thickness(0, 4, 0, 0) };
            cpuFace.Children.Add(spark);

            mem = Ui.Text("", tile.Size == TileSize.Medium ? 28 : 40, Ui.UltraLight);
            memOf = Ui.Text("", 11, opacity: 0.85);
            memBar = new ProgressBar { Height = 4, Maximum = 1, Foreground = Brushes.White, Background = Ui.White(0.25), BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) };
            var memFace = Stack(new Thickness(10, 10, 10, 26), v: VerticalAlignment.Top);
            memFace.HorizontalAlignment = HorizontalAlignment.Stretch;
            memFace.Children.Add(Ui.Text("Memory", 12, Ui.Semibold, 0.85));
            memFace.Children.Add(mem);
            memFace.Children.Add(memOf);
            memFace.Children.Add(memBar);

            flip = new FlipFaces(6, Phase, Wrap(cpuFace), Wrap(memFace));
            Children.Add(flip);
            Children.Add(Label("System"));
        }
        Watch(h => l.Hub.Stats.Changed += h, h => l.Hub.Stats.Changed -= h, Update);
        Update();
    }

    private static Grid Wrap(UIElement e) { var g = new Grid(); g.Children.Add(e); return g; }

    private void Update()
    {
        var s = L.Hub.Stats;
        cpu.Text = $"{Math.Round(s.Cpu * 100)}%";
        if (mem is not null && memOf is not null && memBar is not null)
        {
            const double gib = 1024.0 * 1024 * 1024;
            mem.Text = $"{s.MemUsed / gib:0.0} GB";
            memOf.Text = $"of {s.MemTotal / gib:0} GB";
            memBar.Value = s.MemTotal > 0 ? s.MemUsed / s.MemTotal : 0;
        }
        if (spark is not null)
        {
            spark.Children.Clear();
            const int slots = 30;
            var w = spark.Width / slots;
            var values = s.History.TakeLast(slots).ToList();
            for (var i = 0; i < values.Count; i++)
            {
                var h = Math.Max(1, values[i] * spark.Height);
                var r = new Rectangle { Width = Math.Max(1, w - 1.5), Height = h, Fill = Ui.White(0.75) };
                Canvas.SetLeft(r, i * w);
                Canvas.SetTop(r, spark.Height - h);
                spark.Children.Add(r);
            }
        }
    }

    public override void Tick(DateTime now) => flip?.Tick(now);
}

// MARK: - Battery

public sealed class BatteryContent : TileContent
{
    private readonly Border level = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border shell;
    private readonly TextBlock percent, detail;
    private readonly double scale;

    public BatteryContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        scale = IsSmall ? 0.6 : 1;
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var body = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        shell = new Border
        {
            Width = 40 * scale, Height = 20 * scale, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(3 * scale), Padding = new Thickness(2 * scale), Child = level,
        };
        body.Children.Add(shell);
        body.Children.Add(new Border { Width = 3 * scale, Height = 8 * scale, Background = Brushes.White, CornerRadius = new CornerRadius(0, 1.5, 1.5, 0), VerticalAlignment = VerticalAlignment.Center });
        s.Children.Add(body);

        percent = Ui.Text("", IsSmall ? 13 : 26, Ui.Light);
        detail = Ui.Text("", 11, opacity: 0.85);
        percent.HorizontalAlignment = detail.HorizontalAlignment = HorizontalAlignment.Center;
        percent.Margin = new Thickness(0, 4, 0, 0);
        s.Children.Add(percent);
        if (!IsSmall) s.Children.Add(detail);
        Children.Add(s);

        Watch(h => l.Hub.Battery.Changed += h, h => l.Hub.Battery.Changed -= h, Update);
        if (l.Hub.Battery.State is null) l.Hub.Battery.Sample();
        Update();
    }

    private void Update()
    {
        if (L.Hub.Battery.State is not { } b)
        {
            level.Width = 0;
            percent.Text = IsSmall ? "AC" : "On power";
            detail.Text = "No battery";
            return;
        }
        level.Width = Math.Max(0, (shell.Width - 7 * scale) * b.Percent / 100.0);
        level.Background = b.Percent <= 15 && !b.Charging ? Ui.Brush("#FF6B5B") : Brushes.White;
        percent.Text = $"{b.Percent}%";
        detail.Text = b.Charging ? "Charging" : b.OnAC ? "On power" : b.MinutesLeft is { } m ? $"{m / 60}h {m % 60}m left" : "On battery";
    }
}

// MARK: - Photos

public sealed class PhotosContent : TileContent
{
    private readonly Image back = new() { Stretch = Stretch.UniformToFill };
    private readonly Image front = new() { Stretch = Stretch.UniformToFill };
    private readonly StackPanel placeholder;

    public PhotosContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        Children.Add(back);
        Children.Add(front);
        placeholder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
        var icon = new Path
        {
            Data = Geometry.Parse("M 2,4 H 22 V 20 H 2 Z M 2,16 L 8,10 L 13,15 L 16,12 L 22,18 M 16,7.5 A 1.5,1.5 0 1 1 16,7.49"),
            Stroke = Brushes.White, StrokeThickness = 1.5, Width = 30, Height = 30, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        placeholder.Children.Add(icon);
        var note = Ui.Text("Add pictures to your Pictures folder", 11, opacity: 0.85);
        note.TextWrapping = TextWrapping.Wrap;
        note.TextAlignment = TextAlignment.Center;
        note.Visibility = Visibility.Collapsed;
        placeholder.Children.Add(note);
        Children.Add(placeholder);
        if (!IsSmall) Children.Add(Label("Photos"));

        Watch(h => l.Hub.Photos.Changed += h, h => l.Hub.Photos.Changed -= h, () =>
        {
            note.Visibility = l.Hub.Photos.IsEmpty && !IsSmall ? Visibility.Visible : Visibility.Collapsed;
            Show(l.Hub.Photos.Image);
        });
        Show(l.Hub.Photos.Image);
    }

    private void Show(ImageSource? image)
    {
        if (image is null || image == front.Source) return;
        placeholder.Visibility = Visibility.Collapsed;
        back.Source = front.Source;
        front.Source = image;
        front.Opacity = 0;
        Ui.Animate(front, OpacityProperty, 1, 900);
    }
}

// MARK: - News

public sealed class NewsContent : TileContent
{
    private readonly Grid stage = new() { ClipToBounds = true };
    private readonly TextBlock source;
    private Headline? shown;

    public NewsContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        Children.Add(stage);
        source = (TextBlock)Label("News");
        Children.Add(source);
        Watch(h => l.Hub.NewsChanged += h, h => l.Hub.NewsChanged -= h, Update);
        Update();
    }

    private string? status;

    private void Update()
    {
        var news = L.Hub.News(Tile);
        source.Text = !string.IsNullOrWhiteSpace(Tile.Title) ? Tile.Title! : string.IsNullOrEmpty(news.Source) ? "News" : news.Source;
        var h = news.Current;
        if (h == shown && (h is not null || news.Status == status)) return;
        status = news.Status;
        var animate = shown is not null && h is not null && SystemParameters.ClientAreaAnimation;
        shown = h;

        FrameworkElement next;
        if (h is null)
        {
            var icon = new Path
            {
                Data = Geometry.Parse("M 3,4 H 17 V 20 H 5 A 2,2 0 0 1 3,18 Z M 17,8 H 21 V 18 A 2,2 0 0 1 19,20 H 17 M 6,8 H 14 M 6,12 H 14 M 6,16 H 11"),
                Stroke = Brushes.White, StrokeThickness = 1.5, Width = 30, Height = 30, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(icon);
            if (!string.IsNullOrEmpty(news.Status) && Tile.Size != TileSize.Small)
            {
                var note = Ui.Text(news.Status, 11, FontWeights.Normal);
                note.Opacity = 0.8;
                note.Margin = new Thickness(0, 6, 0, 0);
                note.HorizontalAlignment = HorizontalAlignment.Center;
                panel.Children.Add(note);
            }
            next = panel;
        }
        else
        {
            var size = Tile.Size switch { TileSize.Large => 20.0, TileSize.Wide => 16.0, _ => 13.0 };
            var t = Ui.Text(h.Title, size, Ui.Semibold);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.WordEllipsis;
            t.Margin = new Thickness(10, 10, 10, 26);
            t.VerticalAlignment = VerticalAlignment.Top;
            t.MaxHeight = Frame.Height - 36;
            next = t;
        }
        next.RenderTransform = new TranslateTransform();
        var old = stage.Children.Count > 0 ? (FrameworkElement)stage.Children[stage.Children.Count - 1] : null;
        stage.Children.Add(next);
        if (!animate || old is null)
        {
            stage.Children.Clear();
            stage.Children.Add(next);
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var outAnim = new DoubleAnimation(0, -Frame.Height, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease };
        outAnim.Completed += (_, _) => stage.Children.Remove(old);
        old.RenderTransform ??= new TranslateTransform();
        if (old.RenderTransform is TranslateTransform ot) ot.BeginAnimation(TranslateTransform.YProperty, outAnim);
        ((TranslateTransform)next.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Frame.Height, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
    }
}

// MARK: - Desktop

public sealed class DesktopContent : TileContent
{
    private readonly Image image = new() { Stretch = Stretch.UniformToFill };

    public DesktopContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        Background = new LinearGradientBrush(Ui.ColorOf("#0F4C81"), Ui.ColorOf("#3A7BD5"), 45);
        Children.Add(image);
        Children.Add(Label("Desktop"));
        Watch(h => l.Hub.Desktop.Changed += h, h => l.Hub.Desktop.Changed -= h, () => image.Source = l.Hub.Desktop.Image);
        image.Source = l.Hub.Desktop.Image;
        if (image.Source is null) l.Hub.Desktop.Refresh();
    }
}
