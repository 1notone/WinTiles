using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// Fetches and remembers each site's icon and page title on disk (%APPDATA%\WinTiles\WebIcons),
/// so web tiles look right offline. Picks the sharpest icon the site offers.
/// </summary>
public sealed class WebIconCache
{
    public static string Directory { get; } = Path.Combine(LayoutStore.Directory, "WebIcons");
    private static string TitlesFile => Path.Combine(Directory, "titles.json");

    private readonly Dictionary<string, BitmapSource?> icons = new();     // by host; null = not on disk
    private readonly Dictionary<string, string> titles = new();          // by URL
    private readonly Dictionary<string, string> tints = new();
    private readonly HashSet<string> inFlight = new();
    private readonly Dictionary<string, DateTime> failedAt = new();      // don't retry a site with no icon on every redraw

    private static readonly HttpClient http = CreateClient();

    /// <summary>Raised with the host whose icon or title arrived.</summary>
    public event Action<string>? Changed;

    public WebIconCache()
    {
        try
        {
            if (File.Exists(TitlesFile) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TitlesFile)) is { } saved)
                foreach (var (k, v) in saved) titles[k] = v;
        }
        catch { }
    }

    private static HttpClient CreateClient()
    {
        // Size-limited: a page or icon bigger than this is skipped rather than downloaded.
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 4_000_000 };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 Edg/124.0");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd(System.Globalization.CultureInfo.CurrentUICulture.Name);
        return c;
    }

    public BitmapSource? Icon(Uri url)
    {
        var host = WebLink.DisplayHost(url);
        if (icons.TryGetValue(host, out var cached)) return cached;
        BitmapSource? image = null;
        var file = IconFile(host);
        if (File.Exists(file)) image = Load(File.ReadAllBytes(file));
        icons[host] = image;
        return image;
    }

    public string? Title(Uri url) => titles.TryGetValue(url.AbsoluteUri, out var t) ? t : null;

    /// <summary>Tile color from the site's icon, for "Color app tiles from their icons".</summary>
    public string? Tint(Uri url)
    {
        var host = WebLink.DisplayHost(url);
        if (tints.TryGetValue(host, out var t)) return t;
        if (Icon(url) is not { } image) return null;
        try
        {
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            if (Palette.IconTint(pixels) is { } tint) { tints[host] = tint; return tint; }
        }
        catch { }
        return null;
    }

    /// <summary>Fetches the site's own icon and the page title if we don't have them yet.</summary>
    public async void Load(Uri url, bool force = false)
    {
        if (!WebLink.IsFetchable(url)) return;
        var host = WebLink.DisplayHost(url);
        var key = url.AbsoluteUri;
        if (!force && failedAt.TryGetValue(key, out var failed) && (DateTime.UtcNow - failed).TotalMinutes < 30) return;
        var needIcon = force || Icon(url) is null;
        var needTitle = force || !titles.ContainsKey(key);
        if (!(needIcon || needTitle) || !inFlight.Add(key)) return;
        if (force) { icons.Remove(host); tints.Remove(host); }
        try
        {
            // 1. The page itself: its title, and the icons it declares. Follow redirects
            //    (microsoft.com → www.microsoft.com) so relative icon addresses resolve correctly.
            var html = "";
            var baseUri = url;
            try
            {
                using var response = await http.GetAsync(url);
                baseUri = response.RequestMessage?.RequestUri ?? url;
                if ((int)response.StatusCode < 400)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    html = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 768 * 1024));
                    if (WebPage.Title(html) is { } title)
                    {
                        titles[key] = title;
                        SaveTitles();
                        Changed?.Invoke(host);
                    }
                }
            }
            catch { }
            if (!needIcon) return;

            // 2. Every place the site might keep its icon, sharpest first.
            var (declared, manifest) = WebPage.IconCandidates(html, baseUri);
            var candidates = declared.ToList();
            if (manifest is not null && WebLink.IsFetchable(manifest))
            {
                try { candidates.AddRange(WebPage.ManifestIcons(await http.GetStringAsync(manifest), manifest)); } catch { }
            }
            candidates = candidates.OrderByDescending(c => c.Size).ToList();
            candidates.AddRange(WebPage.ConventionalIcons(baseUri));
            if (baseUri.Host != url.Host) candidates.AddRange(WebPage.ConventionalIcons(url));
            candidates.Add(WebPage.FallbackIcon(baseUri.Host));

            // 3. Keep the best; stop once one is big enough to fill a tile.
            BitmapSource? best = null;
            byte[]? bestBytes = null;
            foreach (var candidate in WebPage.Dedupe(candidates).Where(c => WebLink.IsFetchable(c.Url)).Take(12))
            {
                byte[] data;
                try
                {
                    using var r = await http.GetAsync(candidate.Url);
                    if ((int)r.StatusCode >= 400) continue;
                    data = await r.Content.ReadAsByteArrayAsync();
                }
                catch { continue; }
                if (data.Length == 0 || Load(data) is not { } image || image.PixelWidth < 16) continue;
                if (best is null || image.PixelWidth > best.PixelWidth) { best = image; bestBytes = data; }
                if (image.PixelWidth >= 120) break;
            }
            if (best is not null && bestBytes is not null)
            {
                icons[host] = best;
                tints.Remove(host);
                failedAt.Remove(key);
                Save(best, host);
                Changed?.Invoke(host);
            }
            else failedAt[key] = DateTime.UtcNow;
        }
        finally { inFlight.Remove(key); }
    }

    /// <summary>Decodes PNG, ICO (largest frame), JPEG, GIF or BMP. SVG icons aren't supported by WPF and are skipped.</summary>
    private static BitmapSource? Load(byte[] data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            frame?.Freeze();
            return frame;
        }
        catch { return null; }
    }

    private static string IconFile(string host) =>
        Path.Combine(Directory, Regex.Replace(host, "[^A-Za-z0-9.-]", "_") + ".v2.png");

    private static void Save(BitmapSource image, string host)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(IconFile(host));
            encoder.Save(stream);
        }
        catch { }
    }

    private void SaveTitles()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(TitlesFile, JsonSerializer.Serialize(titles));
        }
        catch { }
    }
}

