using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// The chrome around one tile: color, shape, hover outline, the Windows 8 press "tilt",
/// drag to reorder, and the right-click menu. Kept alive across layout changes so live
/// tiles don't restart and positions can animate.
/// </summary>
public sealed class TileElement : Grid
{
    private readonly Launcher launcher;
    private readonly Border chrome = new() { RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly Path outline = new() { Stroke = Ui.White(0.45), StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false };
    private readonly GlassOverlay glass = new();
    private readonly Ellipse runningDot = new()
    {
        Width = 6, Height = 6, Fill = Brushes.White, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8 },
        ToolTip = "Running",
    };
    private readonly DropShadowEffect shadow = new() { Color = Colors.Black, Direction = 270 };
    private readonly ScaleTransform press = new(1, 1);
    private readonly SkewTransform tilt = new();
    private readonly TranslateTransform entrance = new();
    private TileContent? content;
    private Tile? tile;
    private Metrics metrics;
    private Guid groupId;
    private int column;
    private (Guid Group, double Left, double Top)? placed;
    private Point? pressAt;
    private bool dragged;

    public TileElement(Launcher launcher)
    {
        this.launcher = launcher;
        AllowDrop = true;
        Cursor = Cursors.Hand;
        RenderTransform = entrance;
        chrome.RenderTransform = new TransformGroup { Children = { press, tilt } };
        Children.Add(chrome);
        Children.Add(glass);
        Children.Add(runningDot);
        Children.Add(outline);

        MouseEnter += (_, _) => { outline.Opacity = 1; glass.Hover(true); ApplyShadow(hover: true); };
        MouseLeave += (_, _) => { outline.Opacity = 0; Release(); glass.Hover(false); ApplyShadow(hover: false); };
        MouseLeftButtonDown += OnPress;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnRelease;
        DragEnter += OnDragEnter;
        DragOver += (_, e) => { e.Effects = launcher.Store.DropEffect(e); e.Handled = true; };
        DragLeave += (_, _) => outline.Opacity = 0;
        Drop += OnDrop;
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) => BuildMenu();
        MouseMove += (_, e) => { if (glass.Visibility == Visibility.Visible) glass.Pointer(e.GetPosition(this)); };
        launcher.Store.SettingsChanged += OnSettingsChanged;
        launcher.Catalog.Changed += OnCatalogChanged;
        launcher.Running.Changed += UpdateRunningDot;
        launcher.WebIcons.Changed += OnWebIconChanged;
    }

    private void OnSettingsChanged(AppSettings _)
    {
        if (tile is null) return;
        ApplyBackground();
        ApplyShadow(hover: IsMouseOver);
        UpdateRunningDot();
    }

    private void OnWebIconChanged(string host)
    {
        // A site's icon arrived: icon-colored tiles take its color.
        if (tile is { Kind: TileKind.Web } && WebLink.Of(tile) is { } url && WebLink.DisplayHost(url) == host) ApplyBackground();
    }

    private void UpdateRunningDot()
    {
        var show = tile is { Kind: TileKind.App, AppPath: { } path } && launcher.Store.Settings.ShowRunningIndicator && launcher.Running.IsRunning(path);
        runningDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || tile is null) return;
        // Bottom-right on larger tiles (clear of the label), bottom-center on small ones, like the Dock.
        var inset = Math.Max(7, metrics.CornerRadius * 0.45);
        runningDot.HorizontalAlignment = tile.Size == TileSize.Small ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        runningDot.VerticalAlignment = VerticalAlignment.Bottom;
        runningDot.Margin = new Thickness(0, 0, tile.Size == TileSize.Small ? 0 : inset, tile.Size == TileSize.Small ? 4 : inset);
    }

    private void ApplyShadow(bool hover)
    {
        var (opacity, radius, y) = launcher.Store.Settings.TileShadow.Parameters();
        if (opacity <= 0) { Effect = null; return; }
        shadow.Opacity = hover ? Math.Min(opacity + 0.12, 0.8) : opacity;
        shadow.BlurRadius = (hover ? radius * 1.3 : radius) * 1.6;      // WPF's blur radius is roughly half SwiftUI's
        shadow.ShadowDepth = hover ? y * 1.4 : y;
        Effect = shadow;
    }
    private void OnCatalogChanged()
    {
        // App names arrive with the background scan; refresh the label and tooltip.
        if (tile is not { Kind: TileKind.App, AppPath: { } path }) return;
        ToolTip = launcher.Catalog.Name(path);
        content?.Detach();
        content = TileContent.Create(tile, new Size(Width, Height), launcher);
        chrome.Child = content;
        ApplyBackground();
    }

    public void Configure(Tile t, Metrics m, Guid group, int col)
    {
        // Anything but the color changes what's inside the tile.
        var rebuild = tile is null || tile with { Color = null } != t with { Color = null } ||
                      metrics.Tile != m.Tile || metrics.Shape != m.Shape;
        if (placed is { } p && p.Group != group) placed = null;     // moved to another group: jump, don't slide
        tile = t;
        metrics = m;
        groupId = group;
        column = col;

        var (w, h) = m.Frame(t.Size);
        Width = w;
        Height = h;
        var shape = Ui.Squircle(w, h, m.CornerRadius);
        chrome.Clip = shape;
        outline.Data = shape;
        glass.Configure(w, h, shape, m.CornerRadius);
        ApplyBackground();
        ApplyShadow(hover: IsMouseOver);
        UpdateRunningDot();
        Ui.Set(this, OpacityProperty, launcher.Store.DraggingTile == t.Id ? 0.55 : 1);
        ToolTip = TooltipText(t);

        if (rebuild)
        {
            content?.Detach();
            content = TileContent.Create(t, new Size(w, h), launcher);
            chrome.Child = content;
        }
    }

    /// <summary>Slides to the new spot within the same group, so neighbours glide aside while you drag.</summary>
    public void MoveTo(double left, double top)
    {
        if (placed is { } p && p.Group == groupId && (p.Left != left || p.Top != top))
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(p.Left, left, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            BeginAnimation(Canvas.TopProperty, new DoubleAnimation(p.Top, top, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
        else
        {
            BeginAnimation(Canvas.LeftProperty, null);
            BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(this, left);
            Canvas.SetTop(this, top);
        }
        placed = (groupId, left, top);
    }

    public void Tick(DateTime now) => content?.Tick(now);

    private string TooltipText(Tile t)
    {
        switch (t.Kind)
        {
            case TileKind.App when t.AppPath is { } path:
                return launcher.Catalog.Name(path);
            case TileKind.Weather:
                return "Weather\n" + WeatherApi.Attribution;
            case TileKind.Web when WebLink.Of(t) is { } url:
                return WebTileContent.Name(t, launcher) + "\n" + url.OriginalString;
            case TileKind.Service:
                if (launcher.Services.IntegrationFor(t) is not { } integration) return "This service was removed";
                var lines = new List<string> { integration.DisplayName + " · " + (t.Widget?.Title() ?? "") };
                if (t.Entity is { } entity) lines.Add(entity);
                if (launcher.Services.StatusFor(t)?.Error is { } error) lines.Add(error);
                return string.Join("\n", lines);
            default:
                return t.Kind.Title();
        }
    }

    public void Detach()
    {
        launcher.Store.SettingsChanged -= OnSettingsChanged;
        launcher.Catalog.Changed -= OnCatalogChanged;
        launcher.Running.Changed -= UpdateRunningDot;
        launcher.WebIcons.Changed -= OnWebIconChanged;
        content?.Detach();
        content = null;
        (Parent as Panel)?.Children.Remove(this);
    }

    public void PlayEntrance()
    {
        var delay = Math.Min(450, column * 20);
        Ui.Set(this, OpacityProperty, 0);
        Ui.AnimateFrom(entrance, TranslateTransform.XProperty, 70, 0, 500, delay, new BackEase { Amplitude = 0.15, EasingMode = EasingMode.EaseOut });
        Ui.Animate(this, OpacityProperty, launcher.Store.DraggingTile == tile?.Id ? 0.55 : 1, 350, delay);
    }

    private void ApplyBackground()
    {
        if (tile is null) return;
        string hex;
        if (tile.Color is { } c) hex = c;
        else if (tile.Kind == TileKind.App && tile.AppPath is { } path)
        {
            if (launcher.Store.Settings.IconColoredTiles && !launcher.Catalog.HasTint(path))
                launcher.Catalog.Icon(path, _ => { if (launcher.Catalog.HasTint(path)) ApplyBackground(); });   // tint is known once the icon loads
            hex = launcher.Store.Settings.IconColoredTiles ? launcher.Catalog.Tint(path) : Palette.AutoColor(path);
        }
        else if (tile.Kind == TileKind.Web && WebLink.Of(tile) is { } url)
            hex = launcher.Store.Settings.IconColoredTiles && launcher.WebIcons.Tint(url) is { } tint ? tint : Palette.AutoColor(WebLink.DisplayHost(url));
        else if (tile.Kind == TileKind.Service && launcher.Services.IntegrationFor(tile) is { } integration)
            hex = integration.Kind.DefaultColor();
        else hex = tile.Kind.DefaultColor();

        var s = launcher.Store.Settings;
        if (s.TileFinish == TileFinish.Glass)
        {
            chrome.Background = GlassOverlay.TintBrush(hex, s.GlassTint);
            glass.Visibility = Visibility.Visible;
        }
        else
        {
            chrome.Background = Ui.Brush(hex);
            glass.Visibility = Visibility.Collapsed;
        }
    }

    // MARK: click, tilt and drag

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        pressAt = e.GetPosition(this);
        dragged = false;
        CaptureMouse();
        // The Windows 8 "tilt": press toward the side you clicked.
        var x = (pressAt.Value.X / Math.Max(ActualWidth, 1)) - 0.5;
        Ui.Animate(press, ScaleTransform.ScaleXProperty, 0.94, 80);
        Ui.Animate(press, ScaleTransform.ScaleYProperty, 0.94, 80);
        Ui.Animate(tilt, SkewTransform.AngleYProperty, x * 4, 80);
        e.Handled = true;
    }

    private void Release()
    {
        Ui.Animate(press, ScaleTransform.ScaleXProperty, 1, 140);
        Ui.Animate(press, ScaleTransform.ScaleYProperty, 1, 140);
        Ui.Animate(tilt, SkewTransform.AngleYProperty, 0, 140);
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Release();
        var wasPressed = pressAt is not null;
        pressAt = null;
        if (wasPressed && !dragged && IsMouseOver) Activate();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed || tile is null) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragged = true;
        pressAt = null;
        ReleaseMouseCapture();
        Release();
        launcher.Store.DraggingGroup = null;
        launcher.Store.DraggingTile = tile.Id;
        Ui.Set(this, OpacityProperty, 0.55);
        DragDrop.DoDragDrop(this, new DataObject(LayoutStore.TileFormat, tile.Id.ToString()), DragDropEffects.Move);
        if (launcher.Store.DraggingTile is not null)
        {
            launcher.Store.DraggingTile = null;      // cancelled (dropped outside)
            launcher.Store.SetGroups(launcher.Store.Groups);
        }
        Ui.Set(this, OpacityProperty, 1);
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (tile is null) return;
        if (launcher.Store.DraggingGroup is { } g && launcher.Store.GroupOf(new TileDestination.Before(tile.Id)) is { } target)
            launcher.Store.LiveMoveGroup(g, target);              // dragging a whole group over this one
        else if (launcher.Store.DraggingTile is { } dragging && dragging != tile.Id)
            launcher.Store.LiveMove(dragging, tile.Id);
        else outline.Opacity = 1;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        outline.Opacity = 0;
        if (tile is not null) launcher.Store.AcceptDrop(e.Data, new TileDestination.Before(tile.Id));
        e.Handled = true;
    }

    private void Activate()
    {
        if (tile is null) return;
        switch (tile.Kind)
        {
            case TileKind.App when tile.AppPath is { } path:
                launcher.Launch(path);
                break;
            case TileKind.Clock:
                launcher.OpenUri("ms-clock:");
                break;
            case TileKind.Calendar:
                launcher.OpenUri("outlookcal:");
                break;
            case TileKind.Weather:
                launcher.OpenUri("msnweather:");
                break;
            case TileKind.System:
                launcher.OpenUri("taskmgr.exe");
                break;
            case TileKind.Battery:
                launcher.OpenUri("ms-settings:batterysaver");
                break;
            case TileKind.Photos:
                launcher.OpenUri("ms-photos:");
                break;
            case TileKind.News:
                if (launcher.Hub.News(tile).Current?.Link is { } link) launcher.OpenUri(link.AbsoluteUri);
                else launcher.EditNewsTile(tile);        // nothing loaded yet: maybe the feed is wrong
                break;
            case TileKind.Desktop:
                launcher.ShowDesktop();
                break;
            case TileKind.Web when WebLink.Of(tile) is { } url:
                launcher.OpenUri(url.AbsoluteUri);
                break;
            case TileKind.Service:
                launcher.ActivateService(tile);
                break;
        }
    }

    // MARK: right-click menu

    private void BuildMenu()
    {
        if (tile is null) return;
        var t = tile;
        var store = launcher.Store;
        ContextMenu.Items.Clear();

        ContextMenu.Items.Add(Ui.Submenu("Resize", t.Sizes.Select(size =>
            (object)Ui.Item(size.Title(), () => store.Update(t.Id, x => x with { Size = size }), isChecked: size == t.Size))));

        var colors = new List<object> { Ui.Item("Automatic", () => store.Update(t.Id, x => x with { Color = null }), isChecked: t.Color is null), new Separator() };
        foreach (var c in Palette.Named)
        {
            var swatch = new Rectangle { Width = 12, Height = 12, Fill = Ui.Brush(c.Hex) };
            var item = Ui.Item(c.Name, () => store.Update(t.Id, x => x with { Color = c.Hex }), isChecked: t.Color == c.Hex);
            item.Icon = swatch;
            colors.Add(item);
        }
        ContextMenu.Items.Add(Ui.Submenu("Color", colors));

        var moves = store.Groups.Select(g => (object)Ui.Item(g.DisplayName, () => store.Move(t.Id, new TileDestination.EndOf(g.Id)), enabled: g.Id != groupId)).ToList();
        moves.Add(new Separator());
        moves.Add(Ui.Item("New Group", () => store.Move(t.Id, new TileDestination.NewGroup())));
        ContextMenu.Items.Add(Ui.Submenu("Move to Group", moves));

        if (t.Kind == TileKind.App && t.AppPath is { } path && AppLauncher.IsFilePath(path))
        {
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(Ui.Item("Open file location", () => launcher.Reveal(path)));
        }
        if (t.Kind == TileKind.App && t.AppPath is { } running && launcher.Running.IsRunning(running))
        {
            ContextMenu.Items.Add(new Separator());
            foreach (var item in QuitMenu.Items(launcher, running, launcher.Catalog.Name(running))) ContextMenu.Items.Add(item);
        }
        if (t.Kind == TileKind.Weather)
        {
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(Ui.Item("Change Location…", () => launcher.OpenSettings()));
            ContextMenu.Items.Add(Ui.Item(WeatherApi.Attribution, () => launcher.OpenUri(WeatherApi.AttributionUrl)));
        }
        if (t.Kind == TileKind.News)
        {
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(Ui.Item("Change Feed…", () => launcher.EditNewsTile(t)));
        }
        if (t.Kind == TileKind.Web && WebLink.Of(t) is { } url)
        {
            ContextMenu.Items.Add(new Separator());
            ContextMenu.Items.Add(Ui.Item("Edit Link…", () => launcher.EditWebTile(t)));
            ContextMenu.Items.Add(Ui.Item("Copy Link", () => { try { Clipboard.SetText(url.OriginalString); } catch { } }));
            ContextMenu.Items.Add(Ui.Item("Refresh Icon", () => launcher.WebIcons.Load(url, force: true)));
        }
        if (t.Kind == TileKind.Service)
        {
            ContextMenu.Items.Add(new Separator());
            foreach (var item in ServiceMenu.Items(launcher, t)) ContextMenu.Items.Add(item);
        }
        ContextMenu.Items.Add(new Separator());
        ContextMenu.Items.Add(Ui.Item("Unpin from Start", () => store.Remove(t.Id)));
    }
}
