using OpenClaw.Shared;

namespace OpenClaw.Chat;

/// <summary>Projects advertised choices only. Neither model names nor reasoning booleans infer policy.</summary>
public static class ChatThinkingProfile
{
    public static ThinkingProfile? Resolve(ChatThread thread, IReadOnlyList<ChatModelChoice>? catalog)
    {
        var session = thread.ThinkingContext
            ?? new ThinkingContext(new(thread.ModelProvider, thread.Model));
        if (session.Profile is { } profile)
            return profile;

        var defaults = thread.ThinkingDefaults;
        var identity = session.Identity;
        if (defaults is not null
            && (string.IsNullOrEmpty(identity.Provider) || identity.Provider == defaults.Identity.Provider)
            && (string.IsNullOrEmpty(identity.Model) || identity.Model == defaults.Identity.Model)
            && identity.IsCompatibleWith(defaults.Identity)
            && defaults.Profile is { } defaultProfile)
            return defaultProfile;

        var target = !string.IsNullOrEmpty(identity.Model) || !string.IsNullOrEmpty(identity.Provider)
            ? identity : defaults?.Identity;
        if (target?.Provider is null || target.Model is null)
            return null;
        return catalog?.Select(choice => choice.ThinkingContext).FirstOrDefault(candidate =>
            candidate is not null
            && candidate.Identity.Provider == target.Provider
            && candidate.Identity.Model == target.Model
            && new ThinkingIdentity(RuntimeId: identity.RuntimeId).IsCompatibleWith(candidate.Identity))?.Profile;
    }
}
