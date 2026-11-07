using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinTiles.Core;

namespace WinTiles;

/// <summary>
/// Knows which apps are installed (the same list as the Windows Start menu's "All apps"),
/// plus cached names, icons and tile tints. Watches the Start menu folders, so apps you
/// install or remove appear (or leave) within a few seconds.
/// </summary>
public sealed class AppCatalog
{
    public IReadOnlyList<AppInfo> Apps { get; private set; } = Array.Empty<AppInfo>();
    public event Action? Changed;

    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> tints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<ImageSource?>>> pendingIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> watchers = new();
    private readonly DispatcherTimer debounce;
    private readonly Dispatcher dispatcher;
    private bool scanning, rescanRequested, secondPass;

    public DateTime LastScan { get; private set; } = DateTime.MinValue;

    public AppCatalog()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            Refresh();
            // An installer can still be finishing (or create shortcuts late); check once more.
            if (!secondPass)
            {
                secondPass = true;
                debounce.Interval = TimeSpan.FromSeconds(6);
                debounce.Start();
            }
            else
            {
                secondPass = false;
                debounce.Interval = TimeSpan.FromSeconds(1.5);
            }
        };
    }

    // MARK: scanning

    /// <summary>Synchronous scan, used once at first run to build the default layout.</summary>
    public void ScanNow()
    {
        Apply(AppScanner.Scan());
    }

    public void Refresh()
    {
        if (scanning) { rescanRequested = true; return; }
        scanning = true;
        var thread = new Thread(() =>
        {
            List<AppInfo> found;
            try { found = AppScanner.Scan(); }
            catch (Exception ex) { Log.Write("App scan failed: " + ex.Message); found = new(); }
            dispatcher.BeginInvoke(() =>
            {
                if (found.Count > 0) Apply(found);
                scanning = false;
                if (rescanRequested) { rescanRequested = false; Refresh(); }
            });
        }) { IsBackground = true, Name = "WinTiles app scan" };
        thread.SetApartmentState(ApartmentState.STA);   // the Shell COM objects need STA
        thread.Start();
    }

    private void Apply(List<AppInfo> found)
    {
        LastScan = DateTime.UtcNow;
        foreach (var app in found) names[app.Id] = app.Name;
        if (found.Select(a => a.Id + "|" + a.Name).SequenceEqual(Apps.Select(a => a.Id + "|" + a.Name))) return;
        Apps = found;
        Changed?.Invoke();
    }

    public bool IsInstalled(string id) =>
        Apps.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) || AppLauncher.IsFilePath(id);

    /// <summary>Rescans whenever something changes in the Start menu folders.</summary>
    public void StartWatching()
    {
        if (watchers.Count > 0) return;
        foreach (var root in AppScanner.StartMenuFolders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                FileSystemEventHandler onChange = (_, e) => dispatcher.BeginInvoke(() => FolderChanged(e.FullPath));
                w.Created += onChange;
                w.Deleted += onChange;
                w.Changed += onChange;
                w.Renamed += (_, e) => dispatcher.BeginInvoke(() => FolderChanged(e.FullPath));
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception ex) { Log.Write($"Can't watch {root}: {ex.Message}"); }
        }
    }

    private void FolderChanged(string path)
    {
        // Forget cached icons for anything that changed, so an updated app shows its new icon.
        icons.Remove(path);
        tints.Remove(path);
        secondPass = false;
        debounce.Interval = TimeSpan.FromSeconds(1.5);
        debounce.Stop();
        debounce.Start();
    }

    // MARK: names, icons, tints

    public string Name(string id)
    {
        if (names.TryGetValue(id, out var n)) return n;
        n = AppScanner.FallbackName(id);
        names[id] = n;
        return n;
    }

    /// <summary>Calls back with the icon (on the UI thread). Cached icons arrive immediately.</summary>
    public void Icon(string id, Action<ImageSource?> done)
    {
        if (icons.TryGetValue(id, out var cached)) { done(cached); return; }
        if (pendingIcons.TryGetValue(id, out var waiting)) { waiting.Add(done); return; }
        pendingIcons[id] = new List<Action<ImageSource?>> { done };
        // Loading an icon costs a few milliseconds; spread them out so the UI stays responsive.
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var image = ShellIcons.Load(AppLauncher.ParsingName(id), 256, out var pixels);
            icons[id] = image;
            if (pixels is not null && !tints.ContainsKey(id))
                tints[id] = Palette.IconTint(pixels) ?? Palette.AutoColor(id);
            if (pendingIcons.Remove(id, out var callbacks))
                foreach (var cb in callbacks) cb(image);
        });
    }

    /// <summary>A tile color derived from the icon, once the icon has loaded; until then the automatic color.</summary>
    public string Tint(string id) => tints.TryGetValue(id, out var t) ? t : Palette.AutoColor(id);
    public bool HasTint(string id) => tints.ContainsKey(id);
}

