// <summary>
// Canonical, companion-owned layout and persistence for local inference artifacts:
// LocalAiPaths defines contained directory layout with reparse-point/traversal-safe path
// resolution, LocalAiInstallManifest records the acquired runtime/model receipt, and
// LocalAiManifestStore persists it with same-directory atomic replacement. Also holds port
// and gateway-model validation policies shared by setup and runtime launch.
// Usage:
//   var paths = new LocalAiPaths(localDataDirectory);
//   paths.EnsureDirectories();
//   var store = new LocalAiManifestStore(paths);
//   // null means no receipt; corrupt or unsupported receipts throw InvalidDataException.
//   LocalAiResolvedInstall? install = await store.LoadAsync(cancellationToken);
//   await store.SaveAsync(manifest, cancellationToken); // atomic manifest replacement
//   string contained = paths.ResolveContainedPath("models/model.gguf", "modelPath"); // traversal-safe
// </summary>
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.Shared.IO;

namespace OpenClaw.Connection.LocalAi;

/// <summary>Canonical, companion-owned locations for local inference artifacts.</summary>
public sealed class LocalAiPaths
{
    public LocalAiPaths(string localDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        LocalDataDirectory = Path.GetFullPath(localDataDirectory);
        RootDirectory = Path.Combine(LocalDataDirectory, "LocalAI");
        ManifestPath = Path.Combine(RootDirectory, "state.json");
        EnginesDirectory = Path.Combine(RootDirectory, "engines");
        ModelsDirectory = Path.Combine(RootDirectory, "models");
        DownloadsDirectory = Path.Combine(RootDirectory, "downloads");
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        RouterPresetPath = Path.Combine(RootDirectory, "llama-server-models.ini");
        StandardOutputLogPath = Path.Combine(LogsDirectory, "llama-server.stdout.log");
        StandardErrorLogPath = Path.Combine(LogsDirectory, "llama-server.stderr.log");
    }

    public string LocalDataDirectory { get; }
    public string RootDirectory { get; }
    public string ManifestPath { get; }
    public string EnginesDirectory { get; }
    public string ModelsDirectory { get; }
    public string DownloadsDirectory { get; }
    public string StagingDirectory { get; }
    public string LogsDirectory { get; }
    public string RouterPresetPath { get; }
    public string StandardOutputLogPath { get; }
    public string StandardErrorLogPath { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(EnginesDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// Resolves a manifest-owned relative path and rejects traversal or any existing
    /// reparse point between the local AI root and the resolved target.
    /// </summary>
    public string ResolveContainedPath(string relativePath, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"{fieldName} must be a non-empty relative path.");
        if (Path.IsPathFullyQualified(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"{fieldName} must be relative to the local AI data directory.");

        string resolved;
        try
        {
            resolved = Path.GetFullPath(relativePath, RootDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"{fieldName} is not a valid managed path.", ex);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory));
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{fieldName} escapes the local AI data directory.");

        RejectExistingReparsePoints(root, resolved, fieldName);
        return resolved;
    }

    private static void RejectExistingReparsePoints(string root, string resolvedPath, string fieldName)
    {
        RejectIfReparsePoint(root, fieldName);
        var relative = Path.GetRelativePath(root, resolvedPath);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            RejectIfReparsePoint(current, fieldName);
        }
    }

    private static void RejectIfReparsePoint(string path, string fieldName)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{fieldName} contains an existing reparse point.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{fieldName} could not be safely validated.", ex);
        }
    }
}

