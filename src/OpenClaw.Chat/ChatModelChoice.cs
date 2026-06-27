namespace OpenClaw.Chat;

using OpenClaw.Shared;

/// <summary>
/// Provider-rich description of a model exposed by <c>models.list</c>.
/// </summary>
/// <param name="Id">Wire model id (e.g. <c>claude-opus-4.8</c>).</param>
/// <param name="DisplayName">Human-friendly label (falls back to <paramref name="Id"/>).</param>
/// <param name="Provider">Owning provider (e.g. <c>OpenAI</c>, <c>Anthropic</c>), when known.</param>
/// <param name="ContextWindow">Context-window size in tokens, when known.</param>
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
public sealed record ChatModelChoice(
    string Id,
    string DisplayName,
    string? Provider = null,
    int? ContextWindow = null,
    bool IsConfigured = true,
    bool IsAvailable = true,
    bool RequiresAuth = false,
    bool IsDefault = false)
{
    /// <summary>
    /// Provider-qualified identity used for picker tags and <c>sessions.patch</c>
    /// model refs. Already-qualified ids are preserved.
    /// </summary>
    public string SelectionId => BuildSelectionId(Id, Provider);

    /// <summary>
    /// True when the user may switch the session to this model. Auth-needed
    /// models remain selectable (selecting one routes the user toward the
    /// gateway's provider-auth flow); only explicitly unavailable models are
    /// blocked.
    /// </summary>
    public bool IsSelectable => IsAvailable;

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
            // Hide explicitly unconfigured models unless the gateway reports an
            // auth flow for them; auth-needed rows are useful picker actions.
            if (m.HasConfiguredFlag && !m.IsConfigured && !m.RequiresAuth) continue;
            var choice = new ChatModelChoice(
                Id: m.Id,
                DisplayName: m.DisplayName,
                Provider: m.Provider,
                ContextWindow: m.ContextWindow,
                IsConfigured: m.IsConfigured,
                IsAvailable: m.IsAvailable,
                RequiresAuth: m.RequiresAuth,
                IsDefault: m.IsDefault);
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
    public static string FormatContextWindow(int contextWindow)
    {
        if (contextWindow <= 0) return string.Empty;
        if (contextWindow >= 1_000_000)
        {
            var millions = contextWindow / 1_000_000.0;
            // Trim a trailing ".0" so 2_000_000 → "2M" not "2.0M".
            return millions == Math.Floor(millions)
                ? $"{(int)millions}M"
                : $"{millions:0.#}M";
        }
        if (contextWindow >= 1_000)
        {
            var thousands = contextWindow / 1_000.0;
            return thousands == Math.Floor(thousands)
                ? $"{(int)thousands}K"
                : $"{thousands:0.#}K";
        }
        return contextWindow.ToString();
    }

    /// <summary>
    /// Builds the secondary metadata segment ("OpenAI · 272K") from provider and
    /// context window, or an empty string when neither is known.
    /// </summary>
    public static string BuildMetaSegment(ChatModelChoice choice)
    {
        var hasProvider = !string.IsNullOrWhiteSpace(choice.Provider);
        var ctx = choice.ContextWindow is { } cw ? FormatContextWindow(cw) : string.Empty;
        var hasCtx = ctx.Length > 0;

        if (hasProvider && hasCtx) return $"{choice.Provider} · {ctx}";
        if (hasProvider) return choice.Provider!;
        if (hasCtx) return ctx;
        return string.Empty;
    }

    /// <summary>
    /// Trailing state marker for a model: "default", "auth needed",
    /// "unavailable", or empty. Unavailable takes precedence over auth-needed,
    /// which takes precedence over default. Only explicit gateway signals drive
    /// the markers — a missing <see cref="ChatModelChoice.IsConfigured"/> flag is
    /// not treated as "auth needed" because the gateway's <c>configured</c> view
    /// often omits the field entirely.
    /// </summary>
    public static string BuildStateMarker(ChatModelChoice choice)
    {
        if (!choice.IsAvailable) return "unavailable";
        if (choice.RequiresAuth) return "auth needed";
        if (choice.IsDefault) return "default";
        return string.Empty;
    }

    /// <summary>
    /// Full menu/combo label, e.g. "Claude Opus 4.8 · Anthropic · 200K · default".
    /// State marker is appended last so default/auth-needed/unavailable reads at
    /// the end of the row.
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
