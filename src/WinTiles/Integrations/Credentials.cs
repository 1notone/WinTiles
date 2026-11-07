using System.Runtime.InteropServices;
using System.Text;

namespace WinTiles;

/// <summary>
/// Passwords, tokens and API keys for integrations, kept in Windows Credential Manager
/// (Control Panel → Credential Manager → Windows Credentials, "WinTiles integration …") —
/// never in layout.json.
/// </summary>
public static class IntegrationSecrets
{
    private const string Prefix = "WinTiles integration ";
    private static readonly Dictionary<Guid, string> cache = new();
    private static readonly object gate = new();

    public static string Get(Guid id)
    {
        lock (gate)
        {
            if (cache.TryGetValue(id, out var cached)) return cached;
            var target = Prefix + id.ToString("D").ToUpperInvariant();
            var (value, found) = Read(target);
            if (!found)
            {
                // Saved by Tessera (WinTiles' name before 1.2.0): move it across.
                var old = Migration.OldCredentialTarget(id);
                var (legacy, legacyFound) = Read(old);
                if (legacyFound && Write(target, legacy ?? ""))
                {
                    CredDelete(old, CRED_TYPE_GENERIC, 0);
                    (value, found) = (legacy, true);
                }
            }
            // Only remember a definite answer, so a transient failure is retried next time.
            if (value is not null) cache[id] = value;
            return value ?? "";
        }
    }

    /// <summary>Saves (or, with an empty secret, deletes) a service's secret. False if Credential Manager refused.</summary>
    public static bool Set(Guid id, string secret, string name = "")
    {
        lock (gate)
        {
            var target = Prefix + id.ToString("D").ToUpperInvariant();
            if (secret.Length == 0)
            {
                CredDelete(target, CRED_TYPE_GENERIC, 0);
                cache[id] = "";
                return true;
            }
            if (!Write(target, secret, name))
            {
                cache.Remove(id);
                return false;
            }
            cache[id] = secret;
            return true;
        }
    }

    public static void Remove(Guid id) => Set(id, "");

    /// <summary>Deletes every saved service key (uninstalling with "also delete my tiles and settings").</summary>
    public static void RemoveAll()
    {
        lock (gate)
        {
            cache.Clear();
            if (!CredEnumerate(Prefix + "*", 0, out var count, out var list)) return;
            try
            {
                var targets = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var cred = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                    if (cred.TargetName.StartsWith(Prefix, StringComparison.Ordinal)) targets.Add(cred.TargetName);
                }
                foreach (var t in targets) CredDelete(t, CRED_TYPE_GENERIC, 0);
            }
            finally { CredFree(list); }
        }
    }

    // MARK: Win32

    private const int CRED_TYPE_GENERIC = 1;
    private const int ERROR_NOT_FOUND = 1168;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <returns>The secret, and whether one was found. (null, false) with a last error other than
    /// "not found" means Credential Manager couldn't be read just now.</returns>
    private static (string? Value, bool Found) Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
            return (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND ? "" : null, false);
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return ("", true);
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return (Encoding.Unicode.GetString(bytes), true);
        }
        finally { CredFree(ptr); }
    }

    private static bool Write(string target, string secret, string name = "")
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = string.IsNullOrWhiteSpace(name) ? "Used by WinTiles' service tiles" : $"WinTiles: {name}",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (CredWrite(ref cred, 0)) return true;
            Log.Write($"Couldn't save a credential ({Marshal.GetLastWin32Error()}).");
            return false;
        }
        finally { Marshal.FreeHGlobal(blob); }
    }
}