/// <summary>Immutable source receipt for an acquired runtime or model artifact.</summary>
public sealed record LocalAiAssetReceipt
{
    public required string FileName { get; init; }
    public required string SourceUrl { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record LocalAiInstallManifest
{
    /// <summary>
    /// Legacy app-owned model layout. Kept readable so existing installs and
    /// downgrade recovery continue to use their original weights in place.
    /// </summary>
    public const int CurrentSchemaVersion = 3;
    /// <summary>
    /// Standard Hugging Face hub-cache layout. A schema-4 receipt is selected
    /// only after its cache path is structurally validated; callers that execute
    /// the model must also verify the pinned content before use.
    /// </summary>
    public const int HubCacheReceiptSchemaVersion = 4;
    public const string SupportedEngine = "llama-server";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Engine { get; init; } = SupportedEngine;
    public required string EngineVersion { get; init; }
    public required string Architecture { get; init; }
    /// <summary>
    /// Legacy schema-3 metadata. It remains readable for compatibility but is
    /// not used for qualification and new manifests leave it absent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HardwareProfileId { get; init; }
    public required string RuntimeId { get; init; }
    public required string ModelCatalogId { get; init; }
    public required string SelectedGpuId { get; init; }
    public required string ExecutablePath { get; init; }
    public required ImmutableArray<LocalAiAssetReceipt> RuntimeAssets { get; init; }
    public required string ModelPath { get; init; }
    /// <summary>
    /// Schema-4 receipt for the standard Hugging Face hub cache root.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelCacheRoot { get; init; }
    /// <summary>
    /// Schema-4 receipt for the verified snapshot copy. Schema-3 manifests keep
    /// <see cref="ModelPath"/> active; schema-4 manifests select this path.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CachedModelPath { get; init; }
    public required string ModelId { get; init; }
    public required string ModelAlias { get; init; }
    public required LocalAiAssetReceipt ModelAsset { get; init; }
    /// <summary>
    /// The requested listener port. Zero delegates allocation to llama-server so
    /// the child owns the port continuously from bind through startup.
    /// </summary>
    public int RequestedPort { get; init; }

    /// <summary>
    /// The last endpoint whose listener ownership and health were verified. It is
    /// intentionally absent while an automatic-port runtime has not started yet.
    /// </summary>
    public string? Endpoint { get; init; }
    /// <summary>
    /// The non-Local-AI primary model that was active before setup selected the
    /// managed llama.cpp model. Null means no prior primary model was configured.
    /// </summary>
    public string? GatewayFallbackModel { get; init; }
    public required int ContextLength { get; init; }
    public KvCachePrecision KeyCachePrecision { get; init; } = KvCachePrecision.F16;
    public KvCachePrecision ValueCachePrecision { get; init; } = KvCachePrecision.F16;
    public KvCachePrecision DraftKeyCachePrecision { get; init; } = KvCachePrecision.F16;
    public KvCachePrecision DraftValueCachePrecision { get; init; } = KvCachePrecision.F16;
    public DateTimeOffset InstalledAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record LocalAiResolvedInstall(
    LocalAiInstallManifest Manifest,
    string ExecutablePath,
    string ModelPath,
    Uri? Endpoint);

public enum LocalAiModelMigrationPhase
{
    VerifyingLegacyModel,
    CopyingToCache,
    VerifyingCacheCopy,
}

public sealed record LocalAiModelMigrationProgress(
    LocalAiModelMigrationPhase Phase,
    long CompletedBytes,
    long TotalBytes);

/// <summary>Shared validation for setup, manifests, and runtime launch.</summary>
public static class LocalAiPortPolicy
{
    public const int Automatic = 0;

    public static bool TryValidate(int requestedPort, out string? error)
    {
        error = requestedPort switch
        {
            80 => "Port 80 is reserved and cannot be used for Local AI.",
            < 0 or > 65_535 => "The Local AI port must be zero (automatic) or between 1 and 65535.",
            _ => null,
        };
        return error is null;
    }

    public static void Validate(int requestedPort)
    {
        if (!TryValidate(requestedPort, out string? error))
            throw new InvalidDataException(error);
    }
}

/// <summary>Validation for the non-managed gateway route retained in a manifest.</summary>
public static class LocalAiGatewayModelPolicy
{
    public static void ValidateFallbackModel(string? model)
    {
        if (model is null)
            return;
        int separator = model.IndexOf('/');
        if (model.Length is 0 or > 512 ||
            model.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            separator <= 0 || separator == model.Length - 1 ||
            model.IndexOf('/', separator + 1) >= 0 ||
            model.StartsWith("llamacpp/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The saved gateway fallback model must be a non-Local-AI provider/model identifier.");
        }
    }
}

/// <summary>Persists the installation manifest with same-directory atomic replacement.</summary>
public sealed class LocalAiManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    private readonly LocalAiPaths _paths;
    private readonly Func<string> _cacheRootResolver;
    private readonly Func<CancellationToken, Task> _beforeMigrationCommit;
    private readonly Func<string, CancellationToken, Task> _beforeMigrationPromotion;

    public LocalAiManifestStore(LocalAiPaths paths) =>
        (_paths, _cacheRootResolver, _beforeMigrationCommit, _beforeMigrationPromotion) = (
            paths ?? throw new ArgumentNullException(nameof(paths)),
            HuggingFaceHubCache.ResolveCacheRoot,
            static _ => Task.CompletedTask,
            static (_, _) => Task.CompletedTask);

    internal LocalAiManifestStore(
        LocalAiPaths paths,
        Func<string> cacheRootResolver,
        Func<CancellationToken, Task>? beforeMigrationCommit = null,
        Func<string, CancellationToken, Task>? beforeMigrationPromotion = null) =>
        (_paths, _cacheRootResolver, _beforeMigrationCommit, _beforeMigrationPromotion) = (
            paths ?? throw new ArgumentNullException(nameof(paths)),
            cacheRootResolver ?? throw new ArgumentNullException(nameof(cacheRootResolver)),
            beforeMigrationCommit ?? (static _ => Task.CompletedTask),
            beforeMigrationPromotion ?? (static (_, _) => Task.CompletedTask));

    public async Task<LocalAiResolvedInstall?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ManifestPath))
            return null;

