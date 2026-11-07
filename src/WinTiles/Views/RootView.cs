using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WinTiles.Core;

namespace WinTiles;

/// <summary>Header (name and menu), the current view (tiles, All apps or search), and the footer arrow.</summary>
public sealed class RootView : Grid
{
    private readonly Launcher launcher;
    private readonly StartView start;
    private readonly AppsView apps;
    private readonly SearchView search;
    private readonly ContentControl content = new() { Focusable = false };
    private readonly TextBlock title = Ui.Text("Apps", 44, Ui.Light);
    private readonly TextBlock firstName = Ui.Text("", 22, Ui.Light);
    private readonly TextBlock lastName = Ui.Text("", 13, Ui.Light, 0.8);
    private readonly TextBlock initials = Ui.Text("", 16, FontWeights.Medium);
    private readonly Button footerButton = new();
    private readonly StackPanel header = new() { Orientation = Orientation.Horizontal };
    private readonly Grid headerRow = new();
    private UIElement? current;

    public RootView(Launcher launcher)
    {
        this.launcher = launcher;
        start = new StartView(launcher);
        apps = new AppsView(launcher);
        search = new SearchView(launcher);
        Background = Brushes.Transparent;   // hit-testable everywhere, for the right-click menu

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(Metrics.HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(Metrics.FooterHeight) });

        BuildHeader();
        SetRow(headerRow, 0);
        Children.Add(headerRow);

        content.ClipToBounds = true;
        SetRow(content, 1);
        Children.Add(content);

        BuildFooter();

        // Right-click any empty spot for Settings and friends.
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, e) =>
        {
            ContextMenu.Items.Clear();
            foreach (var item in BackgroundMenu(launcher)) ContextMenu.Items.Add(item);
        };

        launcher.StateChanged += Refresh;
        launcher.Store.LayoutChanged += () => { if (current == start) start.Rebuild(); };
        launcher.Store.SettingsChanged += _ => Refresh();
        launcher.Appeared += () => { if (current == start) start.PlayEntrance(); };
        SizeChanged += (_, _) => Refresh();
        Refresh();
    }

    public static IEnumerable<object> BackgroundMenu(Launcher launcher) => new object[]
    {
        Ui.Item("WinTiles Settings…", launcher.OpenSettings),
        new Separator(),
        Ui.Submenu("Add Live Tile", TileKindInfo.LiveKinds.Select(k => (object)Ui.Item(k.Title(), () => launcher.AddLiveTile(k)))),
        Ui.Item("Add Web Tile…", () => launcher.AddWebTile()),
        Ui.Item("Add Service Tile…", () => launcher.AddServiceTile()),
        Ui.Item("Change Wallpaper…", launcher.ChangeWallpaper),
        new Separator(),
        Ui.Item(launcher.ShowingApps ? "Back to Start" : "All Apps", () => launcher.SetShowingApps(!launcher.ShowingApps)),
        Ui.Item("Show Desktop", launcher.ShowDesktop),
    };

    public void Tick(DateTime now)
    {
        if (current == start) start.Tick(now);
        else if (current == search) search.Tick(now);
    }

    private void Refresh()
    {
        Margin = launcher.ChromeInsets;
        var metrics = CurrentMetrics();
        headerRow.Margin = new Thickness(metrics.LeftMargin, 24, 40, 0);
        footerButton.Margin = new Thickness(metrics.LeftMargin, 0, 0, 0);

        UIElement next = launcher.Query.Length > 0 ? search : launcher.ShowingApps ? apps : start;
        title.Visibility = launcher.ShowingApps && launcher.Query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        footerButton.Content = Ui.CircleArrow(up: launcher.ShowingApps, 30);
        footerButton.ToolTip = launcher.ShowingApps ? "Back to Start" : "All apps";

        var parts = launcher.Store.Settings.UserName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        firstName.Text = parts.ElementAtOrDefault(0) ?? "";
        lastName.Text = parts.ElementAtOrDefault(1) ?? "";
        lastName.Visibility = lastName.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        initials.Text = parts.Length == 0 ? "?" : string.Concat(parts.Select(p => char.ToUpperInvariant(p[0])));

        if (next != current)
        {
            var previous = current;
            current = next;
            content.Content = next;
            if (next == start) { start.Rebuild(); if (previous == apps) start.PlaySlideIn(fromTop: true); }
            if (next == apps) { apps.Rebuild(); apps.PlaySlideIn(); }
        }
        if (current == start) start.Relayout();
        if (current == apps) apps.Relayout();
        if (current == search) search.Rebuild();
    }

    private Metrics CurrentMetrics()
    {
        var w = Math.Max(ActualWidth, 800);
        var h = Math.Max(ActualHeight, 600);
        return new Metrics(w, h, launcher.Store.Settings.TileScale, launcher.Store.Settings.TileShape);
    }

    private void BuildHeader()
    {
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.VerticalAlignment = VerticalAlignment.Center;
        headerRow.Children.Add(title);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        firstName.HorizontalAlignment = HorizontalAlignment.Right;
        lastName.HorizontalAlignment = HorizontalAlignment.Right;
        lastName.Margin = new Thickness(0, -4, 0, 0);
        names.Children.Add(firstName);
        names.Children.Add(lastName);
        header.Children.Add(names);

        var badge = new Grid { Width = 40, Height = 40, Background = Ui.White(0.2), VerticalAlignment = VerticalAlignment.Center };
        initials.HorizontalAlignment = HorizontalAlignment.Center;
        initials.VerticalAlignment = VerticalAlignment.Center;
        badge.Children.Add(initials);
        header.Children.Add(badge);

        var more = new Button
        {
            Content = Ui.Text("•••", 14, FontWeights.Bold),
            Margin = new Thickness(18, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "More",
            Focusable = false,
        };
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            foreach (var item in BackgroundMenu(launcher)) menu.Items.Add(item);
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item("Quit WinTiles", launcher.Quit));
            menu.IsOpen = true;
        };
        header.Children.Add(more);

        SetColumn(header, 1);
        headerRow.Children.Add(header);
    }

    private void BuildFooter()
    {
        footerButton.Background = Brushes.Transparent;
        footerButton.BorderThickness = new Thickness(0);
        footerButton.Cursor = Cursors.Hand;
        footerButton.Focusable = false;
        footerButton.HorizontalAlignment = HorizontalAlignment.Left;
        footerButton.VerticalAlignment = VerticalAlignment.Center;
        footerButton.Opacity = 0.85;
        footerButton.Template = PlainButtonTemplate();
        footerButton.Click += (_, _) => launcher.SetShowingApps(!launcher.ShowingApps);
        SetRow(footerButton, 2);
        Children.Add(footerButton);
    }

    /// <summary>A button with no chrome, just its content (the default WPF button looks out of place here).</summary>
    public static ControlTemplate PlainButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(presenter);
        t.VisualTree = border;
        return t;
    }
}
