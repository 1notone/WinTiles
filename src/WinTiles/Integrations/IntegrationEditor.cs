using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WinTiles.Core;

namespace WinTiles;

/// <summary>Add or edit one service connection (Settings → Integrations → Add Service… / Edit…).</summary>
public sealed class IntegrationEditor : Window
{
    private readonly Integration? original;
    private readonly ComboBox service = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox name = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox address = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox username = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly PasswordBox secret = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox selfSigned = new() { Content = "Allow self-signed certificate" };
    private readonly TextBlock addressLabel = new(), usernameLabel = new(), secretLabel = new();
    private readonly FrameworkElement usernameRow, secretRow;
    private readonly TextBlock placeholder = Note(""), help = Note("");
    private readonly TextBlock insecure = new()
    {
        Foreground = System.Windows.Media.Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, FontSize = 12,
        Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button test = new() { Content = "Test Connection", Padding = new Thickness(12, 4, 12, 4) };
    private readonly TextBlock error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Guid newId = Guid.NewGuid();
    private ServiceKind kind;

    /// <summary>The saved connection, after OK.</summary>
    public Integration? Result { get; private set; }
    /// <summary>The password, token or API key to keep in Credential Manager.</summary>
    public string Secret { get; private set; } = "";

    public IntegrationEditor(Integration? original)
    {
        this.original = original;
        Title = original is null ? "Connect a Service" : $"Edit {original.DisplayName}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = Ui.TextFont;
        ShowInTaskbar = false;
        try { ThemeMode = ThemeMode.System; } catch { }
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/WinTiles.ico")); } catch { }

        foreach (var category in Enum.GetValues<ServiceCategory>())
        {
            service.Items.Add(new ComboBoxItem { Content = category.Title(), IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var k in ServiceKindInfo.InCategory(category))
                service.Items.Add(new ComboBoxItem { Content = "    " + k.Title(), Tag = k });
        }

        var page = new StackPanel { Margin = new Thickness(20) };
        page.Children.Add(Row("Service", original is null ? service : new TextBlock { Text = original.Kind.Title(), VerticalAlignment = VerticalAlignment.Center }));
        page.Children.Add(Row("Name", name));
        var addressCol = new StackPanel();
        addressCol.Children.Add(address);
        addressCol.Children.Add(placeholder);
        addressCol.Children.Add(insecure);
        page.Children.Add(Row(addressLabel, addressCol));
        usernameRow = Row(usernameLabel, username);
        secretRow = Row(secretLabel, secret);
        page.Children.Add(usernameRow);
        page.Children.Add(secretRow);
        page.Children.Add(Row("", selfSigned));
        page.Children.Add(Row("", help));
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        testRow.Children.Add(test);
        testRow.Children.Add(result);
        page.Children.Add(Row("", testRow));
        page.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = original is null ? "Add" : "Save", IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        page.Children.Add(buttons);
        Content = page;

        test.Click += async (_, _) => await Test();
        address.TextChanged += (_, _) => UpdateFields();

        if (original is not null)
        {
            kind = original.Kind;
            name.Text = original.Name;
            address.Text = original.Url;
            username.Text = original.Username;
            selfSigned.IsChecked = original.AllowSelfSigned;
            secret.Password = IntegrationSecrets.Get(original.Id);
            UpdateFields();
        }
        else
        {
            service.SelectionChanged += (_, _) =>
            {
                if (service.SelectedItem is not ComboBoxItem { Tag: ServiceKind k }) return;
                kind = k;
                selfSigned.IsChecked = k.UsuallySelfSigned();
                result.Text = "";
                error.Text = "";
                UpdateFields();
            };
            service.SelectedItem = service.Items.OfType<ComboBoxItem>().First(i => i.Tag is ServiceKind);
        }
        Loaded += (_, _) => (original is null ? (Control)service : address).Focus();
    }

    private void UpdateFields()
    {
        addressLabel.Text = kind.AddressOptional() ? "Address (optional)" : "Address";
        placeholder.Text = kind.AddressOptional() ? kind.Placeholder() : "e.g. " + kind.Placeholder();
        usernameRow.Visibility = kind.UsernameLabel() is null ? Visibility.Collapsed : Visibility.Visible;
        usernameLabel.Text = kind.UsernameLabel() ?? "";
        username.ToolTip = kind.UsernamePrompt();
        secretRow.Visibility = kind.SecretLabel() is null ? Visibility.Collapsed : Visibility.Visible;
        secretLabel.Text = kind.SecretLabel() is { } label ? (kind.SecretRequired() ? label : label + " (optional)") : "";
        selfSigned.Visibility = kind == ServiceKind.Docker && address.Text.Trim().Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        help.Text = kind.Help() + (kind.UsernameLabel() is not null ? $"\n{kind.UsernameLabel()}: {kind.UsernamePrompt()}" : "");
        var keyName = (kind.SecretLabel() ?? "key").ToLowerInvariant();
        insecure.Text = $"⚠ This address uses plain HTTP and isn't on your local network, so the {keyName} would be sent unencrypted. Use https:// if the service supports it.";
        insecure.Visibility = kind.SecretLabel() is not null && Draft().SendsSecretInsecurely ? Visibility.Visible : Visibility.Collapsed;
    }

    private Integration Draft()
    {
        var i = original?.Copy() ?? new Integration { Id = newId };
        i.Kind = kind;
        i.Name = name.Text.Trim();
        i.Url = address.Text.Trim();
        i.Username = kind.UsernameLabel() is null ? "" : username.Text.Trim();
        i.AllowSelfSigned = selfSigned.IsChecked == true;
        return i;
    }

    private string? Validate()
    {
        var a = address.Text.Trim();
        if (a.Length == 0)
        {
            if (!kind.AddressOptional()) return $"Enter the service's address, like {kind.Placeholder()}.";
        }
        else if (Integration.Normalize(a) is null) return $"That doesn't look like an address. Try something like {kind.Placeholder()}.";
        if (kind.UsernameRequired() && username.Text.Trim().Length == 0) return $"Enter the {kind.UsernameLabel()?.ToLowerInvariant()}.";
        if (kind.SecretRequired() && secret.Password.Length == 0) return $"Enter the {kind.SecretLabel()}.";
        return null;
    }

    private async Task Test()
    {
        if (Validate() is { } problem)
        {
            result.Text = "✗ " + problem;
            return;
        }
        test.IsEnabled = false;
        result.Text = "Testing…";
        var (ok, message) = await ServiceHub.Test(Draft(), kind.SecretLabel() is null ? "" : secret.Password);
        result.Text = (ok ? "✓ " : "✗ ") + message;
        result.Foreground = ok ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.IndianRed;
        test.IsEnabled = true;
    }

    private void Save()
    {
        if (Validate() is { } problem)
        {
            error.Text = problem;
            return;
        }
        var draft = Draft();
        var key = kind.SecretLabel() is null ? "" : secret.Password;
        if (!IntegrationSecrets.Set(draft.Id, key, draft.DisplayName))
        {
            error.Text = $"Windows Credential Manager didn't accept the {(kind.SecretLabel() ?? "key").ToLowerInvariant()}. Try again, or restart WinTiles.";
            return;
        }
        Result = draft;
        Secret = key;
        DialogResult = true;
    }

    private static TextBlock Note(string text) => new() { Text = text, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 4, 0, 0) };

    private static FrameworkElement Row(string label, UIElement control) => Row(new TextBlock { Text = label }, control);

    private static FrameworkElement Row(TextBlock label, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        label.VerticalAlignment = VerticalAlignment.Top;
        label.Margin = new Thickness(0, 4, 0, 0);
        g.Children.Add(label);
        Grid.SetColumn((FrameworkElement)control, 1);
        g.Children.Add(control);
        return g;
    }
}
