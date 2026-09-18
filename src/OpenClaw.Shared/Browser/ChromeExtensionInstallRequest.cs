using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Shared.Browser;

/// <summary>Owns a normal per-user Chrome Store request, never an enterprise policy or Chrome profile preference.</summary>
public static class ChromeExtensionInstallRequest
{
    public const string UpdateUrl = "https://clients2.google.com/service/update2/crx";
    public const string RegistryPath = @"Software\Google\Chrome\Extensions\" + BrowserNativeProtocol.ExtensionId;
    public const string OwnerValue = "openclaw_native_host";

    public static bool IsOwned(IReadOnlyDictionary<string, object?> values, string executable, bool allowIncomplete = false) =>
        values.Count is >= 1 and <= 2 && values.Keys.All(k => k is OwnerValue or "update_url") &&
        values.TryGetValue(OwnerValue, out var owner) && owner is string path &&
        string.Equals(path, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) &&
        ((values.TryGetValue("update_url", out var url) && url is string update && update == UpdateUrl) ||
         (allowIncomplete && !values.ContainsKey("update_url")));

    public static bool SettingsAllowRequest(string? json)
    {
        if (json is null) return true; // Fresh install uses SettingsData's Browser control default.
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var fields = document.RootElement.EnumerateObject()
                .Where(p => p.Name == "NodeBrowserProxyEnabled").ToArray();
            return fields.Length == 0 || (fields.Length == 1 && fields[0].Value.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return false; }
    }

    private static bool SavedBrowserControlAllowsRequest()
    {
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray", "settings.json");
        var file = new FileInfo(settingsPath);
        if (!file.Exists) return true;
        // Inspect only the persisted preference. Never deserialize/decrypt or log credential fields.
        return file.Length <= 1024 * 1024 && SettingsAllowRequest(File.ReadAllText(settingsPath));
    }

    [SupportedOSPlatform("windows")]
    public static bool Apply(string executable, bool remove = false)
    {
        using var mutex = new Mutex(false, @"Local\OpenClawTray.ChromeExtensionInstallRequest");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var existing = root.OpenSubKey(RegistryPath);
            if (remove)
            {
                if (existing is null) return true;
                if (existing.SubKeyCount != 0 || !IsOwned(ReadValues(existing), executable, allowIncomplete: true)) return false;
                existing.Dispose();
                root.DeleteSubKey(RegistryPath, false);
                return true;
            }
            // Native host must already be usable before Chrome can see update_url.
            if (!BrowserNativeRegistration.IsRegistered(executable) || !SavedBrowserControlAllowsRequest()) return false;
            // Chromium prefers HKLM32 over HKCU. Never shadow or modify another registration.
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using var otherRoot = RegistryKey.OpenBaseKey(hive, view);
                using var other = otherRoot.OpenSubKey(RegistryPath);
                if (other is null) continue;
                if (hive == RegistryHive.LocalMachine || other.SubKeyCount != 0 ||
                    !IsOwned(ReadValues(other), executable)) return false;
            }
            if (existing is not null) return IsOwned(ReadValues(existing), executable);

            // RegistryKey.CreateSubKey cannot distinguish "created" from "opened". The
            // disposition protects a foreign entry created between our inspection and write.
            var status = RegCreateKeyEx(root.Handle, RegistryPath, 0, null, 0, 0x2001f,
                IntPtr.Zero, out var handle, out var disposition);
            using (handle)
            {
                if (status != 0) return false;
                using var key = RegistryKey.FromHandle(handle);
                if (disposition != 1) return IsOwned(ReadValues(key), executable);
                key.SetValue(OwnerValue, Path.GetFullPath(executable), RegistryValueKind.String);
                // This is the actual Store request. Chrome still requires the user's approval.
                key.SetValue("update_url", UpdateUrl, RegistryValueKind.String);
                return IsOwned(ReadValues(key), executable);
            }
        }
        finally { mutex.ReleaseMutex(); }
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, object?> ReadValues(RegistryKey key) =>
        key.GetValueNames().ToDictionary(name => name, name => key.GetValue(name), StringComparer.Ordinal);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, int reserved,
        string? keyClass, int options, int desiredAccess, IntPtr securityAttributes,
        out SafeRegistryHandle result, out int disposition);
}
