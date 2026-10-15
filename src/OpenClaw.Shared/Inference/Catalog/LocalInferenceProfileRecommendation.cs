namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>Read-only explanation of an automatic hardware choice, never a request to replace an install.</summary>
public sealed record LocalInferenceProfileRecommendation(string PreferredModelName, string SelectedModelName, int ContextTokens)
{
    public static LocalInferenceProfileRecommendation? From(LocalInferenceEligibilityResult eligibility)
    {
        if (!eligibility.CanInstall || eligibility.Plan is not { } plan ||
            plan.ModelSelectionOrigin != LocalInferenceModelSelectionOrigin.Default)
            return null;
        LocalModelInfo preferred = LocalModelCatalog.Default;
        if (plan.BoundGpuStableId is not null ||
            (plan.Model.Id == preferred.Id && plan.Profile.Id == LocalModelCatalog.GetProfiles(preferred)[0].Id))
            return null;
        return new(preferred.DisplayName, plan.Model.DisplayName, plan.Profile.ContextTokens);
    }
}