internal static class AppScanner
{
    public static readonly string[] StartMenuFolders =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
    };

    private static readonly string[] SkippedExtensions = { ".chm", ".txt", ".pdf", ".htm", ".html", ".rtf", ".url", ".ini", ".log" };

    /// <summary>Enumerates shell:AppsFolder — desktop apps and Store apps, exactly as Start lists them.</summary>
    public static List<AppInfo> Scan()
    {
        var found = new List<AppInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return found;
        object? shellObj = null;
        try
        {
            shellObj = Activator.CreateInstance(type);
            dynamic shell = shellObj!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder == null) return found;
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name ?? "";
                string id = item.Path ?? "";
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                if (SkippedExtensions.Any(x => id.EndsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(id)) continue;
                found.Add(new AppInfo(id, name));
            }
        }
        finally
        {
            if (shellObj is not null && Marshal.IsComObject(shellObj)) Marshal.FinalReleaseComObject(shellObj);
        }
        return found.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string FallbackName(string id)
    {
        if (AppLauncher.IsFilePath(id))
        {
            try
            {
                if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var desc = FileVersionInfo.GetVersionInfo(id).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
                }
            }
            catch { }
            return Path.GetFileNameWithoutExtension(id);
        }
        // "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" → "WindowsCalculator"
        var core = id.Split('!')[0].Split('_')[0];
        var dot = core.LastIndexOf('.');
        return dot >= 0 && dot < core.Length - 1 ? core[(dot + 1)..] : core;
    }
}

/// <summary>Opens apps, files and links so the opened app gets keyboard focus.</summary>
public static class AppLauncher
{
    private static readonly string[] PinnableExtensions = { ".exe", ".lnk", ".url", ".appref-ms", ".bat", ".cmd" };

    public static bool IsFilePath(string id) =>
        id.Length > 2 && (id[1] == ':' || id.StartsWith(@"\\", StringComparison.Ordinal)) && (File.Exists(id) || Directory.Exists(id));

    public static bool IsPinnableFile(string path) =>
        File.Exists(path) && PinnableExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>What SHCreateItemFromParsingName needs for this app.</summary>
    public static string ParsingName(string id) => IsFilePath(id) ? id : @"shell:AppsFolder\" + id;

    public static void Open(string id)
    {
        // Let whatever we start take the foreground (Windows' foreground lock would otherwise
        // leave keyboard focus with WinTiles).
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        try
        {
            if (IsFilePath(id))
                Process.Start(new ProcessStartInfo(id) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(id) ?? "" });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", "\"shell:AppsFolder\\" + id + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Couldn't open {id}: {ex.Message}");
            MessageBox.Show($"WinTiles couldn't open this app.\n\n{ex.Message}", "WinTiles", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenUri(string uri)
    {
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Couldn't open {uri}: {ex.Message}"); }
    }

    public static void Reveal(string path)
    {
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Couldn't reveal {path}: {ex.Message}"); }
    }
}

/// <summary>High-quality app icons with transparency, via IShellItemImageFactory.</summary>
internal static class ShellIcons
{
    public static ImageSource? Load(string parsingName, int size, out byte[]? pixels)
    {
        pixels = null;
        Native.IShellItemImageFactory? factory = null;
        try
        {
            Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(Native.IShellItemImageFactory).GUID, out factory);
            var hr = factory.GetImage(new Native.SIZE { cx = size, cy = size }, Native.SIIGBF_BIGGERSIZEOK | Native.SIIGBF_ICONONLY, out var hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            try { return FromHBitmap(hbmp, out pixels); }
            finally { Native.DeleteObject(hbmp); }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? FromHBitmap(IntPtr hbmp, out byte[]? pixels)
    {
        pixels = null;
        if (Native.GetObject(hbmp, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        var w = bm.bmWidth;
        var h = Math.Abs(bm.bmHeight);
        var info = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,          // ask for top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        var dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, hbmp, 0, (uint)h, bits, ref info, 0) == 0) return null;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        // Old-style icons sometimes come back with no alpha at all; make those opaque.
        var anyAlpha = false;
        for (var i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (var i = 3; i < bits.Length; i += 4) bits[i] = 255;

        pixels = bits;
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
        source.Freeze();
        return source;
    }
}
