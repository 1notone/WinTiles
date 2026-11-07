using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WinTiles.Core;

namespace WinTiles;

public enum WallpaperKind { Still, Animated, Video }

/// <summary>Imported wallpapers live in %APPDATA%\WinTiles\Wallpapers, so the originals can move.</summary>
public static class Wallpapers
{
    public static string Directory { get; } = Path.Combine(LayoutStore.Directory, "Wallpapers");

    public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv", ".webm" };
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".jxr" };

    public static string FileFilter =>
        "Pictures and videos|" + string.Join(";", ImageExtensions.Concat(VideoExtensions).Select(e => "*" + e)) + "|All files|*.*";

    public static string? PathFor(string? stored)
    {
        var s = stored?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return Path.IsPathRooted(s) ? s : ImportedCopy(s);
    }

    /// <summary>An imported copy's file inside the Wallpapers folder; null for anything that would
    /// point elsewhere ("..\layout.json" in a hand-edited layout file).</summary>
    public static string? ImportedCopy(string name) =>
        name.Length == 0 || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ? null : Path.Combine(Directory, name);

    /// <summary>Imported copies are "1a2b3c4d-Original Name.mp4"; show the original name.</summary>
    public static string? DisplayName(string? stored)
    {
        if (PathFor(stored) is not { } path) return null;
        var name = Path.GetFileName(path);
        return !Path.IsPathRooted(stored!) && name.Length > 9 && name[8] == '-' ? name[9..] : name;
    }

    public static WallpaperKind? KindOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (VideoExtensions.Contains(ext)) return WallpaperKind.Video;
        if (ext == ".gif") return GifFrames(path) > 1 ? WallpaperKind.Animated : WallpaperKind.Still;
        return ImageExtensions.Contains(ext) ? WallpaperKind.Still : null;
    }

    private static int GifFrames(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new GifBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames.Count;
        }
        catch { return 0; }
    }

    /// <summary>Copies the file into the Wallpapers folder and removes the previous imported copy.</summary>
    public static string Import(string source, string? replacing)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(source);
        File.Copy(source, Path.Combine(Directory, name));
        Remove(replacing);
        return name;
    }

    /// <summary>Deletes an imported copy (never a file elsewhere on your PC).</summary>
    public static void Remove(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || Path.IsPathRooted(stored) || ImportedCopy(stored.Trim()) is not { } path) return;
        try { File.Delete(path); } catch { }
    }
}

/// <summary>Animated wallpapers hold still when Start can't be seen, with animations turned off, or in battery saver.</summary>
public static class MotionPolicy
{
    public static bool AllowsMotion
    {
        get
        {
            if (!SystemParameters.ClientAreaAnimation) return false;      // Settings → Accessibility → Visual effects → Animation effects
            return !(Native.GetSystemPowerStatus(out var p) && p.SystemStatusFlag == 1);     // battery saver
        }
    }
}

/// <summary>
/// Everything behind the tiles: the background color, a picture, an animated GIF, a looping video,
/// or a live wallpaper — with the dim layer over pictures and videos.
/// </summary>
public sealed class BackgroundHost : Grid
{
    private readonly Launcher launcher;
    private readonly Grid media = new() { ClipToBounds = true };
    private readonly Border dim = new() { Background = Brushes.Black, IsHitTestVisible = false };
    private readonly Border sheen = new() { IsHitTestVisible = false };
    private LiveWallpaperView? live;
    private MediaElement? video;
    private string shown = "";

    public BackgroundHost(Launcher launcher)
    {
        this.launcher = launcher;
        IsHitTestVisible = false;
        sheen.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops = { new GradientStop(Color.FromArgb(18, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6) },
        };
        Children.Add(media);
        Children.Add(dim);
        Children.Add(sheen);
        launcher.Store.SettingsChanged += _ => Apply();
        launcher.StateChanged += UpdatePlayback;
        Apply();
    }

