namespace OpenClaw.Chat;

using System.Globalization;
using OpenClaw.Shared;

/// <summary>
/// Provider-rich description of a model exposed by <c>models.list</c>.
/// </summary>
/// <param name="Id">Wire model id (e.g. <c>claude-opus-4.8</c>).</param>
/// <param name="DisplayName">Human-friendly label (falls back to <paramref name="Id"/>).</param>
/// <param name="Provider">Owning provider (e.g. <c>OpenAI</c>, <c>Anthropic</c>), when known.</param>
/// <param name="ContextWindow">Native/catalog context-window size in tokens, when known.</param>
/// <param name="ContextTokens">Effective runtime context budget in tokens, when known.</param>
/// <param name="IsConfigured">True when the provider is configured on the gateway.</param>
/// <param name="IsAvailable">
/// True when the model can be selected right now. When false the picker shows it
/// but does not let the user switch to it.
/// </param>
/// <param name="RequiresAuth">
/// True when the model's provider still needs authentication/credentials before
/// the model is usable.
/// </param>
/// <param name="IsDefault">True when the gateway marks this model as the default.</param>
/// <param name="HasConfiguredFlag">True when the gateway explicitly reported configuration state.</param>
public sealed record ChatModelChoice(
    string Id,
    string DisplayName,
    string? Provider = null,
    int? ContextWindow = null,
    int? ContextTokens = null,
    bool IsConfigured = true,
    bool IsAvailable = true,
    bool RequiresAuth = false,
    bool IsDefault = false,
    bool HasConfiguredFlag = false)
{
    /// <summary>
    /// Provider-qualified identity used for picker tags and <c>sessions.patch</c>
    /// model refs. Already-qualified ids are preserved.
    /// </summary>
    public string SelectionId => BuildSelectionId(Id, Provider);

    /// <summary>
    /// True when the user may switch the session to this model. Auth-needed
    /// models remain selectable so the provider-auth flow can run. Catalog-only
    /// and explicitly unavailable models remain visible but disabled.
    /// </summary>
    public bool IsSelectable =>
        IsAvailable && (!HasConfiguredFlag || IsConfigured || RequiresAuth);

    /// <summary>
    /// Maps gateway models into ordered, selection-deduplicated picker entries.
    /// </summary>
    public static IReadOnlyList<ChatModelChoice> FromModelsList(ModelsListInfo? info)
    {
        if (info?.Models is not { Count: > 0 }) return Array.Empty<ChatModelChoice>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ChatModelChoice>(info.Models.Count);
        foreach (var m in info.Models)
        {
            if (m is null || string.IsNullOrEmpty(m.Id)) continue;
            var choice = new ChatModelChoice(
                Id: m.Id,
                DisplayName: m.DisplayName,
                Provider: m.Provider,
                ContextWindow: m.ContextWindow,
                ContextTokens: m.ContextTokens,
                IsConfigured: m.IsConfigured,
                IsAvailable: m.IsAvailable,
                RequiresAuth: m.RequiresAuth,
                IsDefault: m.IsDefault,
                HasConfiguredFlag: m.HasConfiguredFlag);
            if (!seen.Add(choice.SelectionId)) continue;
            list.Add(choice);
        }
        return list;
    }

    public bool MatchesModel(string? modelId, string? provider = null)
    {
        var normalizedModel = NormalizeId(modelId);
        if (normalizedModel is null) return false;

        if (NormalizeId(provider) is { } normalizedProvider)
        {
            var providerQualified = BuildSelectionId(normalizedModel, normalizedProvider);
            return string.Equals(SelectionId, providerQualified, StringComparison.Ordinal)
                || (string.Equals(Id, normalizedModel, StringComparison.Ordinal)
                    && string.Equals(NormalizeId(Provider), normalizedProvider, StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(Id, normalizedModel, StringComparison.Ordinal)
            || string.Equals(SelectionId, normalizedModel, StringComparison.Ordinal);
    }

    public static string BuildSelectionId(string modelId, string? provider)
    {
        var normalizedModel = NormalizeId(modelId) ?? string.Empty;
        if (normalizedModel.Length == 0) return string.Empty;
        var normalizedProvider = NormalizeId(provider);
        if (normalizedProvider is null) return normalizedModel;

        var prefix = normalizedProvider + "/";
        return normalizedModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedModel
            : $"{normalizedProvider}/{normalizedModel}";
    }

    public static string? ResolveSelectionId(
        string? modelId,
        string? provider,
        IReadOnlyList<ChatModelChoice> choices)
    {
        var normalizedModel = NormalizeId(modelId);
        if (normalizedModel is null) return null;

        if (NormalizeId(provider) is not null)
        {
            var match = choices.FirstOrDefault(c => c.MatchesModel(normalizedModel, provider));
            if (match is not null) return match.SelectionId;

            var bareRawMatches = choices
                .Where(c => c.Id == normalizedModel && string.IsNullOrWhiteSpace(c.Provider))
                .Take(2)
                .ToArray();
            if (bareRawMatches.Length == 1) return bareRawMatches[0].SelectionId;

            return BuildSelectionId(normalizedModel, provider);
        }

        var direct = choices.FirstOrDefault(c => c.SelectionId == normalizedModel);
        if (direct is not null) return direct.SelectionId;

        var rawMatches = choices.Where(c => c.Id == normalizedModel).Take(2).ToArray();
        return rawMatches.Length == 1 ? rawMatches[0].SelectionId : normalizedModel;
    }

    private static string? NormalizeId(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// Pure label/formatting helpers for the model picker. Lives in
/// <c>OpenClaw.Chat</c> (no WinUI dependency) so the display strings can be unit
/// tested without spinning up the composer.
/// </summary>
public static class ChatModelLabels
{
    /// <summary>
    /// True when <paramref name="modelId"/> represents "no explicit model
    /// override" — i.e. the session is tracking the gateway/agent default.
    /// This predicate only describes the <em>current</em> state, derived from an
    /// empty/absent session model. Clearing an override (so a session tracks the
    /// default again) is performed via the tri-state <c>SessionPatch.Clear</c>
    /// (explicit JSON null), not by sending an empty model string.
    /// </summary>
    public static bool IsTrackingDefault(string? modelId) => string.IsNullOrEmpty(modelId);

    /// <summary>
    /// Compact token-count label: 272000 → "272K", 1_048_576 → "1M",
    /// 200000 → "200K". Falls back to the raw number for small values.
    /// </summary>
    public static string FormatContextWindow(int contextWindow) =>
        FormatContextWindow(contextWindow, "0.#");

    private static string FormatContextWindow(int contextWindow, string fractionalFormat)
    {
        if (contextWindow <= 0) return string.Empty;
        if (contextWindow >= 1_000_000)
        {
            var millions = contextWindow / 1_000_000.0;
            // Trim a trailing ".0" so 2_000_000 → "2M" not "2.0M".
            return millions == Math.Floor(millions)
                ? $"{(int)millions}M"
                : $"{millions.ToString(fractionalFormat, CultureInfo.InvariantCulture)}M";
        }
        if (contextWindow >= 1_000)
        {
            var thousands = contextWindow / 1_000.0;
            return thousands == Math.Floor(thousands)
                ? $"{(int)thousands}K"
                : $"{thousands.ToString(fractionalFormat, CultureInfo.InvariantCulture)}K";
        }
        return contextWindow.ToString(CultureInfo.InvariantCulture);
    }

    private static (string Runtime, string Native) FormatDistinctContextValues(
        int runtimeTokens,
        int nativeTokens)
    {
        var runtime = FormatContextWindow(runtimeTokens);
        var native = FormatContextWindow(nativeTokens);
        if (!string.Equals(runtime, native, StringComparison.Ordinal))
            return (runtime, native);

        runtime = FormatContextWindow(runtimeTokens, "0.###");
        native = FormatContextWindow(nativeTokens, "0.###");
        if (!string.Equals(runtime, native, StringComparison.Ordinal))
            return (runtime, native);

        return (
            runtimeTokens.ToString("N0", CultureInfo.InvariantCulture),
            nativeTokens.ToString("N0", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Builds the secondary metadata segment from provider and context metadata.
    /// Runtime budget is shown first when available; a differing native/catalog
    /// window follows it. Older gateways that only expose a context window keep
    /// the original unqualified display.
    /// </summary>
    public static string BuildMetaSegment(ChatModelChoice choice)
    {
        var hasProvider = !string.IsNullOrWhiteSpace(choice.Provider);
        var runtimeTokens = choice.ContextTokens is > 0 ? choice.ContextTokens : null;
        var nativeTokens = choice.ContextWindow is > 0 ? choice.ContextWindow : null;
        string ctx;

        if (runtimeTokens is { } runtime)
        {
            if (nativeTokens is { } native)
            {
                if (runtime == native)
                {
                    ctx = FormatContextWindow(runtime);
                }
                else
                {
                    var labels = FormatDistinctContextValues(runtime, native);
                    ctx = $"{labels.Runtime} runtime · {labels.Native} native";
                }
            }
            else
            {
                ctx = $"{FormatContextWindow(runtime)} runtime";
            }
        }
        else
        {
            ctx = nativeTokens is { } native ? FormatContextWindow(native) : string.Empty;
        }
        var hasCtx = ctx.Length > 0;

        if (hasProvider && hasCtx) return $"{choice.Provider} · {ctx}";
        if (hasProvider) return choice.Provider!;
        if (hasCtx) return ctx;
        return string.Empty;
    }

    /// <summary>
    /// Trailing state marker for a model: "default", "auth needed",
    /// "not configured", "unavailable", or empty. Explicit configuration state
    /// is shown first so catalog-only rows explain why they are disabled. Missing
    /// configuration metadata remains neutral for older gateways.
    /// </summary>
    public static string BuildStateMarker(ChatModelChoice choice)
    {
        if (choice.HasConfiguredFlag && !choice.IsConfigured && !choice.RequiresAuth)
            return "not configured";
        if (!choice.IsAvailable) return "unavailable";
        if (choice.RequiresAuth) return "auth needed";
        if (choice.IsDefault) return "default";
        return string.Empty;
    }

    /// <summary>
    /// Full menu/combo label, e.g. "Claude Opus 4.8 · Anthropic · 200K · default".
    /// State marker is appended last so default/auth-needed/not-configured/unavailable
    /// reads at the end of the row.
    /// </summary>
    public static string BuildMenuLabel(ChatModelChoice choice)
    {
        var label = choice.DisplayName;
        var meta = BuildMetaSegment(choice);
        if (meta.Length > 0) label = $"{label} · {meta}";
        var marker = BuildStateMarker(choice);
        if (marker.Length > 0) label = $"{label} · {marker}";
        return label;
    }

    /// <summary>
    /// Label for the "clear to gateway default" picker entry. Selecting it clears
    /// the session's explicit model override (the gateway falls back to its
    /// agent/default model). When the default model is known its name is
    /// surfaced, e.g. "Default (Claude Opus 4.8)".
    /// </summary>
    public static string BuildDefaultEntryLabel(ChatModelChoice? defaultChoice)
    {
        if (defaultChoice is not null && !string.IsNullOrWhiteSpace(defaultChoice.DisplayName))
            return $"Default ({defaultChoice.DisplayName})";
        return "Default";
    }
}
