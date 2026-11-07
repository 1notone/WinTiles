using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

public sealed class SettingsWindow : Window
{
    private readonly Launcher launcher;
    private LayoutStore Store => launcher.Store;
    private AppSettings S => launcher.Store.Settings;

    private readonly TextBox hexBox = new() { Width = 90, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private readonly TextBlock hexError = Note("");
    private readonly ColorWheel wheel = new();
    private readonly StackPanel presets = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock cityStatus = Note("");
    private readonly TextBlock hotKeyStatus = Note("");
    private readonly StackPanel mediaRows = new();
    private readonly StackPanel liveRows = new();
    private readonly TextBlock wallpaperName = new() { Width = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly StackPanel integrationList = new();
    private bool updating;

    public SettingsWindow(Launcher launcher)
    {
        this.launcher = launcher;
        Title = "WinTiles Settings";
        Width = 620;
        Height = 820;
        MinWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Ui.TextFont;
        try { ThemeMode = ThemeMode.System; } catch { }   // Windows 11 Fluent look, following light/dark mode
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/WinTiles.ico")); } catch { }

        var page = new StackPanel { Margin = new Thickness(24, 16, 24, 24) };
        page.Children.Add(StartScreenSection());
        page.Children.Add(BehaviorSection());
        page.Children.Add(OpeningSection());
        page.Children.Add(LiveTilesSection());
        page.Children.Add(IntegrationsSection());
        page.Children.Add(FooterSection());
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Store.SettingsChanged += OnSettingsChanged;
        launcher.IntegrationRequested += OpenIntegrationEditor;
        Closed += (_, _) =>
        {
            Store.SettingsChanged -= OnSettingsChanged;
            launcher.IntegrationRequested -= OpenIntegrationEditor;
        };
        SyncColor();
        SyncBackground();
    }

    private void OnSettingsChanged(AppSettings _)
    {
        SyncColor();
        SyncBackground();
    }

    // MARK: sections

    private UIElement StartScreenSection()
    {
        var s = Section("Start screen");

        var name = new TextBox { Text = S.UserName, Width = 260 };
        name.TextChanged += (_, _) => Store.ChangeSettings(x => x.UserName = name.Text);
        s.Children.Add(Row("Name", name));

        foreach (var hex in Palette.Backgrounds)
        {
            var dot = new Ellipse { Width = 22, Height = 22, Fill = Ui.Brush(hex), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = hex, Tag = hex };
            dot.MouseLeftButtonUp += (_, _) => Store.ChangeSettings(x => x.Background = hex);
            presets.Children.Add(dot);
        }
        // Background: a color, a picture or video, or a live wallpaper.
        var styles = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var style in Enum.GetValues<BackgroundStyle>())
        {
            var b = new RadioButton { Content = style.Title(), GroupName = "background", IsChecked = S.BackgroundStyle == style, Margin = new Thickness(0, 0, 16, 0) };
            b.Checked += (_, _) =>
            {
                if (style == BackgroundStyle.Media && Wallpapers.PathFor(S.WallpaperFile) is null && !ChooseWallpaper())
                {
                    Dispatcher.BeginInvoke(() => SyncStyleRadios(styles, S.BackgroundStyle));     // cancelled: back to the current style
                    return;
                }
                Store.ChangeSettings(x => x.BackgroundStyle = style);
            };
            styles.Children.Add(b);
        }
        s.Children.Add(Row("Background", styles));

        var choose = new Button { Content = "Choose…", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
        choose.Click += (_, _) => ChooseWallpaper();
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal };
        fileRow.Children.Add(wallpaperName);
        fileRow.Children.Add(choose);
        mediaRows.Children.Add(Row("File", fileRow, "A picture, an animated GIF, or a video (MP4, MOV, WMV…) that loops silently. WinTiles keeps its own copy."));
        var dim = new Slider { Minimum = 0, Maximum = 0.7, Value = S.WallpaperDim, Width = 240 };
        dim.ValueChanged += (_, e) => Store.ChangeSettings(x => x.WallpaperDim = e.NewValue);
        mediaRows.Children.Add(Row("Dim", dim, "Darkens the picture so tiles stay readable."));
        s.Children.Add(mediaRows);

        var liveStyles = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var lw in Enum.GetValues<LiveWallpaper>())
        {
            var b = new RadioButton { Content = lw.ToString(), GroupName = "live", IsChecked = S.LiveWallpaper == lw, Margin = new Thickness(0, 0, 16, 0) };
            b.Checked += (_, _) => Store.ChangeSettings(x => x.LiveWallpaper = lw);
            liveStyles.Children.Add(b);
        }
        liveRows.Children.Add(Row("Style", liveStyles, "Drawn by WinTiles and tinted with the color below."));
        s.Children.Add(liveRows);
        var motion = Note("Animated wallpapers pause when Start is hidden or covered, with animation effects turned off, and in battery saver.");
        motion.Margin = new Thickness(150, 0, 0, 8);
        s.Children.Add(motion);

        s.Children.Add(Row("Color", presets));

        // Any color: a color wheel with a brightness slider, plus a hex code. Start changes live.
        wheel.ColorChanged += hex => { if (!updating) Store.ChangeSettings(x => x.Background = hex); };
        hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        hexBox.LostKeyboardFocus += (_, _) => ApplyHex();
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        hexRow.Children.Add(hexBox);
        hexRow.Children.Add(hexError);
        hexError.Margin = new Thickness(10, 0, 0, 0);
        hexError.VerticalAlignment = VerticalAlignment.Center;
        var any = new StackPanel();
        any.Children.Add(wheel);
        any.Children.Add(hexRow);
        s.Children.Add(Row("Any color", any));

        var size = new Slider { Minimum = 0.8, Maximum = 1.5, Value = S.TileScale, Width = 240, SmallChange = 0.05, LargeChange = 0.1 };
        size.ValueChanged += (_, e) => Store.ChangeSettings(x => x.TileScale = e.NewValue);
        s.Children.Add(Row("Tile size", size));

        var shapes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var shape in Enum.GetValues<TileShape>())
        {
            var b = new RadioButton
            {
                Content = ShapeLabel(shape),
                GroupName = "shape",
                IsChecked = S.TileShape == shape,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            b.Checked += (_, _) => Store.ChangeSettings(x => x.TileShape = shape);
            shapes.Children.Add(b);
        }
        s.Children.Add(Row("Tile shape", shapes));

        var finishes = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var finish in Enum.GetValues<TileFinish>())
        {
            var b = new RadioButton { Content = finish.ToString(), GroupName = "finish", IsChecked = S.TileFinish == finish, Margin = new Thickness(0, 0, 16, 0) };
            b.Checked += (_, _) => Store.ChangeSettings(x => x.TileFinish = finish);
            finishes.Children.Add(b);
        }
        s.Children.Add(Row("Tile finish", finishes, "Glass: translucent tiles with a sheen, a bright rim and a highlight that follows the pointer."));
        var tint = new Slider { Minimum = 0.1, Maximum = 0.9, Value = S.GlassTint, Width = 240 };
        tint.ValueChanged += (_, e) => Store.ChangeSettings(x => x.GlassTint = e.NewValue);
        s.Children.Add(Row("Glass tint", tint, "From clear to strongly colored."));
        var shadows = new ComboBox { Width = 160, ItemsSource = Enum.GetValues<TileShadow>(), SelectedItem = S.TileShadow };
        shadows.SelectionChanged += (_, _) => { if (shadows.SelectedItem is TileShadow sh) Store.ChangeSettings(x => x.TileShadow = sh); };
        s.Children.Add(Row("Tile shadow", shadows));

