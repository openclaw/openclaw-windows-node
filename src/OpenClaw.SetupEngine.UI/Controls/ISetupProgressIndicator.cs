namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>Presentation of the host's canonical route stages. Indicators never own navigation.</summary>
public interface ISetupProgressIndicator
{
    void Update(IReadOnlyList<OnboardingStage> stages, OnboardingStage current);

    void Update(IReadOnlyList<OnboardingStage> stages, OnboardingStage current, OnboardingStage? previous) =>
        Update(stages, current);
}