    private void Apply()
    {
        var s = launcher.Store.Settings;
        var path = Wallpapers.PathFor(s.WallpaperFile);
        var key = s.BackgroundStyle switch
        {
            BackgroundStyle.Media when path is not null && File.Exists(path) => "media:" + path,
            BackgroundStyle.Live => $"live:{s.LiveWallpaper}:{s.Background}",
            _ => "color",
        };
        dim.Opacity = key.StartsWith("media:") ? s.WallpaperDim : 0;
        sheen.Visibility = key == "color" ? Visibility.Visible : Visibility.Collapsed;
        if (key != shown)
        {
            shown = key;
            Clear();
            if (key.StartsWith("media:")) ShowMedia(path!);
            else if (key.StartsWith("live:"))
            {
                live = new LiveWallpaperView(s.LiveWallpaper, s.Background);
                media.Children.Add(live);
            }
        }
        UpdatePlayback();
    }

    private void Clear()
    {
        live?.Stop();
        live = null;
        if (video is not null) { video.Stop(); video.Close(); video = null; }
        media.Children.Clear();
    }

    private void ShowMedia(string path)
    {
        switch (Wallpapers.KindOf(path))
        {
            case WallpaperKind.Video:
                video = new MediaElement
                {
                    Source = new Uri(path),
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    IsMuted = true,
                    Volume = 0,
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                // Loop silently.
                video.MediaEnded += (_, _) => { if (video is { } v) { v.Position = TimeSpan.Zero; v.Play(); } };
                media.Children.Add(video);
                break;
            case WallpaperKind.Animated:
                media.Children.Add(new AnimatedGif(path) { Stretch = Stretch.UniformToFill });
                break;
            default:
                media.Children.Add(new Image
                {
                    Source = PhotosProvider.Thumbnail(path, (int)Math.Max(1920, SystemParameters.PrimaryScreenWidth * 2)),
                    Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                break;
        }
    }

    /// <summary>Animated wallpapers pause when Start is hidden or covered, with animations off, or in battery saver.</summary>
    private void UpdatePlayback()
    {
        var play = launcher.TilesLive && MotionPolicy.AllowsMotion;
        if (video is not null) { if (play) video.Play(); else video.Pause(); }
        foreach (var gif in media.Children.OfType<AnimatedGif>()) gif.SetPlaying(play);
        if (live is not null) { if (play) live.Start(); else live.Stop(); }
    }
}

/// <summary>Plays an animated GIF (WPF's Image only shows the first frame).</summary>
public sealed class AnimatedGif : Image
{
    private readonly ObjectAnimationUsingKeyFrames? frames;
    private bool playing;

    public AnimatedGif(string path)
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return;
            var canvas = Composite(decoder);
            Source = canvas[0];
            if (canvas.Count < 2) return;
            frames = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var t = TimeSpan.Zero;
            for (var i = 0; i < canvas.Count; i++)
            {
                frames.KeyFrames.Add(new DiscreteObjectKeyFrame(canvas[i], KeyTime.FromTimeSpan(t)));
                t += Delay(decoder.Frames[i]);
            }
            frames.Duration = t;
        }
        catch (Exception ex) { Log.Write("Couldn't read GIF wallpaper: " + ex.Message); }
    }

    public void SetPlaying(bool play)
    {
        if (frames is null || play == playing) return;
        playing = play;
        BeginAnimation(SourceProperty, play ? frames : null);
    }

    private static TimeSpan Delay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata m && m.GetQuery("/grctlext/Delay") is ushort centiseconds && centiseconds > 1)
                return TimeSpan.FromMilliseconds(centiseconds * 10);
        }
        catch { }
        return TimeSpan.FromMilliseconds(100);
    }

    /// <summary>GIF frames are often partial patches; draw each over the previous to get whole pictures.</summary>
    private static List<BitmapSource> Composite(GifBitmapDecoder decoder)
    {
        var first = decoder.Frames[0];
        int w = first.PixelWidth, h = first.PixelHeight;
        try
        {
            if (decoder.Metadata?.GetQuery("/logscrdesc/Width") is ushort lw && decoder.Metadata.GetQuery("/logscrdesc/Height") is ushort lh) { w = lw; h = lh; }
        }
        catch { }
        var result = new List<BitmapSource>();
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        foreach (var frame in decoder.Frames)
        {
            double left = 0, top = 0;
            try
            {
                if (frame.Metadata is BitmapMetadata m)
                {
                    if (m.GetQuery("/imgdesc/Left") is ushort l) left = l;
                    if (m.GetQuery("/imgdesc/Top") is ushort t) top = t;
                }
            }
            catch { }
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawImage(frame, new Rect(left, top, frame.PixelWidth, frame.PixelHeight));
            target.Render(visual);
            var copy = new WriteableBitmap(target);
            copy.Freeze();
            result.Add(copy);
            if (result.Count >= 400) break;      // very long GIFs: keep memory reasonable
        }
        return result;
    }
}

