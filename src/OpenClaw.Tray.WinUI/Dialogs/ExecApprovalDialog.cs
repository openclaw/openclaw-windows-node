using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// Approval prompt for a command the agent wants to run on this machine. Shows the
/// already-sanitized command text plus context rows and offers Deny / Allow Once /
/// Allow Always.
///
/// Security posture: the allow buttons are never default-focused and stay disabled for
/// a short delay so the dialog cannot be click-through approved; Escape, the X button,
/// and Alt+F4 all resolve to Deny.
/// </summary>
public sealed class ExecApprovalDialog : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private static readonly TimeSpan AllowArmDelay = TimeSpan.FromMilliseconds(1500);

    private readonly TaskCompletionSource<ExecApprovalPromptOutcome> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Button _denyButton;
    private readonly Button _allowOnceButton;
    private readonly Button _allowAlwaysButton;

    private DispatcherTimer? _armTimer;
    private ExecApprovalPromptOutcome _outcome = ExecApprovalPromptOutcome.Deny;

    // Anti-clickthrough gate: the allow buttons arm only once BOTH the arm delay has
    // elapsed AND the user has seen the whole command (scrolled to the end, or it fit).
    private ScrollViewer? _bodyScroll;
    private bool _delayElapsed;
    private bool _commandFullySeen;

    public bool IsClosed { get; private set; }

    public ExecApprovalDialog(ExecApprovalPromptView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var windowTitle = $"{AppIdentity.DisplayName} - {LocalizationHelper.GetString("ExecApproval_WindowTitle")}";
        Title = windowTitle;
        this.SetWindowSize(520, 400);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        // ── Custom title bar ──
        var titleBar = new Grid { Height = 48, Padding = new Thickness(16, 0, 140, 0) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIcon = new BrandMark { MarkSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(titleIcon, 0);
        titleBar.Children.Add(titleIcon);
        var titleText = new TextBlock
        {
            Text = windowTitle,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        Grid.SetColumn(titleText, 1);
        titleBar.Children.Add(titleText);
        SetTitleBar(titleBar);

        // ── Layout: [title bar][header][scrollable body][buttons] ──
        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        outerGrid.Children.Add(titleBar);

        var root = new Grid { Padding = new Thickness(28, 8, 28, 24), RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: shield glyph + heading
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var shield = new FontIcon
        {
            Glyph = "\uE72E", // shield
            FontSize = 28,
            Foreground = ResolveBrush("SystemFillColorCautionBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(shield, 0);
        header.Children.Add(shield);
        var heading = new TextBlock
        {
            Text = LocalizationHelper.GetString("ExecApproval_Heading"),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Body (scrollable: long translations / high scaling degrade to scrolling)
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("ExecApproval_Body"),
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });

        // Homoglyph / mixed-script warning: the command draws letters from more than one
        // Latin-confusable script, so it may not read the way it looks.
        if (view.HasConfusableWarning)
            body.Children.Add(BuildConfusableWarning());

        // Command card: monospaced, selectable. A single body scroll region (below) owns
        // all scrolling so there is one place to reach "you have seen the whole command".
        var commandText = new TextBlock
        {
            Text = view.CommandText,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        body.Children.Add(new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = commandText,
        });

        AddContextRow(body, "ExecApproval_AgentLabel", view.AgentLabel);
        AddContextRow(body, "ExecApproval_CwdLabel", view.CwdText);
        AddContextRow(body, "ExecApproval_ExecutableLabel", view.ExecutablePathText);

        _bodyScroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _bodyScroll.ViewChanged += (_, _) => UpdateCommandSeen();
        _bodyScroll.SizeChanged += (_, _) => UpdateCommandSeen();
        _bodyScroll.Loaded += (_, _) => UpdateCommandSeen();
        Grid.SetRow(_bodyScroll, 1);
        root.Children.Add(_bodyScroll);

        // Buttons: [Deny] ............ [Allow Always] [Allow Once (accent)]
        var buttonGrid = new Grid { ColumnSpacing = 8 };
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _denyButton = new Button { Content = LocalizationHelper.GetString("ExecApproval_Deny") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_denyButton, "ExecApprovalDenyAction");
        _denyButton.Click += (_, _) => Decide(ExecApprovalPromptOutcome.Deny);
        Grid.SetColumn(_denyButton, 0);
        buttonGrid.Children.Add(_denyButton);

        _allowAlwaysButton = new Button { Content = LocalizationHelper.GetString("ExecApproval_AllowAlways") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_allowAlwaysButton, "ExecApprovalAllowAlwaysAction");
        _allowAlwaysButton.Click += (_, _) => Decide(ExecApprovalPromptOutcome.AllowAlways);
        Grid.SetColumn(_allowAlwaysButton, 2);
        buttonGrid.Children.Add(_allowAlwaysButton);

        _allowOnceButton = new Button
        {
            Content = LocalizationHelper.GetString("ExecApproval_AllowOnce"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_allowOnceButton, "ExecApprovalAllowOnceAction");
        _allowOnceButton.Click += (_, _) => Decide(ExecApprovalPromptOutcome.AllowOnce);
        Grid.SetColumn(_allowOnceButton, 3);
        buttonGrid.Children.Add(_allowOnceButton);

        Grid.SetRow(buttonGrid, 2);
        root.Children.Add(buttonGrid);

        Grid.SetRow(root, 1);
        outerGrid.Children.Add(root);
        Content = outerGrid;

        // Escape denies from anywhere in the window.
        var escAccel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = global::Windows.System.VirtualKey.Escape,
        };
        escAccel.Invoked += (_, e) =>
        {
            e.Handled = true;
            Decide(ExecApprovalPromptOutcome.Deny);
        };
        outerGrid.KeyboardAccelerators.Add(escAccel);
        outerGrid.KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        Closed += OnWindowClosed;
        ArmAllowGuard();
    }

    /// <summary>
    /// Activates the window, forces it to the foreground (it opens from a background
    /// gateway event), and returns the user's decision. Closing via X or Alt+F4 denies.
    /// </summary>
    public Task<ExecApprovalPromptOutcome> ShowAsync()
    {
        Activate();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { /* best-effort */ }

        // Initial focus lands on Deny so Enter can never approve.
        _denyButton.Focus(FocusState.Programmatic);
        return _tcs.Task;
    }

    private void Decide(ExecApprovalPromptOutcome outcome)
    {
        if (IsClosed) return;
        _outcome = outcome;
        Close();
    }

    // Anti-clickthrough: allow buttons start disabled and arm only when the delay has
    // elapsed AND the whole command has been seen. Deny is always active.
    private void ArmAllowGuard()
    {
        _allowOnceButton.IsEnabled = false;
        _allowAlwaysButton.IsEnabled = false;
        MaybeArmAllow();
        _armTimer = new DispatcherTimer { Interval = AllowArmDelay };
        _armTimer.Tick += (_, _) =>
        {
            _armTimer?.Stop();
            _delayElapsed = true;
            MaybeArmAllow();
        };
        _armTimer.Start();
    }

    // A command that fits needs no scrolling; a long one must be scrolled to its end
    // before it counts as seen. Once seen it stays seen (scrolling back up is fine).
    private void UpdateCommandSeen()
    {
        if (_commandFullySeen || _bodyScroll is null) return;
        var atEnd = _bodyScroll.ScrollableHeight <= 0.5
            || _bodyScroll.VerticalOffset >= _bodyScroll.ScrollableHeight - 2.0;
        if (atEnd)
        {
            _commandFullySeen = true;
            MaybeArmAllow();
        }
    }

    private void MaybeArmAllow()
    {
        if (IsClosed) return;
        if (_delayElapsed && _commandFullySeen)
        {
            _allowOnceButton.IsEnabled = true;
            _allowAlwaysButton.IsEnabled = true;
            ToolTipService.SetToolTip(_allowOnceButton, null);
            ToolTipService.SetToolTip(_allowAlwaysButton, null);
            return;
        }

        var hintKey = _commandFullySeen ? "ExecApproval_ArmHint" : "ExecApproval_ScrollHint";
        var hint = LocalizationHelper.GetString(hintKey);
        ToolTipService.SetToolTip(_allowOnceButton, hint);
        ToolTipService.SetToolTip(_allowAlwaysButton, hint);
    }

    private static Border BuildConfusableWarning()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon
        {
            Glyph = "\uE7BA", // warning
            FontSize = 14,
            Foreground = ResolveBrush("SystemFillColorCautionBrush"),
            VerticalAlignment = VerticalAlignment.Top,
        });
        row.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("ExecApproval_ConfusableWarning"),
            Foreground = ResolveBrush("SystemFillColorCautionBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        return new Border
        {
            Background = ResolveBrush("SystemFillColorAttentionBackgroundBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Child = row,
        };
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _armTimer?.Stop();
        Closed -= OnWindowClosed;
        _tcs.TrySetResult(_outcome);
    }

    private static Brush ResolveBrush(string themeKey) =>
        Application.Current.Resources.TryGetValue(themeKey, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static void AddContextRow(StackPanel parent, string labelKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = LocalizationHelper.GetString(labelKey),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        var text = new TextBlock
        {
            Text = value,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        parent.Children.Add(row);
    }
}
