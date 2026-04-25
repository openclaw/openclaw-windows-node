using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Infrastructure;
using OpenClawTray.Infrastructure.Hosting;
using WinUIEx;

namespace OpenClawTray.Onboarding;

/// <summary>
/// Host window for the Reactor-based onboarding wizard.
/// Wraps Reactor content in a ScrollViewer so tall pages scroll
/// instead of pushing the nav bar off screen.
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

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        var state = new OnboardingState(settings);
        state.Finished += OnOnboardingFinished;

        _host = new ReactorHostControl();
        _host.Mount(ctx =>
        {
            var (s, _) = ctx.UseState(state);
            return Factories.Component<OnboardingApp, OnboardingState>(s);
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
