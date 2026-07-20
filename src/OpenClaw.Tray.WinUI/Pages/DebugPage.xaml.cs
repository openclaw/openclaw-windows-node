using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// Diagnostics page (route still "debug" for back-compat with command-palette
/// and deep-link aliases). Organized around three user tasks:
///   1. Share diagnostics with support
///   2. Inspect local diagnostics
///   3. Developer tools
///
/// The connection event timeline opens in its own ConnectionStatusWindow
/// (same pattern as the Chat explorations window) so the live event
/// stream doesn't push the rest of the page off-screen. The recent-log
/// reader stays in-page via a Visibility-swap DetailView, mirroring
/// ConnectionPage.AddGatewayPanel.
///
/// Observes the single application model (AppState) directly per
/// docs/DATA_FLOW_ARCHITECTURE.md - no HubWindow dependency.
/// </summary>
public sealed partial class DebugPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;

    private AppState? _appState;
    private bool _suppressOverrideChange;
    private bool _suppressOpenTelemetryEndpointChange;

    private IGatewayTerminalLauncher? _terminalLauncher;
    private GatewayHostAccessPlan _doctorAccessPlan = GatewayHostAccessPlan.None();

    private IGatewayTerminalLauncher TerminalLauncher =>
        _terminalLauncher ??= new GatewayTerminalLauncher(new OpenClawTray.AppLogger());

    private static readonly string LocalAppData = AppIdentity.ResolveLocalDataDirectory();
    private static readonly string LogPath = Path.Combine(LocalAppData, "openclaw-tray.log");
    private static readonly string DeviceKeyPath = Path.Combine(LocalAppData, "device-key-ed25519.json");

    // Brushes for the colored log rendering. Use SystemFill* theme
    // tokens (per docs/design/tokens.md) so the colors track
    // light/dark/HC themes and stay consistent with the ConnectionPage
    // status dots. (The connection event timeline lives in
    // ConnectionStatusWindow, which still uses ARGB literals - flagged
    // as drift in docs/design/surfaces/diagnostics-page.md "Drift
    // candidates".)
    private static Brush ErrorTextBrush => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    private static Brush WarnTextBrush  => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
    private static Brush DimTextBrush   => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    // Detail view mode tracking. Determines what the toolbar buttons do
    // and what content gets rendered into DetailRichText. Bumped on
    // every EnterDetailView so deferred work (background log read) sees
    // a stale generation and skips its UI write if the user has left
    // the detail view in the meantime (Hanselman v2 review #5/#6).
    // Kept as an enum (rather than a bool) so future detail modes can
    // be added without rewriting the generation/race plumbing.
    private enum DetailMode { None, Log }
    private DetailMode _detailMode = DetailMode.None;
    private int _detailGeneration;

    // Hard cap on rows kept in the log RichTextBlock / plain-text
    // mirror. ReadLogTail returns 200 lines today; the 500 ceiling
    // leaves headroom if that ever grows or future detail modes are
    // added without forcing them to know this constant.
    private const int MaxLogRows = 500;

    // Plain-text mirror of log rows for the Copy toolbar action.
    // Capped to MaxLogRows in O(1) via Queue.
    private readonly Queue<string> _detailPlainLines = new();
    private bool _bundlePreviewOpen;

    public DebugPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            CurrentApp.SettingsChanged -= OnSettingsChanged;
            StopCopyFeedbackTimer();
        };
    }

    public void Initialize()
    {
        // Defensive -= before += guards against double-subscription if
        // the page ever gets cached (NavigationCacheMode != Disabled)
        // and Initialize() runs twice on the same instance. Mirrors
        // SessionsPage.xaml.cs:34 (Hanselman v2 review #3).
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        CurrentApp.SettingsChanged -= OnSettingsChanged;

        _appState = CurrentApp.AppState!;
        if (_appState != null) _appState.PropertyChanged += OnAppStateChanged;
        // Listen for Settings → Save round-trips so the gateway URL in
        // the top InfoBar updates without waiting for a Status flip
        // (per docs/DATA_FLOW_ARCHITECTURE.md reactive-by-default ethos).
        CurrentApp.SettingsChanged += OnSettingsChanged;
        UpdateStatusInfoBar();
        UpdateGatewayDoctorCard();
        LoadDeviceIdentity();
        LoadOpenTelemetryEndpoint();
        LoadChatSurfaceOverrides();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Status):
            case nameof(AppState.GatewaySelf):
                UpdateStatusInfoBar();
                UpdateGatewayDoctorCard();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        UpdateStatusInfoBar();
        UpdateGatewayDoctorCard();
        LoadOpenTelemetryEndpoint();
    }

    /// <summary>
    /// Reset detail-mode state when the user navigates to a different
    /// page so any in-flight async log read becomes a no-op via the
    /// generation counter. Also stops the copy-feedback timer so it
    /// can't tick on a detached visual tree.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _detailMode = DetailMode.None;
        _detailGeneration++;
        StopCopyFeedbackTimer();
        base.OnNavigatedFrom(e);
    }

    // ── Top status InfoBar ───────────────────────────────────────────

    private void UpdateStatusInfoBar()
    {
        var gatewayUrl = CurrentApp.Settings?.GetEffectiveGatewayUrl();
        var gatewayDisplay = string.IsNullOrWhiteSpace(gatewayUrl)
            ? LocalizationHelper.GetString("DebugPage_NoGatewayConfigured")
            : gatewayUrl;
        var status = _appState?.Status ?? ConnectionStatus.Disconnected;

        switch (status)
        {
            case ConnectionStatus.Connected:
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = LocalizationHelper.GetConnectionStatusText(status);
                StatusInfoBar.Message = LocalizationHelper.Format("DebugPage_StatusConnectedFormat", gatewayDisplay);
                break;
            case ConnectionStatus.Connecting:
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = LocalizationHelper.GetConnectionStatusText(status);
                StatusInfoBar.Message = LocalizationHelper.Format("DebugPage_StatusConnectingFormat", gatewayDisplay);
                break;
            case ConnectionStatus.Disconnected:
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = LocalizationHelper.GetConnectionStatusText(status);
                StatusInfoBar.Message = LocalizationHelper.Format("DebugPage_StatusDisconnectedFormat", gatewayDisplay);
                break;
            case ConnectionStatus.Error:
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = LocalizationHelper.GetConnectionStatusText(status);
                StatusInfoBar.Message = LocalizationHelper.Format("DebugPage_StatusErrorFormat", gatewayDisplay);
                break;
            default:
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = LocalizationHelper.GetConnectionStatusText(status);
                StatusInfoBar.Message = LocalizationHelper.Format("DebugPage_StatusGatewayFormat", gatewayDisplay);
                break;
        }
    }

    private void OnManageOnConnection(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    // ── Gateway doctor (app-managed WSL only) ────────────────────────

    /// <summary>
    /// Show the "Run gateway doctor" card only when the active gateway is an
    /// app-managed WSL distro we can run commands in (CanControlWslGateway).
    /// SSH/remote gateways have no such control surface, so the section stays
    /// collapsed. Mirrors ConnectionPage's gateway-host gating.
    /// </summary>
    private void UpdateGatewayDoctorCard()
    {
        var activeRecord = CurrentApp.Registry?.GetActive();
        _doctorAccessPlan = GatewayHostAccessClassifier.Classify(activeRecord);
        GatewayDoctorSection.Visibility = _doctorAccessPlan.CanControlWslGateway
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnRunGatewayDoctor(object sender, RoutedEventArgs e)
    {
        if (!_doctorAccessPlan.CanControlWslGateway)
        {
            return;
        }

        try
        {
            TerminalLauncher.OpenGatewayDoctor(_doctorAccessPlan);
        }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Warn($"[DebugPage] Failed to launch gateway doctor: {ex.Message}");
        }
    }

    // ── Detail view (recent log) ─────────────────────────────────────

    private void OnOpenEventTimeline(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).ShowConnectionStatus();

    private void OnShowRecentLog(object sender, RoutedEventArgs e)
        => EnterDetailView(DetailMode.Log);

    private void OnBackToMain(object sender, RoutedEventArgs e)
        => LeaveDetailView();

    private void EnterDetailView(DetailMode mode)
    {
        _detailMode = mode;
        // Bump generation BEFORE clearing buffers so any in-flight
        // pending log-read continuation sees a stale generation and
        // skips its UI write.
        _detailGeneration++;
        _detailPlainLines.Clear();
        DetailRichText.Blocks.Clear();

        if (mode == DetailMode.Log)
        {
            DetailTitle.Text = LocalizationHelper.GetString("DebugPage_RecentLogTitle");
            DetailCaption.Text = LocalizationHelper.Format("DebugPage_RecentLogCaptionFormat", LogPath);
            DetailOpenFileButton.Visibility = Visibility.Visible;
            DetailRefreshButton.Visibility = Visibility.Visible;
            _ = LoadLogFileAsync(_detailGeneration);
        }

        MainView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Visible;

        // Focus the back link so screen readers + keyboard users land on
        // the right element when entering the detail view.
        _ = DetailBackButton.Focus(FocusState.Programmatic);
    }

    private void LeaveDetailView()
    {
        _detailMode = DetailMode.None;
        _detailGeneration++;
        DetailView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        DetailRichText.Blocks.Clear();
        _detailPlainLines.Clear();
    }

    private void OnDetailRefresh(object sender, RoutedEventArgs e)
    {
        if (_detailMode == DetailMode.Log)
        {
            _detailGeneration++;
            _ = LoadLogFileAsync(_detailGeneration);
        }
    }

    private void OnDetailCopy(object sender, RoutedEventArgs e)
    {
        ClipboardHelper.CopyText(DiagnosticsExportSanitizer.SanitizeTextBlock(string.Concat(_detailPlainLines)));
    }

    private void OnDetailOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LogPath))
                Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open log file failed: {ex.Message}");
        }
    }

    // ── Detail mode: recent log ──────────────────────────────────────

    // Parse the leading "[severity]" or "LEVEL " marker in log lines.
    // Matches "[info]" / "[warn]" / "[error]" / "[debug]" plus a few of
    // the legacy uppercase forms ("INFO", "WARN", "ERROR"). Anything
    // else falls back to default coloring.
    private static readonly Regex LogSeverityPattern = new(
        @"\[(?<sev>info|warn|warning|error|debug|trace)\]|\b(?<bare>INFO|WARN|WARNING|ERROR|DEBUG|TRACE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    // Timestamp prefix recognized in our log lines so we can render it
    // dim without forcing a brittle full grammar.
    private static readonly Regex LogTimestampPattern = new(
        @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    private async Task LoadLogFileAsync(int generation)
    {
        DetailRichText.Blocks.Clear();
        _detailPlainLines.Clear();

        if (!File.Exists(LogPath))
        {
            DetailRichText.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = "No log file found.", Foreground = DimTextBrush } }
            });
            AppendPlain("No log file found.\n");
            return;
        }

        string[] lines;
        try
        {
            lines = await Task.Run(() => DiagnosticsLogTailReader.ReadSanitizedTail(
                LogPath,
                new DiagnosticsTailOptions(MaxLines: 200, MaxLineChars: 8_000, MaxSectionChars: 128_000, MaxReadBytes: 512_000)).ToArray());
        }
        catch (Exception ex)
        {
            // The user may have switched modes or navigated away while
            // we awaited. Skip writing to the UI if so (Hanselman v2 #5).
            if (_detailMode != DetailMode.Log || _detailGeneration != generation) return;
            DetailRichText.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = $"Failed to read log: {ex.Message}", Foreground = ErrorTextBrush } }
            });
            return;
        }

        // Hanselman v2 #5: re-check generation after the await so a
        // long log read can't clobber a timeline view the user switched
        // to in the meantime.
        if (_detailMode != DetailMode.Log || _detailGeneration != generation) return;

        foreach (var line in lines)
        {
            DetailRichText.Blocks.Add(CreateLogParagraph(line));
            AppendPlain(line + "\n");
        }
        ScrollDetailToEnd();
    }

    private static Paragraph CreateLogParagraph(string line)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };

        // Render the leading timestamp dim if present so the severity-
        // colored portion stands out without busy text.
        var tsMatch = LogTimestampPattern.Match(line);
        var bodyStart = 0;
        if (tsMatch.Success)
        {
            para.Inlines.Add(new Run { Text = tsMatch.Value, Foreground = DimTextBrush });
            bodyStart = tsMatch.Length;
        }

        var rest = line.Substring(bodyStart);
        var sevBrush = ResolveLogSeverityBrush(rest);

        if (sevBrush != null)
            para.Inlines.Add(new Run { Text = rest, Foreground = sevBrush });
        else
            para.Inlines.Add(new Run { Text = rest });

        return para;
    }

    private static SolidColorBrush? ResolveLogSeverityBrush(string text)
    {
        var match = LogSeverityPattern.Match(text);
        if (!match.Success) return null;
        var sev = (match.Groups["sev"].Success ? match.Groups["sev"].Value
                                                : match.Groups["bare"].Value).ToLowerInvariant();
        return sev switch
        {
            "error" => ErrorTextBrush as SolidColorBrush,
            "warn" or "warning" => WarnTextBrush as SolidColorBrush,
            "debug" or "trace" => DimTextBrush as SolidColorBrush,
            _ => null
        };
    }

    // ── Detail view: shared helpers ──────────────────────────────────

    private void AppendPlain(string text)
    {
        _detailPlainLines.Enqueue(text);
        // Keep plain-text mirror in lock-step with the visual buffer
        // cap so Copy never serializes more than MaxLogRows.
        while (_detailPlainLines.Count > MaxLogRows)
            _detailPlainLines.Dequeue();
    }

    private void ScrollDetailToEnd()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            DetailScroll.UpdateLayout();
            DetailScroll.ChangeView(null, DetailScroll.ScrollableHeight, null);
        });
    }

    // ── Section 1: Share diagnostics with support ────────────────────

    private void OnCreateDiagnosticsBundle(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            ShowDiagnosticsBundlePreviewAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnCreateDiagnosticsBundle));

    private async Task ShowDiagnosticsBundlePreviewAsync()
    {
        if (_bundlePreviewOpen)
            return;

        _bundlePreviewOpen = true;
        try
        {
            var connectionEvents = CurrentApp.GetConnectionDiagnosticEvents().ToArray();
            await ShowBundlePreviewAsync(
                title: "Diagnostics bundle",
                buildText: state => DiagnosticsBundleBuilder.BuildCached(state, connectionEvents),
                suggestedFileName: $"openclaw-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                headerCaption: "This is the complete sanitized bundle that would be copied or saved. Review before sharing.");
        }
        finally
        {
            _bundlePreviewOpen = false;
        }
    }

    private async Task ShowBundlePreviewAsync(
        string title,
        Func<GatewayCommandCenterState, string> buildText,
        string suggestedFileName,
        string headerCaption)
    {
        if (XamlRoot == null) return;
        var state = CurrentApp.BuildCommandCenterState();
        if (state == null) return;

        var buildTask = Task.Run(() =>
        {
            try
            {
                return buildText(state) ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Error($"Diagnostics bundle build failed: {ex}");
                return $"Failed to build diagnostics bundle: {ex.Message}";
            }
        });

        var dialog = new DiagnosticsBundleDialog { XamlRoot = XamlRoot, Title = title };
        // Just-in-time HWND resolution so a Hub-window close that happens
        // between dialog open and Save click can't land a stale handle in
        // the file picker (Hanselman v2 #4).
        dialog.Configure("Preparing diagnostics bundle…", headerCaption, suggestedFileName,
            hwndProvider: () => CurrentApp.GetHubWindowHandle());
        dialog.SetBundleText("Preparing diagnostics bundle…", isReady: false);
        var updateTask = UpdateBundleDialogWhenReadyAsync(dialog, buildTask);
        await Task.Yield();
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            await updateTask;
        }
    }

    private async Task UpdateBundleDialogWhenReadyAsync(DiagnosticsBundleDialog dialog, Task<string> buildTask)
    {
        var text = await buildTask;
        dialog.SetBundleText(text, isReady: true);
    }

    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var logsDir = Path.Combine(LocalAppData, "Logs");
            Directory.CreateDirectory(logsDir);
            Process.Start(new ProcessStartInfo(logsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open diagnostics folder failed: {ex.Message}");
        }
    }

    private void OnCopySupportContext(object sender, RoutedEventArgs e)
        => CopyDiagnosticText("Support context", CommandCenterTextHelper.BuildSupportContext);

    private void OnCopyDebugBundle(object sender, RoutedEventArgs e)
        => CopyDiagnosticText(
            "Summary debug bundle",
            CommandCenterTextHelper.BuildDebugBundle);

    private void OnCopyBrowserSetup(object sender, RoutedEventArgs e)
        => CopyDiagnosticText("Browser setup guidance", CommandCenterTextHelper.BuildBrowserSetupGuidance);

    private void OnCopyPortDiagnostics(object sender, RoutedEventArgs e)
        => CopyDiagnosticText("Port diagnostics", s => CommandCenterTextHelper.BuildPortDiagnosticsSummary(s.PortDiagnostics));

    private void OnCopyCapabilityDiagnostics(object sender, RoutedEventArgs e)
        => CopyDiagnosticText("Capability diagnostics", CommandCenterTextHelper.BuildCapabilityDiagnosticsSummary);

    private void CopyDiagnosticText(string label, Func<GatewayCommandCenterState, string> build)
    {
        var state = CurrentApp.BuildCommandCenterState();
        if (state == null) return;
        try
        {
            ClipboardHelper.CopyText(DiagnosticsExportSanitizer.SanitizeTextBlock(build(state) ?? string.Empty));
            ShowCopyFeedback(label);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy diagnostic failed: {ex.Message}");
        }
    }

    // ── Copy-to-clipboard feedback ───────────────────────────────────

    // DispatcherTimer that auto-closes the inline "Copied" InfoBar so
    // the success notice doesn't linger after the user has moved on.
    // Reused across copy actions - Start() restarts the countdown if
    // the user fires another copy before the previous tick.
    //
    // Lifecycle: created lazily on first ShowCopyFeedback, stopped + nulled
    // by StopCopyFeedbackTimer() in Unloaded and OnNavigatedFrom so it
    // can't tick on a detached visual tree. Mirrors the
    // ConnectionPage._reconnectMaskTimer / PermissionsPage._execSavedHintTimer
    // pattern (Hanselman dual-model review #1).
    private DispatcherTimer? _copyFeedbackTimer;
    private static readonly TimeSpan CopyFeedbackDuration = TimeSpan.FromSeconds(2.5);

    private void ShowCopyFeedback(string label)
    {
        CopyFeedbackInfoBar.Message = LocalizationHelper.Format("DebugPage_CopyFeedbackFormat", label);
        CopyFeedbackInfoBar.IsOpen = true;

        if (_copyFeedbackTimer == null)
        {
            _copyFeedbackTimer = new DispatcherTimer { Interval = CopyFeedbackDuration };
            _copyFeedbackTimer.Tick += (_, _) =>
            {
                // Use ?.Stop() rather than !.Stop() - StopCopyFeedbackTimer()
                // may have nulled the field between queue-time and execute-time
                // (DispatcherTimer.Stop in teardown does not cancel ticks
                // already queued on the DispatcherQueue).
                _copyFeedbackTimer?.Stop();
                // IsLoaded guard: same reason - a tick queued before
                // teardown can still fire after Unloaded. Touching IsOpen
                // on a detached FrameworkElement is undefined in WinUI 3.
                if (CopyFeedbackInfoBar.IsLoaded)
                    CopyFeedbackInfoBar.IsOpen = false;
            };
        }
        else
        {
            // Restart the countdown so back-to-back copies keep the
            // notice visible for the full duration after the last one.
            _copyFeedbackTimer.Stop();
        }
        _copyFeedbackTimer.Start();
    }

    private void StopCopyFeedbackTimer()
    {
        if (_copyFeedbackTimer != null)
        {
            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer = null;
        }
    }

    // ── Device identity ──────────────────────────────────────────────

    private void LoadDeviceIdentity()
    {
        try
        {
            if (File.Exists(DeviceKeyPath))
            {
                var json = File.ReadAllText(DeviceKeyPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("deviceId", out var id))
                {
                    var deviceId = id.GetString() ?? "Unknown";
                    DeviceIdText.Text = deviceId.Length > 24
                        ? string.Concat(deviceId.AsSpan(0, 16), "…", deviceId.AsSpan(deviceId.Length - 6))
                        : deviceId;
                    DeviceIdText.Tag = deviceId;
                }
                else
                {
                    DeviceIdText.Text = "Not found";
                }

                if (doc.RootElement.TryGetProperty("publicKey", out var pk))
                {
                    var pkText = pk.GetString() ?? "Unknown";
                    PublicKeyText.Text = pkText;
                    PublicKeyText.Tag = pkText;
                }
                else
                {
                    PublicKeyText.Text = "Not found";
                }
            }
            else
            {
                DeviceIdText.Text = "No device key file";
                PublicKeyText.Text = "-";
            }
        }
        catch (Exception ex)
        {
            DeviceIdText.Text = $"Error: {ex.Message}";
            PublicKeyText.Text = "-";
        }
    }

    private void OnCopyDeviceId(object sender, RoutedEventArgs e)
    {
        var full = DeviceIdText.Tag as string ?? DeviceIdText.Text ?? string.Empty;
        ClipboardHelper.CopyText(full);
    }

    private void OnCopyPublicKey(object sender, RoutedEventArgs e)
    {
        var full = PublicKeyText.Tag as string ?? PublicKeyText.Text ?? string.Empty;
        ClipboardHelper.CopyText(full);
    }

    // ── Section 3: Developer tools ───────────────────────────────────

    private void LoadOpenTelemetryEndpoint()
    {
        _suppressOpenTelemetryEndpointChange = true;
        try
        {
            OpenTelemetryEndpointBox.Text = CurrentApp.Settings?.OpenTelemetryEndpoint ?? string.Empty;
            SelectOpenTelemetryProtocol(CurrentApp.Settings?.OpenTelemetryProtocol);
            UpdateOpenTelemetryEndpointStatus();
        }
        finally
        {
            _suppressOpenTelemetryEndpointChange = false;
        }
    }

    private void OnOpenTelemetryEndpointTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOpenTelemetryEndpointChange)
            return;

        UpdateOpenTelemetryEndpointStatus();
    }

    private void OnOpenTelemetryProtocolChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressOpenTelemetryEndpointChange)
            return;

        UpdateOpenTelemetryEndpointStatus();
    }

    private void OnSaveOpenTelemetryEndpoint(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings == null)
            return;

        if (!TryNormalizeOpenTelemetryEndpoint(OpenTelemetryEndpointBox.Text, out var endpoint, out _))
        {
            UpdateOpenTelemetryEndpointStatus();
            return;
        }

        CurrentApp.Settings.OpenTelemetryEndpoint = endpoint ?? string.Empty;
        CurrentApp.Settings.OpenTelemetryProtocol = GetSelectedOpenTelemetryProtocol();
        CurrentApp.Settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        UpdateOpenTelemetryEndpointStatus(saved: true);
    }

    private void OnClearOpenTelemetryEndpoint(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Settings == null)
            return;

        _suppressOpenTelemetryEndpointChange = true;
        try
        {
            OpenTelemetryEndpointBox.Text = string.Empty;
            SelectOpenTelemetryProtocol(OpenTelemetryEndpointProtocol.Grpc);
        }
        finally
        {
            _suppressOpenTelemetryEndpointChange = false;
        }

        CurrentApp.Settings.OpenTelemetryEndpoint = string.Empty;
        CurrentApp.Settings.OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Grpc;
        CurrentApp.Settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        UpdateOpenTelemetryEndpointStatus(cleared: true);
    }

    private void OnResendOpenTelemetryProbe(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            ResendOpenTelemetryProbeAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnResendOpenTelemetryProbe));

    private async Task ResendOpenTelemetryProbeAsync()
    {
        ResendOpenTelemetryProbeButton.IsEnabled = false;
        bool? probeFlushed = null;
        try
        {
            probeFlushed = await ((IAppCommands)CurrentApp).ResendOpenTelemetryProbeAsync();
        }
        finally
        {
            UpdateOpenTelemetryEndpointStatus(probeFlushed: probeFlushed);
        }
    }

    private void UpdateOpenTelemetryEndpointStatus(
        bool saved = false,
        bool cleared = false,
        bool? probeFlushed = null)
    {
        var current = CurrentApp.Settings?.OpenTelemetryEndpoint ?? string.Empty;
        var currentProtocol = OpenTelemetryEndpointProtocol.Normalize(CurrentApp.Settings?.OpenTelemetryProtocol);
        var selectedProtocol = GetSelectedOpenTelemetryProtocol();
        var raw = OpenTelemetryEndpointBox.Text;
        var valid = TryNormalizeOpenTelemetryEndpoint(raw, out var endpoint, out var error);
        var endpointText = endpoint ?? string.Empty;
        var hasEndpoint = !string.IsNullOrWhiteSpace(endpointText);
        var dirty =
            !string.Equals(current, endpointText, StringComparison.Ordinal) ||
            !string.Equals(currentProtocol, selectedProtocol, StringComparison.Ordinal);

        SaveOpenTelemetryEndpointButton.IsEnabled = valid && dirty;
        ResendOpenTelemetryProbeButton.IsEnabled = valid && hasEndpoint && !dirty;
        ClearOpenTelemetryEndpointButton.IsEnabled = hasEndpoint || !string.IsNullOrWhiteSpace(current);
        OpenTelemetryEndpointSummary.Text = string.IsNullOrWhiteSpace(current)
            ? LocalizationHelper.GetString("DiagnosticsPage_OpenTelemetrySummary_NotConfigured")
            : LocalizationHelper.GetString("DiagnosticsPage_OpenTelemetrySummary_Configured");

        if (!valid)
        {
            OpenTelemetryEndpointStatusText.Foreground = WarnTextBrush;
            OpenTelemetryEndpointStatusText.Text = error ?? LocalizationHelper.GetString("DiagnosticsPage_OpenTelemetryEndpointStatus_InvalidMessage");
            return;
        }

        OpenTelemetryEndpointStatusText.Foreground = DimTextBrush;

        if (cleared)
        {
            OpenTelemetryEndpointStatusText.Text = LocalizationHelper.GetString("DiagnosticsPage_OpenTelemetryEndpointStatus_ClearedTitle");
            return;
        }

        if (saved && hasEndpoint)
        {
            OpenTelemetryEndpointStatusText.Text = LocalizationHelper.GetString("DiagnosticsPage_OpenTelemetryEndpointStatus_SavedTitle");
            return;
        }

        if (probeFlushed.HasValue)
        {
            OpenTelemetryEndpointStatusText.Foreground =
                probeFlushed.Value ? DimTextBrush : WarnTextBrush;
            OpenTelemetryEndpointStatusText.Text = LocalizationHelper.GetString(
                probeFlushed.Value
                    ? "DiagnosticsPage_OpenTelemetryEndpointStatus_ProbeFlushedMessage"
                    : "DiagnosticsPage_OpenTelemetryEndpointStatus_ProbeFailedMessage");
            return;
        }

        OpenTelemetryEndpointStatusText.Text = dirty
            ? LocalizationHelper.GetString("DiagnosticsPage_OpenTelemetryEndpointStatus_UnsavedMessage")
            : string.Empty;
    }

    private void SelectOpenTelemetryProtocol(string? protocol)
    {
        var normalized = OpenTelemetryEndpointProtocol.Normalize(protocol);
        OpenTelemetryProtocolGrpcButton.IsChecked = normalized == OpenTelemetryEndpointProtocol.Grpc;
        OpenTelemetryProtocolHttpButton.IsChecked = normalized == OpenTelemetryEndpointProtocol.HttpProtobuf;
    }

    private string GetSelectedOpenTelemetryProtocol() =>
        OpenTelemetryProtocolHttpButton.IsChecked == true
            ? OpenTelemetryEndpointProtocol.HttpProtobuf
            : OpenTelemetryEndpointProtocol.Grpc;

    private static bool TryNormalizeOpenTelemetryEndpoint(string? raw, out string? endpoint, out string? error)
    {
        endpoint = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        error = null;

        if (endpoint == null)
            return true;

        if (!OpenTelemetryEndpointOptions.TryCreateEndpointUri(endpoint, out _))
        {
            error = LocalizationHelper.GetString("DiagnosticsPage_OpenTelemetryEndpointStatus_InvalidMessage");
            return false;
        }

        return true;
    }

    private void LoadChatSurfaceOverrides()
    {
        _suppressOverrideChange = true;
        try
        {
            SelectByTag(HubChatOverrideCombo, DebugChatSurfaceOverrides.HubChat.ToString());
            SelectByTag(TrayChatOverrideCombo, DebugChatSurfaceOverrides.TrayChat.ToString());
        }
        finally
        {
            _suppressOverrideChange = false;
        }
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static ChatSurfaceOverride ParseOverride(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ChatSurfaceOverride>(item.Tag?.ToString(), out var v))
            return v;
        return ChatSurfaceOverride.NoOverride;
    }

    private void OnHubChatOverrideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverrideChange) return;
        DebugChatSurfaceOverrides.HubChat = ParseOverride(HubChatOverrideCombo);
    }

    private void OnTrayChatOverrideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOverrideChange) return;
        DebugChatSurfaceOverrides.TrayChat = ParseOverride(TrayChatOverrideCombo);
    }

}