/// <summary>
/// Animated wallpapers drawn by WinTiles at up to 30 fps, tinted with the background color
/// (same scenes as MacTiles: Aurora, Ribbons, Bokeh and Squares).
/// </summary>
public sealed class LiveWallpaperView : FrameworkElement
{
    private readonly LiveWallpaper style;
    private readonly Color[] colors;
    private readonly DateTime epoch = DateTime.UtcNow;
    private bool running;
    private TimeSpan lastFrame;
    private double time;

    public LiveWallpaperView(LiveWallpaper style, string tint)
    {
        this.style = style;
        colors = Palette4(tint);
        IsHitTestVisible = false;
        time = (DateTime.UtcNow - DateTime.UtcNow.Date).TotalSeconds;     // a different moment each time
    }

    public void Start()
    {
        if (running) return;
        running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!running) return;
        running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if ((now - lastFrame).TotalMilliseconds < 33) return;       // 30 fps is plenty
        var dt = lastFrame == TimeSpan.Zero ? 0 : Math.Min((now - lastFrame).TotalSeconds, 0.1);
        lastFrame = now;
        time += dt;
        InvalidateVisual();
    }

    /// <summary>Four related colors derived from the tint: neighbouring hues, lighter and more saturated.</summary>
    public static Color[] Palette4(string hex)
    {
        var (h, s, v) = Palette.HexToHsv(hex);
        var hue = h / 360;
        var baseSat = Math.Max(s, 0.35);
        var bri = Math.Max(v, 0.35);
        static double Wrap(double x) => ((x % 1) + 1) % 1;
        (double H, double S, double B)[] hsb =
        {
            (Wrap(hue), Math.Min(1, baseSat + 0.15), Math.Min(1, bri + 0.45)),
            (Wrap(hue + 0.08), Math.Min(1, baseSat + 0.10), Math.Min(1, bri + 0.35)),
            (Wrap(hue - 0.10), Math.Min(1, baseSat + 0.05), Math.Min(1, bri + 0.40)),
            (Wrap(hue + 0.33), Math.Max(0.25, baseSat - 0.15), Math.Min(1, bri + 0.30)),
        };
        return hsb.Select(c =>
        {
            var (r, g, b) = Palette.HsvToRgb(c.H * 360, c.S, c.B);
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }).ToArray();
    }

    /// <summary>Deterministic pseudo-random numbers, so every launch draws the same scene.</summary>
    private static double Rand(int i, int salt)
    {
        unchecked
        {
            var x = (ulong)(i * 73_856_093 ^ salt * 19_349_663) + 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return x % 1_000_000 / 1_000_000.0;
        }
    }

    private static Color A(Color c, double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B);

    private static void Glow(DrawingContext dc, Color c, Point center, double r, double inner, double mid)
    {
        if (r <= 0) return;
        var brush = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(A(c, inner), 0), new GradientStop(A(c, mid), 0.5), new GradientStop(A(c, 0), 1) },
        };
        dc.DrawEllipse(brush, null, center, r, r);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, t = time;
        if (w <= 0 || h <= 0) return;
        switch (style)
        {
            case LiveWallpaper.Aurora:
                // Slow, glowing color fields drifting on Lissajous paths.
                var m = Math.Max(w, h);
                for (var i = 0; i < 5; i++)
                {
                    var speed = 0.035 + Rand(i, 1) * 0.03;
                    var x = w * (0.5 + 0.45 * Math.Sin(t * speed + Rand(i, 2) * 6.28));
                    var y = h * (0.5 + 0.40 * Math.Cos(t * speed * 0.8 + Rand(i, 3) * 6.28));
                    var r = m * (0.35 + 0.2 * Rand(i, 4)) * (1 + 0.08 * Math.Sin(t * 0.2 + i));
                    Glow(dc, colors[i % colors.Length], new Point(x, y), r, 0.42, 0.12);
                }
                break;

            case LiveWallpaper.Ribbons:
                // Flowing translucent bands, like the Windows 8 Start backgrounds.
                var step = Math.Max(12, w / 120);
                for (var i = 0; i < 4; i++)
                {
                    var c = colors[i % colors.Length];
                    var baseY = h * (0.25 + 0.17 * i);
                    var amp = h * (0.08 + 0.05 * Rand(i, 5));
                    var k = (1.2 + Rand(i, 6) * 1.6) * 2 * Math.PI / w;
                    var speed = 0.12 + Rand(i, 7) * 0.1;
                    var thickness = h * (0.05 + 0.06 * Rand(i, 8));
                    var top = new List<Point>();
                    var bottom = new List<Point>();
                    for (var x = -step; x <= w + step; x += step)
                    {
                        var y = baseY + amp * Math.Sin(k * x + t * speed + i * 1.7);
                        var th = thickness * (0.6 + 0.4 * Math.Sin(k * 0.5 * x - t * speed * 0.7 + i));
                        top.Add(new Point(x, y - th));
                        bottom.Add(new Point(x, y + th));
                    }
                    var figure = new PathFigure { StartPoint = top[0], IsClosed = true, IsFilled = true };
                    figure.Segments.Add(new PolyLineSegment(top.Skip(1).Concat(Enumerable.Reverse(bottom)), false));
                    var brush = new LinearGradientBrush
                    {
                        MappingMode = BrushMappingMode.Absolute,
                        StartPoint = new Point(0, baseY),
                        EndPoint = new Point(w, baseY),
                        GradientStops = { new GradientStop(A(c, 0), 0), new GradientStop(A(c, 0.32), 0.33), new GradientStop(A(c, 0.18), 0.66), new GradientStop(A(c, 0), 1) },
                    };
                    dc.DrawGeometry(brush, null, new PathGeometry(new[] { figure }));
                }
                break;

            case LiveWallpaper.Bokeh:
                // Soft out-of-focus lights drifting upward.
                for (var i = 0; i < 40; i++)
                {
                    var r = 18 + 90 * Math.Pow(Rand(i, 9), 2);
                    var speed = 6 + 18 * Rand(i, 10);
                    var span = h + r * 2;
                    var y = h + r - (Rand(i, 11) * span + t * speed) % span;
                    var x = w * Rand(i, 12) + 30 * Math.Sin(t * 0.15 + i);
                    var twinkle = 0.55 + 0.45 * Math.Sin(t * (0.3 + Rand(i, 13) * 0.5) + i);
                    var alpha = (0.10 + 0.22 * Rand(i, 14)) * twinkle;
                    Glow(dc, colors[i % colors.Length], new Point(x, y), r, alpha, alpha * 0.7);
                }
                break;

            case LiveWallpaper.Squares:
                // Translucent Metro squares gliding sideways on a grid.
                var unit = Math.Max(40, Math.Min(w, h) / 9);
                int[] sizes = { 1, 1, 2, 2, 3 };
                for (var i = 0; i < 28; i++)
                {
                    var side = unit * sizes[(int)(Rand(i, 15) * 5) % 5];
                    var speed = 4 + 10 * Rand(i, 16);
                    var span = w + side * 2;
                    var x = (Rand(i, 17) * span + t * speed) % span - side;
                    var row = Math.Floor(Rand(i, 18) * (h / unit));
                    var y = row * unit + unit * 0.1 * Math.Sin(t * 0.1 + i);
                    var alpha = 0.05 + 0.13 * Rand(i, 19) * (0.7 + 0.3 * Math.Sin(t * 0.25 + i));
                    var fill = new SolidColorBrush(A(colors[i % colors.Length], alpha));
                    dc.DrawRectangle(fill, null, new Rect(x, y, Math.Max(side - 6, 1), Math.Max(side - 6, 1)));
                }
                break;
        }
    }
}
