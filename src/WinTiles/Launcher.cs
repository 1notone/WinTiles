using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinTiles.Core;
using Forms = System.Windows.Forms;

namespace WinTiles;

/// <summary>
/// Coordinates the Start window. Like Windows 8, Start is always there:
///  • Front: Start covers the screen (hot key, Windows key, tray icon, hot corner).
///  • On the desktop: after you launch an app or press Esc, Start drops behind every
///    window and stays there, so it's what you see when your windows are closed or
///    minimized. Its tiles are still clickable.
///  • Hidden: the Desktop tile hides Start to reveal the real desktop, until you open it again.
/// "Keep Start on the desktop" can be turned off in Settings for overlay-only behavior.
/// </summary>
public sealed class Launcher
{
    public LayoutStore Store { get; } = new();
    public AppCatalog Catalog { get; } = new();
    public LiveHub Hub { get; }
    public ServiceHub Services { get; }
    public RunningApps Running { get; } = new();
    public WebIconCache WebIcons { get; } = new();
    public bool PanelOpen => panel is not null;

    public bool IsFront { get; private set; }
    public bool IsOnScreen { get; private set; }
    /// <summary>Live tiles animate and poll only when Start can actually be seen.</summary>
    public bool TilesLive { get; private set; }
    public string Query { get; private set; } = "";
    public bool ShowingApps { get; private set; }
    /// <summary>Space taken by the taskbar (in device-independent pixels) when Start covers it.</summary>
    public Thickness ChromeInsets { get; private set; }

    public event Action? StateChanged;       // front/background, apps view, query, insets
    public event Action? Appeared;           // play the tile cascade
    /// <summary>Asks Settings to open the integration editor (null target = a new service).</summary>
    public event Action<Guid?>? IntegrationRequested;

    private StartWindow? window;
    private SettingsWindow? settingsWindow;
    private FrameworkElement? panel;
    private IntPtr previousForeground;
    private HotKey? hotKey;
    private readonly WindowsKeyHook winKey;
    private readonly DispatcherTimer liveTimer;
    private readonly DispatcherTimer cornerTimer;
    private bool cornerArmed = true;
    private int liveCheckCounter;

    private bool Persistent => Store.Settings.KeepOnDesktop;

    public Launcher()
    {
        Hub = new LiveHub(Store);
        Services = new ServiceHub(Store);
        winKey = new WindowsKeyHook(Toggle);
        liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        liveTimer.Tick += (_, _) => LiveTick();
        cornerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        cornerTimer.Tick += (_, _) => CheckHotCorner();
    }

    public void Start()
    {
        var firstRun = !System.IO.File.Exists(LayoutStore.FilePath);
        if (firstRun) Catalog.ScanNow();          // the default layout pins apps that are installed
        Store.Load(Catalog.IsInstalled);
        if (!firstRun) Catalog.Refresh();
        Catalog.StartWatching();

        Store.SettingsChanged += SettingsChanged;
        Store.WebLinkDropped += url => WebIcons.Load(url);
        window = new StartWindow(this);
        var source = window.EnsureSource();
        hotKey = new HotKey(source, Toggle);
        ApplyHotKey();
        ApplyWindowsKey();
        ApplyHotCorner();
        liveTimer.Start();
        Hub.RefreshStale();

        Application.Current.Deactivated += (_, _) =>
        {
            // Clicking another app sends Start back to the desktop (or away, if not persistent).
            if (IsFront) Dismiss(restoreFocus: false);
            SetQuery("");
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => window?.Dispatcher.BeginInvoke(Reposition);

        if (Persistent) PlaceInBackground();
    }

    public void Shutdown()
    {
        Services.Shutdown();
        winKey.Dispose();
        hotKey?.Dispose();
        Store.SaveNow();
    }

    // MARK: showing Start

    public void Toggle()
    {
        if (IsFront) Dismiss(restoreFocus: true); else Show();
    }

    /// <summary>Brings Start to the front, full screen, with the tile cascade.</summary>
    public void Show()
    {
        if (window is null) return;
        if (IsFront)
        {
            Native.ForceForeground(window.Handle);
            return;
        }
        var fg = Native.GetForegroundWindow();
        if (fg != window.Handle) previousForeground = fg;

        var screen = Persistent ? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0] : ScreenUnderPointer();
        IsFront = true;
        IsOnScreen = true;
        ShowingApps = false;
        Query = "";
        if ((DateTime.UtcNow - Catalog.LastScan).TotalMinutes > 5) Catalog.Refresh();

        window.Topmost = true;
        if (!window.IsVisible) window.Show();
        window.PlaceAt(screen.Bounds, Native.HWND_TOPMOST);
        UpdateInsets(screen);
        window.Activate();
        Native.ForceForeground(window.Handle);
        window.FocusContent();

        StateChanged?.Invoke();
        Appeared?.Invoke();
        UpdateLiveState();
    }