/// <summary>A web tile: the site's icon (or its initial) and the page title or your name for it.</summary>
public sealed class WebTileContent : TileContent
{
    private readonly Image icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock initial = Ui.Text("", 40, Ui.Light);
    private readonly Grid labelHost = new();
    private readonly TextBlock? host;
    private readonly Uri? url;

    public WebTileContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        url = WebLink.Of(tile);
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        initial.VerticalAlignment = VerticalAlignment.Center;
        var art = new Grid { Margin = new Thickness(0, 0, 0, IsSmall ? 0 : 12) };
        art.Children.Add(icon);
        art.Children.Add(initial);
        Children.Add(art);
        if (!IsSmall)
        {
            if (tile.Size != TileSize.Medium && url is not null)
            {
                host = Ui.Text(WebLink.DisplayHost(url), 11, opacity: 0.8);
                host.Margin = new Thickness(10);
                host.VerticalAlignment = VerticalAlignment.Top;
                Children.Add(host);
            }
            Children.Add(labelHost);
        }
        Watch<string>(h => l.WebIcons.Changed += h, h => l.WebIcons.Changed -= h, _ => Update());
        Update();
        if (url is not null) l.WebIcons.Load(url);
    }

    public static string DisplayName(Tile t, Launcher l)
    {
        if (!string.IsNullOrWhiteSpace(t.Title)) return t.Title!;
        if (WebLink.Of(t) is not { } url) return "Web Link";
        return l.WebIcons.Title(url) ?? WebLink.DisplayHost(url);
    }

    private void Update()
    {
        var name = DisplayName(Tile, L);
        labelHost.Children.Clear();
        if (!IsSmall) labelHost.Children.Add(Label(name));
        if (host is not null && url is not null) host.Visibility = name == WebLink.DisplayHost(url) ? Visibility.Collapsed : Visibility.Visible;

        var side = Tile.Size switch
        {
            TileSize.Small => Frame.Width * 0.62,
            TileSize.Medium => Frame.Height * 0.42,
            TileSize.Wide => Frame.Height * 0.46,
            _ => Frame.Height * 0.36,
        };
        if (url is not null && L.WebIcons.Icon(url) is { } image)
        {
            // Favicons are often tiny: don't blow them up into a blur.
            var shown = Math.Min(side, Math.Max(image.PixelWidth / 1.5, side * 0.55));
            icon.Source = image;
            icon.Width = icon.Height = shown;
            icon.Visibility = Visibility.Visible;
            initial.Visibility = Visibility.Collapsed;
        }
        else
        {
            icon.Visibility = Visibility.Collapsed;
            initial.Text = url is null ? "?" : WebLink.DisplayHost(url)[..1].ToUpperInvariant();
            initial.FontSize = side * 0.6;
            initial.Visibility = Visibility.Visible;
        }
    }
}

