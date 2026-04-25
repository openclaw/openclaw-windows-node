using Microsoft.UI.Xaml.Media;
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
/// Non-resizable, Mica backdrop, centered — matches macOS 630×752 spec (scaled to 720×752 for Windows).
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

        // Non-resizable window (matches macOS spec)
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // Mount the Reactor-based onboarding root component
        var state = new OnboardingState(settings);
        state.Finished += OnOnboardingFinished;

        // Mount the Reactor-based onboarding root component
        _host = new ReactorHostControl();
        _host.Mount(ctx =>
        {
            // UseState to hold the OnboardingState so it persists across re-renders
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