    /// <summary>Leaves the front. In persistent mode Start drops behind your windows; otherwise it hides.</summary>
    public void Dismiss(bool restoreFocus)
    {
        if (!IsFront || window is null) return;
        IsFront = false;
        ClosePanel();
        Query = "";
        ShowingApps = false;
        Store.DraggingTile = null;
        Store.DraggingGroup = null;

        if (Persistent)
        {
            window.Topmost = false;
            var screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
            window.PlaceAt(screen.WorkingArea, Native.HWND_BOTTOM);
            UpdateInsets(screen);
            if (restoreFocus) RestorePreviousForeground();
        }
        else
        {
            IsOnScreen = false;
            window.Topmost = false;
            window.Hide();
            if (restoreFocus) RestorePreviousForeground();
        }
        StateChanged?.Invoke();
        UpdateLiveState();
    }

    /// <summary>The Desktop tile: hide Start completely to reveal the desktop.</summary>
    public void ShowDesktop()
    {
        if (window is null) return;
        ClosePanel();
        IsFront = false;
        IsOnScreen = false;
        Query = "";
        ShowingApps = false;
        window.Topmost = false;
        window.Hide();
        StateChanged?.Invoke();
        UpdateLiveState();
        RestorePreviousForeground();
    }

    /// <summary>Puts Start on the desktop behind all windows, without taking focus.</summary>
    private void PlaceInBackground()
    {
        if (window is null) return;
        var screen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        window.Topmost = false;
        window.ShowWithoutActivating();
        window.PlaceAt(screen.WorkingArea, Native.HWND_BOTTOM);
        UpdateInsets(screen);
        IsFront = false;
        IsOnScreen = true;
        StateChanged?.Invoke();
        Appeared?.Invoke();
        UpdateLiveState();
    }

    private void Reposition()
    {
        if (window is null || !IsOnScreen) return;
        var primary = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        if (IsFront) window.PlaceAt(primary.Bounds, Native.HWND_TOPMOST);
        else window.PlaceAt(primary.WorkingArea, Native.HWND_BOTTOM);
        UpdateInsets(primary);
    }

    private void RestorePreviousForeground()
    {
        if (previousForeground != IntPtr.Zero && Native.IsWindowVisible(previousForeground))
            Native.SetForegroundWindow(previousForeground);
        previousForeground = IntPtr.Zero;
    }

    private void UpdateInsets(Forms.Screen screen)
    {
        if (window is null) return;
        // When Start covers the whole screen, keep the content inside the taskbar area so
        // nothing jumps when Start moves between the front and the desktop.
        var scale = window.DpiScale;
        var b = screen.Bounds;
        var w = screen.WorkingArea;
        ChromeInsets = IsFront
            ? new Thickness((w.Left - b.Left) / scale, (w.Top - b.Top) / scale, (b.Right - w.Right) / scale, (b.Bottom - w.Bottom) / scale)
            : new Thickness(0);
    }

    private static Forms.Screen ScreenUnderPointer()
    {
        Native.GetCursorPos(out var p);
        return Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
    }

    // MARK: search & views

    public IReadOnlyList<AppInfo> Results => AppSearch.Rank(Query, Catalog.Apps);

    public void SetQuery(string query)
    {
        if (query == Query) return;
        Query = query;
        StateChanged?.Invoke();
    }

    public void SetShowingApps(bool show)
    {
        if (show == ShowingApps) return;
        ShowingApps = show;
        StateChanged?.Invoke();
    }