/// <summary>A Metro-style panel over Start for adding or editing a web tile.</summary>
public sealed class WebTileEditor : Grid
{
    private readonly Launcher launcher;
    private readonly Tile? editing;
    private readonly TileDestination? destination;
    private readonly TextBox address = Panels.Field();
    private readonly TextBox name = Panels.Field();
    private readonly TextBlock error = Panels.Error();

    public WebTileEditor(Launcher launcher, Tile? editing, TileDestination? destination)
    {
        this.launcher = launcher;
        this.editing = editing;
        this.destination = destination;

        address.Text = editing?.Url ?? ClipboardAddress() ?? "";
        name.Text = editing?.Title ?? "";
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        address.TextChanged += (_, _) => error.Text = "";

        var body = new StackPanel();
        body.Children.Add(Panels.Labeled("Web address", address));
        body.Children.Add(Panels.Labeled("Name (optional)", name, "Leave empty to use the page's title."));
        body.Children.Add(error);
        body.Children.Add(Panels.Buttons(("Cancel", false, launcher.ClosePanel), (editing is null ? "Add" : "Save", true, Save)));
        Children.Add(Panels.Card(this, editing is null ? "Add a web tile" : "Edit web tile", body, launcher.ClosePanel));
        Loaded += (_, _) => { address.Focus(); address.SelectAll(); };
    }

    private static string? ClipboardAddress()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            return text.Length < 2048 && WebLink.Normalize(text) is { Scheme: "http" or "https" } ? text : null;
        }
        catch { return null; }
    }

    private void Save()
    {
        if (WebLink.Normalize(address.Text) is not { } url)
        {
            error.Text = "That doesn't look like a web address. Try something like microsoft.com.";
            return;
        }
        var title = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
        if (editing is not null)
            launcher.Store.Update(editing.Id, t => t with { Url = url.AbsoluteUri, Title = title });
        else
            launcher.Store.AddTile(Tile.WebLink(url.AbsoluteUri, title), destination);
        launcher.WebIcons.Load(url);
        launcher.ClosePanel();
    }
}

/// <summary>
/// A Metro-style panel over Start for choosing a News tile's source. Accepts a feed address or
/// a site's address (its feed is found for you).
/// </summary>
public sealed class NewsTileEditor : Grid
{
    private readonly Launcher launcher;
    private readonly Tile? editing;
    private readonly TextBox feed = Panels.Field();
    private readonly TextBox name = Panels.Field();
    private readonly TextBlock message = Panels.Error();
    private readonly Button save;
    private bool checking;
    private bool saveAnyway;

    public NewsTileEditor(Launcher launcher, Tile? editing)
    {
        this.launcher = launcher;
        this.editing = editing;
        feed.Text = editing is null ? launcher.Store.Settings.NewsFeed : launcher.Store.NewsFeedOf(editing);
        name.Text = editing?.Title ?? "";
        feed.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        feed.TextChanged += (_, _) => { message.Text = ""; saveAnyway = false; UpdateSaveLabel(); };

        var presets = new ContextMenu();
        foreach (var (presetName, presetUrl) in NewsFeeds.Popular)
        {
            var n = presetName;
            var u = presetUrl;
            presets.Items.Add(Ui.Item(n, () => Choose(u, n)));
        }
        var popular = new TextBlock
        {
            Text = "Popular feeds ▾", Foreground = Brushes.White, Opacity = 0.85, FontSize = 12, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6),
        };
        popular.MouseLeftButtonUp += (_, _) => { presets.PlacementTarget = popular; presets.IsOpen = true; };

