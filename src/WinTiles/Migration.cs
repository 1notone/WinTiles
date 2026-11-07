using System.IO;
using Microsoft.Win32;

namespace WinTiles;

/// <summary>
/// WinTiles was called Tessera before 1.2.0. On the first run after the rename, carry over
/// the tiles and settings, the saved service keys and the "start when I sign in" choice.
/// </summary>
public static class Migration
{
    private const string OldName = "Tessera";

    public static void FromTessera()
    {
        try { MoveData(); } catch (Exception ex) { Log.Write("Couldn't move Tessera's settings: " + ex.Message); }
        try { MoveStartup(); } catch (Exception ex) { Log.Write("Couldn't move Tessera's sign-in startup: " + ex.Message); }
    }

    /// <summary>%APPDATA%\Tessera → %APPDATA%\WinTiles, once.</summary>
    private static void MoveData()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);
        if (File.Exists(LayoutStore.FilePath) || !File.Exists(Path.Combine(old, "layout.json"))) return;
        if (!Directory.Exists(LayoutStore.Directory))
        {
            try { Directory.Move(old, LayoutStore.Directory); }
            catch (IOException) { Copy(old, LayoutStore.Directory); }
        }
        else Copy(old, LayoutStore.Directory);      // e.g. only a log file was written so far
        var oldLog = Path.Combine(LayoutStore.Directory, "tessera.log");
        if (File.Exists(oldLog)) File.Delete(oldLog);
        Log.Write("Moved settings from Tessera.");
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>A "Tessera" sign-in entry becomes a "WinTiles" one.</summary>
    private static void MoveStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key?.GetValue(OldName) is null) return;
        key.DeleteValue(OldName, throwOnMissingValue: false);
        StartupRegistration.Set(true);
    }

    /// <summary>The Credential Manager name Tessera used for a service's secret.</summary>
    public static string OldCredentialTarget(Guid id) => OldName + " integration " + id.ToString("D").ToUpperInvariant();
}
