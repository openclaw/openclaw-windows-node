using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Runtime.InteropServices;
using WinUIEx;

namespace OpenClawTray.Dialogs;

/// <summary>
/// Focused approval surface for inbound pairing requests (devices / nodes asking to join the
/// gateway). Presents one request at a time with full identity + the operator scopes being
/// granted, mirroring the macOS pairing prompt. Acts on the live <see cref="PairingApprovalCoordinator"/>
/// and advances through the queue as decisions are made or requests are resolved elsewhere.
///
/// Security posture: Approve is never the default-focused button and stays disabled for a short
/// delay after a request appears so the dialog cannot be "click-through" approved by a stray
/// keypress/click. Reject and "Decide later" are always available.
/// </summary>
public sealed class PairingApprovalDialog : WindowEx
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;

    private static readonly TimeSpan ApproveArmDelay = TimeSpan.FromMilliseconds(1500);

    private readonly PairingApprovalCoordinator _coordinator;
    private readonly StackPanel _bodyPanel;
    private readonly TextBlock _headingText;
    private readonly TextBlock _queueChip;
    private readonly Button _approveButton;
    private readonly Button _rejectButton;
    private readonly Button _laterButton;

    private DispatcherTimer? _armTimer;
    private string? _currentKey;
    private bool _busy;

    public bool IsClosed { get; private set; }

    public PairingApprovalDialog(PairingApprovalCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        var windowTitle = $"{AppIdentity.DisplayName} - {LocalizationHelper.GetString("PairingApproval_WindowTitle")}";
        Title = windowTitle;
        this.SetWindowSize(460, 460);
        this.CenterOnScreen();
        this.SetIcon("Assets\\openclaw.ico");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        // ── Custom title bar ──
        var titleBar = new Grid { Height = 48, Padding = new Thickness(16, 0, 140, 0) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleIcon = new TextBlock { Text = "🦞", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
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

        // ── Layout: [title][header][scrollable body][buttons] ──
        var outerGrid = new Grid();
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        outerGrid.Children.Add(titleBar);

        var root = new Grid { Padding = new Thickness(28, 8, 28, 24), RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

        // Header: shield glyph + heading + queue chip
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var shield = new FontIcon
        {
            Glyph = "\uE72E", // shield
            FontSize = 28,
            Foreground = ResolveBrush("SystemFillColorCautionBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(shield, 0);
        header.Children.Add(shield);

        _headingText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(_headingText, 1);
        header.Children.Add(_headingText);

        _queueChip = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_queueChip, 2);
        header.Children.Add(_queueChip);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Body (scrollable detail card)
        _bodyPanel = new StackPanel { Spacing = 12 };
        var scroll = new ScrollViewer
        {
            Content = _bodyPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Buttons: [Reject] [Decide later] ............ [Approve]
        var buttonGrid = new Grid { ColumnSpacing = 8 };
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _rejectButton = new Button { Content = BuildButtonContent("\uE711", "SystemFillColorCriticalBrush", LocalizationHelper.GetString("PairingApproval_Reject")) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_rejectButton, "PairingRejectAction");
        _rejectButton.Click += async (_, _) => await DecideAsync(approve: false);
        Grid.SetColumn(_rejectButton, 0);
        buttonGrid.Children.Add(_rejectButton);

        _laterButton = new Button { Content = LocalizationHelper.GetString("PairingApproval_Later") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_laterButton, "PairingLaterAction");
        _laterButton.Click += (_, _) => Close(); // dismiss without deciding; request stays pending
        Grid.SetColumn(_laterButton, 1);
        buttonGrid.Children.Add(_laterButton);

        _approveButton = new Button
        {
            Content = BuildButtonContent("\uE73E", null, LocalizationHelper.GetString("PairingApproval_Approve")),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_approveButton, "PairingApproveAction");
        _approveButton.Click += async (_, _) => await DecideAsync(approve: true);
        Grid.SetColumn(_approveButton, 3);
        buttonGrid.Children.Add(_approveButton);

        Grid.SetRow(buttonGrid, 2);
        root.Children.Add(buttonGrid);

        Grid.SetRow(root, 1);
        outerGrid.Children.Add(root);
        Content = outerGrid;

        _coordinator.ApprovalsChanged += OnApprovalsChanged;
        Closed += OnWindowClosed;

        Render();
    }

    /// <summary>Activates the dialog and forces it to the foreground (it may open from a background event).</summary>
    public void ShowForeground()
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
    }

    private void OnApprovalsChanged(object? sender, EventArgs e)
    {
        if (IsClosed) return;
        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    private void Render()
    {
        if (IsClosed) return;

        var current = _coordinator.Current;
        if (current.Count == 0)
        {
            // Nothing left to decide — close.
            Close();
            return;
        }

        var approval = current[0];
        var keyChanged = !string.Equals(_currentKey, approval.Key, StringComparison.Ordinal);

        // The queue chip always reflects the latest count, even when the
        // front-of-queue request is unchanged.
        if (current.Count > 1)
        {
            _queueChip.Text = string.Format(LocalizationHelper.GetString("PairingApproval_MoreWaiting"), current.Count - 1);
            _queueChip.Visibility = Visibility.Visible;
        }
        else
        {
            _queueChip.Visibility = Visibility.Collapsed;
        }

        // If the request at the front of the queue hasn't changed (an unrelated
        // list refresh arrived), do NOT rebuild the card, reset the in-flight
        // decision guard, or re-arm the approve delay — that would let a second
        // click through while a decision is still in flight for this same request.
        if (!keyChanged)
            return;

        _currentKey = approval.Key;
        _headingText.Text = LocalizationHelper.GetString(
            approval.Kind == PairingApprovalKind.NodePair ? "PairingApproval_NodeHeading" : "PairingApproval_DeviceHeading");

        // Kind-aware approve label ("Approve device" / "Approve node") to reinforce intent.
        _approveButton.Content = BuildButtonContent(
            "\uE73E",
            null,
            LocalizationHelper.GetString(approval.Kind == PairingApprovalKind.NodePair
                ? "PairingApproval_ApproveNode"
                : "PairingApproval_ApproveDevice"));

        _bodyPanel.Children.Clear();
        _bodyPanel.Children.Add(BuildDetailCard(approval));

        // Fresh request — reset interaction state and re-arm the approve guard.
        _busy = false;
        _rejectButton.IsEnabled = true;
        _laterButton.IsEnabled = true;
        ArmApproveGuard();
    }

    private Border BuildDetailCard(PendingApproval approval)
    {
        var card = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
        };
        var stack = new StackPanel { Spacing = 10 };

        var name = string.IsNullOrWhiteSpace(approval.DisplayName)
            ? (string.IsNullOrEmpty(approval.DeviceId)
                ? LocalizationHelper.GetString("PairingApproval_UnknownDevice")
                : approval.DeviceId)
            : approval.DisplayName!;

        stack.Children.Add(new TextBlock
        {
            Text = name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });

        var detailParts = new System.Collections.Generic.List<string>
        {
            string.IsNullOrWhiteSpace(approval.Platform)
                ? LocalizationHelper.GetString("PairingApproval_UnknownPlatform")
                : approval.Platform!,
        };
        if (!string.IsNullOrWhiteSpace(approval.Role)) detailParts.Add(approval.Role!);
        if (!string.IsNullOrWhiteSpace(approval.Version)) detailParts.Add($"v{approval.Version}");
        stack.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", detailParts),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });

        // Friendly scope list (device requests only).
        if (approval.Scopes.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("PairingApproval_AccessRequested"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
            foreach (var label in PairingScopeDescriptions.DescribeAll(approval.Scopes))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 12,
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                stack.Children.Add(row);
            }
        }

        // Repair badge.
        if (approval.IsRepair)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("PairingApproval_RepairBadge"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("SystemFillColorCautionBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        // Footer: truncated id · ip (monospace, tertiary).
        var footerParts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(approval.DeviceId))
            footerParts.Add($"ID {TruncateId(approval.DeviceId)}");
        if (!string.IsNullOrWhiteSpace(approval.RemoteIp))
            footerParts.Add($"IP {approval.RemoteIp}");
        if (footerParts.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", footerParts),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("TextFillColorTertiaryBrush"),
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        // Reassurance line.
        stack.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("PairingApproval_Subtitle"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        card.Child = stack;
        return card;
    }

    private void ArmApproveGuard()
    {
        _armTimer?.Stop();
        _approveButton.IsEnabled = false;
        ToolTipService.SetToolTip(_approveButton, LocalizationHelper.GetString("PairingApproval_ApproveHint"));
        _armTimer = new DispatcherTimer { Interval = ApproveArmDelay };
        _armTimer.Tick += (_, _) =>
        {
            _armTimer?.Stop();
            if (!_busy)
            {
                _approveButton.IsEnabled = true;
                ToolTipService.SetToolTip(_approveButton, null);
            }
        };
        _armTimer.Start();
    }

    private async System.Threading.Tasks.Task DecideAsync(bool approve)
    {
        if (_busy || _currentKey == null) return;
        var decisionKey = _currentKey;
        _busy = true;
        _approveButton.IsEnabled = false;
        _rejectButton.IsEnabled = false;
        _laterButton.IsEnabled = false;

        bool ok;
        try
        {
            // Success drives a coordinator ApprovalsChanged -> Render() which advances or closes.
            ok = approve
                ? await _coordinator.ApproveAsync(decisionKey)
                : await _coordinator.RejectAsync(decisionKey);
        }
        catch
        {
            ok = false;
        }

        // The window may have closed (disconnect -> Reset -> Close) or the queue may have advanced
        // to a different request while the RPC was in flight. Only touch UI if we're still on a live
        // window showing the same request — otherwise this stale continuation must not mutate state.
        if (IsClosed || !string.Equals(_currentKey, decisionKey, StringComparison.Ordinal))
            return;

        if (!ok)
        {
            // Re-enable the secondary actions and RE-ARM the approve guard (don't force-enable
            // Approve — that would defeat the anti-clickthrough delay on the retry).
            _busy = false;
            _rejectButton.IsEnabled = true;
            _laterButton.IsEnabled = true;
            ArmApproveGuard();
        }
        // On success, leave buttons disabled; the pending ApprovalsChanged -> Render() will advance
        // to the next request (re-enabling) or close the dialog.
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        IsClosed = true;
        _armTimer?.Stop();
        _coordinator.ApprovalsChanged -= OnApprovalsChanged;
        Closed -= OnWindowClosed;
    }

    private static StackPanel BuildButtonContent(string glyph, string? glyphBrushKey, string label)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var icon = new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        if (glyphBrushKey != null)
            icon.Foreground = ResolveBrush(glyphBrushKey);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return stack;
    }

    private static string TruncateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length <= 20) return id;
        return $"{id[..8]}…{id[^7..]}";
    }

    private static Brush ResolveBrush(string themeKey) =>
        Application.Current.Resources.TryGetValue(themeKey, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
