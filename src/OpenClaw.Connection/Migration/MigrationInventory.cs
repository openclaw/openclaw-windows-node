using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Read-only inventory of the application data directories, not their profile parents.
/// This is evidence for preparation, not permission to adopt or delete state. Consumers
/// must capture again before acting. No credentials or payload bytes leave Capture.
/// </summary>
public sealed record MigrationInventory(
    string Fingerprint,
    bool AutoStart,
    string RoamingDirectory,
    string LocalDirectory,
    ImmutableArray<MigrationInventory.Entry> Entries)
{
    /// <summary>Missing files have null length and hash. Root identifies the relative-name base.</summary>
    public sealed record Entry(
        string Root,
        string RelativeName,
        string CanonicalPath,
        bool Exists,
        long? Length,
        string? Sha256);

    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumTotalBytes = 16 * 1024 * 1024;
    private const int MaximumGateways = 128;
    private const string IdentityFileName = "device-key-ed25519.json";
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Captures bounded metadata, including optional-file absence. Existing sources remain
    /// open without write/delete sharing until all paths and hashes have been rechecked.
    /// Throws on invalid, unsupported, changing, unreadable or reparse-point state.
    /// Does not create directories, resolve WSL registrations or
    /// inspect runtime/model payloads. AutoStart is false when settings are absent.
    /// Setup metadata and legacy approvals await owner validation during import;
    /// recording their bytes does not establish ownership or authorize a policy.
    /// </summary>
    public static MigrationInventory Capture(string roamingDirectory, string localDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        var roaming = Path.TrimEndingDirectorySeparator(Path.GetFullPath(roamingDirectory));
        var local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localDirectory));
        using var capture = new CaptureSession();
        try
        {
            CheckPath(roaming, directory: true);
            CheckPath(local, directory: true);
            var settings = capture.ReadJson("roaming", roaming, "settings.json");
            var autoStart = settings is not null && ValidateSettings(settings.Value);
            var registry = capture.ReadJson("roaming", roaming, "gateways.json");
            var gatewayIds = ValidateRegistry(registry);
            var directories = GetGatewayDirectories(roaming);
            // Removing a saved gateway deliberately leaves its identity directory behind.
            // Inventory that state without reinstating the record or selecting a gateway.
            var identityDirectories = gatewayIds.Union(directories, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal).ToArray();
            if (identityDirectories.Length > MaximumGateways)
                throw new InvalidDataException("Too many gateway identity directories.");

            CaptureIdentities(capture, "roaming", roaming, "");
            CaptureIdentities(capture, "local", local, "");
            foreach (var id in identityDirectories)
                CaptureIdentities(capture, "roaming", roaming, Path.Combine("gateways", id));

            var approvalsPath = Path.GetFullPath(ExecApprovalsStore.ResolveFilePath(roaming));
            var approvals = capture.ReadJson(
                "exec-state", Path.GetDirectoryName(approvalsPath)!, Path.GetFileName(approvalsPath));
            if (approvals is not null)
                ValidateApprovals(approvalsPath);
            if (!string.Equals(approvalsPath, Path.Combine(roaming, "exec-approvals.json"), StringComparison.OrdinalIgnoreCase))
                capture.ReadJson("roaming", roaming, "exec-approvals.json");

            var localAiPaths = new LocalAiPaths(local);
            var manifest = capture.ReadJson(
                "local", local, Path.GetRelativePath(local, localAiPaths.ManifestPath), StringComparer.OrdinalIgnoreCase);
            if (manifest is not null)
                ValidateManifest(manifest.Value, localAiPaths);
            // The INI is generated configuration, not a JSON schema or a model payload.
            capture.Read("local", local, Path.GetRelativePath(local, localAiPaths.RouterPresetPath));
            // These are opaque SetupEngine receipts. Phase 1 neither follows their
            // embedded paths nor interprets them as permission to adopt/delete a distro.
            capture.ReadJson("local", local, "setup-state.json");
            capture.ReadJson("roaming", roaming, "setup-state.json");
            capture.ReadJson("local", local, "setup-managed-distro.json");
            capture.ReadJson("local", local, "windows-node-context.json");

            capture.Recheck();
            if (!directories.SequenceEqual(GetGatewayDirectories(roaming), StringComparer.Ordinal) ||
                !string.Equals(approvalsPath, Path.GetFullPath(ExecApprovalsStore.ResolveFilePath(roaming)), StringComparison.Ordinal))
                throw new IOException("Migration sources changed during capture.");

            var entries = capture.Entries.OrderBy(entry => entry.Root, StringComparer.Ordinal)
                .ThenBy(entry => entry.RelativeName, StringComparer.Ordinal).ToImmutableArray();
            var fingerprint = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Roaming = roaming, Local = local, AutoStart = autoStart, Entries = entries })));
            return new(fingerprint, autoStart, roaming, local, entries);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // Parser exceptions can contain a credential-bearing value. Never propagate them.
            throw new InvalidDataException("Migration source JSON or schema is invalid.");
        }
    }

    private static bool ValidateSettings(JsonElement root)
    {
        var data = root.Deserialize<SettingsData>() ?? throw new InvalidDataException("Settings must be an object.");
        return data.AutoStart;
    }

    private static HashSet<string> ValidateRegistry(JsonElement? root)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root is null)
            return ids;
        var gateways = root.Value.GetProperty("gateways");
        if (gateways.ValueKind != JsonValueKind.Array || gateways.GetArrayLength() > MaximumGateways)
            throw new InvalidDataException("Gateway registry must contain a bounded gateway array.");
        foreach (var item in gateways.EnumerateArray())
        {
            var record = item.Deserialize<GatewayRecord>(CamelCaseOptions);
            if (record is null)
                throw new InvalidDataException("Gateway registry record must be an object.");
            ValidateSegment(record.Id);
            if (!ids.Add(record.Id))
                throw new InvalidDataException("Gateway registry contains an invalid or duplicate reference.");
            if (item.TryGetProperty("identityDirName", out var identity) &&
                !string.Equals(identity.GetString(), record.Id, StringComparison.Ordinal))
                throw new InvalidDataException("Gateway identity directory reference does not match its ID.");
        }
        if (root.Value.TryGetProperty("activeId", out var active) && active.ValueKind != JsonValueKind.Null &&
            !ids.Contains(active.GetString() ?? "", StringComparer.Ordinal))
            throw new InvalidDataException("Active gateway reference is missing from the registry.");
        return ids;
    }

    private static string[] GetGatewayDirectories(string roaming)
    {
        var path = Path.Combine(roaming, "gateways");
        if (!CheckPath(path, directory: true))
            return [];
        var names = new List<string>();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (names.Count >= MaximumGateways)
                throw new InvalidDataException("Too many gateway identity directories.");
            CheckPath(child, directory: true);
            var name = Path.GetFileName(child);
            ValidateSegment(name);
            names.Add(name);
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private static void CaptureIdentities(CaptureSession capture, string root, string directory, string relativeDirectory)
    {
        var relativeName = Path.Combine(relativeDirectory, IdentityFileName);
        var identity = capture.ReadJson(root, directory, relativeName);
        if (identity is not null)
        {
            // The source is already size-bounded and locked against writers. Reuse the
            // canonical strict cryptographic validator without Initialize's create path.
            var result = DeviceIdentity.ReadStoredDeviceToken(Path.Combine(directory, relativeDirectory));
            if (result.Status is DeviceTokenReadStatus.Corrupt or DeviceTokenReadStatus.Unreadable)
                throw new InvalidDataException("Device identity is malformed or unreadable.");
        }
        // Historical names have no supported reader in the current application.
        // Preserve their absence, but never guess a key schema for adoption.
        foreach (var legacy in new[] { "device-key.json", "device.json" })
            if (capture.Read(root, directory, Path.Combine(relativeDirectory, legacy)) is not null)
                throw new InvalidDataException("Unsupported legacy device identity requires explicit migration.");
    }

    private static void ValidateApprovals(string path)
    {
        // Resolve against the canonical directory so the owner reads only the bounded,
        // locked file, not an unmigrated legacy policy. No subscription starts a watcher.
        var directory = Path.GetDirectoryName(path)!;
        if (!string.Equals(Path.GetFullPath(ExecApprovalsStore.ResolveFilePath(directory)), path, StringComparison.Ordinal))
            throw new IOException("Exec approvals location changed during capture.");
        using var store = new ExecApprovalsStore(directory, NullLogger.Instance);
        var result = store.GetSnapshotReadOnlyAsync().GetAwaiter().GetResult();
        if (!result.IsSuccess || result.Snapshot is not { Exists: true } snapshot)
            throw new InvalidDataException("Exec approvals schema is invalid or unsupported.");
        if (!string.Equals(Path.GetFullPath(snapshot.Path), path, StringComparison.Ordinal))
            throw new IOException("Exec approvals location changed during capture.");
    }

    private static void ValidateManifest(JsonElement root, LocalAiPaths paths)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        var manifest = root.Deserialize<LocalAiInstallManifest>(options)
            ?? throw new InvalidDataException("Local AI receipt must be an object.");
        try
        {
            _ = new LocalAiManifestStore(paths).ResolveAndValidate(manifest);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Local AI receipt schema or path is invalid.");
        }
    }

    private static void ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.EndsWith('.') || value.EndsWith(' ') || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('\\') || value.Contains('/'))
            throw new InvalidDataException("Source contains an unsafe path segment.");
    }

    private static bool CheckPath(string path, bool directory)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is not null && parent != path)
            CheckPath(parent, directory: true);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Migration sources cannot contain reparse points.");
        if (((attributes & FileAttributes.Directory) != 0) != directory)
            throw new InvalidDataException("Migration source has an unexpected file type.");
        return true;
    }

    private sealed class CaptureSession : IDisposable
    {
        private readonly List<FileStream> _streams = [];
        private readonly List<JsonDocument> _documents = [];
        private int _totalBytes;
        public List<Entry> Entries { get; } = [];

        public byte[]? Read(string root, string directory, string name)
        {
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (!CheckPath(path, directory: false))
            {
                Entries.Add(new(root, name, path, false, null, null));
                return null;
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _streams.Add(stream);
            if (stream.Length > MaximumFileBytes || stream.Length > MaximumTotalBytes - _totalBytes)
                throw new InvalidDataException("Migration source exceeds the inventory size limit.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            _totalBytes += bytes.Length;
            Entries.Add(new(root, name, path, true, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
            return bytes;
        }

        public JsonElement? ReadJson(string root, string directory, string name, StringComparer? propertyComparer = null)
        {
            var bytes = Read(root, directory, name);
            if (bytes is null)
                return null;
            var content = bytes.AsMemory();
            if (content.Span.StartsWith(Encoding.UTF8.Preamble))
                content = content[Encoding.UTF8.Preamble.Length..];
            var document = JsonDocument.Parse(content);
            _documents.Add(document);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Migration source JSON must be an object.");
            RejectDuplicateProperties(document.RootElement, propertyComparer ?? StringComparer.Ordinal);
            return document.RootElement;
        }

        public void Recheck()
        {
            foreach (var entry in Entries)
            {
                if (CheckPath(entry.CanonicalPath, directory: false) != entry.Exists)
                    throw new IOException("Migration sources changed during capture.");
                if (!entry.Exists)
                    continue;
                using var stream = new FileStream(entry.CanonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != entry.Length ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), entry.Sha256, StringComparison.Ordinal))
                    throw new IOException("Migration sources changed during capture.");
            }
        }

        private static void RejectDuplicateProperties(JsonElement element, StringComparer comparer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(comparer);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("Migration source JSON has ambiguous duplicate properties.");
                    RejectDuplicateProperties(property.Value, comparer);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    RejectDuplicateProperties(item, comparer);
            }
        }

        public void Dispose()
        {
            foreach (var document in _documents)
                document.Dispose();
            foreach (var stream in _streams)
                stream.Dispose();
        }
    }
}
