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
/// Security posture: the prompt opens from a background gateway event, so it never takes
/// the foreground or keyboard focus from the application the user is typing in. Every
/// decision button, Deny included, stays disabled for a short delay after the prompt is
/// first rendered so a stray click or keystroke cannot decide it. Once the user activates
/// the prompt and it is armed, focus moves to Deny so Enter can never approve. Escape
/// (after the delay), the X button, and Alt+F4 all resolve to Deny.
/// </summary>
public sealed class ExecApprovalDialog : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint FLASHW_TRAY = 0x0002;
    private const uint FLASHW_TIMERNOFG = 0x000C;

    private const int WindowWidth = 520;
    private const int WindowHeight = 400;

    private static readonly TimeSpan DecisionArmDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan FirstFrameFallback = TimeSpan.FromSeconds(1);

    private readonly TaskCompletionSource<ExecApprovalPromptOutcome> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Button _denyButton;
    private readonly Button _allowOnceButton;
    private readonly Button _allowAlwaysButton;
    private readonly Grid _windowContent;

    private DispatcherTimer? _armTimer;
    private DispatcherTimer? _firstFrameFallbackTimer;
    private bool _armDelayStarted;
    private bool _decisionsArmed;
    private bool _windowActive;
    private bool _denyFocusApplied;
    private ExecApprovalPromptOutcome _outcome = ExecApprovalPromptOutcome.Deny;

    private readonly bool _allowAlwaysAvailable;

    public bool IsClosed { get; private set; }

    public ExecApprovalDialog(ExecApprovalPromptView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        _allowAlwaysAvailable = view.AllowAlwaysAvailable;

        var windowTitle = $"{AppIdentity.DisplayName} - {LocalizationHelper.GetString("ExecApproval_WindowTitle")}";
        Title = windowTitle;
        PlaceCenteredWithoutActivating();
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

        // Command card: monospaced, selectable.
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

        var bodyScroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(bodyScroll, 1);
        root.Children.Add(bodyScroll);

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
        // Allow Always is hidden when the policy would not persist a reusable rule
        // (ask=always or a one-shot command), matching the macOS decision set. Deny and
        // Allow Once always remain.
        if (!_allowAlwaysAvailable)
            _allowAlwaysButton.Visibility = Visibility.Collapsed;

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
        _windowContent = outerGrid;
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

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
        ArmDecisionGuard();
    }

    /// <summary>
    /// Shows the prompt above other windows without activating it, so keystrokes keep
    /// going to the application the user is working in, and returns the user's decision.
    /// Closing via X or Alt+F4 denies.
    /// </summary>
    public Task<ExecApprovalPromptOutcome> ShowAsync()
    {
        AppWindow.Show(activateWindow: false);
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                // Taskbar attention cue in place of taking focus; stops once the user
                // switches to the prompt.
                var flash = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = hwnd,
                    dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                };
                FlashWindowEx(ref flash);
            }
        }
        catch { /* best-effort */ }

        // No focus on open: the user brings the prompt forward by clicking or switching
        // to it, and only then does Deny receive focus (see FocusDenyAfterUserActivation).
        if (_firstFrameFallbackTimer is null)
        {
            CompositionTarget.Rendering += OnFirstFrameRendering;
            _firstFrameFallbackTimer = new DispatcherTimer { Interval = FirstFrameFallback };
            _firstFrameFallbackTimer.Tick += (_, _) => StartArmDelay();
            _firstFrameFallbackTimer.Start();
        }
        return _tcs.Task;
    }

    private void Decide(ExecApprovalPromptOutcome outcome)
    {
        // Decisions, Escape included, are ignored until the guard arms. Closing the
        // window (X or Alt+F4) still resolves Deny at any time.
        if (IsClosed || !_decisionsArmed) return;
        _outcome = outcome;
        Close();
    }

    // Restores the keyboard safeguard without taking focus: Deny is focused only after
    // the user has activated the prompt and decisions are armed, once per prompt.
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        FocusDenyAfterUserActivation();
    }

    private void FocusDenyAfterUserActivation()
    {
        if (IsClosed || _denyFocusApplied || !_decisionsArmed || !_windowActive) return;
        _denyFocusApplied = true;
        _denyButton.Focus(FocusState.Programmatic);
    }

    // WinUIEx SetWindowSize and CenterOnScreen call SetWindowPos without SWP_NOACTIVATE.
    // AppWindow placement never requests activation.
    private void PlaceCenteredWithoutActivating()
    {
        var scale = this.GetDpiForWindow() / 96d;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)Math.Round(WindowWidth * scale), area.Width);
        var height = Math.Min((int)Math.Round(WindowHeight * scale), area.Height);
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2),
            width, height));
    }

    // Rendering is raised before each frame on the UI thread, so wait until this window's
    // content is loaded; the arm delay then runs from the prompt's first render pass.
    private void OnFirstFrameRendering(object? sender, object e)
    {
        if (!_windowContent.IsLoaded) return;
        StartArmDelay();
    }

    // Starts the arm delay once: from the first render pass, or from the fallback if no
    // frame is observed, so the prompt can never stay undecidable.
    private void StartArmDelay()
    {
        CompositionTarget.Rendering -= OnFirstFrameRendering;
        _firstFrameFallbackTimer?.Stop();
        if (_armDelayStarted || IsClosed) return;
        _armDelayStarted = true;
        _armTimer?.Start();
    }

    // Anti-clickthrough: every decision button starts disabled and arms together after a
    // short delay from the first render pass, independently of command length or scroll
    // position. A prompt that appears under the pointer or keyboard cannot be decided early.
    private void ArmDecisionGuard()
    {
        _denyButton.IsEnabled = false;
        _allowOnceButton.IsEnabled = false;
        _allowAlwaysButton.IsEnabled = false;
        var hint = LocalizationHelper.GetString("ExecApproval_ArmHint");
        ToolTipService.SetToolTip(_allowOnceButton, hint);
        if (_allowAlwaysAvailable)
            ToolTipService.SetToolTip(_allowAlwaysButton, hint);

        _armTimer = new DispatcherTimer { Interval = DecisionArmDelay };
        _armTimer.Tick += (_, _) =>
        {
            _armTimer?.Stop();
            if (IsClosed) return;

            _decisionsArmed = true;
            _denyButton.IsEnabled = true;
            _allowOnceButton.IsEnabled = true;
            _allowAlwaysButton.IsEnabled = _allowAlwaysAvailable;
            ToolTipService.SetToolTip(_allowOnceButton, null);
            ToolTipService.SetToolTip(_allowAlwaysButton, null);
            FocusDenyAfterUserActivation();
        };
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
        _firstFrameFallbackTimer?.Stop();
        CompositionTarget.Rendering -= OnFirstFrameRendering;
        Activated -= OnWindowActivated;
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
