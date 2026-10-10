// <summary>
// Builds the deterministic launch plan for the managed llama-server router: validates the
// qualified install receipt against the runtime/model catalogs, emits the fixed loopback
// argument list, environment (CUDA device pinning), and generates the lazy-load model preset
// consumed by the router at startup.
// Usage:
//   var plan = LlamaServerRouterConfiguration.Build(paths, install);
//   // plan.Arguments -> fixed loopback argv; plan.Environment -> CUDA device pinning;
//   // plan.PresetPath / plan.PresetContent -> write PresetContent to PresetPath before launch;
//   // plan.ModelAlias -> the model id the router exposes.
// </summary>
using OpenClaw.Shared.Inference.Catalog;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Connection.LocalAi;

/// <summary>A deterministic lazy-load router configuration for a qualified local inference install.</summary>
public sealed record LlamaServerRouterLaunchPlan(
    ImmutableArray<string> Arguments,
    ImmutableDictionary<string, string> Environment,
    string PresetPath,
    string PresetContent,
    string ModelAlias);

public static class LlamaServerRouterConfiguration
{
    public static LlamaServerRouterLaunchPlan Build(
        LocalAiPaths paths,
        LocalAiResolvedInstall install,
        int? listenPort = null) =>
        BuildCore(paths, install, install.ModelPath, verifiedDraftModelPath: null, listenPort);

    /// <param name="verifiedDraftModelPath">
    /// The draft checkpoint's handle-resolved physical path, from the same verification
    /// that opened it. Passing the persisted snapshot path instead would let a
    /// snapshot-link replacement change the file llama-server finally opens, which is
    /// exactly what resolving the primary model through its own handle prevents.
    /// </param>
    internal static LlamaServerRouterLaunchPlan BuildForVerifiedRuntime(
        LocalAiPaths paths,
        LocalAiResolvedInstall install,
        string verifiedModelPath,
        string? verifiedDraftModelPath,
        int? listenPort = null) =>
        BuildCore(paths, install, verifiedModelPath, verifiedDraftModelPath, listenPort);

    private static LlamaServerRouterLaunchPlan BuildCore(
        LocalAiPaths paths,
        LocalAiResolvedInstall install,
        string modelPath,
        string? verifiedDraftModelPath,
        int? listenPort)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        LocalAiInstallManifest manifest = install.Manifest;
        int port = listenPort ?? manifest.RequestedPort;
        LocalAiPortPolicy.Validate(port);
        // FindInstalled, not Variants: an installation recorded before the last
        // runtime bump must keep launching until setup upgrades it, instead of being
        // stranded the moment the catalog moves to a newer pinned release.
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.FindInstalled(manifest.RuntimeId)
            ?? throw new InvalidDataException("The managed llama-server runtime is no longer qualified.");
        LocalModelInfo model = LocalModelCatalog.FindInstalled(manifest.ModelCatalogId)
            ?? throw new InvalidDataException("The managed local AI model is no longer qualified.");

        LocalInferenceRunProfile profile = ResolveQualifiedReceipt(manifest, runtime, model);
        string? draftModelPath = ResolveDraftModelPath(manifest, model, verifiedDraftModelPath);

        string presetPath = paths.ResolveContainedPath(
            Path.GetRelativePath(paths.RootDirectory, paths.RouterPresetPath),
            nameof(paths.RouterPresetPath));
        var arguments = ImmutableArray.Create(
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--models-preset", presetPath,
            "--models-max", "1",
            "--models-autoload",
            "--no-webui",
            "--metrics",
            "--offline",
            "--cors-origins", "localhost",
            "--log-verbosity", "4",
            "--no-log-prefix",
            "--no-log-timestamps");

