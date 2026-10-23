// Frozen pre-PR serialization contract from src/OpenClaw.Connection/LocalAi/LocalAiManifest.cs
// at 127016568ef2af52a28922bcc3ce1e2af42a0218. DTOs, JSON options, parser and validation
// are frozen; only type names, visibility, comments and the byte-load adapter are adapted.
// Tests exercise real new-writer state.json bytes, not a projected/re-serialized DTO.
// This proves strict JSON format compatibility, not old-runtime launch or Gateway takeover.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.Shared.IO;
using OpenClaw.Connection.LocalAi;

namespace OpenClaw.Connection.Tests.FrozenPreReplacement;

public sealed record FrozenAssetReceipt
{
    public required string FileName { get; init; }
    public required string SourceUrl { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record FrozenInstallManifest
{
    public const int CurrentSchemaVersion = 3;
    public const int HubCacheReceiptSchemaVersion = 4;
    public const int AdditionalAssetsSchemaVersion = 5;
    public const string SupportedEngine = "llama-server";

    [JsonIgnore]
    public bool UsesHubCache => SchemaVersion is HubCacheReceiptSchemaVersion or AdditionalAssetsSchemaVersion;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Engine { get; init; } = SupportedEngine;
    public required string EngineVersion { get; init; }
    public required string Architecture { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HardwareProfileId { get; init; }
    public required string RuntimeId { get; init; }
    public required string ModelCatalogId { get; init; }
    public required string SelectedGpuId { get; init; }
    public required string ExecutablePath { get; init; }
    public required ImmutableArray<FrozenAssetReceipt> RuntimeAssets { get; init; }
    public required string ModelPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelCacheRoot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CachedModelPath { get; init; }
    public required string ModelId { get; init; }
    public required string ModelAlias { get; init; }
    public required FrozenAssetReceipt ModelAsset { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<FrozenAssetReceipt> AdditionalModelAssets { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<string> AdditionalModelPaths { get; init; }
    [JsonIgnore]
    public ImmutableArray<FrozenAssetReceipt> AdditionalModelAssetsOrEmpty =>
        AdditionalModelAssets.IsDefault ? ImmutableArray<FrozenAssetReceipt>.Empty : AdditionalModelAssets;
    [JsonIgnore]
    public ImmutableArray<string> AdditionalModelPathsOrEmpty =>
        AdditionalModelPaths.IsDefault ? ImmutableArray<string>.Empty : AdditionalModelPaths;
    public int RequestedPort { get; init; }

    public string? Endpoint { get; init; }
    public string? GatewayFallbackModel { get; init; }
    public required int ContextLength { get; init; }
    public KvCachePrecision KeyCachePrecision { get; init; } = KvCachePrecision.F16;
    public KvCachePrecision ValueCachePrecision { get; init; } = KvCachePrecision.F16;
    public KvCachePrecision DraftKeyCachePrecision { get; init; } = KvCachePrecision.F16;
    public KvCachePrecision DraftValueCachePrecision { get; init; } = KvCachePrecision.F16;
    public DateTimeOffset InstalledAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record FrozenResolvedInstall(FrozenInstallManifest Manifest, string ExecutablePath, string ModelPath, Uri? Endpoint);

internal sealed class FrozenManifestReader
{
    private readonly LocalAiPaths _paths;
    internal FrozenManifestReader(LocalAiPaths paths) => _paths = paths;
    internal FrozenResolvedInstall Load(byte[] content) => ResolveAndValidate(Read(content));

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

    internal static FrozenInstallManifest Read(byte[] content)
    {
        try
        {
            ReadOnlySpan<byte> json = content;
            ReadOnlySpan<byte> preamble = Encoding.UTF8.Preamble;
            if (json.StartsWith(preamble))
                json = json[preamble.Length..];

            return JsonSerializer.Deserialize<FrozenInstallManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("The local AI installation manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The local AI installation manifest is invalid JSON or uses an unsupported format.",
                ex);
        }
    }


    public FrozenResolvedInstall ResolveAndValidate(FrozenInstallManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion is not (
                FrozenInstallManifest.CurrentSchemaVersion or
                FrozenInstallManifest.HubCacheReceiptSchemaVersion or
                FrozenInstallManifest.AdditionalAssetsSchemaVersion))
        {
            throw new InvalidDataException($"Unsupported local AI manifest schema version {manifest.SchemaVersion}.");
        }
        if (!string.Equals(manifest.Engine, FrozenInstallManifest.SupportedEngine, StringComparison.Ordinal))
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
        string model = manifest.UsesHubCache
            ? ResolveHubCacheModelPath(manifest)
            : legacyModel;
        ValidateModelPath(model, manifest.ModelAsset, "active");
        ValidateAdditionalAssets(manifest);

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

        return new FrozenResolvedInstall(manifest, executable, model, endpoint);
    }

    private static void ValidateModelPath(
        string path,
        FrozenAssetReceipt asset,
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

    private static void ValidateAssetReceipt(FrozenAssetReceipt? receipt, string fieldName)
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
        FrozenInstallManifest manifest)
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
        FrozenInstallManifest manifest,
        HuggingFaceModelProvenance provenance)
    {
        if (manifest.SchemaVersion == FrozenInstallManifest.CurrentSchemaVersion)
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

    private static string ResolveHubCacheModelPath(FrozenInstallManifest manifest) =>
        WindowsPathSafety.NormalizePath(manifest.CachedModelPath!);

    /// <summary>
    /// Validates schema-5 additional assets (a DFlash draft checkpoint). Each
    /// receipt derives its own repository and
    /// revision from its own <c>SourceUrl</c> -- unlike the primary
    /// <see cref="FrozenInstallManifest.ModelAsset"/>, additional assets are
    /// not required to share the primary model's repository (the DFlash
    /// draft checkpoint is pinned from a different one).
    /// </summary>
    private static void ValidateAdditionalAssets(FrozenInstallManifest manifest)
    {
        if (manifest.SchemaVersion != FrozenInstallManifest.AdditionalAssetsSchemaVersion)
        {
            if (!manifest.AdditionalModelAssets.IsDefaultOrEmpty || !manifest.AdditionalModelPaths.IsDefaultOrEmpty)
            {
                throw new InvalidDataException(
                    "Only schema-5 local AI manifests may record additional model assets.");
            }
            return;
        }

        if (manifest.AdditionalModelAssets.IsDefaultOrEmpty ||
            manifest.AdditionalModelAssetsOrEmpty.Length != manifest.AdditionalModelPathsOrEmpty.Length)
        {
            throw new InvalidDataException(
                "A schema-5 local AI manifest must record a matching additional-asset receipt and cache path pair.");
        }

        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifest.ModelAsset.FileName };
        for (int i = 0; i < manifest.AdditionalModelAssetsOrEmpty.Length; i++)
        {
            FrozenAssetReceipt receipt = manifest.AdditionalModelAssetsOrEmpty[i];
            ValidateAssetReceipt(receipt, $"{nameof(manifest.AdditionalModelAssets)}[{i}]");
            if (!seenFileNames.Add(receipt.FileName))
                throw new InvalidDataException("The local AI manifest additional asset filenames must be unique.");

            HuggingFaceModelProvenance provenance = ParseHuggingFaceProvenance(receipt);
            if (!HuggingFaceHubCache.TryGetSnapshotPaths(
                    manifest.ModelCacheRoot!,
                    provenance.RepositoryId,
                    provenance.Revision,
                    provenance.RelativePath,
                    out string expectedPath,
                    out _,
                    out string error) ||
                !string.Equals(manifest.AdditionalModelPathsOrEmpty[i], expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    string.IsNullOrWhiteSpace(error)
                        ? "The local AI manifest additional asset cache receipt is invalid."
                        : error);
            }
        }
    }

    private static HuggingFaceModelProvenance ParseHuggingFaceProvenance(FrozenAssetReceipt receipt)
    {
        var source = new Uri(receipt.SourceUrl, UriKind.Absolute);
        if (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            source.Query is not ("" or "?download=true"))
        {
            throw new InvalidDataException("The local AI manifest asset source must be an immutable Hugging Face resolve URL.");
        }

        string[] pathSegments = Uri.UnescapeDataString(source.AbsolutePath).Split(
            '/', StringSplitOptions.RemoveEmptyEntries);
        int resolveIndex = Array.IndexOf(pathSegments, "resolve");
        if (resolveIndex != 2 || pathSegments.Length < resolveIndex + 3)
        {
            throw new InvalidDataException("The local AI manifest asset source must be an immutable Hugging Face resolve URL.");
        }

        string repositoryId = $"{pathSegments[0]}/{pathSegments[1]}";
        string revision = pathSegments[resolveIndex + 1];
        if (revision.Length != 40 ||
            revision.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "The local AI manifest asset revision must be a lowercase 40-character commit digest.");
        }

        string[] relativeSegments = pathSegments[(resolveIndex + 2)..];
        string relativePath = string.Join('/', relativeSegments);
        if (relativeSegments.Any(segment => !WindowsPathSafety.IsSafeSegment(segment)) ||
            !string.Equals(relativeSegments[^1], receipt.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The local AI manifest asset source must match its own repository, revision, and filename.");
        }

        return new HuggingFaceModelProvenance(repositoryId, revision, relativePath);
    }

    internal sealed record HuggingFaceModelProvenance(
        string RepositoryId,
        string Revision,
        string RelativePath);
}
