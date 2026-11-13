using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// The two-pane setup composition for windows other than <see cref="SetupWindow"/>, such as the
/// post-restart ready window: backdrop tint, <see cref="SetupCompanion"/> and the page frame.
/// </summary>
/// <example>
/// <code>
/// var shell = new SetupShellLayout();
/// window.Content = shell;
/// shell.Frame.Navigate(typeof(AiReadyPage), args);
/// </code>
/// </example>
public sealed partial class SetupShellLayout : UserControl
{
    public SetupShellLayout()
    {
        InitializeComponent();
        Root.SizeChanged += (_, args) => CompanionColumn.Width =
            new Microsoft.UI.Xaml.GridLength(SetupWindowSizing.CompanionPaneWidth(args.NewSize.Width));
    }

    public Frame Frame => ContentFrame;

    public SetupCompanion Companion => CompanionControl;
}