        return new LlamaServerRouterLaunchPlan(
            arguments,
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("CUDA_VISIBLE_DEVICES", manifest.SelectedGpuId),
            presetPath,
            BuildPreset(model, profile, modelPath, draftModelPath),
            model.Id);
    }

    private static LocalInferenceRunProfile ResolveQualifiedReceipt(
        LocalAiInstallManifest manifest,
        LlamaRuntimeVariant runtime,
        LocalModelInfo model)
    {
        ValidateArtifactReceipts(manifest, runtime, model);
        return LocalModelCatalog.FindProfile(
            model,
            manifest.ContextLength,
            manifest.KeyCachePrecision,
            manifest.ValueCachePrecision,
            manifest.DraftKeyCachePrecision,
            manifest.DraftValueCachePrecision)
            ?? throw new InvalidDataException(
                "The managed local AI context and KV cache receipt do not match a qualified catalog profile.");
    }

    internal static void ValidateArtifactReceipts(
        LocalAiInstallManifest manifest,
        LlamaRuntimeVariant runtime,
        LocalModelInfo model)
    {
        Architecture expectedArchitecture = manifest.Architecture switch
        {
            "x64" => Architecture.X64,
            "arm64" => Architecture.Arm64,
            _ => throw new InvalidDataException("The managed local AI architecture is invalid."),
        };
        if (runtime.Architecture != expectedArchitecture)
        {
            throw new InvalidDataException("The managed local AI architecture and runtime receipt do not match.");
        }
        if (!string.Equals(manifest.EngineVersion, runtime.ReleaseTag, StringComparison.Ordinal) ||
            !string.Equals(manifest.ModelAlias, model.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The managed local AI model recipe receipt does not match the qualified catalog.");
        }

        if (manifest.RuntimeAssets.Length != runtime.Artifacts.Count ||
            runtime.Artifacts.Any(artifact => !manifest.RuntimeAssets.Any(receipt =>
                string.Equals(receipt.FileName, Path.GetFileName(artifact.RelativePath), StringComparison.Ordinal) &&
                string.Equals(receipt.SourceUrl, artifact.DownloadUri.AbsoluteUri, StringComparison.Ordinal) &&
                receipt.SizeBytes == artifact.SizeBytes &&
                string.Equals(receipt.Sha256, artifact.Sha256.Value, StringComparison.Ordinal))))
        {
            throw new InvalidDataException("The managed llama-server artifact receipts do not match the qualified catalog.");
        }

        if (model.Weights.Source is not HuggingFaceRevisionSource source ||
            !string.Equals(manifest.ModelId, $"{source.RepositoryId}@{source.RevisionSha}", StringComparison.Ordinal) ||
            !string.Equals(manifest.ModelAsset.FileName, Path.GetFileName(model.Weights.RelativePath), StringComparison.Ordinal) ||
            manifest.ModelAsset.SizeBytes != model.Weights.SizeBytes ||
            !string.Equals(manifest.ModelAsset.Sha256, model.Weights.Sha256.Value, StringComparison.Ordinal) ||
            !string.Equals(manifest.ModelAsset.SourceUrl, model.Weights.DownloadUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The managed model artifact receipt does not match the qualified catalog.");
        }

        ImmutableArray<PinnedArtifact> expectedAdditionalArtifacts = LocalModelCatalog.AdditionalArtifacts(model);
        if (manifest.AdditionalModelAssetsOrEmpty.Length != expectedAdditionalArtifacts.Length ||
            manifest.AdditionalModelPathsOrEmpty.Length != expectedAdditionalArtifacts.Length)
        {
            throw new InvalidDataException(
                "The managed additional model asset receipts do not match the qualified catalog.");
        }
        for (int i = 0; i < expectedAdditionalArtifacts.Length; i++)
        {
            PinnedArtifact artifact = expectedAdditionalArtifacts[i];
            LocalAiAssetReceipt receipt = manifest.AdditionalModelAssetsOrEmpty[i];
            if (!string.Equals(receipt.FileName, Path.GetFileName(artifact.RelativePath), StringComparison.Ordinal) ||
                receipt.SizeBytes != artifact.SizeBytes ||
                !string.Equals(receipt.Sha256, artifact.Sha256.Value, StringComparison.Ordinal) ||
                !string.Equals(receipt.SourceUrl, artifact.DownloadUri.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The managed additional model asset receipts do not match the qualified catalog.");
            }
        }
    }

    /// <summary>
    /// The DFlash draft checkpoint's path for the preset. Prefers the handle-resolved
    /// physical path supplied by the caller that verified and still holds the file, so a
    /// snapshot-link replacement cannot change the identity llama-server opens. Falls
    /// back to the persisted receipt path only for callers that do not verify first
    /// (<see cref="Build"/>, used for inspection rather than launch). Null for recipes
    /// with no separate draft checkpoint. Callers must validate the manifest via
    /// <see cref="ValidateArtifactReceipts"/> first, which guarantees
    /// <c>AdditionalModelPaths</c> has one entry per catalog-pinned artifact.
    /// </summary>
    private static string? ResolveDraftModelPath(
        LocalAiInstallManifest manifest,
        LocalModelInfo model,
        string? verifiedDraftModelPath)
    {
        if (model.Recipe.DraftWeights is null)
            return null;
        return string.IsNullOrWhiteSpace(verifiedDraftModelPath)
            ? manifest.AdditionalModelPathsOrEmpty[^1]
            : verifiedDraftModelPath;
    }

    private static string BuildPreset(
        LocalModelInfo model,
        LocalInferenceRunProfile profile,
        string modelPath,
        string? draftModelPath)
    {
        if (modelPath.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("The managed model path cannot be represented safely in a llama-server preset.");
        if (draftModelPath is not null && draftModelPath.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("The managed draft model path cannot be represented safely in a llama-server preset.");

        LocalModelRunRecipe recipe = model.Recipe;
        if (recipe.SpeculativeDecoding == SpeculativeDecodingMode.DraftDFlash && draftModelPath is null)
            throw new InvalidDataException("Draft-flash decoding requires a resolved draft model path.");
        ModelSamplingPreset sampling = recipe.Sampling;
        var preset = new StringBuilder();
        preset.AppendLine("version = 1");
        preset.AppendLine();
        preset.Append('[').Append(model.Id).AppendLine("]");
        preset.Append("model = ").AppendLine(modelPath);
        preset.AppendLine("load-on-startup = false");
        preset.Append("ctx-size = ").AppendLine(Invariant(profile.ContextTokens));
        preset.Append("n-predict = ").AppendLine(Invariant(LocalAiGatewayProviderDefinition.MaximumOutputTokens));
        preset.Append("parallel = ").AppendLine(Invariant(recipe.ParallelRequests));
        preset.Append("cache-type-k = ").AppendLine(LocalModelCatalog.ToLlamaServerCacheType(profile.KeyCachePrecision));
        preset.Append("cache-type-v = ").AppendLine(LocalModelCatalog.ToLlamaServerCacheType(profile.ValueCachePrecision));
        preset.Append("cache-type-k-draft = ").AppendLine(LocalModelCatalog.ToLlamaServerCacheType(profile.DraftKeyCachePrecision));
        preset.Append("cache-type-v-draft = ").AppendLine(LocalModelCatalog.ToLlamaServerCacheType(profile.DraftValueCachePrecision));
        preset.Append("batch-size = ").AppendLine(Invariant(recipe.BatchTokens));
        preset.Append("ubatch-size = ").AppendLine(Invariant(recipe.MicroBatchTokens));
        preset.AppendLine("flash-attn = on");
        preset.AppendLine("gpu-layers = all");
        preset.AppendLine("split-mode = none");
        preset.AppendLine("main-gpu = 0");
        preset.AppendLine("fit = off");
        preset.AppendLine("load-mode = dio");
        switch (recipe.SpeculativeDecoding)
        {
            case SpeculativeDecodingMode.DraftMtp:
                preset.AppendLine("spec-type = draft-mtp");
                preset.Append("spec-draft-n-max = ").AppendLine(Invariant(recipe.SpeculativeDraftMaxTokens));
                preset.AppendLine("spec-draft-backend-sampling = true");
                break;
            case SpeculativeDecodingMode.DraftDFlash:
                preset.AppendLine("spec-type = draft-dflash");
                preset.Append("spec-draft-model = ").AppendLine(draftModelPath);
                preset.Append("spec-draft-n-max = ").AppendLine(Invariant(recipe.SpeculativeDraftMaxTokens));
                preset.AppendLine("spec-draft-backend-sampling = true");
                break;
            case SpeculativeDecodingMode.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(recipe.SpeculativeDecoding));
        }
        preset.Append("temperature = ").AppendLine(Invariant(sampling.Temperature));
        preset.Append("top-k = ").AppendLine(Invariant(sampling.TopK));
        preset.Append("top-p = ").AppendLine(Invariant(sampling.TopP));
        preset.Append("min-p = ").AppendLine(Invariant(sampling.MinP));
        preset.Append("repeat-penalty = ").AppendLine(Invariant(sampling.RepetitionPenalty));
        preset.Append("presence-penalty = ").AppendLine(Invariant(sampling.PresencePenalty));
        preset.AppendLine("jinja = true");
        preset.AppendLine("reasoning = on");
        preset.AppendLine("reasoning-format = deepseek");
        preset.AppendLine("context-shift = true");
        return preset.ToString();
    }

    private static string Invariant<T>(T value) where T : IFormattable =>
        value.ToString(null, CultureInfo.InvariantCulture);
}