        s.Children.Add(Check("Color app tiles from their icons", S.IconColoredTiles, v => Store.ChangeSettings(x => x.IconColoredTiles = v)));
        s.Children.Add(Check("Show a dot on tiles of apps that are running", S.ShowRunningIndicator, v => Store.ChangeSettings(x => x.ShowRunningIndicator = v)));
        return s;
    }

    private static void SyncStyleRadios(Panel styles, BackgroundStyle current)
    {
        foreach (var r in styles.Children.OfType<RadioButton>()) r.IsChecked = (r.Content as string) == current.Title();
    }

    private void SyncBackground()
    {
        wallpaperName.Text = Wallpapers.DisplayName(S.WallpaperFile) ?? "None";
        mediaRows.Visibility = S.BackgroundStyle == BackgroundStyle.Media ? Visibility.Visible : Visibility.Collapsed;
        liveRows.Visibility = S.BackgroundStyle == BackgroundStyle.Live ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <returns>true if a wallpaper was chosen.</returns>
    private bool ChooseWallpaper()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a wallpaper", Filter = Wallpapers.FileFilter };
        if (dialog.ShowDialog(this) != true) return false;
        if (Wallpapers.KindOf(dialog.FileName) is null)
        {
            MessageBox.Show(this, "WinTiles can't use this kind of file as a wallpaper. Choose a picture, a GIF or a video.", "Wallpaper", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        try
        {
            var stored = Wallpapers.Import(dialog.FileName, S.WallpaperFile);
            Store.ChangeSettings(x => { x.WallpaperFile = stored; x.BackgroundStyle = BackgroundStyle.Media; });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The file couldn't be copied: " + ex.Message, "Wallpaper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>The right-click menu's "Change Wallpaper…" opens Settings and this dialog.</summary>
    public void ChangeWallpaper() => ChooseWallpaper();

    private static UIElement ShapeLabel(TileShape shape)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new Path
        {
            Data = Ui.Squircle(16, 16, shape.CornerRadius() / 3),
            Fill = SystemColors.ControlTextBrush,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        p.Children.Add(new TextBlock { Text = shape.ToString(), VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    private UIElement BehaviorSection()
    {
        var s = Section("Behavior");
        var caption = Note(KeepCaption(S.KeepOnDesktop));
        s.Children.Add(Check("Keep Start on the desktop behind your apps", S.KeepOnDesktop, v =>
        {
            Store.ChangeSettings(x => x.KeepOnDesktop = v);
            caption.Text = KeepCaption(v);
        }));
        caption.Margin = new Thickness(24, 0, 0, 8);
        s.Children.Add(caption);
        return s;
    }

    private static string KeepCaption(bool on) => on
        ? "Like Windows 8: launching an app puts Start behind it, and you see Start whenever your windows are closed or minimized."
        : "Start appears only when you open it, and disappears when you launch an app.";

    private UIElement OpeningSection()
    {
        var s = Section("Opening Start");

        var combo = new ComboBox { Width = 220, DisplayMemberPath = nameof(HotKeyPreset.Label), ItemsSource = HotKeyPreset.All };
        combo.SelectedItem = HotKeyPreset.Find(S.HotKey);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not HotKeyPreset p) return;
            Store.ChangeSettings(x => x.HotKey = p.Id);
            hotKeyStatus.Text = p.VirtualKey == 0 || launcher.ApplyHotKey() ? "" : "Another app is already using this shortcut.";
        };
        var hk = new StackPanel();
        hk.Children.Add(combo);
        hk.Children.Add(hotKeyStatus);
        s.Children.Add(Row("Keyboard shortcut", hk));

        s.Children.Add(Check("Open WinTiles with the Windows key (instead of the Start menu)", S.WindowsKey, v => Store.ChangeSettings(x => x.WindowsKey = v)));
        var winNote = Note("Windows key shortcuts like Win+E and Win+D keep working.");
        winNote.Margin = new Thickness(24, 0, 0, 8);
        s.Children.Add(winNote);
        var toggles = Check("Corner also puts Start away and shows the desktop when Start is open", S.HotCornerToggles, v => Store.ChangeSettings(x => x.HotCornerToggles = v));
        toggles.Margin = new Thickness(24, 0, 0, 6);
        toggles.IsEnabled = S.HotCorner;
        s.Children.Add(Check("Open by moving the pointer into the bottom-left corner", S.HotCorner, v =>
        {
            Store.ChangeSettings(x => x.HotCorner = v);
            toggles.IsEnabled = v;
        }));
        s.Children.Add(toggles);
        s.Children.Add(Check("Start WinTiles when I sign in", StartupRegistration.IsEnabled, StartupRegistration.Set));
        return s;
    }

    private UIElement LiveTilesSection()
    {
        var s = Section("Live tiles");

        var city = new TextBox { Text = S.WeatherCity ?? "", Width = 200 };
        var find = new Button { Content = "Find", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
        var postal = new TextBox { Text = S.WeatherPostalCode ?? "", Width = 200 };
        var use = new Button { Content = "Use", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };

        void UsePlace(Place place, string? postalCode)
        {
            Store.ChangeSettings(x =>
            {
                x.WeatherCity = place.Name;
                x.WeatherLat = place.Latitude;
                x.WeatherLon = place.Longitude;
                x.WeatherPostalCode = postalCode;
            });
            city.Text = place.Name;
            postal.Text = postalCode ?? "";
            cityStatus.Text = "Using " + place.Label + (postalCode is null ? "" : $" ({postalCode})");
        }

        async void FindPostalCode()
        {
            var code = postal.Text.Trim();
            if (code.Length == 0) return;
            if (!WeatherApi.LooksLikePostalCode(code))
            {
                cityStatus.Text = $"“{code}” doesn't look like a ZIP or postal code.";
                return;
            }
            cityStatus.Text = $"Looking up {code}…";
            try
            {
                if (await WeatherProvider.LookupPostalCode(code) is not { } place)
                {
                    var region = WeatherApi.PostalCountry is { } r ? " in " + new System.Globalization.RegionInfo(r).DisplayName : "";
                    cityStatus.Text = $"No place with the postal code “{code}” was found{region}.";
                    return;
                }
                UsePlace(place, WeatherApi.NormalizePostalCode(code, WeatherApi.PostalCountry));
            }
            catch
            {
                cityStatus.Text = "Couldn't reach the weather service. Check your connection.";
            }
        }

        async void FindCity()
        {
            var q = city.Text.Trim();
            if (q.Length == 0) return;
            // A ZIP typed into the city box works too.
            if (WeatherApi.LooksLikePostalCode(q))
            {
                postal.Text = q;
                FindPostalCode();
                return;
            }
            cityStatus.Text = "Searching…";
            try
            {
                if (await WeatherProvider.Geocode(q) is not { } place)
                {
                    cityStatus.Text = $"No place called “{q}” was found.";
                    return;
                }
                UsePlace(place, null);
            }
            catch
            {
                cityStatus.Text = "Couldn't reach the weather service. Check your connection.";
            }
        }
        find.Click += (_, _) => FindCity();
        city.KeyDown += (_, e) => { if (e.Key == Key.Enter) FindCity(); };
        use.Click += (_, _) => FindPostalCode();
        postal.KeyDown += (_, e) => { if (e.Key == Key.Enter) FindPostalCode(); };
        var cityRow = new StackPanel { Orientation = Orientation.Horizontal };
        cityRow.Children.Add(city);
        cityRow.Children.Add(find);
        s.Children.Add(Row("Weather city", cityRow));
        var postalRow = new StackPanel { Orientation = Orientation.Horizontal };
        postalRow.Children.Add(postal);
        postalRow.Children.Add(use);
        var postalCol = new StackPanel();
        postalCol.Children.Add(postalRow);
        postalCol.Children.Add(cityStatus);
        s.Children.Add(Row("Or ZIP / postal code", postalCol));
        var credit = Note("");
        credit.Inlines.Add(new System.Windows.Documents.Run("Weather data by "));
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Open-Meteo.com")) { NavigateUri = new Uri(WeatherApi.AttributionUrl) };
        link.RequestNavigate += (_, e) => { AppLauncher.OpenUri(e.Uri.AbsoluteUri); e.Handled = true; };
        credit.Inlines.Add(link);
        credit.Inlines.Add(new System.Windows.Documents.Run(" (CC BY 4.0)."));
        credit.Margin = new Thickness(150, 0, 0, 8);
        s.Children.Add(credit);
        s.Children.Add(Check("Show temperatures in Fahrenheit", S.Fahrenheit, v => Store.ChangeSettings(x => x.Fahrenheit = v)));

        var feed = new TextBox { Text = S.NewsFeed, Width = 280 };
        var apply = new Button { Content = "Apply", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
        void ApplyFeed()
        {
            if (WebLink.Normalize(feed.Text) is { } url && WebLink.IsFetchable(url))
            {
                Store.ChangeSettings(x => x.NewsFeed = url.AbsoluteUri);
                feed.Text = url.AbsoluteUri;
            }
            else feed.Text = S.NewsFeed;
        }
        apply.Click += (_, _) => ApplyFeed();
        feed.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyFeed(); };
        var feedRow = new StackPanel { Orientation = Orientation.Horizontal };
        feedRow.Children.Add(feed);
        feedRow.Children.Add(apply);
        s.Children.Add(Row("Default news feed", feedRow,
            "Used by News tiles that don't have their own feed. Right-click a News tile and choose Change Feed… to give it one; add more News tiles for more sources."));

        var folder = new TextBlock { Text = S.PhotosFolder, Width = 250, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        var choose = new Button { Content = "Choose…", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = S.PhotosFolder, Title = "Photos folder" };
            if (dialog.ShowDialog(this) == true)
            {
                Store.ChangeSettings(x => x.PhotosFolder = dialog.FolderName);
                folder.Text = dialog.FolderName;
            }
        };
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        folderRow.Children.Add(folder);
        folderRow.Children.Add(choose);
        s.Children.Add(Row("Photos folder", folderRow));

        var cal = new TextBox { Text = S.CalendarFeed ?? "", Width = 340 };
        cal.LostKeyboardFocus += (_, _) => Store.ChangeSettings(x => x.CalendarFeed = string.IsNullOrWhiteSpace(cal.Text) ? null : cal.Text.Trim());
        s.Children.Add(Row("Calendar feed", cal,
            "Optional: an iCalendar (.ics) address, e.g. Outlook → Settings → Calendar → Shared calendars → Publish, or Google Calendar's “Secret address in iCal format”."));
        return s;
    }

    // MARK: integrations

    private UIElement IntegrationsSection()
    {
        var s = Section("Integrations");
        s.Children.Add(Note("Connect Plex, Jellyfin, Sonarr, Pi-hole, Home Assistant, OPNsense, Proxmox, Docker, Beszel, ComfyUI and more. WinTiles talks to each one directly — no Homarr needed. Passwords, tokens and API keys are kept in Windows Credential Manager."));
        s.Children.Add(integrationList);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = "Add Service…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => OpenIntegrationEditor(null);
        var tile = new Button { Content = "Add a Tile to Start…", Padding = new Thickness(12, 4, 12, 4) };
        tile.Click += (_, _) => launcher.AddServiceTile();
        buttons.Children.Add(add);
        buttons.Children.Add(tile);
        s.Children.Add(buttons);
        FillIntegrations();
        return s;
    }

    private void FillIntegrations()
    {
        integrationList.Children.Clear();
        foreach (var integration in S.Integrations)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var badge = new Border
            {
                Width = 28, Height = 28, Background = Ui.Brush(integration.Kind.DefaultColor()), Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = integration.Kind.Symbol(), FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            row.Children.Add(badge);
            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) => RemoveIntegration(integration);
            var editButton = new Button { Content = "Edit…", Padding = new Thickness(10, 2, 10, 2) };
            editButton.Click += (_, _) => OpenIntegrationEditor(integration.Id);
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(editButton, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(editButton);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = integration.DisplayName });
            var address = integration.Url.Trim();
            text.Children.Add(Note(integration.Kind.Title() + " · " + (address.Length == 0 ? (integration.Kind == ServiceKind.Docker ? "This PC" : "No address") : address)));
            row.Children.Add(text);
            integrationList.Children.Add(row);
        }
    }

    private void RemoveIntegration(Integration integration)
    {
        var count = Store.ServiceTiles(integration.Id).Count;
        var tiles = count == 0 ? "" : count == 1 ? " Its tile will be unpinned from Start." : $" Its {count} tiles will be unpinned from Start.";
        if (MessageBox.Show(this, "Its saved key or password will be deleted from Credential Manager." + tiles,
                $"Remove {integration.DisplayName}?", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Store.RemoveIntegration(integration.Id);
        launcher.Services.IntegrationChanged(integration.Id);
        FillIntegrations();
    }

    /// <summary>Add (null) or edit one service connection.</summary>
    public void OpenIntegrationEditor(Guid? id)
    {
        var original = id is { } x ? S.Integrations.FirstOrDefault(i => i.Id == x) : null;
        var editor = new IntegrationEditor(original) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } saved) return;
        Store.ChangeSettings(s =>
        {
            var list = s.Integrations.ToList();
            var index = list.FindIndex(i => i.Id == saved.Id);
            if (index >= 0) list[index] = saved; else list.Add(saved);
            s.Integrations = list;
        });
        launcher.Services.IntegrationChanged(saved.Id);      // the editor already saved the secret
        FillIntegrations();
    }

    private UIElement FooterSection()
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        Button B(string text, Action a)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => a();
            return b;
        }
        s.Children.Add(B("Show Layout File", () =>
        {
            Store.SaveNow();
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LayoutStore.FilePath}\"") { UseShellExecute = true });
        }));
        s.Children.Add(B("Reset Layout…", () =>
        {
            if (MessageBox.Show(this, "All tiles and groups will be replaced with the default layout. Your settings are kept.",
                    "Reset the Start layout?", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                Store.ResetLayout(launcher.Catalog.IsInstalled);
        }));
        s.Children.Add(B("Uninstall WinTiles…", Uninstall));
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        s.Children.Add(new TextBlock { Text = $"WinTiles {version?.ToString(3)}", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        return s;
    }

    private void Uninstall()
    {
        var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        var uninstaller = System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "unins*.exe").FirstOrDefault() : null;
        if (uninstaller is null)
        {
            MessageBox.Show(this, "This copy of WinTiles wasn't installed with the installer. Quit WinTiles and delete its folder to remove it.",
                "Uninstall WinTiles", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, "WinTiles will quit and its uninstaller will open. Your tiles and settings stay in your AppData folder unless you delete them.",
                "Uninstall WinTiles?", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true });
        launcher.Quit();
    }

    // MARK: color sync

    private void SyncColor()
    {
        updating = true;
        hexBox.Text = S.Background;
        hexError.Text = "";
        wheel.SetColor(S.Background);
        foreach (var dot in presets.Children.OfType<Ellipse>())
        {
            var selected = string.Equals(dot.Tag as string, S.Background, StringComparison.OrdinalIgnoreCase);
            dot.Stroke = selected ? SystemColors.ControlTextBrush : null;
            dot.StrokeThickness = selected ? 2 : 0;
        }
        updating = false;
    }

    private void ApplyHex()
    {
        if (Palette.NormalizeHex(hexBox.Text) is { } hex)
        {
            if (hex != S.Background) Store.ChangeSettings(x => x.Background = hex);
            hexError.Text = "";
        }
        else hexError.Text = "Use six hex digits, like #25115A";
    }

    // MARK: layout helpers

    private static StackPanel Section(string title)
    {
        var s = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        s.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        return s;
    }

    private static UIElement Row(string label, UIElement control, string? note = null)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) });
        var right = new StackPanel();
        right.Children.Add(control);
        if (note is not null)
        {
            var n = Note(note);
            n.Margin = new Thickness(0, 4, 0, 0);
            right.Children.Add(n);
        }
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 6) };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
}

