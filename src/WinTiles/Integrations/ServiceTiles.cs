using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// A service tile: Pi-hole stats, what's playing on Plex, a Home Assistant light, your Proxmox
/// cluster… Every service is drawn from the same <see cref="ServiceSnapshot"/>, in the Metro style.
/// </summary>
public sealed class ServiceTileContent : TileContent
{
    private static readonly FontFamily SymbolFont = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");
    private readonly string key;
    private FlipFaces? flip;
    private string drawn = "";

    public ServiceTileContent(Tile tile, Size frame, Launcher l) : base(tile, frame, l)
    {
        key = ServiceHub.Key(tile);
        Watch<string>(h => l.Services.Changed += h, h => l.Services.Changed -= h, changed => { if (changed == key) Rebuild(); });
        Rebuild();
        if (l.Services.StatusFor(tile) is null && l.TilesLive) l.Services.Refresh(tile);
    }

    private ServiceWidget Widget => Tile.Widget ?? ServiceWidget.Resources;
    private Integration? Integration => L.Services.IntegrationFor(Tile);

    public override void Tick(DateTime now) => flip?.Tick(now);

    private void Rebuild()
    {
        var status = L.Services.StatusFor(Tile);
        var snapshot = status?.Snapshot;
        // Redraw only when something visible changed (posters arriving count too).
        var signature = (snapshot?.Signature() ?? "") + "|" + status?.Error + "|" + status?.Loading + "|" +
                        string.Join(",", snapshot?.Items.Take(8).Select(i => L.Services.Poster(i.Image) is null ? "0" : "1") ?? Array.Empty<string>());
        if (signature == drawn && Children.Count > 0) return;
        drawn = signature;

        Children.Clear();
        flip = null;
        if (snapshot is not null)
        {
            Children.Add(Content(snapshot));
            if (status?.Error is { } error) Children.Add(ErrorBadge(error));
        }
        else if (status?.Error is { } error) Children.Add(Message("⚠", IsSmall ? null : error));
        else Children.Add(Message(Integration?.Kind.Symbol() ?? "🖥", IsSmall ? null : "Connecting…"));
        if (!IsSmall) Children.Add(Label(LabelText(snapshot)));
    }

    private string LabelText(ServiceSnapshot? s)
    {
        if (!string.IsNullOrWhiteSpace(Tile.Title)) return Tile.Title!;
        if (Widget == ServiceWidget.Entity && s?.Caption is { Length: > 0 } entityName) return entityName;
        if (s?.Name is { Length: > 0 } name) return name;
        if (Integration is not { } integration) return "Service";
        return integration.Kind.Widgets().Count > 1 && Tile.Size != TileSize.Medium
            ? $"{integration.DisplayName} · {Widget.Title()}"
            : integration.DisplayName;
    }

    // MARK: faces

    private UIElement Content(ServiceSnapshot s)
    {
        var flips = Widget.FlipsItems() && !s.Busy;
        switch (Tile.Size)
        {
            case TileSize.Small:
                return SmallFace(s);
            case TileSize.Medium:
            {
                var faces = new List<FrameworkElement> { Summary(s, compact: true) };
                if (flips) faces.AddRange(s.Items.Take(4).Select(i => ItemFace(i, compact: true)));
                flip = new FlipFaces(7, Phase, faces.ToArray());
                return flip;
            }
            case TileSize.Wide:
            {
                var faces = new List<FrameworkElement> { WideSummary(s) };
                if (flips) faces.AddRange(s.Items.Take(5).Select(i => ItemFace(i, compact: false)));
                flip = new FlipFaces(7, Phase, faces.ToArray());
                return flip;
            }
            default:
                return LargeFace(s);
        }
    }

    private FrameworkElement SmallFace(ServiceSnapshot s)
    {
        var symbol = s.Symbol ?? Integration?.Kind.Symbol() ?? "🖥";
        FrameworkElement shown = s.Headline.Length <= 5 && s.Symbol is null
            ? new Viewbox { Child = Ui.Text(s.Headline, 17, Ui.Light), MaxWidth = Frame.Width - 12, StretchDirection = StretchDirection.DownOnly }
            : Symbol(symbol, 22);
        shown.HorizontalAlignment = HorizontalAlignment.Center;
        shown.VerticalAlignment = VerticalAlignment.Center;
        shown.Opacity = s.IsOn == false ? 0.55 : 1;
        return Wrap(shown);
    }

