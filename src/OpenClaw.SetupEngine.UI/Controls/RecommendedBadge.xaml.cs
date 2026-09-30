using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI.Controls;

public sealed partial class RecommendedBadge : UserControl
{
    public RecommendedBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateEnabledState();
        IsEnabledChanged += (_, _) => UpdateEnabledState();
    }

    private void UpdateEnabledState() =>
        VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", false);
}
