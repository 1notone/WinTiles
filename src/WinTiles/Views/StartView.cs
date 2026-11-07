using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

/// <summary>The Start screen: groups of tiles scrolling sideways.</summary>
public sealed class StartView : ScrollViewer
{
    private readonly Launcher launcher;
    private readonly StackPanel panel = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<Guid, TileElement> tiles = new();
    private readonly TranslateTransform slide = new();
    private Metrics metrics;
    private (double W, double H, double Scale, TileShape Shape) lastGeometry;

    public StartView(Launcher launcher)
    {
        this.launcher = launcher;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        Content = panel;
        panel.RenderTransform = slide;
        panel.Background = Brushes.Transparent;

        // A plain mouse wheel scrolls Start sideways, like Windows 8.
        PreviewMouseWheel += (_, e) =>
        {
            ScrollToHorizontalOffset(HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        SizeChanged += (_, _) => Relayout();
    }

    /// <summary>Rebuilds only if the window size, tile size or shape changed.</summary>
    public void Relayout()
    {
        var g = (ActualWidth, ActualHeight, launcher.Store.Settings.TileScale, launcher.Store.Settings.TileShape);
        if (g == lastGeometry) return;
        lastGeometry = g;
        Rebuild();
    }

    public void Rebuild()
    {
        var height = ActualHeight > 0 ? ActualHeight + Metrics.HeaderHeight + Metrics.FooterHeight : 900;
        var width = ActualWidth > 0 ? ActualWidth : 1400;
        metrics = new Metrics(width, height, launcher.Store.Settings.TileScale, launcher.Store.Settings.TileShape);
        lastGeometry = (ActualWidth, ActualHeight, launcher.Store.Settings.TileScale, launcher.Store.Settings.TileShape);

        // Detach cached tiles so they can move between groups.
        foreach (var el in tiles.Values) (el.Parent as Panel)?.Children.Remove(el);
        panel.Children.Clear();
        panel.Margin = new Thickness(metrics.LeftMargin, 8, 80, 0);

        var live = new HashSet<Guid>();
        var column = 0;
        var groups = launcher.Store.Groups;
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            var pack = Packer.Pack(group.Tiles, metrics.Rows);
            var view = new GroupView(launcher, group, pack, metrics, gi == 0, gi == groups.Count - 1);
            view.Margin = new Thickness(0, 0, metrics.GroupSpacing, 0);
            foreach (var placed in pack.Items)
            {
                live.Add(placed.Tile.Id);
                if (!tiles.TryGetValue(placed.Tile.Id, out var el))
                {
                    el = new TileElement(launcher);
                    tiles[placed.Tile.Id] = el;
                }
                el.Configure(placed.Tile, metrics, group.Id, column + placed.X);
                view.Place(el, placed.X * metrics.Step, placed.Y * metrics.Step);
            }
            column += pack.Width + 1;
            panel.Children.Add(view);
        }
        panel.Children.Add(new NewGroupZone(launcher, metrics));

        // Forget tiles that were unpinned.
        foreach (var gone in tiles.Keys.Where(id => !live.Contains(id)).ToList())
        {
            tiles[gone].Detach();
            tiles.Remove(gone);
        }
    }

    public void Tick(DateTime now)
    {
        foreach (var el in tiles.Values) el.Tick(now);
    }

    /// <summary>Tiles fly in from the right in a column-staggered cascade each time Start opens.</summary>
    public void PlayEntrance()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        foreach (var el in tiles.Values) el.PlayEntrance();
    }

    public void PlaySlideIn(bool fromTop)
    {
        Ui.AnimateFrom(slide, TranslateTransform.YProperty, fromTop ? -60 : 60, 0, 320);
        panel.Opacity = 0;
        Ui.Animate(panel, OpacityProperty, 1, 260);
    }
}

/// <summary>One group: its name (drag it to move the group, double-click to rename) and its tiles.</summary>
public sealed class GroupView : StackPanel
{
    private readonly Launcher launcher;
    private readonly TileGroup group;
    private readonly Canvas canvas = new() { Background = Brushes.Transparent, AllowDrop = true, ClipToBounds = false };
    private readonly Grid headerHost = new() { Height = Metrics.GroupTitleHeight - 10 };
    private Point? pressAt;

    public GroupView(Launcher launcher, TileGroup group, Packer.Result pack, Metrics m, bool isFirst, bool isLast)
    {
        this.launcher = launcher;
        this.group = group;
        Orientation = Orientation.Vertical;
        if (launcher.Store.DraggingGroup == group.Id) Opacity = 0.45;

        headerHost.Margin = new Thickness(0, 0, 0, 10);
        headerHost.Children.Add(Header(isFirst, isLast));
        Children.Add(headerHost);

        canvas.Width = m.Span(pack.Width);
        canvas.Height = m.Span(m.Rows);
        canvas.DragEnter += (_, e) => DragOverGroup(e);
        canvas.DragOver += (_, e) => { e.Effects = launcher.Store.DropEffect(e); e.Handled = true; };
        canvas.DragLeave += (_, _) => canvas.Background = Brushes.Transparent;
        canvas.Drop += (_, e) =>
        {
            canvas.Background = Brushes.Transparent;
            launcher.Store.AcceptDrop(e.Data, new TileDestination.EndOf(group.Id));
            e.Handled = true;
        };
        Children.Add(canvas);
    }