    private FrameworkElement Summary(ServiceSnapshot s, bool compact)
    {
        var g = new Grid { Margin = new Thickness(10, 10, 10, 26) };
        var top = new DockPanel { VerticalAlignment = VerticalAlignment.Top, LastChildFill = false };
        if (s.Symbol is { } symbol) top.Children.Add(Symbol(symbol, 22));
        if (StateBadge(s) is { } badge) { DockPanel.SetDock(badge, Dock.Right); top.Children.Add(badge); }
        g.Children.Add(top);

        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        bottom.Children.Add(Fit(Ui.Text(s.Headline.Length == 0 ? "—" : s.Headline, 30, Ui.UltraLight)));
        if (Widget != ServiceWidget.Entity && s.Caption.Length > 0) bottom.Children.Add(Ui.Text(Caption(s), 11, opacity: 0.85));
        if (s.Series.Count > 0) bottom.Children.Add(Spark(s.Series, compact ? 18 : 22));
        else if (s.Progress is { } p) bottom.Children.Add(Bar(p, 2));
        else if (s.Gauges.FirstOrDefault() is { } gauge) bottom.Children.Add(Bar(gauge.Fraction, 2));
        g.Children.Add(bottom);
        return Wrap(g);
    }

    private string Caption(ServiceSnapshot s)
    {
        if (Widget == ServiceWidget.DnsSummary && s.IsOn == false) return "blocking paused";
        if (s.Items.Count == 0 && s.EmptyText.Length > 0 && s.Headline == "0") return s.EmptyText;
        return s.Caption;
    }