        save = Panels.Button(editing is null ? "Add" : "Save", true, Save);
        var body = new StackPanel();
        body.Children.Add(popular);
        body.Children.Add(Panels.Labeled("Feed or website", feed, "Any RSS or Atom feed, or a site's address like theverge.com. Clicking the tile opens the headline it's showing."));
        body.Children.Add(Panels.Labeled("Name (optional)", name, "Shown on the tile; otherwise the feed's own name."));
        body.Children.Add(message);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(Panels.Button("Cancel", false, launcher.ClosePanel));
        row.Children.Add(save);
        body.Children.Add(row);
        Children.Add(Panels.Card(this, editing is null ? "Add a news tile" : "Change news feed", body, launcher.ClosePanel));
        Loaded += (_, _) => { feed.Focus(); feed.SelectAll(); };
    }

    private void Choose(string url, string presetName)
    {
        feed.Text = url;
        if (name.Text.Trim().Length == 0 || NewsFeeds.Popular.Any(p => p.Name == name.Text.Trim())) name.Text = presetName;
    }

    private void UpdateSaveLabel()
    {
        var verb = editing is null ? "Add" : "Save";
        if (save.Content is TextBlock label) label.Text = saveAnyway ? verb + " Anyway" : verb;
    }

    private async void Save()
    {
        if (checking) return;
        if (WebLink.Normalize(feed.Text) is not { } url || !WebLink.IsFetchable(url))
        {
            message.Text = "That doesn't look like a web address. Try something like theverge.com.";
            return;
        }
        var title = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
        if (saveAnyway) { Commit(url.AbsoluteUri, title); return; }
        checking = true;
        save.IsEnabled = false;
        message.Text = "Checking the feed…";
        try
        {
            var result = await NewsProvider.Fetch(url.AbsoluteUri);
            Commit(result.FeedUrl, title);
        }
        catch
        {
            message.Text = "Couldn't find a news feed at that address. Check it, or choose one from Popular feeds.";
            saveAnyway = true;
            UpdateSaveLabel();
        }
        finally
        {
            checking = false;
            save.IsEnabled = true;
        }
    }

    private void Commit(string feedUrl, string? title)
    {
        if (editing is not null)
            launcher.Store.Update(editing.Id, t => t with { Url = feedUrl, Title = title });
        else
            launcher.Store.AddNews(feedUrl, title);
        launcher.Hub.NewsFeedsChanged();
        if (launcher.TilesLive) launcher.Hub.SetActive(true);
        launcher.ClosePanel();
    }
}

/// <summary>Building blocks for the dark Metro panels shown over Start.</summary>
public static class Panels
{
    public static TextBox Field() => new()
    {
        FontSize = 15,
        Padding = new Thickness(8, 6, 8, 6),
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        Background = Ui.White(0.09),
        BorderThickness = new Thickness(0),
        SelectionBrush = Ui.White(0.35),
    };

    public static TextBlock Error() => new()
    {
        Foreground = Ui.Brush("#FF8A80"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
    };

    public static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = Brushes.White,
        Opacity = 0.65,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    public static FrameworkElement Labeled(string label, UIElement control, string? note = null)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        s.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, Opacity = 0.75, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        s.Children.Add(control);
        if (note is not null) s.Children.Add(new TextBlock { Text = note, Foreground = Brushes.White, Opacity = 0.55, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return s;
    }

    public static Button Button(string text, bool filled, Action action)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 14, Foreground = Brushes.White },
            Padding = new Thickness(18, 7, 18, 7),
            Margin = new Thickness(10, 0, 0, 0),
            Background = filled ? Ui.White(0.22) : Brushes.Transparent,
            BorderBrush = Ui.White(0.6),
            BorderThickness = new Thickness(filled ? 0 : 1.5),
            Cursor = Cursors.Hand,
            Template = BorderedTemplate(),
        };
        b.Click += (_, _) => action();
        return b;
    }

    public static FrameworkElement Buttons(params (string Text, bool Filled, Action Action)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (text, filled, action) in buttons) row.Children.Add(Button(text, filled, action));
        return row;
    }

    /// <summary>The dimmed backdrop and the dark card. Clicking the backdrop cancels.</summary>
    public static FrameworkElement Card(Grid host, string title, UIElement body, Action cancel, double width = 520)
    {
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) };
        backdrop.MouseLeftButtonDown += (_, e) => { cancel(); e.Handled = true; };
        host.Children.Add(backdrop);

        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.Light, Margin = new Thickness(0, 0, 0, 16), FontFamily = Ui.Font });
        content.Children.Add(body);
        return new Border
        {
            Width = width,
            Padding = new Thickness(28),
            Background = Ui.Brush("#1D1D1D"),
            BorderBrush = Ui.White(0.12),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.5 },
        };
    }

    private static ControlTemplate BorderedTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = border;
        return t;
    }
}