/// <summary>An HSV color wheel with a brightness slider — any color or shade.</summary>
public sealed class ColorWheel : StackPanel
{
    private const int Size = 200;
    private readonly Image image = new() { Width = Size, Height = Size, Cursor = Cursors.Cross };
    private readonly Ellipse marker = new() { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Ellipse markerShadow = new() { Width = 16, Height = 16, Stroke = Brushes.Black, StrokeThickness = 1, Opacity = 0.5, IsHitTestVisible = false };
    private readonly Slider brightness = new() { Minimum = 0.05, Maximum = 1, Width = Size, Margin = new Thickness(0, 8, 0, 0), ToolTip = "Brightness" };
    private readonly Border swatch = new() { Width = 36, Height = 36, CornerRadius = new CornerRadius(4), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    private double h, s, v = 0.5;
    private double renderedV = -1;
    private bool setting;

    public event Action<string>? ColorChanged;

    public ColorWheel()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var canvas = new Canvas { Width = Size, Height = Size };
        canvas.Children.Add(image);
        canvas.Children.Add(markerShadow);
        canvas.Children.Add(marker);
        row.Children.Add(canvas);
        row.Children.Add(swatch);
        Children.Add(row);
        Children.Add(brightness);

        image.MouseLeftButtonDown += (_, e) => { image.CaptureMouse(); Pick(e.GetPosition(image)); };
        image.MouseMove += (_, e) => { if (image.IsMouseCaptured) Pick(e.GetPosition(image)); };
        image.MouseLeftButtonUp += (_, _) => image.ReleaseMouseCapture();
        brightness.ValueChanged += (_, e) =>
        {
            if (setting) return;
            v = e.NewValue;
            Render();
            Emit();
        };
    }

    public void SetColor(string hex)
    {
        setting = true;
        (h, s, v) = Palette.HexToHsv(hex);
        brightness.Value = Math.Max(v, brightness.Minimum);
        Render();
        setting = false;
    }

    private void Pick(Point p)
    {
        var c = Size / 2.0;
        var dx = p.X - c;
        var dy = p.Y - c;
        h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        s = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / c, 0, 1);
        Render();
        Emit();
    }

