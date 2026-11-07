using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinTiles.Core;
using Forms = System.Windows.Forms;

namespace WinTiles;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Run by the uninstaller when you choose to delete your tiles and settings.
        if (args.Contains("--forget-secrets"))
        {
            IntegrationSecrets.RemoveAll();
            return;
        }
        // One WinTiles at a time: a second launch just asks the running one to show Start.
        using var mutex = new Mutex(true, @"Local\WinTiles.SingleInstance", out var first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WinTiles.Show");
        if (!first)
        {
            showSignal.Set();
            return;
        }

        var app = new WinTilesApp(background: args.Contains("--background"), showSignal);
        app.Run();
        GC.KeepAlive(mutex);
    }
}

public sealed class WinTilesApp : Application
{
    private readonly bool startInBackground;
    private readonly EventWaitHandle showSignal;
    private Launcher? launcher;
    private Forms.NotifyIcon? tray;
    private RegisteredWaitHandle? signalWait;

    public WinTilesApp(bool background, EventWaitHandle showSignal)
    {
        startInBackground = background;
        this.showSignal = showSignal;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        // Signing out or restarting: save the layout before Windows ends the session.
        SessionEnding += (_, _) => launcher?.Store.SaveNow();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Migration.FromTessera();
        launcher = new Launcher();
        launcher.Start();
        BuildTray();
        StartupRegistration.Repair();

        signalWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => launcher?.Show()), null, Timeout.Infinite, executeOnlyOnce: false);

        // Windows 8 greets you with Start; so does WinTiles when opened by hand.
        if (!startInBackground || !launcher.Store.Settings.KeepOnDesktop) launcher.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        signalWait?.Unregister(null);
        launcher?.Shutdown();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        base.OnExit(e);
    }

    private void BuildTray()
    {
        tray = new Forms.NotifyIcon
        {
            Text = "WinTiles",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) launcher?.Toggle(); };
        tray.ContextMenuStrip.Opening += (_, _) => FillTrayMenu(tray.ContextMenuStrip);
        FillTrayMenu(tray.ContextMenuStrip);
    }

    private void FillTrayMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add("Show Start", null, (_, _) => launcher?.Show());
        menu.Items.Add("Show Desktop", null, (_, _) => launcher?.ShowDesktop());
        var live = new Forms.ToolStripMenuItem("Add Live Tile");
        foreach (var kind in TileKindInfo.LiveKinds)
        {
            var k = kind;
            live.DropDownItems.Add(k.Title(), null, (_, _) => { launcher?.AddLiveTile(k); launcher?.Show(); });
        }
        menu.Items.Add(live);
        menu.Items.Add("Add Web Tile…", null, (_, _) => launcher?.AddWebTile());
        menu.Items.Add("Add Service Tile…", null, (_, _) => launcher?.AddServiceTile());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => launcher?.OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit WinTiles", null, (_, _) => Shutdown());
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // WinTiles is a shell: log and keep going rather than vanish.
        Log.Write("Unhandled: " + e.Exception);
        e.Handled = true;
    }
}
