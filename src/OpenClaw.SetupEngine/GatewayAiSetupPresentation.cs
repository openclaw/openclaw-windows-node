using System.Text.Json;

namespace OpenClaw.SetupEngine;

public enum ProviderArtworkFallback { Key, Account, Pair, Install, Configure, Verified, Code }

/// <summary>Only bundled names can become app-resource URIs. Metadata never becomes a XAML URI.</summary>
public sealed record ProviderArtworkDescriptor(
    string? BundledFileName, Uri? RemoteUri, ProviderArtworkFallback Fallback, bool RemoteRejected = false);

public static class GatewayAiSetupPresentation
{
    public static bool ShowNativeRecovery(GatewayAiSetupPhase phase, bool isBusy, bool hasError) =>
        !isBusy && phase != GatewayAiSetupPhase.Verified &&
        (hasError || phase is GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.Rejected);

    public static string? GetPromptAction(GatewayAiSetupWizardStep? step)
    {
        if (step is null || step.Type == "progress" || step.Executor == "gateway")
            return null;
        if (step.Type is "text" or "select" or "multiselect" or "confirm")
            return "Submit";
        return step.DeviceCode is not null ? "SignedIn" : "Continue.Content";
    }

    public static bool ShowProviderStep(GatewayAiSetupWizardStep? step, GatewayAiSetupPhase phase,
        bool isBusy, bool isSubmittingAnswer = false) =>
        step is not null && (phase == GatewayAiSetupPhase.Running ||
            phase == GatewayAiSetupPhase.Uncertain && isBusy && isSubmittingAnswer);

    public static bool ShowProviderDialog(GatewayAiSetupWizardStep? step, GatewayAiSetupPhase phase,
        bool isBusy, bool hasError, bool isSubmittingAnswer = false)
    {
        if (ShowProviderStep(step, phase, isBusy, isSubmittingAnswer) && step is not null &&
            (step.DeviceCode is not null || step.ExternalUrl is not null ||
             GetPromptAction(step) is not null))
            return true;

        return !isBusy && (hasError || phase is GatewayAiSetupPhase.Prepared or
            GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.VerificationRequired);
    }

    public static IReadOnlyList<GatewayAiSetupWizardOption> GetInitialOptions(GatewayAiSetupWizardStep step)
    {
        if (step.InitialValue is not { } initial || step.Type is not ("select" or "multiselect"))
            return [];
        return step.Options.Where(option => step.Type == "multiselect" && initial.ValueKind == JsonValueKind.Array
            ? initial.EnumerateArray().Any(value => JsonElement.DeepEquals(value, option.Value))
            : JsonElement.DeepEquals(initial, option.Value)).ToArray();
    }

    public static string? GetBundledProviderIconFileName(string? brandId)
    {
        if (brandId is null || brandId.Length > 128)
            return null;
        var normalized = brandId.Trim().ToLowerInvariant();
        var brand = BrandResource(normalized);
        if (brand is null && normalized.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } leading)
            brand = BrandResource(leading);
        return brand is null ? null : $"ProviderIcon-{brand}.svg";
    }

    private static string? BrandResource(string brand) => brand switch
    {
        "claude-cli" or "claude-code" or "claude" or "anthropic" => "claude",
        "codex-cli" or "codex" or "openai" or "chatgpt" => "codex",
        "gemini-cli" or "gemini" or "googlegemini" or "google" => "gemini",
        "ollama" => "ollama",
        "lmstudio" or "lm-studio" => "lmstudio",
        "pi" => "pi",
        "opencode" => "opencode",
        "kimi-code" or "kimi" or "moonshot" => "kimi",
        "grok-build" or "grok" or "xai" => "xai",
        _ => null
    };

    public static ProviderArtworkDescriptor GetProviderArtwork(
        string? brandId, string? id, string? icon, ProviderArtworkFallback fallback)
    {
        var bundled = GetBundledProviderIconFileName(brandId) ?? GetBundledProviderIconFileName(id);
        if (bundled is not null)
            return new(bundled, null, fallback);
        var hasIcon = !string.IsNullOrWhiteSpace(icon);
        var allowed = ProviderArtworkNetworkPolicy.TryGetUri(icon, out var uri);
        return new(null, allowed ? uri : null, fallback, hasIcon && !allowed);
    }

    public static ProviderArtworkFallback GetProviderFallback(GatewayAiSetupChoiceKind choiceKind, string? kind) =>
        choiceKind switch
        {
            GatewayAiSetupChoiceKind.ManualProvider => ProviderArtworkFallback.Key,
            GatewayAiSetupChoiceKind.Prepare => ProviderArtworkFallback.Install,
            GatewayAiSetupChoiceKind.Candidate => kind switch
            {
                "existing-model" => ProviderArtworkFallback.Verified,
                "codex-cli" => ProviderArtworkFallback.Code,
                _ => ProviderArtworkFallback.Key
            },
            _ => kind switch
            {
                "device-code" => ProviderArtworkFallback.Pair,
                "install" => ProviderArtworkFallback.Install,
                "custom" => ProviderArtworkFallback.Configure,
                _ => ProviderArtworkFallback.Account
            }
        };

    public static string GetProviderActionLabel(string? metadataActionLabel, string? kind,
        GatewayAiSetupChoiceKind choiceKind = GatewayAiSetupChoiceKind.Auth) =>
        !string.IsNullOrWhiteSpace(metadataActionLabel) ? metadataActionLabel :
        choiceKind == GatewayAiSetupChoiceKind.Prepare ? "Connect / Set up" : kind switch
        {
            "device-code" => "Pair",
            "install" => "Set up…",
            "custom" => "Configure…",
            _ => "Sign in"
        };

    public static bool TryGetExternalUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme is "http" or "https" && string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }
}