    private void Emit() => ColorChanged?.Invoke(Palette.HsvToHex(h, s, v));

    private void Render()
    {
        if (Math.Abs(v - renderedV) > 0.001)
        {
            renderedV = v;
            image.Source = Wheel(v);
        }
        var c = Size / 2.0;
        var rad = h * Math.PI / 180;
        var x = c + Math.Cos(rad) * s * c;
        var y = c + Math.Sin(rad) * s * c;
        Canvas.SetLeft(marker, x - 7); Canvas.SetTop(marker, y - 7);
        Canvas.SetLeft(markerShadow, x - 8); Canvas.SetTop(markerShadow, y - 8);
        swatch.Background = Ui.Brush(Palette.HsvToHex(h, s, v));
    }

    private static BitmapSource Wheel(double value)
    {
        var px = new byte[Size * Size * 4];
        var c = Size / 2.0;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = x + 0.5 - c;
                var dy = y + 0.5 - c;
                var r = Math.Sqrt(dx * dx + dy * dy) / c;
                if (r > 1) continue;
                var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var (rr, gg, bb) = Palette.HsvToRgb(hue, r, value);
                var alpha = Math.Clamp((1 - r) * c, 0, 1);    // soft edge
                var i = (y * Size + x) * 4;
                px[i] = (byte)(bb * 255 * alpha);
                px[i + 1] = (byte)(gg * 255 * alpha);
                px[i + 2] = (byte)(rr * 255 * alpha);
                px[i + 3] = (byte)(255 * alpha);
            }
        }
        var bmp = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Pbgra32, null, px, Size * 4);
        bmp.Freeze();
        return bmp;
    }
}
