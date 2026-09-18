using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenClaw.Shared.Browser;

/// <summary>Owns only the per-user native host. This never requests an extension installation.</summary>
public static class BrowserNativeRegistration
{
    private const string Key = @"Software\Google\Chrome\NativeMessagingHosts\ai.openclaw.browser_bootstrap";
    private static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenClawTray", "browser-native", "ai.openclaw.browser_bootstrap.json");

    public static string BuildManifest(string executable) => JsonSerializer.Serialize(new
    {
        name = BrowserNativeProtocol.HostName,
        description = "OpenClaw Companion browser pairing",
        path = Path.GetFullPath(executable),
        type = "stdio",
        allowed_origins = new[] { BrowserNativeProtocol.Origin }
    });

    public static bool ManifestMatches(string json, string executable)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var expected = JsonDocument.Parse(BuildManifest(executable));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == 5 &&
                doc.RootElement.EnumerateObject().Count() == 5 &&
                JsonElement.DeepEquals(doc.RootElement, expected.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool StoredManifestMatches(string executable)
    {
        var info = new FileInfo(ManifestPath);
        return info.Exists && info.Length is > 0 and <= 4096 &&
            (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
            ManifestMatches(File.ReadAllText(ManifestPath), executable);
    }

    [SupportedOSPlatform("windows")]
    public static bool IsRegistered(string executable)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
        using var key = root.OpenSubKey(Key);
        return key?.GetValue("") is string registered &&
            string.Equals(registered, ManifestPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(ManifestPath) &&
            (File.GetAttributes(ManifestPath) & FileAttributes.ReparsePoint) == 0 &&
            StoredManifestMatches(executable);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasForeignRegistration(string executable)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(Key);
            if (key is null) continue;
            if (key.GetValue("") is not string path ||
                !string.Equals(path, ManifestPath, StringComparison.OrdinalIgnoreCase) ||
                !StoredManifestMatches(executable)) return true;
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    public static bool Apply(string executable, bool remove = false)
    {
        using var mutex = new Mutex(false, @"Local\OpenClawTray.BrowserNativeRegistration");
        if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            using var existing = root.OpenSubKey(Key);
            // Do not shadow a host installed by another product, user install, or registry view.
            if (!remove && HasForeignRegistration(executable)) return false;
            if (existing is not null && !IsRegistered(executable)) return false;
            if (remove)
            {
                if (existing is null) return true;
                // Never delete a key with someone else's additional values or children.
                if (existing.SubKeyCount != 0 || existing.GetValueNames().Any(name => name != "")) return false;
                existing.Dispose();
                root.DeleteSubKey(Key, false);
                File.Delete(ManifestPath);
                return true;
            }
            if (!File.Exists(executable)) return false;
            var directory = Path.GetDirectoryName(ManifestPath)!;
            Directory.CreateDirectory(directory);
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (File.Exists(ManifestPath))
            {
                if (!StoredManifestMatches(executable)) return false;
            }
            else
            {
                using var file = new FileStream(ManifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file);
                writer.Write(BuildManifest(executable));
            }
            using var key = root.CreateSubKey(Key);
            if (key.GetValue("") is { } current &&
                (current is not string currentPath ||
                 !string.Equals(currentPath, ManifestPath, StringComparison.OrdinalIgnoreCase))) return false;
            if (!StoredManifestMatches(executable)) return false;
            key.SetValue("", ManifestPath, RegistryValueKind.String);
            return IsRegistered(executable);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