    private FrameworkElement WideSummary(ServiceSnapshot s)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Min(170, Frame.Width * 0.45)) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(Summary(s, compact: false));
        var right = new StackPanel { Margin = new Thickness(4, 12, 10, 26) };
        if (s.Traffic.Count > 0)
            foreach (var t in s.Traffic.Take(4)) right.Children.Add(TrafficRow(t));
        else if (s.Gauges.Count > 0 && Widget != ServiceWidget.DnsSummary)
            foreach (var gauge in s.Gauges.Take(3)) right.Children.Add(GaugeRow(gauge));
        else if (s.Stats.Count > 0)
            foreach (var stat in s.Stats.Take(4)) right.Children.Add(StatRow(stat));
        else
            foreach (var item in s.Items.Take(3)) right.Children.Add(Ui.Text(item.Title, 11));
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private FrameworkElement LargeFace(ServiceSnapshot s)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 10, 10, 26) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (s.Symbol is { } symbol) { var sym = Symbol(symbol, 26); sym.Margin = new Thickness(0, 0, 8, 0); header.Children.Add(sym); }
        header.Children.Add(Fit(Ui.Text(s.Headline.Length == 0 ? "—" : s.Headline, 40, Ui.UltraLight), Frame.Width * 0.55));
        if (Widget != ServiceWidget.Entity)
        {
            var cap = Ui.Text(Widget == ServiceWidget.DnsSummary && s.IsOn == false ? "blocking paused" : s.Caption, 12, opacity: 0.85);
            cap.VerticalAlignment = VerticalAlignment.Bottom;
            cap.Margin = new Thickness(8, 0, 0, 8);
            header.Children.Add(cap);
        }
        var head = new DockPanel { LastChildFill = true };
        if (StateBadge(s) is { } badge) { DockPanel.SetDock(badge, Dock.Right); badge.VerticalAlignment = VerticalAlignment.Top; head.Children.Add(badge); }
        head.Children.Add(header);
        panel.Children.Add(head);

        var used = 0;
        if (s.Series.Count > 0) { panel.Children.Add(Spark(s.Series, 34)); used += 2; }
        if (s.Progress is { } p) { panel.Children.Add(Bar(p, 4)); used += 1; }
        foreach (var t in s.Traffic.Take(5)) { panel.Children.Add(TrafficRow(t)); }
        used += (Math.Min(s.Traffic.Count, 5) + 1) / 2;
        foreach (var gauge in s.Gauges.Take(4)) { panel.Children.Add(GaugeRow(gauge)); used += 1; }
        if (s.Stats.Count > 0)
        {
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var stat in s.Stats.Take(6)) grid.Children.Add(StatRow(stat, margin: 10));
            panel.Children.Add(grid);
            used += (Math.Min(s.Stats.Count, 6) + 1) / 2;
        }
        if (s.Items.Count > 0)
        {
            panel.Children.Add(new Rectangle { Height = 1, Fill = Ui.White(0.25), Margin = new Thickness(0, 8, 0, 6) });
            foreach (var item in s.Items.Take(Math.Max(1, 7 - used))) panel.Children.Add(ItemRow(item));
        }
        else if (s.EmptyText.Length > 0 && Widget != ServiceWidget.Entity)
            panel.Children.Add(Ui.Text(s.EmptyText, 12, opacity: 0.7));
        return Wrap(panel);
    }

    /// <summary>One movie, episode, container or guest — with its poster filling the tile when there is one.</summary>
    private FrameworkElement ItemFace(ServiceItem item, bool compact)
    {
        var g = new Grid();
        if (L.Services.Poster(item.Image) is { } poster)
        {
            g.Children.Add(new Image { Source = poster, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top });
            g.Children.Add(new Border
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0.45),
                    EndPoint = new Point(0.5, 1),
                    GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Color.FromArgb(190, 0, 0, 0), 1) },
                },
            });
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 10, 10, 26) };
        var title = Ui.Text(item.Title, compact ? 12 : 14, Ui.Semibold);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxHeight = compact ? 34 : 40;
        text.Children.Add(title);
        if (item.Subtitle.Length > 0) text.Children.Add(Ui.Text(item.Subtitle, 10.5, opacity: 0.9));
        if (item.Progress is { } p) text.Children.Add(Bar(p, 3));
        g.Children.Add(text);
        return g;
    }

    // MARK: rows and bits

    private FrameworkElement ItemRow(ServiceItem item)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (L.Services.Poster(item.Image) is { } poster)
            g.Children.Add(new Image { Source = poster, Width = 22, Height = 32, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left });
        else
            g.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Ui.White(item.IsActive ? 0.95 : 0.3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(item.Title, 12, FontWeights.Medium));
        if (item.Subtitle.Length > 0) text.Children.Add(Ui.Text(item.Subtitle, 10, opacity: 0.8));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (item.Progress is { } p)
        {
            var pct = Ui.Text(Fmt.Percent(p), 10, opacity: 0.8);
            pct.VerticalAlignment = VerticalAlignment.Center;
            pct.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(pct, 2);
            g.Children.Add(pct);
        }
        return g;
    }

    private static FrameworkElement GaugeRow(ServiceGauge gauge)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        var line = new DockPanel();
        var detail = Ui.Text(gauge.Detail, 10, opacity: 0.85);
        DockPanel.SetDock(detail, Dock.Right);
        line.Children.Add(detail);
        line.Children.Add(Ui.Text(gauge.Label, 10, Ui.Semibold, 0.85));
        s.Children.Add(line);
        s.Children.Add(Bar(gauge.Fraction, 2));
        return s;
    }

    private static FrameworkElement StatRow(ServiceStat stat, double margin = 0)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, margin, 4) };
        var value = Ui.Text(stat.Value, 11);
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(value);
        line.Children.Add(Ui.Text(stat.Label, 10, Ui.Semibold, 0.75));
        return line;
    }

    /// <summary>"WAN   ↓ 48 Mb/s  ↑ 3.1 Mb/s"</summary>
    private static FrameworkElement TrafficRow(ServiceTraffic t)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var speeds = new StackPanel { Orientation = Orientation.Horizontal };
        speeds.Children.Add(Ui.Text("↓ " + Fmt.Bitrate(t.Down), 10.5));
        var up = Ui.Text("↑ " + Fmt.Bitrate(t.Up), 10.5, opacity: 0.8);
        up.Margin = new Thickness(8, 0, 0, 0);
        speeds.Children.Add(up);
        DockPanel.SetDock(speeds, Dock.Right);
        line.Children.Add(speeds);
        line.Children.Add(Ui.Text(t.Name, 10, Ui.Semibold, 0.85));
        return line;
    }

    /// <summary>A thin Metro progress bar.</summary>
    private static FrameworkElement Bar(double fraction, double top)
    {
        var f = Math.Clamp(fraction, 0, 1);
        var g = new Grid { Height = 4, Margin = new Thickness(0, top, 0, 0), Background = Ui.White(0.25) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
        g.Children.Add(new Rectangle { Fill = Brushes.White });
        return g;
    }

    private static FrameworkElement Spark(IReadOnlyList<double> values, double height)
    {
        var line = new PathFigure { StartPoint = new Point(0, 1 - values[0]) };
        var area = new PathFigure { StartPoint = new Point(0, 1), IsClosed = true };
        area.Segments.Add(new LineSegment(new Point(0, 1 - values[0]), false));
        for (var i = 1; i < values.Count; i++)
        {
            var p = new Point(i, 1 - Math.Clamp(values[i], 0, 1));
            line.Segments.Add(new LineSegment(p, true));
            area.Segments.Add(new LineSegment(p, false));
        }
        area.Segments.Add(new LineSegment(new Point(values.Count - 1, 1), false));
        var g = new Grid { Height = height, Margin = new Thickness(0, 3, 0, 0) };
        g.Children.Add(new Path { Data = new PathGeometry(new[] { area }), Fill = Ui.White(0.2), Stretch = Stretch.Fill });
        g.Children.Add(new Path { Data = new PathGeometry(new[] { line }), Stroke = Brushes.White, StrokeThickness = 1.5, Stretch = Stretch.Fill });
        return g;
    }

    private FrameworkElement? StateBadge(ServiceSnapshot s)
    {
        if (s.IsOn != false) return null;
        var text = Widget switch
        {
            ServiceWidget.DnsSummary => "PAUSED",
            ServiceWidget.Network => "OFFLINE",
            ServiceWidget.Resources => "CHECK",
            ServiceWidget.Firewall => "GATEWAY DOWN",
            ServiceWidget.Systems => "SYSTEM DOWN",
            _ => null,
        };
        if (text is null) return null;
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(76, 0, 0, 0)),
            Padding = new Thickness(5, 2, 5, 2),
            Child = Ui.Text(text, 9, FontWeights.Bold),
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    private static FrameworkElement ErrorBadge(string message)
    {
        var t = new TextBlock
        {
            Text = "⚠",
            FontFamily = SymbolFont,
            FontSize = 11,
            Foreground = Ui.Brush("#FFD54F"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6),
            ToolTip = message,
        };
        return t;
    }

    private FrameworkElement Message(string symbol, string? text)
    {
        var s = new StackPanel
        {
            Margin = new Thickness(10, 10, 10, 26),
            HorizontalAlignment = IsSmall ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            VerticalAlignment = IsSmall ? VerticalAlignment.Center : VerticalAlignment.Top,
        };
        s.Children.Add(Symbol(symbol, IsSmall ? 22 : 26));
        if (text is not null)
        {
            var t = Ui.Text(text, 11, opacity: 0.85);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.WordEllipsis;
            t.MaxHeight = Tile.Size == TileSize.Medium ? 46 : 60;
            t.Margin = new Thickness(0, 6, 0, 0);
            s.Children.Add(t);
        }
        return s;
    }

    private static TextBlock Symbol(string symbol, double size) => new()
    {
        Text = symbol,
        FontFamily = SymbolFont,
        FontSize = size,
        Foreground = Brushes.White,
    };

    private static FrameworkElement Fit(TextBlock t, double? maxWidth = null) =>
        new Viewbox { Child = t, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = maxWidth ?? double.PositiveInfinity };

    private static Grid Wrap(UIElement e)
    {
        var g = new Grid();
        g.Children.Add(e);
        return g;
    }
}

