using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WinTiles;

/// <summary>A system-wide keyboard shortcut via RegisterHotKey.</summary>
public sealed class HotKey : IDisposable
{
    private const int Id = 0x7E55;
    private readonly HwndSource source;
    private readonly Action pressed;
    private bool registered;

    public HotKey(HwndSource source, Action pressed)
    {
        this.source = source;
        this.pressed = pressed;
        source.AddHook(Hook);
    }

    /// <returns>false if another app already owns the shortcut.</returns>
    public bool Register(uint modifiers, uint vk)
    {
        Unregister();
        if (vk == 0) return true;
        registered = Native.RegisterHotKey(source.Handle, Id, modifiers | Native.MOD_NOREPEAT, vk);
        if (!registered) Log.Write($"Couldn't register hot key (error {Marshal.GetLastWin32Error()}); another app may be using it.");
        return registered;
    }

    public void Unregister()
    {
        if (registered) Native.UnregisterHotKey(source.Handle, Id);
        registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        source.RemoveHook(Hook);
    }
}

/// <summary>
/// Makes a tap of the Windows key open WinTiles instead of the Start menu, like Windows 8.
/// Win+anything shortcuts (Win+E, Win+D, Win+Shift+S…) keep working.
/// </summary>
public sealed class WindowsKeyHook : IDisposable
{
    private readonly Action tapped;
    private readonly Native.LowLevelKeyboardProc proc;   // kept alive so the GC doesn't collect the callback
    private IntPtr hook;
    private bool winDown, comboUsed;

    public WindowsKeyHook(Action tapped)
    {
        this.tapped = tapped;
        proc = Callback;
    }

    public bool Enabled => hook != IntPtr.Zero;

    public void Enable()
    {
        if (hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(module?.ModuleName), 0);
        if (hook == IntPtr.Zero) Log.Write($"Couldn't install the Windows key hook (error {Marshal.GetLastWin32Error()}).");
    }

    public void Disable()
    {
        if (hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        winDown = false;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var injected = (info.flags & Native.LLKHF_INJECTED) != 0;
            var msg = wParam.ToInt32();
            var isDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            var isUp = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            var isWin = info.vkCode is Native.VK_LWIN or Native.VK_RWIN;

            if (!injected)
            {
                if (isWin && isDown)
                {
                    if (!winDown) { winDown = true; comboUsed = false; }
                }
                else if (!isWin && isDown && winDown)
                {
                    comboUsed = true;        // Win+E etc.: leave it alone
                }
                else if (isWin && isUp && winDown)
                {
                    winDown = false;
                    if (!comboUsed)
                    {
                        // Tapping an unassigned key first makes Windows treat this as a "combo",
                        // so it doesn't open its own Start menu on release.
                        Native.SendMaskKey();
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(tapped);
                    }
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public void Dispose() => Disable();
}

/// <summary>Start WinTiles when you sign in (HKCU\…\Run, which the installer also sets).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinTiles";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
        }
        catch (Exception ex) { Log.Write("Couldn't change sign-in startup: " + ex.Message); }
    }

    /// <summary>Keeps the Run entry pointing at this copy if WinTiles was moved or updated.</summary>
    public static void Repair()
    {
        if (IsEnabled) Set(true);
    }
}
