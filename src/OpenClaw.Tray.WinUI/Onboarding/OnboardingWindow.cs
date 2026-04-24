using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Hosting;
using WinUIEx;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Host window for the Reactor-based onboarding wizard.
/// Replaces the legacy SetupWizardWindow with a full Mac-parity onboarding flow.
/// </summary>
public sealed class OnboardingWindow : WindowEx
{
    public event EventHandler? OnboardingCompleted;
    public bool Completed { get; private set; }

    private readonly SettingsManager _settings;
    private readonly ReactorHostControl _host;

    public OnboardingWindow(SettingsManager settings)
    {
        _settings = settings;

        Title = LocalizationHelper.GetString("Onboarding_Title");
        this.SetWindowSize(720, 752);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();

        // Mount the Reactor-based onboarding root component
        var state = new OnboardingState(settings);
        state.Finished += OnOnboardingFinished;

        _host = new ReactorHostControl();
        _host.Mount(ctx =>
        {
            return Factories.Component<OnboardingApp, OnboardingState>(state);
        });
        Content = _host;
    }

    private void OnOnboardingFinished(object? sender, EventArgs e)
    {
        _settings.Save();
        Completed = true;
        OnboardingCompleted?.Invoke(this, EventArgs.Empty);
        Close();
    }
}
