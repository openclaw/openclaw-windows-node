// <summary>
// Loads user-authored llama-server preset overrides from LocalAI\recipe-overrides.ini so the
// generated recipe can be tuned without a rebuild. The file is optional; every line in every
// section is validated so mistakes surface on the next Local AI start or restart. Keys in
// [*] apply to every model; keys in [<model-id>] apply to that model and win over [*]. An
// empty value removes the generated key.
// Usage:
//   ImmutableArray<LocalAiRecipeOverride> overrides = LocalAiRecipeOverrides.Load(paths, model.Id);
//   // empty when the file is absent; InvalidDataException("recipe-overrides.ini line N: ...") when invalid.
// </summary>
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Connection.LocalAi;

/// <summary>One user-authored change to a generated llama-server preset key. A null Value removes the generated key.</summary>
public readonly record struct LocalAiRecipeOverride(string Key, string? Value);

public static partial class LocalAiRecipeOverrides
{
    public const string FileName = "recipe-overrides.ini";
    public const string AllModelsSection = "*";
    internal const int MaximumFileBytes = 64 * 1024;

    /// <summary>
    /// The only keys the override file may set, as canonical long llama-server option
    /// names from the pinned runtimes' <c>common/arg.cpp</c> (b11026 and b11320 have
    /// identical alias tables). This is an allowlist, not a deny-list: llama.cpp
    /// registers many aliases beyond the two-character ones (<c>-ag</c> is
    /// <c>--agent</c>, <c>-hft</c> is <c>--hf-token</c>), and its preset parser
    /// (<c>common/preset.cpp</c>) resolves every alias, so name-based deny rules can
    /// be bypassed. Only tuning keys that are safe for an authenticated local user
    /// are permitted; a leading <c>no-</c> is accepted only for options llama.cpp
    /// defines a negated form for. Model identity, paths, network endpoints,
    /// credentials, agent/MCP/tool keys, and the capacity-fit keys
    /// (<c>ctx-size</c>, <c>fit</c>, <c>parallel</c>, <c>n-predict</c>, unified KV)
    /// are excluded on purpose: those are owned by the pinned receipt, the launch
    /// arguments, or <see cref="LocalAiGatewayProviderDefinition"/> publishing.
    /// </summary>
    private static readonly FrozenSet<string> AllowedKeys = FrozenSet.ToFrozenSet(
        [
            // CPU scheduling
            "threads", "threads-batch", "cpu-mask", "cpu-range", "cpu-strict", "prio", "poll",
            // Batch and prompt processing
            "batch-size", "ubatch-size",
            // Attention, KV cache, and memory shape
            "flash-attn", "swa-full", "cache-type-k", "cache-type-v",
            "cache-reuse", "cache-idle-slots", "context-shift", "kv-offload", "repack",
            "cont-batching", "cache-prompt", "defrag-thold",
            // Speculative decoding tuning (model identity stays pinned)
            "spec-type", "spec-draft-n-max", "spec-draft-backend-sampling",
            // Sampling
            "temp", "top-k", "top-p", "min-p", "typical", "xtc-threshold", "xtc-probability",
            "repeat-penalty", "presence-penalty", "frequency-penalty",
            "dry-multiplier", "dry-base", "dry-allowed-length", "dry-penalty-last-n",
            "samplers", "dynatemp-range",
            // Reasoning output
            "reasoning", "reasoning-budget", "reasoning-format", "reasoning-preserve",
            // RoPE / YaRN
            "rope-scaling", "rope-scale", "rope-freq-base", "rope-freq-scale",
            "yarn-orig-ctx", "yarn-ext-factor", "yarn-attn-factor", "yarn-beta-fast", "yarn-beta-slow",
            // GPU placement
            "gpu-layers", "main-gpu", "tensor-split", "split-mode",
        ],
        StringComparer.Ordinal);

