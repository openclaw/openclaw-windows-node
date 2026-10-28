using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>
/// The verified identity and GGUF-derived capacity geometry of a user-supplied
/// Hugging Face model. Persisted in the installation receipt so launch, gateway
/// publication, and eligibility can rebuild the same pinned model without the catalog.
/// </summary>
public sealed record LocalCustomModelRecipe(
    string RepositoryId,
    string RevisionSha,
    string FilePath,
    long SizeBytes,
    string Sha256,
    string DisplayName,
    string Architecture,
    int BlockCount,
    int KeyValueHeadCount,
    int KeyValueHeadDimension,
    int TrainedContextTokens);

/// <summary>
/// Maps a custom recipe onto the same <see cref="LocalModelInfo"/> and profile shapes the
/// catalog uses, so selection, memory math, receipts, and launch share one code path.
/// Custom models always run on the generic llama.cpp runtime with no speculative draft.
/// </summary>
public static partial class LocalCustomModel
{
    public const string IdPrefix = "hf-";
    private const string InvalidReceipt = "The custom Local AI model receipt is invalid.";

    public static bool IsCustomId(string? id) => id?.StartsWith(IdPrefix, StringComparison.Ordinal) == true;

    public static string CreateId(string repositoryId, string revisionSha, string filePath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{repositoryId}@{revisionSha}/{filePath}"));
        return IdPrefix + Convert.ToHexStringLower(hash)[..16];
    }

    public static string CreateId(LocalCustomModelRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return CreateId(recipe.RepositoryId, recipe.RevisionSha, recipe.FilePath);
    }

    public static void Validate(LocalCustomModelRecipe recipe)
    {
        if (recipe is null)
            throw new InvalidDataException(InvalidReceipt);
        try
        {
            _ = new HuggingFaceRevisionSource(recipe.RepositoryId, recipe.RevisionSha);
            _ = PinnedArtifactValidation.EscapeRelativePath(recipe.FilePath);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(InvalidReceipt, ex);
        }

        if (!recipe.FilePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
            recipe.SizeBytes <= 0 ||
            !PinnedArtifactValidation.IsLowerHex(recipe.Sha256, 64) ||
            string.IsNullOrWhiteSpace(recipe.DisplayName) ||
            recipe.DisplayName.Length > 200 ||
            recipe.DisplayName.Any(char.IsControl) ||
            string.IsNullOrEmpty(recipe.Architecture) ||
            recipe.Architecture.Length > 64 ||
            recipe.Architecture.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-')) ||
            recipe.BlockCount is < 1 or > 1024 ||
            recipe.KeyValueHeadCount is < 1 or > 1024 ||
            recipe.KeyValueHeadDimension is < 1 or > 4096 ||
            recipe.TrainedContextTokens is < 1 or > 1_048_576)
        {
            throw new InvalidDataException(InvalidReceipt);
        }
    }

    public static LocalModelInfo ToModelInfo(LocalCustomModelRecipe recipe)
    {
        Validate(recipe);
        string id = CreateId(recipe);
        return new LocalModelInfo(
            Id: id,
            DisplayName: recipe.DisplayName,
            Family: recipe.Architecture,
            Quantization: QuantizationFromFileName(recipe.FilePath),
            Weights: new PinnedArtifact(
                id,
                ArtifactRole.ModelWeights,
                new HuggingFaceRevisionSource(recipe.RepositoryId, recipe.RevisionSha),
                recipe.FilePath,
                recipe.SizeBytes,
                new Sha256Digest(recipe.Sha256)),
            Recipe: new LocalModelRunRecipe(
                batchTokens: 2048,
                microBatchTokens: 512,
                parallelRequests: 1,
                fullAttentionLayerCount: recipe.BlockCount,
                keyValueHeadCount: recipe.KeyValueHeadCount,
                keyValueHeadDimension: recipe.KeyValueHeadDimension,
                flashAttention: true,
                offloadAllLayers: true,
                speculativeDecoding: SpeculativeDecodingMode.None,
                speculativeDraftMaxTokens: 0,
                sampling: new ModelSamplingPreset(0.7, 20, 0.95, 0.0, 1.0, 0.0)),
            IsDefault: false,
            IsExplicitAlternative: false,
            SupportsVision: false,
            RecommendationPriority: 0,
            RuntimeFamily: "llama.cpp",
            MinimumSystemMemoryBytes: 0,
            MinimumFreeDiskBytes: recipe.SizeBytes,
            CustomRecipe: recipe);
    }

    /// <summary>Catalog context tiers the model was trained for, largest first, F16 before Q8_0.</summary>
    public static IReadOnlyList<LocalInferenceRunProfile> GetProfiles(LocalCustomModelRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        (int Tokens, long Workspace)[] tiers =
        [
            (LocalModelCatalog.NativeContextTokens, LocalModelCatalog.RuntimeWorkspaceReserveBytes),
            (LocalModelCatalog.IntermediateContextTokens, LocalModelCatalog.IntermediateContextWorkspaceReserveBytes),
            (LocalModelCatalog.ReducedContextTokens, LocalModelCatalog.ReducedContextWorkspaceReserveBytes),
            (LocalModelCatalog.MinimumContextTokens, LocalModelCatalog.MinimumContextWorkspaceReserveBytes),
        ];
        var profiles = new List<LocalInferenceRunProfile>(tiers.Length * 2);
        foreach ((int tokens, long workspace) in tiers)
        {
            if (tokens > recipe.TrainedContextTokens)
                continue;
            profiles.Add(Profile(tokens, KvCachePrecision.F16, workspace));
            profiles.Add(Profile(tokens, KvCachePrecision.Q8_0, workspace));
        }
        if (profiles.Count == 0)
        {
            int context = Math.Max(1024, recipe.TrainedContextTokens / 1024 * 1024);
            profiles.Add(Profile(context, KvCachePrecision.F16, LocalModelCatalog.MinimumContextWorkspaceReserveBytes));
            profiles.Add(Profile(context, KvCachePrecision.Q8_0, LocalModelCatalog.MinimumContextWorkspaceReserveBytes));
        }
        return profiles.AsReadOnly();
    }

    internal static string QuantizationFromFileName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        MatchCollection matches = QuantizationPattern().Matches(name);
        return matches.Count == 0 ? "custom" : matches[^1].Value.ToUpperInvariant();
    }

    private static LocalInferenceRunProfile Profile(int tokens, KvCachePrecision precision, long workspace) =>
        new($"ctx-{tokens}-{LocalModelCatalog.ToLlamaServerCacheType(precision)}",
            tokens, precision, precision, precision, precision, workspace);

    [GeneratedRegex(@"(?i)(UD-)?(IQ\d_[A-Z0-9_]+|Q\d(_[A-Z0-9]+)*|BF16|F16|F32)")]
    private static partial Regex QuantizationPattern();
}