        LocalAiInstallManifest manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        return ResolveAndValidate(manifest);
    }

    /// <summary>
    /// Copies a canonical schema-3 model into the standard Hugging Face cache and
    /// records a schema-4 receipt whose verified cache snapshot becomes active.
    /// Passive manifest loads never invoke this operation.
    /// </summary>
    public async Task<LocalAiResolvedInstall?> MigrateLegacyModelToHubCacheAsync(
        IProgress<LocalAiModelMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ManifestPath))
            return null;

        byte[] originalManifest = await File
            .ReadAllBytesAsync(_paths.ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        LocalAiInstallManifest manifest = DeserializeManifest(originalManifest);
        LocalAiResolvedInstall resolved = ResolveAndValidate(manifest);
        if (manifest.SchemaVersion != LocalAiInstallManifest.CurrentSchemaVersion)
            return resolved;

        LocalAiInstallManifest migrated = await LocalAiManifestMigration
            .MigrateAsync(
                _paths,
                resolved,
                _cacheRootResolver(),
                progress,
                _beforeMigrationPromotion,
                cancellationToken)
            .ConfigureAwait(false);
        if (ReferenceEquals(migrated, manifest))
            return resolved;

        await _beforeMigrationCommit(cancellationToken).ConfigureAwait(false);
        await using FileStream writeLock = await AcquireManifestWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] currentManifest;
        try
        {
            currentManifest = await File
                .ReadAllBytesAsync(_paths.ManifestPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidDataException(
                "The local AI installation manifest changed while its cache migration was in progress.");
        }
        if (!originalManifest.AsSpan().SequenceEqual(currentManifest))
        {
            throw new InvalidDataException(
                "The local AI installation manifest changed while its cache migration was in progress.");
        }

        await SaveWithoutLockAsync(migrated, cancellationToken).ConfigureAwait(false);
        return ResolveAndValidate(migrated);
    }

    private async Task<LocalAiInstallManifest> ReadManifestAsync(
        CancellationToken cancellationToken)
    {
        LocalAiInstallManifest? manifest;
        try
        {
            await using var stream = new FileStream(
                _paths.ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<LocalAiInstallManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The local AI installation manifest is invalid JSON or uses an unsupported format.", ex);
        }

        if (manifest is null)
            throw new InvalidDataException("The local AI installation manifest is empty.");
        return manifest;
    }

    private static LocalAiInstallManifest DeserializeManifest(byte[] content)
    {
        try
        {
            ReadOnlySpan<byte> json = content;
            ReadOnlySpan<byte> preamble = Encoding.UTF8.Preamble;
            if (json.StartsWith(preamble))
                json = json[preamble.Length..];

            return JsonSerializer.Deserialize<LocalAiInstallManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("The local AI installation manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The local AI installation manifest is invalid JSON or uses an unsupported format.",
                ex);
        }
    }

    public async Task SaveAsync(LocalAiInstallManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _ = ResolveAndValidate(manifest);
        await using FileStream writeLock = await AcquireManifestWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        await SaveWithoutLockAsync(manifest, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveWithoutLockAsync(
        LocalAiInstallManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        _ = _paths.ResolveContainedPath(Path.GetFileName(_paths.ManifestPath), nameof(_paths.ManifestPath));

        var temporaryPath = Path.Combine(
            _paths.RootDirectory,
            $".{Path.GetFileName(_paths.ManifestPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _ = _paths.ResolveContainedPath(Path.GetFileName(temporaryPath), nameof(temporaryPath));
            File.Move(temporaryPath, _paths.ManifestPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup must not mask the persistence result.
            }
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await using FileStream writeLock = await AcquireManifestWriteLockAsync(cancellationToken)
            .ConfigureAwait(false);
        File.Delete(_paths.ManifestPath);
    }

    private async Task<FileStream> AcquireManifestWriteLockAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        string lockPath = _paths.ResolveContainedPath(
            $".{Path.GetFileName(_paths.ManifestPath)}.lock",
            "manifestLockPath");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    public LocalAiResolvedInstall ResolveAndValidate(LocalAiInstallManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion is not (
                LocalAiInstallManifest.CurrentSchemaVersion or
                LocalAiInstallManifest.HubCacheReceiptSchemaVersion))
        {
            throw new InvalidDataException($"Unsupported local AI manifest schema version {manifest.SchemaVersion}.");
        }
        if (!string.Equals(manifest.Engine, LocalAiInstallManifest.SupportedEngine, StringComparison.Ordinal))
            throw new InvalidDataException("The local AI manifest engine must be llama-server.");
        if (string.IsNullOrWhiteSpace(manifest.EngineVersion))
            throw new InvalidDataException("The local AI manifest engine version is required.");
        if (manifest.Architecture is not ("x64" or "arm64"))
            throw new InvalidDataException("The local AI manifest architecture must be x64 or arm64.");
        ValidatePlanIdentifier(manifest.RuntimeId, nameof(manifest.RuntimeId));
        ValidatePlanIdentifier(manifest.ModelCatalogId, nameof(manifest.ModelCatalogId));
        if (string.IsNullOrWhiteSpace(manifest.SelectedGpuId) ||
            manifest.SelectedGpuId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidDataException("The local AI manifest selected GPU identifier is invalid.");
        }
        if (string.IsNullOrWhiteSpace(manifest.ModelId))
            throw new InvalidDataException("The local AI manifest model identifier is required.");
        if (string.IsNullOrWhiteSpace(manifest.ModelAlias) ||
            manifest.ModelAlias.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\'))
        {
            throw new InvalidDataException("The local AI manifest model alias must be a non-empty path-safe token.");
        }
        if (manifest.ContextLength <= 0)
            throw new InvalidDataException("The local AI manifest context length must be positive.");
        if (!Enum.IsDefined(manifest.KeyCachePrecision) ||
            !Enum.IsDefined(manifest.ValueCachePrecision) ||
            !Enum.IsDefined(manifest.DraftKeyCachePrecision) ||
            !Enum.IsDefined(manifest.DraftValueCachePrecision))
        {
            throw new InvalidDataException("The local AI manifest KV cache precision is unsupported.");
        }

        if (manifest.RuntimeAssets.IsDefaultOrEmpty)
            throw new InvalidDataException("The local AI manifest must record at least one runtime asset receipt.");

        var runtimeFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtimeAsset in manifest.RuntimeAssets)
        {
            ValidateAssetReceipt(runtimeAsset, nameof(manifest.RuntimeAssets));
            if (!runtimeFileNames.Add(runtimeAsset.FileName))
                throw new InvalidDataException("The local AI manifest runtime asset filenames must be unique.");
        }
        ValidateAssetReceipt(manifest.ModelAsset, nameof(manifest.ModelAsset));
        HuggingFaceModelProvenance provenance = ValidateHuggingFaceModelProvenance(manifest);

        var executable = _paths.ResolveContainedPath(manifest.ExecutablePath, nameof(manifest.ExecutablePath));
        if (!string.Equals(Path.GetFileName(executable), "llama-server.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The managed local AI executable must be llama-server.exe.");

        ValidateHubCacheReceipt(manifest, provenance);
        string legacyModel = _paths.ResolveContainedPath(manifest.ModelPath, nameof(manifest.ModelPath));
        ValidateModelPath(legacyModel, manifest.ModelAsset, "legacy-compatible");
        string model = manifest.SchemaVersion == LocalAiInstallManifest.HubCacheReceiptSchemaVersion
            ? ResolveHubCacheModelPath(manifest)
            : legacyModel;
        ValidateModelPath(model, manifest.ModelAsset, "active");

        LocalAiPortPolicy.Validate(manifest.RequestedPort);
        LocalAiGatewayModelPolicy.ValidateFallbackModel(manifest.GatewayFallbackModel);

        Uri? endpoint = null;
        if (manifest.Endpoint is not null)
        {
            if (!Uri.TryCreate(manifest.Endpoint, UriKind.Absolute, out endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttp ||
                !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
                endpoint.IsDefaultPort ||
                endpoint.Port is <= 0 or > 65535 ||
                endpoint.Port == 80 ||
                !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment) ||
                !string.Equals(endpoint.AbsolutePath, "/v1", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The local AI endpoint must be an HTTP IPv4 loopback /v1 address with an explicit non-reserved port.");
            }

            if (manifest.RequestedPort != LocalAiPortPolicy.Automatic && endpoint.Port != manifest.RequestedPort)
                throw new InvalidDataException("The verified Local AI endpoint does not match its requested fixed port.");
        }

        return new LocalAiResolvedInstall(manifest, executable, model, endpoint);
    }

    private static void ValidateModelPath(
        string path,
        LocalAiAssetReceipt asset,
        string description)
    {
        if (!string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The managed {description} Local AI model must be a GGUF file.");
        }
        if (!string.Equals(Path.GetFileName(path), asset.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The managed {description} model path must match its asset receipt filename.");
        }
    }

    private static void ValidatePlanIdentifier(string? identifier, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            identifier.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new InvalidDataException($"The local AI manifest {fieldName} is invalid.");
        }
    }

    private static void ValidateAssetReceipt(LocalAiAssetReceipt? receipt, string fieldName)
    {
        if (receipt is null)
            throw new InvalidDataException($"{fieldName} is required.");
        if (string.IsNullOrWhiteSpace(receipt.FileName) ||
            !string.Equals(receipt.FileName, Path.GetFileName(receipt.FileName), StringComparison.Ordinal) ||
            receipt.FileName is "." or "..")
        {
            throw new InvalidDataException($"{fieldName}.FileName must be a single file name.");
        }
        if (!Uri.TryCreate(receipt.SourceUrl, UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(source.Host) ||
            !string.IsNullOrEmpty(source.UserInfo) ||
            !string.IsNullOrEmpty(source.Fragment))
        {
            throw new InvalidDataException($"{fieldName}.SourceUrl must be an HTTPS URL without credentials or a fragment.");
        }
        if (receipt.SizeBytes <= 0)
            throw new InvalidDataException($"{fieldName}.SizeBytes must be positive.");
        if (receipt.Sha256 is null ||
            receipt.Sha256.Length != 64 ||
            receipt.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"{fieldName}.Sha256 must be a lowercase SHA-256 digest.");
    }

    private static HuggingFaceModelProvenance ValidateHuggingFaceModelProvenance(
        LocalAiInstallManifest manifest)
    {
        var revisionSeparator = manifest.ModelId.LastIndexOf('@');
        if (revisionSeparator <= 0 || revisionSeparator == manifest.ModelId.Length - 1)
        {
            throw new InvalidDataException(
                "The local AI manifest model identifier must include an immutable Hugging Face revision.");
        }

        var repositoryId = manifest.ModelId[..revisionSeparator];
        var revision = manifest.ModelId[(revisionSeparator + 1)..];
        var repositorySegments = repositoryId.Split('/');
        if (repositorySegments.Length != 2 ||
            repositorySegments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.'))))
        {
            throw new InvalidDataException("The local AI manifest model repository identifier is invalid.");
        }
        if (revision.Length != 40 ||
            revision.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The local AI manifest model revision must be a lowercase 40-character commit digest.");
        }

        var source = new Uri(manifest.ModelAsset.SourceUrl, UriKind.Absolute);
        var expectedPrefix = $"/{repositoryId}/resolve/{revision}/";
        string sourcePath = Uri.UnescapeDataString(source.AbsolutePath);
        if (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            !sourcePath.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            source.Query is not ("" or "?download=true"))
        {
            throw new InvalidDataException(
                "The local AI manifest model source must match its immutable Hugging Face repository, revision, and filename.");
        }

        string relativePath = sourcePath[expectedPrefix.Length..];
        string[] relativeSegments = relativePath.Split('/');
        if (relativeSegments.Any(segment => !WindowsPathSafety.IsSafeSegment(segment)) ||
            !string.Equals(
                relativeSegments[^1],
                manifest.ModelAsset.FileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The local AI manifest model source must match its immutable Hugging Face repository, revision, and filename.");
        }

        return new HuggingFaceModelProvenance(repositoryId, revision, relativePath);
    }

    private static void ValidateHubCacheReceipt(
        LocalAiInstallManifest manifest,
        HuggingFaceModelProvenance provenance)
    {
        if (manifest.SchemaVersion == LocalAiInstallManifest.CurrentSchemaVersion)
        {
            if (manifest.ModelCacheRoot is not null || manifest.CachedModelPath is not null)
            {
                throw new InvalidDataException(
                    "Schema-3 local AI manifests cannot contain a Hugging Face cache migration receipt.");
            }

            return;
        }

        string error = "";
        if (string.IsNullOrWhiteSpace(manifest.ModelCacheRoot) ||
            string.IsNullOrWhiteSpace(manifest.CachedModelPath) ||
            !HuggingFaceHubCache.TryGetSnapshotPaths(
                manifest.ModelCacheRoot,
                provenance.RepositoryId,
                provenance.Revision,
                provenance.RelativePath,
                out string expectedCachedModelPath,
                out _,
                out error) ||
            !string.Equals(
                manifest.CachedModelPath,
                expectedCachedModelPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(error)
                    ? "The local AI manifest Hugging Face cache migration receipt is invalid."
                    : error);
        }
    }

    private static string ResolveHubCacheModelPath(LocalAiInstallManifest manifest) =>
        WindowsPathSafety.NormalizePath(manifest.CachedModelPath!);

    internal sealed record HuggingFaceModelProvenance(
        string RepositoryId,
        string Revision,
        string RelativePath);
}
