using Microsoft.UI.Xaml;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.SetupEngine.UI;

/// <summary>Passive old-process progress only. No setup, connection or shutdown callback ownership.</summary>
public sealed class SetupLoadingWindow : Window
{
    private readonly SetupLoadingProgress _progress = new();
    private readonly SetupLoadingProgress.Scope _scope;
    private readonly SetupLoadingView _view = new();
    public SetupLoadingWindow()
    {
        _scope = _progress.Begin(SetupLoadingGroup.Finishing, SetupLoadingStep.RestartCompanion);
        _view.Bind(_progress);
        Content = _view;
        Title = SetupLoadingView.TitleFor(SetupLoadingGroup.Finishing);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 720));
        Closed += (_, _) => { _view.Dispose(); _progress.Dispose(); };
    }
    public void Report(SetupLoadingStep step) => _scope.Report(step);
}
