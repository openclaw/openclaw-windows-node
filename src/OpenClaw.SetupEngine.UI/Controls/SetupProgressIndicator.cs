using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClaw.SetupEngine.UI.Controls;

public sealed class SetupProgressIndicator : StackPanel
{
    private IReadOnlyList<OnboardingStage> _stages = [];
    private OnboardingStage _current;

    public SetupProgressIndicator()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        IsTabStop = false;
        ActualThemeChanged += (_, _) => Render();
    }

    public void Update(IReadOnlyList<OnboardingStage> stages, OnboardingStage current)
    {
        _stages = stages;
        _current = current;
        Render();
    }

    private void Render()
    {
        Children.Clear();
        var position = _stages.ToList().IndexOf(_current);
        Visibility = position >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (position < 0)
            return;

        AutomationProperties.SetName(this, SetupLocalization.Format(
            "Onboarding_Flow_Position", position + 1, _stages.Count));
        for (var index = 0; index < _stages.Count; index++)
        {
            var dot = new Border
            {
                Width = index == position ? 20 : 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources[
                    index == position ? "SetupIndicatorAccentBrush" : "SetupInactiveDotBrush"],
            };
            AutomationProperties.SetAccessibilityView(dot, AccessibilityView.Raw);
            Children.Add(dot);
        }
    }
}
