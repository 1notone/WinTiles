using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace WinTiles;

/// <summary>
/// Knows which apps have a window open, like the taskbar, so their tiles can show a small dot.
/// Windows has no "app launched" event for ordinary apps, so this looks at the open windows every
/// two seconds — only while Start can be seen.
///
/// A tile's app ID is matched to a window by its AppUserModelID (Store apps and most modern apps),
/// or by the program file for desktop apps (resolving shortcuts and known-folder IDs).
/// </summary>
public sealed class RunningApps
{
    private sealed record AppWindow(IntPtr Handle, uint ProcessId, string? Aumid, string? Exe);

    private List<AppWindow> windows = new();
    private HashSet<string> aumids = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> exes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> exeFor = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer;
    private string signature = "";

    public event Action? Changed;

    public RunningApps()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Refresh();
    }

    public void SetActive(bool on)
    {
        if (on) { Refresh(); timer.Start(); }
        else timer.Stop();
    }

    public bool IsRunning(string appId) => Matching(appId).Any();

    private IEnumerable<AppWindow> Matching(string appId)
    {
        var exe = ExeFor(appId);
        return windows.Where(w =>
            (w.Aumid is not null && string.Equals(w.Aumid, appId, StringComparison.OrdinalIgnoreCase)) ||
            (exe is not null && w.Exe is not null && string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Asks the app to close its windows, as if you clicked their close buttons (it can still ask to save).</summary>
    public void Quit(string appId)
    {
        foreach (var w in Matching(appId).ToList()) PostMessage(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        RefreshSoon();
    }

    /// <summary>Whether Force Quit can end just this app (Store apps share a host process with others).</summary>
    public bool CanForceQuit(string appId) =>
        Matching(appId).Any(w => w.Exe is { } exe && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
                                 && !exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ends the app's processes immediately. Unsaved changes are lost.</summary>
    public void ForceQuit(string appId)
    {
        foreach (var pid in Matching(appId).Where(w => w.Exe is { } exe &&
                     !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
                     !exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                 .Select(w => w.ProcessId).Distinct().ToList())
        {
            try { Process.GetProcessById((int)pid).Kill(); } catch (Exception ex) { Log.Write($"Force quit failed: {ex.Message}"); }
        }
        RefreshSoon();
    }

    private void RefreshSoon()
    {
        var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        once.Tick += (_, _) => { once.Stop(); Refresh(); };
        once.Start();
    }

    /// <summary>Re-reads the open app windows (the ones the taskbar would show).</summary>
    public void Refresh()
    {
        var found = new List<AppWindow>();
        var me = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // hidden Store windows
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == me) return true;
            found.Add(new AppWindow(hwnd, pid, WindowAumid(hwnd), ProcessPath(pid)));
            return true;
        }, IntPtr.Zero);

        windows = found;
        aumids = new HashSet<string>(found.Select(w => w.Aumid).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        exes = new HashSet<string>(found.Select(w => w.Exe).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var sig = string.Join("|", aumids.OrderBy(x => x)) + "#" + string.Join("|", exes.OrderBy(x => x));
        if (sig == signature) return;
        signature = sig;
        Changed?.Invoke();
    }

    // MARK: matching a tile's app ID to a program file

    private string? ExeFor(string appId)
    {
        if (exeFor.TryGetValue(appId, out var cached)) return cached;
        string? exe = null;
        try
        {
            if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(appId)) exe = Path.GetFullPath(appId);
            else if (appId.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(appId)) exe = ShortcutTarget(appId);
            else if (KnownFolderPath(appId) is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = path;
        }
        catch (Exception) { }
        exeFor[appId] = exe;
        return exe;
    }

    /// <summary>"{6D809377-6AF0-444B-8957-A3773F02200E}\Vendor\App.exe" → "C:\Program Files\Vendor\App.exe".</summary>
    private static string? KnownFolderPath(string appId)
    {
        if (appId.Length < 40 || appId[0] != '{' || appId[37] != '}' || !Guid.TryParse(appId[1..37], out var folder)) return null;
        if (SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out var p) != 0) return null;
        try
        {
            var root = Marshal.PtrToStringUni(p);
            return root is null ? null : Path.Combine(root, appId[38..].TrimStart('\\'));
        }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    private static string? ShortcutTarget(string lnk)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return null;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            dynamic link = sh.CreateShortcut(lnk);
            string target = link.TargetPath ?? "";
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The window's AppUserModelID (PKEY_AppUserModel_ID), set for Store apps and many others.</summary>
    private static string? WindowAumid(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try { return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        catch (Exception) { return null; }
        finally { Marshal.ReleaseComObject(store); }
    }

    // MARK: Win32

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint WM_CLOSE = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(16)] private IntPtr padding;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }
}