    public void LaunchFirstResult()
    {
        if (Results.FirstOrDefault() is { } first) Launch(first.Id);
    }

    // MARK: actions

    public void Launch(string id)
    {
        // A program that was uninstalled or moved: say so with the system sound, and leave Start open.
        var looksLikePath = id.Length > 2 && (id[1] == ':' || id.StartsWith(@"\\", StringComparison.Ordinal));
        if (looksLikePath && !System.IO.File.Exists(id) && !System.IO.Directory.Exists(id))
        {
            System.Media.SystemSounds.Beep.Play();
            return;
        }
        Dismiss(restoreFocus: false);      // Start stays on the desktop, behind the app
        AppLauncher.Open(id);
    }

    public void OpenUri(string uri)
    {
        Dismiss(restoreFocus: false);
        AppLauncher.OpenUri(uri);
    }

    public void Reveal(string path)
    {
        Dismiss(restoreFocus: false);
        AppLauncher.Reveal(path);
    }

    // MARK: panels, web and service tiles

    /// <summary>Shows a panel over Start, bringing Start forward so you can type in it.</summary>
    public void ShowPanel(FrameworkElement p)
    {
        if (!IsFront) Show();
        panel = p;
        window?.ShowPanel(p);
    }

    public void ClosePanel()
    {
        if (panel is null) return;
        panel = null;
        window?.ShowPanel(null);
        window?.FocusContent();
    }

    /// <summary>The "Add a web tile" panel. A copied web address is filled in for you.</summary>
    public void AddWebTile(TileDestination? destination = null) =>
        ShowPanel(new WebTileEditor(this, null, destination));

    public void EditWebTile(Tile tile) => ShowPanel(new WebTileEditor(this, tile, null));

    /// <summary>The News tile panel: choose a feed for a new tile, or change an existing tile's.</summary>
    public void AddNewsTile() => ShowPanel(new NewsTileEditor(this, null));

    public void EditNewsTile(Tile tile) => ShowPanel(new NewsTileEditor(this, tile));

    public void AddServiceTile(TileDestination? destination = null) =>
        ShowPanel(new ServiceTileEditor(this, null, destination));

    public void EditServiceTile(Tile tile) => ShowPanel(new ServiceTileEditor(this, tile, null));

    /// <summary>
    /// Clicking a service tile: Home Assistant switches toggle and scripts run (locks only from the
    /// menu); everything else opens the service's web page.
    /// </summary>
    public void ActivateService(Tile tile, bool openOnly = false)
    {
        if (Services.IntegrationFor(tile) is not { } integration)
        {
            EditServiceTile(tile);
            return;
        }
        if (!openOnly && tile.Widget == ServiceWidget.Entity && tile.Entity is { } entity)
        {
            var domain = HomeAssistant.Domain(entity);
            if (domain != "lock" && HomeAssistant.Service(domain) is not null)
            {
                Services.Perform(new ServiceAction.ToggleEntity(entity), tile);
                return;
            }
        }
        if (integration.Kind == ServiceKind.Docker && string.IsNullOrWhiteSpace(integration.Url))
        {
            var docker = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
            if (System.IO.File.Exists(docker)) OpenUri(docker);
            return;
        }
        if (integration.WebUri is { } uri) OpenUri(uri.AbsoluteUri);
    }

    public void AddLiveTile(TileKind kind)
    {
        // Each News tile can show its own source, so ask which one.
        if (kind == TileKind.News) { AddNewsTile(); return; }
        Store.AddLive(kind);
        Hub.RefreshStale();
        if (TilesLive) Hub.SetActive(true);
    }

    public void OpenSettings() => OpenSettings(addingIntegration: false, editing: null);

