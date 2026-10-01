using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine.UI.Pages;

internal sealed record ProgressPageArgs(
    SetupConfig Config,
    bool ShowMilestoneOnly,
    bool LocalAiRecoveryOnly,
    string DataDir,
    string LocalDataDir);

public sealed partial class ProgressPage : Page, IAsyncDisposable
{
    private SetupConfig? _config;
    private SetupPipeline? _pipeline;
    private SetupLogger? _logger;
    private CancellationTokenSource? _runCts;
    private int _logLineCount;
    private bool _pipelineFinished;
    private string _dataDir = null!;
    private string _localDataDir = null!;
    private Uri? _tailscaleAuthorizationUri;
    private HashSet<string> _activeStepIds = [];
    private bool _localAiRecoveryOnly;
    private SetupInstallationProgress? _installationProgress;
    private Task _pipelineTask = Task.CompletedTask;
    private bool _closed;
    private SetupWindow? _window;
    private const int MaxLogLines = 200;

    internal bool IsPipelineRunning => _runCts != null && !_pipelineFinished;

    public ProgressPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPipeline();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = e.Parameter as ProgressPageArgs;
        _config = args?.Config ?? e.Parameter as SetupConfig ?? new SetupConfig();
        _window = SetupWindow.Active;
        _dataDir = args?.DataDir ?? SetupContext.ResolveDataDir();
        _localDataDir = args?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        _localAiRecoveryOnly = args?.LocalAiRecoveryOnly == true;
        var steps = BuildSteps(_config, _localAiRecoveryOnly);
        _installationProgress = new(steps, _localAiRecoveryOnly);
        _activeStepIds = steps
            .Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);
        TitleText.Text = SetupLocalization.GetString(_config.NativeLocalAiAcquisition
            ? "Onboarding_AiSetup_LocalInstalling"
            : _localAiRecoveryOnly ? "Onboarding_V4_RecoveryTitle" : "Onboarding_V4_InstallationTitle");
        SubtitleText.Text = SetupLocalization.GetString("Onboarding_V4_InstallationSubtitle");
        if (_localAiRecoveryOnly)
            InstallPhase.Header = SetupLocalization.GetString("Onboarding_V4_RecoveryInstall");

        RenderInstallationOverview();
        if (args?.ShowMilestoneOnly == true)
        {
            ShowGatewayInstalledMilestone();
            return;
        }

        if (SetupPreview.IsActive)
        {
            if (SetupPreview.RequestedPage == "milestone")
            {
                ShowGatewayInstalledMilestone();
                return;
            }
            RenderProgressPreview();
            return;
        }
        StartPipeline();
    }

    private void RenderProgressPreview()
    {
        bool localAiPreview =
            _config?.LocalAi.Enabled == true ||
            SetupPreview.RequestedPage == "progress-local-ai";
        foreach (var step in BuildSteps(_config!, _localAiRecoveryOnly))
        {
            var isCurrent = step.Id == (localAiPreview ? "acquire-local-ai-model" : "wsl-create");
            _installationProgress!.Apply(new(step.Id, step.DisplayName, isCurrent ? null : StepOutcome.Success, null));
            if (isCurrent) break;
        }
        RenderInstallationOverview();
        if (localAiPreview)
            ApplyDetailProgress(new("acquire-local-ai-model", "Downloading Qwen3.8-27B-UD-Q4_K_M.gguf",
                6_322_405_376, 16_464_440_224, SetupDetailProgressUnit.Bytes));
        LogText.Text =
            "[12:04:01] [info] Windows 11 26100 · WSL 2 present\n" +
            "[12:04:03] [info] port 127.0.0.1:18789 available\n" +
            "[12:04:05] [info] wsl --install -d Ubuntu-24.04 --name OpenClawGateway --no-launch\n" +
            "[12:04:38] [info] downloading distro image (disk use varies)\n" +
            "[12:04:38] [changed] created %LOCALAPPDATA%\\OpenClawTray\\wsl\\OpenClawGateway\\\n" +
            "[12:04:38] [info] next: install CLI via HTTPS, configure loopback gateway\n";
    }

    private void StartPipeline() =>
        AsyncEventHandlerGuard.Run(
            () => _pipelineTask = StartPipelineAsync(),
            NullLogger.Instance,
            nameof(StartPipeline));

    private async Task StartPipelineAsync()
    {
        var config = _config!;
        if (_runCts != null)
            return;

        config.LogPath ??= Path.Combine(
            _dataDir, "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        _runCts = cts;

        try
        {
            _logger = new SetupLogger(config.LogPath,
                Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);

            _logger.LogEmitted += OnLogEmitted;

            var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
            using var journal = new TransactionJournal(journalPath);
            var commands = new CommandRunner(_logger);
            var ctx = new SetupContext(
                config,
                _logger,
                journal,
                commands,
                cts.Token,
                _dataDir,
                _localDataDir);
            ctx.ExternalAuthorizationPresenter = new ProgressAuthorizationPresenter(DispatcherQueue, ShowTailscaleAuthorization);
            ctx.DetailProgress = new DirectProgress<SetupDetailProgressEvent>(OnDetailProgress);

            var steps = BuildSteps(config, _localAiRecoveryOnly);
            var setupOwner = _window;
            ctx.ExpectedGatewayRegistry = config.NativeLocalAiAcquisition ? null : setupOwner?.BeginGatewaySetup();
            ctx.PersistTraySettings = _window is { } settingsOwner ? settingsOwner.PersistPipelineSettings : null;
            _pipeline = new SetupPipeline(steps);
            _pipeline.StepProgress += OnStepProgress;

            var pipeline = _pipeline;
            var result = await SetupPipeline.RunWithSettlementAsync(
                () => Task.Run(() => pipeline.RunAsync(ctx), cts.Token),
                outcome => config.NativeLocalAiAcquisition ? Task.CompletedTask : setupOwner?.SettleGatewaySetupAsync(ctx.ExpectedGatewayRegistry,
                    outcome?.Outcome == PipelineOutcome.Success ? config.LocalAiRecoveryGatewayId ?? ctx.GatewayRecordId : null)
                    ?? Task.CompletedTask);
            sw.Stop();
            _pipelineFinished = true;
            if (_closed || _window?.IsClosed == true)
                return;

            var success = result.Outcome == PipelineOutcome.Success;
            ProgressMascot.Mood = success ? OnboardingMascotMood.Happy : OnboardingMascotMood.Sad;
            if (success)
            {
                var gatewayId = config.LocalAiRecoveryGatewayId ?? ctx.GatewayRecordId;
                if (config.LocalAi.Enabled && !config.NativeLocalAiAcquisition)
                {
                    var modelRef = ctx.ResolvedLocalAiModelRef ??
                        throw new InvalidOperationException("The completed Local AI install did not provide its configured model.");
                    _window?.SetExpectedConfiguredModelRef(modelRef,
                        gatewayId ?? throw new InvalidOperationException("The completed Local AI install did not provide its Gateway."));
                }
                if (config.NativeLocalAiAcquisition)
                {
                    if (_window is not { } owner)
                        throw new InvalidOperationException(SetupLocalization.GetString("Onboarding_Flow_CompletionUnavailable"));
                    owner.ContinueInstalledNativeLocalAi();
                }
                else if (OnboardingFlowPolicy.RequiresAiSetup(config))
                {
                    if (_window?.TryNavigateToWizard() != true)
                        throw new InvalidOperationException(SetupLocalization.GetString("Onboarding_Flow_AiNavigationFailed"));
                }
                else
                {
                    if (_window is not { } setupWindow)
                        throw new InvalidOperationException(SetupLocalization.GetString("Onboarding_Flow_CompletionUnavailable"));
                    await setupWindow.CompleteSetupAsync();
                }
            }
            else
            {
                if (result.Outcome == PipelineOutcome.Cancelled)
                {
                    _installationProgress?.Cancel();
                    RenderInstallationOverview();
                }
                var errorMsg = result.Outcome == PipelineOutcome.Cancelled
                    ? "Setup was cancelled."
                    : result.FailedStepId != null
                        ? $"Step '{result.FailedStepId}' failed: {result.Message}"
                        : result.Message;
                _window?.NavigateToComplete(
                    false,
                    sw.Elapsed,
                    config.LogPath,
                    errorMsg,
                    result.CompatibilityFailure,
                    result.Detail,
                    restartRequired: result.RequiresRestart);
            }
        }
        catch (SetupPipelineSettlementException ex)
        {
            sw.Stop();
            _pipelineFinished = true;
            _logger?.Error($"Setup pipeline and settlement outcomes: {ex}");
            ProgressMascot.Mood = OnboardingMascotMood.Sad;
            if (!_closed && _window?.IsClosed == false)
                _window.NavigateToComplete(false, sw.Elapsed, config.LogPath, SetupLogger.Sanitize(ex.Message),
                    ex.OriginalResult?.CompatibilityFailure, ex.OriginalResult?.Detail,
                    restartRequired: ex.OriginalResult?.RequiresRestart == true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            _pipelineFinished = true;
            if (!_closed && _window?.IsClosed == false)
            {
                _installationProgress?.Cancel();
                RenderInstallationOverview();
                _window.NavigateToComplete(false, sw.Elapsed, config.LogPath, "Setup was cancelled.");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _pipelineFinished = true;
            _logger?.Error($"Setup UI pipeline failed: {ex.Message}");
            ProgressMascot.Mood = OnboardingMascotMood.Sad;
            if (!_closed && _window?.IsClosed == false)
                _window.NavigateToComplete(false, sw.Elapsed, config.LogPath, $"Setup crashed: {ex.Message}");
        }
        finally
        {
            if (_logger != null)
                _logger.LogEmitted -= OnLogEmitted;
            if (_pipeline != null)
                _pipeline.StepProgress -= OnStepProgress;
            _logger?.Dispose();
            _logger = null;
            _pipeline = null;
            if (ReferenceEquals(_runCts, cts))
                _runCts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        CancelPipeline();
        await _pipelineTask;
    }

    private void CancelPipeline()
    {
        if (!_pipelineFinished)
            _runCts?.Cancel();
    }

    private void OnStepProgress(object? sender, StepProgressEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _installationProgress?.Apply(e);
            RenderInstallationOverview();
            DownloadActivity.Visibility = DownloadProgress.Visibility = Visibility.Collapsed;
            ProgressMascot.Mood = e.Outcome is StepOutcome.Failed or StepOutcome.FailedTerminal
                ? OnboardingMascotMood.Sad : OnboardingMascotMood.Working;
        });
    }

    private void RenderInstallationOverview()
    {
        if (_installationProgress is not { } progress) return;
        foreach (var phase in progress.Phases)
        {
            var text = phase.Phase switch
            {
                SetupInstallationPhase.Prepare => PrepareStatus,
                SetupInstallationPhase.Install => InstallStatus,
                _ => ConnectStatus,
            };
            text.Apply(phase.Status);
        }
        StepCount.Text = SetupLocalization.Format("Onboarding_V4_StepCount", progress.CompletedSteps, progress.TotalSteps);
        CurrentActivity.Text = progress.CurrentActivity ?? SetupLocalization.GetString(
            progress.CompletedSteps == progress.TotalSteps ? "Onboarding_V4_StatusComplete" : "Onboarding_V4_StatusPending");
    }

    private void OnDetailProgress(SetupDetailProgressEvent progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            ApplyDetailProgress(progress);
        });
    }

    private void ApplyDetailProgress(SetupDetailProgressEvent progress)
    {
        if (!_activeStepIds.Contains(progress.StepId))
        {
            Trace.TraceWarning("Setup detail progress for an inactive step was ignored: {0}", progress.StepId);
            return;
        }
        var measurement = progress.Unit switch
        {
            SetupDetailProgressUnit.Bytes when progress.Total is > 0 =>
                SetupLocalization.Format("Onboarding_V5_DownloadMeasurement", FormatBytes(progress.Completed), FormatBytes(progress.Total.Value)),
            SetupDetailProgressUnit.Items when progress.Total is > 0 =>
                SetupLocalization.Format("Onboarding_V5_DownloadMeasurement", progress.Completed, progress.Total.Value),
            _ => "",
        };
        DownloadActivity.Text = string.IsNullOrWhiteSpace(measurement) ? progress.Detail : $"{progress.Detail}  {measurement}";
        DownloadActivity.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = progress.Total is > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (progress.Total is > 0)
            DownloadProgress.Value = Math.Clamp((double)progress.Completed / progress.Total.Value, 0, 1);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_000_000_000
            ? $"{bytes / 1_000_000_000d:0.0} GB"
            : $"{bytes / 1_000_000d:0} MB";

    private void OnLogEmitted(object? sender, LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var line = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}\n";
            _logLineCount++;
            if (_logLineCount > MaxLogLines)
            {
                // Trim old lines (simple: just keep appending; reset periodically)
                if (_logLineCount % MaxLogLines == 0)
                    LogText.Text = line;
                else
                    LogText.Text += line;
            }
            else
            {
                LogText.Text += line;
            }

            // Auto-scroll
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
        });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_config?.LogPath);
    }

    private void ShowTailscaleAuthorization(ExternalAuthorizationRequest request)
    {
        _tailscaleAuthorizationUri = request.AuthorizationUri;
        TailscaleAuthorizationText.Text = request.Message;
        TailscaleAuthorizationPanel.Visibility = Visibility.Visible;
        _ = global::Windows.System.Launcher.LaunchUriAsync(request.AuthorizationUri);
    }

    private void TailscaleAuthorization_Click(object sender, RoutedEventArgs e)
    {
        if (_tailscaleAuthorizationUri is not null)
            _ = global::Windows.System.Launcher.LaunchUriAsync(_tailscaleAuthorizationUri);
    }

    // Swap the install UI for a "Gateway installed" milestone with an explicit
    // onboard CTA. The gateway keeps running (WSL keepalive), so the wizard
    // connects when the user chooses to continue.
    private void ShowGatewayInstalledMilestone()
    {
        InstallHeader.Visibility = Visibility.Collapsed;
        InstallContent.Visibility = Visibility.Collapsed;
        MilestonePanel.Visibility = Visibility.Visible;
        OnboardButton.Visibility = Visibility.Visible;
    }

    private void Onboard_Click(object sender, RoutedEventArgs e)
    {
        if (SetupWindow.Active?.TryNavigateToWizard() == true)
            return;

        MilestoneStatusText.Text = "Another setup task is still active. Wait for it to finish, then start OpenClaw onboard.";
    }

    private static List<SetupStep> BuildSteps(SetupConfig config, bool localAiRecoveryOnly = false)
        => config.NativeLocalAiAcquisition
            ? SetupStepFactory.BuildNativeLocalAiAcquisitionSteps()
            : OnboardingFlowPolicy.BuildInstallationSteps(localAiRecoveryOnly);
}

internal sealed class ProgressAuthorizationPresenter(
    DispatcherQueue dispatcherQueue,
    Action<ExternalAuthorizationRequest> present) : IExternalAuthorizationPresenter
{
    public Task PresentAsync(ExternalAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dispatcherQueue.TryEnqueue(() => present(request)))
            throw new InvalidOperationException("Setup UI closed before the Tailscale authorization link could be shown.");
        return Task.CompletedTask;
    }
}

internal sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Action<T> _report = report ?? throw new ArgumentNullException(nameof(report));

    public void Report(T value) => _report(value);
}