    public void Place(TileElement el, double left, double top)
    {
        canvas.Children.Add(el);
        el.MoveTo(left, top);
    }

    private void DragOverGroup(DragEventArgs e)
    {
        if (launcher.Store.DraggingGroup is { } dragged && dragged != group.Id)
            launcher.Store.LiveMoveGroup(dragged, group.Id);
        else if (launcher.Store.DraggingGroup is null)
            canvas.Background = Ui.White(0.05);
        e.Handled = true;
    }

    private FrameworkElement Header(bool isFirst, bool isLast)
    {
        var name = group.Name;
        var text = Ui.Text(string.IsNullOrEmpty(name) ? "Name group" : name, 18, Ui.Light, string.IsNullOrEmpty(name) ? 0.12 : 0.9);
        text.Background = Brushes.Transparent;
        text.Cursor = Cursors.SizeWE;
        text.ToolTip = "Drag to move this group · double-click to rename";
        text.HorizontalAlignment = HorizontalAlignment.Left;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.MinWidth = 120;
        text.MouseEnter += (_, _) => { if (string.IsNullOrEmpty(name)) text.Opacity = 0.45; };
        text.MouseLeave += (_, _) => { if (string.IsNullOrEmpty(name)) text.Opacity = 0.12; };

        text.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { BeginRename(); e.Handled = true; return; }
            pressAt = e.GetPosition(this);
        };
        text.MouseLeftButtonUp += (_, _) => pressAt = null;
        text.MouseMove += (_, e) =>
        {
            if (pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressAt = null;
            // Drag a group by its name to move it left or right.
            launcher.Store.DraggingTile = null;
            launcher.Store.DraggingGroup = group.Id;
            Opacity = 0.45;
            DragDrop.DoDragDrop(text, new DataObject(LayoutStore.GroupFormat, group.Id.ToString()), DragDropEffects.Move);
            if (launcher.Store.DraggingGroup is not null)
            {
                launcher.Store.DraggingGroup = null;      // drag cancelled
                launcher.Store.SetGroups(launcher.Store.Groups);
            }
        };

        text.ContextMenu = new ContextMenu();
        text.ContextMenu.Items.Add(Ui.Item("Rename Group", BeginRename));
        text.ContextMenu.Items.Add(new Separator());
        text.ContextMenu.Items.Add(Ui.Item("Move Group Left", () => launcher.Store.ShiftGroup(group.Id, -1), enabled: !isFirst));
        text.ContextMenu.Items.Add(Ui.Item("Move Group Right", () => launcher.Store.ShiftGroup(group.Id, 1), enabled: !isLast));
        text.ContextMenu.Items.Add(new Separator());
        text.ContextMenu.Items.Add(Ui.Item("Remove Group", () => launcher.Store.RemoveGroup(group.Id)));
        return text;
    }

    private void BeginRename()
    {
        var box = new TextBox
        {
            Text = group.Name,
            FontSize = 18,
            FontWeight = Ui.Light,
            Width = 240,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            Background = Ui.White(0.12),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var done = false;
        void Commit(bool save)
        {
            if (done) return;
            done = true;
            if (save) launcher.Store.Rename(group.Id, box.Text);
            else launcher.Store.SetGroups(launcher.Store.Groups);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(true); e.Handled = true; }
            if (e.Key == Key.Escape) { Commit(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Commit(true);
        headerHost.Children.Clear();
        headerHost.Children.Add(box);
        box.Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }
}

/// <summary>Drop a tile (or an app from File Explorer) past the last group to start a new group.</summary>
public sealed class NewGroupZone : StackPanel
{
    public NewGroupZone(Launcher launcher, Metrics m)
    {
        Children.Add(new Border { Height = Metrics.GroupTitleHeight });
        var zone = new Grid { Width = m.Span(4), Height = m.Span(m.Rows), Background = Brushes.Transparent, AllowDrop = true };
        var outline = new Rectangle { Stroke = Ui.White(0.6), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0 };
        zone.Children.Add(outline);
        zone.DragEnter += (_, e) =>
        {
            if (launcher.Store.DraggingGroup is { } g && launcher.Store.Groups.Count > 0)
                launcher.Store.LiveMoveGroup(g, launcher.Store.Groups[^1].Id);
            else outline.Opacity = 1;
        };
        zone.DragOver += (_, e) => { e.Effects = launcher.Store.DropEffect(e); e.Handled = true; };
        zone.DragLeave += (_, _) => outline.Opacity = 0;
        zone.Drop += (_, e) =>
        {
            outline.Opacity = 0;
            launcher.Store.AcceptDrop(e.Data, new TileDestination.NewGroup());
            e.Handled = true;
        };
        Children.Add(zone);
    }
}
