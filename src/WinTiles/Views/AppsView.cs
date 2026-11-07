using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinTiles.Core;

namespace WinTiles;

/// <summary>The Windows 8.1 "Apps" view: every installed app, alphabetically, grouped by letter.</summary>
public sealed class AppsView : ScrollViewer
{
    private readonly Launcher launcher;
    private readonly WrapPanel panel = new() { Orientation = Orientation.Vertical };
    private readonly TranslateTransform slide = new();
    private bool dirty = true;
    private double lastHeight;

    public AppsView(Launcher launcher)
    {
        this.launcher = launcher;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        Content = panel;
        panel.RenderTransform = slide;
        panel.Background = Brushes.Transparent;
        PreviewMouseWheel += (_, e) => { ScrollToHorizontalOffset(HorizontalOffset - e.Delta); e.Handled = true; };
        launcher.Catalog.Changed += () => { dirty = true; if (IsVisible) Rebuild(); };
        SizeChanged += (_, _) => Relayout();
    }

    public void Relayout()
    {
        if (Math.Abs(ActualHeight - lastHeight) > 1) { lastHeight = ActualHeight; panel.Height = Math.Max(ActualHeight - 16, 200); }
    }

    public void Rebuild()
    {
        var metrics = new Metrics(Math.Max(ActualWidth, 800), Math.Max(ActualHeight + Metrics.HeaderHeight + Metrics.FooterHeight, 600), 1);
        panel.Margin = new Thickness(metrics.LeftMargin, 0, 80, 0);
        Relayout();
        if (!dirty) return;
        dirty = false;
        panel.Children.Clear();
        if (launcher.Catalog.Apps.Count == 0)
        {
            panel.Children.Add(Ui.Text("Finding apps…", 18, Ui.Light, 0.7));
            dirty = true;
            return;
        }
        var currentLetter = "";
        foreach (var app in launcher.Catalog.Apps)
        {
            var letter = AppSearch.Letter(app.Name);
            if (letter != currentLetter)
            {
                currentLetter = letter;
                var header = Ui.Text(letter, 26, Ui.Light, 0.9);
                header.Width = 240;
                header.Height = 46;
                header.Padding = new Thickness(6, 6, 0, 0);
                header.Margin = new Thickness(0, 0, 28, 6);
                panel.Children.Add(header);
            }
            panel.Children.Add(new AppRow(launcher, app));
        }
    }

    public void PlaySlideIn()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        Ui.AnimateFrom(slide, TranslateTransform.YProperty, 60, 0, 320);
        panel.Opacity = 0;
        Ui.Animate(panel, OpacityProperty, 1, 260);
    }
}

/// <summary>"Start typing" results: what you've typed, big, with a caret — there is no search box.</summary>
public sealed class SearchView : Grid
{
    private readonly Launcher launcher;
    private readonly TextBlock query = Ui.Text("", 44, Ui.Light);
    private readonly Rectangle caret = new() { Width = 2, Height = 42, Fill = Brushes.White, Margin = new Thickness(3, 6, 0, 0) };
    private readonly TextBlock subtitle = Ui.Text("", 14, opacity: 0.65);
    private readonly WrapPanel results = new() { Orientation = Orientation.Vertical };
    private readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private string shownQuery = "\0";

    public SearchView(Launcher launcher)
    {
        this.launcher = launcher;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(query);
        line.Children.Add(caret);
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        top.Children.Add(line);
        top.Children.Add(subtitle);
        Children.Add(top);

        scroller.Content = results;
        scroller.PreviewMouseWheel += (_, e) => { scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta); e.Handled = true; };
        SetRow(scroller, 1);
        Children.Add(scroller);
        SizeChanged += (_, _) => results.Height = Math.Max(scroller.ActualHeight - 8, 150);

        // Blinking caret.
        var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1100) };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550))));
        caret.BeginAnimation(OpacityProperty, blink);
    }

    public void Rebuild()
    {
        var metrics = new Metrics(Math.Max(ActualWidth, 800), 900, 1);
        Margin = new Thickness(metrics.LeftMargin, 0, 0, 0);
        if (launcher.Query == shownQuery) return;
        shownQuery = launcher.Query;
        query.Text = launcher.Query;

        var found = launcher.Results;
        subtitle.Text = found.Count switch
        {
            0 => "No apps match",
            1 => "1 app · Enter to open",
            _ => $"{found.Count} apps · Enter opens the first",
        };
        results.Children.Clear();
        for (var i = 0; i < Math.Min(found.Count, 60); i++)
            results.Children.Add(new AppRow(launcher, found[i], highlighted: i == 0));
        scroller.ScrollToHorizontalOffset(0);
    }

    public void Tick(DateTime now) { }
}

/// <summary>An app in All apps or the search results: icon and name; right-click to pin.</summary>
public sealed class AppRow : Border
{
    public AppRow(Launcher launcher, AppInfo app, bool highlighted = false)
    {
        Width = 240;
        Height = 46;
        Margin = new Thickness(0, 0, 28, 6);
        Padding = new Thickness(6, 0, 6, 0);
        Cursor = Cursors.Hand;
        var idle = highlighted ? Ui.White(0.14) : Brushes.Transparent;
        Background = idle;
        ToolTip = app.Name;

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Image { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        g.Children.Add(icon);
        var name = Ui.Text(app.Name, 14);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        // Running apps get the same small dot as their tiles.
        if (launcher.Store.Settings.ShowRunningIndicator && launcher.Running.IsRunning(app.Id))
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5, Height = 5, Fill = Brushes.White, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(15, 0, 0, 3),
            };
            g.Children.Add(dot);
        }
        Child = g;
        launcher.Catalog.Icon(app.Id, src => icon.Source = src);

        MouseEnter += (_, _) => Background = Ui.White(0.14);
        MouseLeave += (_, _) => Background = idle;
        MouseLeftButtonUp += (_, e) => { launcher.Launch(app.Id); e.Handled = true; };

        // Drag an app from All apps onto Start to pin it there.
        Point? pressAt = null;
        MouseLeftButtonDown += (_, e) => pressAt = e.GetPosition(this);
        MouseMove += (_, e) =>
        {
            if (pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - start;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressAt = null;
            if (AppLauncher.IsFilePath(app.Id))
                DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, new[] { app.Id }), DragDropEffects.Copy | DragDropEffects.Link);
        };

        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) =>
        {
            ContextMenu.Items.Clear();
            if (launcher.Store.IsPinned(app.Id))
                ContextMenu.Items.Add(Ui.Item("Unpin from Start", () => launcher.Store.Unpin(app.Id)));
            else
                ContextMenu.Items.Add(Ui.Item("Pin to Start", () => launcher.Store.Pin(app.Id)));
            if (AppLauncher.IsFilePath(app.Id))
                ContextMenu.Items.Add(Ui.Item("Open file location", () => launcher.Reveal(app.Id)));
            if (launcher.Running.IsRunning(app.Id))
            {
                ContextMenu.Items.Add(new Separator());
                foreach (var item in QuitMenu.Items(launcher, app.Id, app.Name)) ContextMenu.Items.Add(item);
            }
        };
    }
}