    /// <summary>Options llama.cpp defines a negated <c>no-</c> form for, so a leading
    /// <c>no-</c> is accepted for exactly these and for nothing else.</summary>
    private static readonly FrozenSet<string> NegatableKeys = FrozenSet.ToFrozenSet(
        [
            "cache-idle-slots", "context-shift", "kv-offload", "repack", "cont-batching",
            "cache-prompt", "flash-attn", "swa-full", "cache-reuse", "reasoning",
            "reasoning-preserve", "spec-draft-backend-sampling",
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Reads the override file for <paramref name="modelId"/>. Empty when the file does not
    /// exist. Throws <see cref="InvalidDataException"/> for any invalid content; I/O and access
    /// failures propagate unchanged.
    /// </summary>
    public static ImmutableArray<LocalAiRecipeOverride> Load(LocalAiPaths paths, string modelId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        string path = paths.ResolveContainedPath(
            Path.GetRelativePath(paths.RootDirectory, paths.RecipeOverridesPath),
            nameof(paths.RecipeOverridesPath));
        var file = new FileInfo(path);
        if (!file.Exists)
            return ImmutableArray<LocalAiRecipeOverride>.Empty;
        if (file.Length > MaximumFileBytes)
            throw new InvalidDataException($"{FileName} is larger than 64 KiB.");

        return Parse(File.ReadAllText(path, Encoding.UTF8), modelId);
    }

    internal static ImmutableArray<LocalAiRecipeOverride> Parse(string content, string modelId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var sections = new Dictionary<string, List<LocalAiRecipeOverride>>(StringComparer.OrdinalIgnoreCase);
        List<LocalAiRecipeOverride>? current = null;
        string? currentName = null;
        string[] lines = content.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int lineNumber = index + 1;
            string line = lines[index].TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                currentName = line[1..^1].Trim();
                if (currentName.Length == 0)
                    throw LineError(lineNumber, "section name is empty.");
                if (!sections.TryGetValue(currentName, out current))
                {
                    current = [];
                    sections.Add(currentName, current);
                }
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator < 0)
                throw LineError(lineNumber, "expected 'key = value'.");
            if (current is null)
                throw LineError(lineNumber, "add a [*] or [model-id] section before setting keys.");

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            ValidateKey(lineNumber, key);
            if (value.Any(char.IsControl))
                throw LineError(lineNumber, $"the value for '{key}' contains a control character.");
            if (current.Exists(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)))
                throw LineError(lineNumber, $"'{key}' is set more than once in [{currentName}].");
            current.Add(new LocalAiRecipeOverride(key, value.Length == 0 ? null : value));
        }

        var result = new List<LocalAiRecipeOverride>();
        if (sections.TryGetValue(AllModelsSection, out var allModels))
            result.AddRange(allModels);
        if (sections.TryGetValue(modelId, out var modelSpecific))
        {
            foreach (LocalAiRecipeOverride entry in modelSpecific)
            {
                int existing = result.FindIndex(item => string.Equals(item.Key, entry.Key, StringComparison.Ordinal));
                if (existing >= 0)
                    result[existing] = entry;
                else
                    result.Add(entry);
            }
        }
        return [.. result];
    }

    private static void ValidateKey(int lineNumber, string key)
    {
        if (!OptionNamePattern().IsMatch(key))
            throw LineError(lineNumber, $"'{key}' is not a llama-server option name.");

        // Resolve exactly like llama.cpp's preset parser: strip a leading 'no-' and
        // flip the boolean. Short aliases and every other name resolve only through
        // the allowlist, so an unknown or denied option is rejected regardless of the
        // name it was written under ('ag', 'no-ag', 'hft' all fail here).
        string option = key;
        bool negated = false;
        if (key.StartsWith("no-", StringComparison.Ordinal))
        {
            option = key[3..];
            negated = true;
        }

        if (!AllowedKeys.Contains(option))
            throw LineError(lineNumber, $"'{key}' is not an allowed llama-server tuning option.");
        if (negated && !NegatableKeys.Contains(option))
            throw LineError(lineNumber, $"'{key}' cannot be negated. Write '{option} = true|false' instead.");
    }

    private static InvalidDataException LineError(int lineNumber, string detail) =>
        new($"{FileName} line {lineNumber}: {detail}");

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionNamePattern();
}