    /// <summary>Opens Settings, optionally with the integration editor showing (adding, or editing one service).</summary>
    public void OpenSettings(bool addingIntegration, Guid? editing)
    {
        Dismiss(restoreFocus: false);
        if (settingsWindow is null || !settingsWindow.IsLoaded)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        settingsWindow.Activate();
        Native.ForceForeground(new WindowInteropHelper(settingsWindow).Handle);
        if (addingIntegration || editing is not null)
        {
            var target = editing;
            settingsWindow.Dispatcher.BeginInvoke(() => IntegrationRequested?.Invoke(target), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>The right-click menu's "Change Wallpaper…": Settings, then the file picker.</summary>
    public void ChangeWallpaper()
    {
        OpenSettings();
        settingsWindow?.Dispatcher.BeginInvoke(() => settingsWindow?.ChangeWallpaper(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void Quit() => Application.Current.Shutdown();

    // MARK: settings

    private void SettingsChanged(AppSettings before)
    {
        var s = Store.Settings;
        if (before.HotKey != s.HotKey) ApplyHotKey();
        if (before.WindowsKey != s.WindowsKey) ApplyWindowsKey();
        if (before.HotCorner != s.HotCorner) ApplyHotCorner();
        if (before.KeepOnDesktop != s.KeepOnDesktop) ApplyPersistence();
        if (before.Fahrenheit != s.Fahrenheit || before.WeatherLat != s.WeatherLat || before.WeatherLon != s.WeatherLon)
            Hub.Weather.Refresh(s, force: true);
        if (before.NewsFeed != s.NewsFeed) Hub.NewsFeedsChanged();
        if (before.CalendarFeed != s.CalendarFeed) Hub.Calendar.Refresh(s.CalendarFeed, force: true);
        if (before.PhotosFolder != s.PhotosFolder && TilesLive) Hub.Photos.Start(s.PhotosFolder);
        StateChanged?.Invoke();
    }

    /// <returns>false if another app already uses the shortcut.</returns>
    public bool ApplyHotKey()
    {
        var preset = HotKeyPreset.Find(Store.Settings.HotKey);
        return hotKey?.Register(preset.Modifiers, preset.VirtualKey) ?? false;
    }

    private void ApplyWindowsKey()
    {
        if (Store.Settings.WindowsKey) winKey.Enable(); else winKey.Disable();
    }

    private void ApplyHotCorner()
    {
        if (Store.Settings.HotCorner) cornerTimer.Start(); else cornerTimer.Stop();
    }

    private void ApplyPersistence()
    {
        if (IsFront || window is null) return;
        if (Persistent) PlaceInBackground();
        else if (IsOnScreen)
        {
            window.Hide();
            IsOnScreen = false;
            UpdateLiveState();
        }
    }

    /// <summary>Bottom-left corner of any display opens Start, like Windows 8.</summary>
    private void CheckHotCorner()
    {
        Native.GetCursorPos(out var p);
        var inCorner = Forms.Screen.AllScreens.Any(s =>
            p.X <= s.Bounds.Left + 2 && p.X >= s.Bounds.Left - 1 && p.Y >= s.Bounds.Bottom - 3 && p.Y <= s.Bounds.Bottom);
        if (inCorner && cornerArmed)
        {
            // Fires once per visit: leave the corner before it can fire again.
            cornerArmed = false;
            if (!IsFront) Show();
            else if (Store.Settings.HotCornerToggles) ShowDesktop();     // a toggle: put Start away, show the desktop
        }
        else if (!inCorner) cornerArmed = true;
    }

    // MARK: live tiles

    private void LiveTick()
    {
        if (++liveCheckCounter % 2 == 0) UpdateLiveState();
        if (!TilesLive) return;
        var now = DateTime.Now;
        Hub.Tick(now);
        window?.Tick(now);
    }

    private void UpdateLiveState()
    {
        var live = false;
        if (window is not null && IsOnScreen)
            live = IsFront || !IsCovered();
        if (live == TilesLive) return;
        TilesLive = live;
        Hub.SetActive(live);
        Services.SetActive(live);
        Running.SetActive(live);
        StateChanged?.Invoke();
    }

    /// <summary>On the desktop, Start counts as covered when a maximized or full-screen window is in front.</summary>
    private bool IsCovered()
    {
        if (window is null) return true;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == window.Handle) return false;
        var cls = Native.ClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (Native.IsZoomed(fg)) return true;
        if (Native.GetWindowRect(fg, out var r))
        {
            var primary = (Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]).Bounds;
            return r.Left <= primary.Left && r.Top <= primary.Top && r.Right >= primary.Right && r.Bottom >= primary.Bottom;
        }
        return false;
    }
}