/// <summary>Refresh, pause blocking, toggle, start/stop containers, interrupt ComfyUI…</summary>
public static class ServiceMenu
{
    public static IEnumerable<object> Items(Launcher launcher, Tile tile)
    {
        var services = launcher.Services;
        if (services.IntegrationFor(tile) is not { } integration)
        {
            yield return Ui.Item("Choose a Service…", () => launcher.EditServiceTile(tile));
            yield break;
        }
        var snapshot = services.StatusFor(tile)?.Snapshot;
        yield return Ui.Item($"Open {integration.DisplayName}", () => launcher.ActivateService(tile, openOnly: true));
        yield return Ui.Item("Refresh", () => services.Refresh(tile));
        switch (tile.Widget)
        {
            case ServiceWidget.DnsSummary:
                if (snapshot?.IsOn == false)
                    yield return Ui.Item("Resume Blocking", () => services.Perform(new ServiceAction.ResumeBlocking(), tile));
                else
                    yield return Ui.Submenu("Pause Blocking", new object[]
                    {
                        Ui.Item("For 1 Minute", () => services.Perform(new ServiceAction.PauseBlocking(60), tile)),
                        Ui.Item("For 5 Minutes", () => services.Perform(new ServiceAction.PauseBlocking(300), tile)),
                        Ui.Item("For 30 Minutes", () => services.Perform(new ServiceAction.PauseBlocking(1800), tile)),
                        Ui.Item("For 1 Hour", () => services.Perform(new ServiceAction.PauseBlocking(3600), tile)),
                        new Separator(),
                        Ui.Item("Until I Resume", () => services.Perform(new ServiceAction.PauseBlocking(null), tile)),
                    });
                break;
            case ServiceWidget.Entity when tile.Entity is { } entity && HomeAssistant.Service(HomeAssistant.Domain(entity)) is not null:
                yield return Ui.Item(HomeAssistant.IsRunnable(HomeAssistant.Domain(entity)) ? "Run" : "Toggle",
                    () => services.Perform(new ServiceAction.ToggleEntity(entity), tile));
                break;
            case ServiceWidget.Generation:
                if (snapshot?.Busy == true)
                    yield return Ui.Item("Interrupt Current Job", () => services.Perform(new ServiceAction.Interrupt(), tile));
                yield return Ui.Item("Clear Queue", () => services.Perform(new ServiceAction.ClearQueue(), tile));
                break;
            case ServiceWidget.Containers when integration.Kind == ServiceKind.Docker && snapshot?.Items.Count > 0:
                yield return Ui.Submenu("Containers", snapshot.Items.Take(25).Select(item => (object)Ui.Submenu(item.Title, item.IsActive
                    ? new object[]
                    {
                        Ui.Item("Stop", () => services.Perform(new ServiceAction.Container(item.Id, "stop"), tile)),
                        Ui.Item("Restart", () => services.Perform(new ServiceAction.Container(item.Id, "restart"), tile)),
                    }
                    : new object[] { Ui.Item("Start", () => services.Perform(new ServiceAction.Container(item.Id, "start"), tile)) })));
                break;
        }
        yield return new Separator();
        yield return Ui.Item("Edit Tile…", () => launcher.EditServiceTile(tile));
        yield return Ui.Item($"Edit {integration.DisplayName} Connection…", () => launcher.OpenSettings(addingIntegration: false, editing: integration.Id));
    }
}

