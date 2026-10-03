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
    /// Keys the override file may not touch. Model and draft identity are pinned and
    /// hash-verified by setup; file and URL keys would let the preset read or write arbitrary
    /// paths; host, port, api-key, web UI, CORS, and router keys are owned by the launch
    /// arguments; RPC, MCP, and built-in tool or agent keys would let llama-server reach other
    /// hosts or run host commands; and <c>ctx-size</c>, <c>fit</c>, <c>parallel</c>, the
    /// unified-KV keys, and <c>n-predict</c> must keep the per-request context equal to
    /// <see cref="LocalAiInstallManifest.ContextLength"/> and the output limit equal to
    /// <see cref="LocalAiGatewayProviderDefinition.MaximumOutputTokens"/>, which
    /// <see cref="LocalAiGatewayProviderDefinition"/> publishes to the gateway and which the
    /// capacity fit-test sized. Model-source and path-valued options whose names lack a
    /// recognizable prefix or suffix are listed by name from the pinned llama.cpp <c>common/arg.cpp</c>.
    /// A <c>no-</c> negation is checked against the same rules, so <c>no-webui</c> is denied too.
    /// </summary>
    private static readonly FrozenSet<string> DeniedKeys = FrozenSet.ToFrozenSet(
        [
            "model", "model-url", "model-draft", "spec-draft-model", "spec-draft-hf", "docker-repo",
            "mmproj", "mmproj-url",
            "lora", "lora-scaled", "control-vector", "control-vector-scaled",
            "host", "port", "path", "api-key", "api-prefix", "alias", "server-base",
            "models-preset", "models-dir", "models-max", "models-autoload", "load-on-startup",
            "ctx-size", "fit", "fit-ctx", "parallel", "kv-unified", "kv-unified-per-slot",
            "n-predict", "predict",
            "offline", "webui", "ui",
            "rpc", "tools", "agent",
            "lookup-cache-static", "lookup-cache-dynamic", "prompt-cache", "file", "output",
            "save-all-logits", "kl-divergence-base", "image", "audio", "video",
        ],
        StringComparer.Ordinal);

    private static readonly string[] DeniedPrefixes = ["hf-", "ssl-", "ui-", "webui-", "cors-", "mcp-", "tools-"];
    private static readonly string[] DeniedSuffixes = ["-file", "-path", "-dir", "-url", "-repo"];

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
        if (key.Length <= 2)
            throw LineError(lineNumber, $"'{key}' is a short alias. Use the long llama-server option name.");
        if (IsDenied(key))
            throw LineError(lineNumber, $"'{key}' cannot be overridden.");
    }

    private static bool IsDenied(string key)
    {
        string option = key.StartsWith("no-", StringComparison.Ordinal) ? key[3..] : key;
        return DeniedKeys.Contains(option)
            || DeniedPrefixes.Any(prefix => option.StartsWith(prefix, StringComparison.Ordinal))
            || DeniedSuffixes.Any(suffix => option.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static InvalidDataException LineError(int lineNumber, string detail) =>
        new($"{FileName} line {lineNumber}: {detail}");

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex OptionNamePattern();
}
