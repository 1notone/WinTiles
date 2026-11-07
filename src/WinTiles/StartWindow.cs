using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinTiles.Core;

namespace WinTiles;

/// <summary>The borderless Start window. Keeps itself at the bottom of the window stack while on the desktop.</summary>
public sealed class StartWindow : Window
{
    private readonly Launcher launcher;
    private readonly RootView root;
    private readonly Grid panelLayer = new();
    private HwndSource? source;
    private bool allowZOrderChange;

    public IntPtr Handle => source?.Handle ?? IntPtr.Zero;
    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public StartWindow(Launcher launcher)
    {
        this.launcher = launcher;
        Title = "WinTiles";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        UseLayoutRounding = true;
        Focusable = true;
        Background = Ui.Brush(launcher.Store.Settings.Background);
        FontFamily = Ui.Font;

        root = new RootView(launcher);
        // Wallpaper behind, the Start screen, and a layer for panels (Add Web Tile, Add Service Tile).
        var layers = new Grid();
        layers.Children.Add(new BackgroundHost(launcher));
        layers.Children.Add(root);
        layers.Children.Add(panelLayer);
        Content = layers;

        launcher.Store.SettingsChanged += _ => Background = Ui.Brush(launcher.Store.Settings.Background);
        TextInput += OnTextInput;
        PreviewKeyDown += OnKeyDown;
    }

    public HwndSource EnsureSource()
    {
        if (source is not null) return source;
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        source = HwndSource.FromHwnd(helper.Handle)!;
        source.AddHook(WndProc);
        // A tool window doesn't appear in Alt+Tab or the taskbar, like the Windows 8 Start screen.
        var ex = Native.GetWindowLongPtr(helper.Handle, Native.GWL_EXSTYLE).ToInt64();
        ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(helper.Handle, Native.GWL_EXSTYLE, new IntPtr(ex));
        return source;
    }

    /// <summary>Moves and sizes the window in physical pixels and sets its place in the window stack.</summary>
    public void PlaceAt(System.Drawing.Rectangle bounds, IntPtr insertAfter)
    {
        EnsureSource();
        allowZOrderChange = true;
        try
        {
            Native.SetWindowPos(Handle, insertAfter, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
        }
        finally { allowZOrderChange = false; }
    }

    public void ShowWithoutActivating()
    {
        ShowActivated = false;
        Show();
    }

    public void FocusContent()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Focus();
            Keyboard.Focus(this);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Tick(DateTime now) => root.Tick(now);

    /// <summary>Shows a panel over Start (or removes it with null).</summary>
    public void ShowPanel(FrameworkElement? panel)
    {
        panelLayer.Children.Clear();
        if (panel is not null) panelLayer.Children.Add(panel);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // While Start sits on the desktop, clicking it must not raise it above your other windows.
        if (msg == Native.WM_WINDOWPOSCHANGING && !launcher.IsFront && !allowZOrderChange)
        {
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((pos.flags & Native.SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    // MARK: keyboard — just start typing

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox || launcher.PanelOpen) return;          // renaming a group, or typing in a panel
        var action = TypeToSearch.ForText(e.Text, launcher.Query);
        if (action.Kind == TypeToSearch.Kind.Ignore) return;
        launcher.SetQuery(TypeToSearch.Apply(action, launcher.Query));
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // A panel is open: Escape closes it; other keys belong to the panel.
        if (launcher.PanelOpen)
        {
            if (e.Key == Key.Escape) { launcher.ClosePanel(); e.Handled = true; }
            return;
        }
        if (e.OriginalSource is TextBox) return;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.Escape)
        {
            if (launcher.Query.Length > 0) launcher.SetQuery("");
            else if (launcher.ShowingApps) launcher.SetShowingApps(false);
            else if (launcher.IsFront) launcher.Dismiss(restoreFocus: true);
            else return;
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.OemComma)
        {
            launcher.OpenSettings();
            e.Handled = true;
            return;
        }

        var key = e.Key switch
        {
            Key.Back => TypeToSearch.Key.Backspace,
            Key.Enter => TypeToSearch.Key.Enter,
            _ => TypeToSearch.Key.Other,
        };
        if (key == TypeToSearch.Key.Other) return;
        var action = TypeToSearch.ForKey(key, ctrl, mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt), launcher.Query);
        switch (action.Kind)
        {
            case TypeToSearch.Kind.Ignore:
                return;
            case TypeToSearch.Kind.Submit:
                launcher.LaunchFirstResult();
                break;
            default:
                launcher.SetQuery(TypeToSearch.Apply(action, launcher.Query));
                break;
        }
        e.Handled = true;
    }
}