/// <summary>
/// A Metro-style panel over Start: pick a connected service, what to show and (for Home Assistant
/// and Beszel) which entity or system.
/// </summary>
public sealed class ServiceTileEditor : Grid
{
    private readonly Launcher launcher;
    private readonly Tile? editing;
    private readonly TileDestination? destination;
    private readonly ComboBox service = new() { FontSize = 14, MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel widgets = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock widgetDetail = Panels.Note("");
    private readonly StackPanel entityBox = new();
    private readonly TextBox entitySearch = Panels.Field();
    private readonly StackPanel entityList = new();
    private readonly TextBlock entityStatus = Panels.Note("");
    private readonly TextBox name = Panels.Field();
    private readonly TextBlock error = Panels.Error();
    private ServiceWidget widget = ServiceWidget.Resources;
    private string entity = "";
    private List<PickableEntity> entities = new();
    private int loadToken;

    private List<Integration> Integrations => launcher.Store.Settings.Integrations;
    private Integration? Selected => service.SelectedItem is ComboBoxItem { Tag: Integration i } ? i : null;

    public ServiceTileEditor(Launcher launcher, Tile? editing, TileDestination? destination)
    {
        this.launcher = launcher;
        this.editing = editing;
        this.destination = destination;
        var body = new StackPanel();
        var title = editing is null ? "Add a service tile" : "Edit service tile";

        if (Integrations.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Connect Plex, Pi-hole, Home Assistant, Proxmox, ComfyUI and more in Settings → Integrations, then add their tiles here.",
                Foreground = Brushes.White, Opacity = 0.85, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            });
            body.Children.Add(Panels.Buttons(("Cancel", false, launcher.ClosePanel), ("Connect a Service…", true, () =>
            {
                launcher.ClosePanel();
                launcher.OpenSettings(addingIntegration: true, editing: null);
            })));
            Children.Add(Panels.Card(this, title, body, launcher.ClosePanel));
            return;
        }

        foreach (var category in Enum.GetValues<ServiceCategory>())
        {
            var members = Integrations.Where(i => i.Kind.Category() == category).ToList();
            if (members.Count == 0) continue;
            service.Items.Add(new ComboBoxItem { Content = category.Title(), IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var i in members) service.Items.Add(new ComboBoxItem { Content = "    " + i.Kind.Symbol() + "  " + i.DisplayName, Tag = i });
        }
        service.SelectionChanged += (_, _) => ServiceChanged();

        entitySearch.TextChanged += (_, _) => FillEntities();
        var scroll = new ScrollViewer { Content = entityList, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.White(0.04), Margin = new Thickness(0, 6, 0, 4) };
        entityBox.Children.Add(entitySearch);
        entityBox.Children.Add(scroll);
        entityBox.Children.Add(entityStatus);

        body.Children.Add(Panels.Labeled("Service", service));
        var show = new StackPanel();
        show.Children.Add(widgets);
        show.Children.Add(widgetDetail);
        widgetDetail.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(Panels.Labeled("Show", show));
        body.Children.Add(entityBox);
        body.Children.Add(Panels.Labeled("Name (optional)", name));
        body.Children.Add(error);

        var manage = new TextBlock { Text = "Manage Services…", Foreground = Brushes.White, Opacity = 0.75, FontSize = 12, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        manage.MouseLeftButtonUp += (_, _) => { launcher.ClosePanel(); launcher.OpenSettings(); };
        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        footer.Children.Add(manage);
        var buttons = Panels.Buttons(("Cancel", false, launcher.ClosePanel), (editing is null ? "Add" : "Save", true, Save));
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Insert(0, buttons);
        body.Children.Add(footer);
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };

        Children.Add(Panels.Card(this, title, body, launcher.ClosePanel, width: 560));

        // Start from the tile being edited, or the first service.
        name.Text = editing?.Title ?? "";
        entity = editing?.Entity ?? "";
        var start = service.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Integration x && x.Id == editing?.IntegrationId)
                    ?? service.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Integration);
        if (editing?.Widget is { } w) widget = w;
        service.SelectedItem = start;
    }

    private void ServiceChanged()
    {
        error.Text = "";
        if (Selected is not { } integration) return;
        var kinds = integration.Kind.Widgets();
        if (!kinds.Contains(widget)) widget = kinds[0];
        widgets.Children.Clear();
        foreach (var w in kinds)
        {
            var b = new RadioButton
            {
                Content = new TextBlock { Text = w.Title(), Foreground = Brushes.White },
                GroupName = "widget",
                IsChecked = w == widget,
                Margin = new Thickness(0, 0, 16, 0),
            };
            b.Checked += (_, _) => { widget = w; widgetDetail.Text = w.Detail(); };
            widgets.Children.Add(b);
        }
        widgetDetail.Text = widget.Detail();
        entityBox.Visibility = integration.Kind.PicksEntity() ? Visibility.Visible : Visibility.Collapsed;
        if (integration.Kind.PicksEntity()) LoadEntities(integration);
    }

    private async void LoadEntities(Integration integration)
    {
        var token = ++loadToken;
        entities = new();
        FillEntities();
        entityStatus.Text = integration.Kind == ServiceKind.Beszel ? "Loading systems…" : "Loading entities…";
        try
        {
            var list = await launcher.Services.Entities(integration);
            if (token != loadToken) return;
            entities = list;
            entityStatus.Text = list.Count == 0 ? (integration.Kind == ServiceKind.Beszel ? "No systems found." : "No entities found.") : "";
        }
        catch (Exception ex)
        {
            if (token == loadToken) entityStatus.Text = ServiceHub.Message(ex);
        }
        FillEntities();
    }

    private void FillEntities()
    {
        entityList.Children.Clear();
        var systems = Selected?.Kind == ServiceKind.Beszel;
        if (systems) entityList.Children.Add(EntityRow("", "All systems", "", "▦"));
        var q = entitySearch.Text.Trim().ToLowerInvariant();
        foreach (var e in entities.Where(e => q.Length == 0 || e.Name.ToLowerInvariant().Contains(q) || e.Id.ToLowerInvariant().Contains(q)).Take(300))
        {
            var symbol = systems ? "🖥" : HomeAssistant.Symbol(e.Domain, null, e.State == "on");
            entityList.Children.Add(EntityRow(e.Id, e.Name, systems ? TitleCase(e.State) : HomeAssistant.Label(e.State), symbol, systems ? null : e.Id));
        }
    }

    private static string TitleCase(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private FrameworkElement EntityRow(string id, string title, string state, string symbol, string? detail = null)
    {
        var row = new Grid { Background = entity == id ? Ui.White(0.22) : Brushes.Transparent, Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = symbol, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"), Foreground = Brushes.White, Margin = new Thickness(8, 5, 0, 5), VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        text.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail is not null) text.Children.Add(new TextBlock { Text = detail, Foreground = Brushes.White, Opacity = 0.55, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var st = new TextBlock { Text = state, Foreground = Brushes.White, Opacity = 0.7, FontSize = 11, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(st, 2);
        row.Children.Add(st);
        row.MouseLeftButtonUp += (_, _) => { entity = id; error.Text = ""; FillEntities(); };
        return row;
    }

    private void Save()
    {
        if (Selected is not { } integration) return;
        var kinds = integration.Kind.Widgets();
        var chosen = kinds.Contains(widget) ? widget : kinds[0];
        if (integration.Kind == ServiceKind.HomeAssistant && entity.Length == 0)
        {
            error.Text = "Choose an entity.";
            return;
        }
        string? entityValue = integration.Kind switch
        {
            ServiceKind.HomeAssistant => entity,
            ServiceKind.Beszel => entity.Length == 0 ? null : entity,      // empty = all systems
            _ => null,
        };
        var title = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
        if (editing is not null)
        {
            launcher.Store.Update(editing.Id, t => t with
            {
                IntegrationId = integration.Id,
                Widget = chosen,
                Entity = entityValue,
                Title = title,
                Size = t.Widget != chosen && !chosen.Sizes().Contains(t.Size) ? chosen.DefaultSize() : t.Size,
            });
        }
        else
        {
            var tile = Tile.ForService(integration.Id, chosen, entityValue, title);
            if (chosen == ServiceWidget.Entity)
                tile = tile with { Size = HomeAssistant.IsRunnable(HomeAssistant.Domain(entity)) ? TileSize.Small : TileSize.Medium };
            if (chosen == ServiceWidget.Systems && entityValue is not null) tile = tile with { Size = TileSize.Medium };
            launcher.Store.AddTile(tile, destination);
        }
        launcher.ClosePanel();
        if (launcher.Services.Active) launcher.Services.Tick();
    }
}
