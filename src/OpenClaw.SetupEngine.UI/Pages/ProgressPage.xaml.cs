using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

internal sealed record ProgressPageArgs(
    SetupConfig Config,
    bool ShowMilestoneOnly,
    string DataDir,
    string LocalDataDir);

public sealed partial class ProgressPage : Page
{
    private SetupConfig? _config;
    private SetupPipeline? _pipeline;
    private SetupLogger? _logger;
    private CancellationTokenSource? _runCts;
    private readonly Dictionary<string, StepRow> _rows = new();
    private int _logLineCount;
    private bool _pipelineFinished;
    private string _dataDir = null!;
    private string _localDataDir = null!;
    private const int MaxLogLines = 200;

    internal bool IsPipelineRunning => _runCts != null && !_pipelineFinished;

    // Map pipeline step IDs to display groups (N:1)
    private static readonly (string GroupId, string DisplayName, string[] StepIds)[] StepGroups =
    [
        ("preflight", "Check system", ["validate-distro-path", "preflight-os", "preflight-wsl"]),
        ("cleanup", "Removing existing gateway", ["cleanup-distro", "cleanup-gateway"]),
        ("port", "Checking gateway port", ["preflight-port"]),
        ("wsl-create", "Installing clean WSL gateway", ["wsl-create"]),
        ("wsl-configure", "Configuring instance", ["wsl-configure", "validate-wsl-lockdown"]),
        ("install-cli", "Installing OpenClaw", ["install-cli"]),
        ("configure", "Preparing gateway", ["configure-gateway", "install-service"]),
        ("start", "Starting gateway", ["start-gateway", "mint-token"]),
        ("pairing", "Pairing device", ["pair-operator", "pair-node", "verify-e2e"]),
        ("finish", "Finishing setup", ["run-wizard", "start-keepalive"]),
    ];

    public ProgressPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPipeline();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = e.Parameter as ProgressPageArgs;
        _config = args?.Config ?? e.Parameter as SetupConfig ?? new SetupConfig();
        _dataDir = args?.DataDir ?? SetupContext.ResolveDataDir();
        _localDataDir = args?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        SubtitleText.Text = $"Creating {_config.DistroName} WSL instance";

        BuildStepRows();
        if (args?.ShowMilestoneOnly == true)
        {
            foreach (var (groupId, _, _) in StepGroups)
                if (_rows.TryGetValue(groupId, out var row))
                    row.SetStatus(StepStatus.Done);
            ShowGatewayInstalledMilestone();
            return;
        }

        if (SetupPreview.IsActive)
        {
            if (SetupPreview.RequestedPage == "milestone")
            {
                foreach (var (groupId, _, _) in StepGroups)
                    if (_rows.TryGetValue(groupId, out var row))
                        row.SetStatus(StepStatus.Done);
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
        SubtitleText.Text = "Creating OpenClawGateway WSL instance — about 4 minutes left";
        var ids = StepGroups.Select(g => g.GroupId).ToArray();
        for (int i = 0; i < ids.Length; i++)
        {
            var status = i < 3 ? StepStatus.Done : i == 3 ? StepStatus.Running : StepStatus.Idle;
            if (_rows.TryGetValue(ids[i], out var row))
                row.SetStatus(status);
        }
        LogText.Text =
            "[12:04:01] [info] Windows 11 26100 · WSL 2 present\n" +
            "[12:04:03] [info] port 127.0.0.1:18789 available\n" +
            "[12:04:05] [info] wsl --install -d Ubuntu-24.04 --name OpenClawGateway --no-launch\n" +
            "[12:04:38] [info] downloading distro … 142/200 MB\n" +
            "[12:04:38] [changed] created %LOCALAPPDATA%\\OpenClawTray\\wsl\\OpenClawGateway\\\n" +
            "[12:04:38] [info] next: install CLI via HTTPS, configure loopback gateway\n";
    }

    private void BuildStepRows()
    {
        foreach (var (groupId, displayName, _) in StepGroups)
        {
            var row = new StepRow(displayName);
            _rows[groupId] = row;
            StepsPanel.Children.Add(row.Element);
        }
    }

    private void StartPipeline() =>
        AsyncEventHandlerGuard.Run(
            StartPipelineAsync,
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

            var steps = BuildSteps(config);
            _pipeline = new SetupPipeline(steps);
            _pipeline.StepProgress += OnStepProgress;

            var result = await Task.Run(() => _pipeline.RunAsync(ctx), cts.Token);
            sw.Stop();
            _pipelineFinished = true;

            var success = result.Outcome == PipelineOutcome.Success;
            if (success)
            {
                if (!config.SkipWizard)
                {
                    if (_rows.TryGetValue("finish", out var finishRow))
                        finishRow.SetStatus(StepStatus.Done);
                    // Pause on a "Gateway installed" milestone so the user knowingly steps
                    // from install (gateway provisioning) into onboarding (the OpenClaw wizard),
                    // instead of being thrown straight into the questions.
                    ShowGatewayInstalledMilestone();
                }
                else
                    // Permissions are now surfaced inline on the capabilities screen, so
                    // the standalone permissions step is skipped — go straight to done.
                    SetupWindow.Active?.NavigateToComplete(true, sw.Elapsed, config.LogPath);
            }
            else
            {
                var errorMsg = result.Outcome == PipelineOutcome.Cancelled
                    ? "Setup was cancelled."
                    : result.FailedStepId != null
                        ? $"Step '{result.FailedStepId}' failed: {result.Message}"
                        : result.Message;
                SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, errorMsg);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            _pipelineFinished = true;
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, "Setup was cancelled.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _pipelineFinished = true;
            _logger?.Error($"Setup UI pipeline failed: {ex.Message}");
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, $"Setup crashed: {ex.Message}");
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

    private void CancelPipeline()
    {
        if (!_pipelineFinished)
            _runCts?.Cancel();
    }

    private void OnStepProgress(object? sender, StepProgressEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Find which group this step belongs to
            var groupIndex = Array.FindIndex(StepGroups, g => g.StepIds.Contains(e.StepId));
            if (groupIndex < 0) return;

            var group = StepGroups[groupIndex];
            var row = _rows[group.GroupId];

            if (e.Outcome == null)
            {
                // Step started — mark all previous groups as done if still running
                for (int i = 0; i < groupIndex; i++)
                {
                    var prevRow = _rows[StepGroups[i].GroupId];
                    if (prevRow.Status == StepStatus.Running)
                        prevRow.SetStatus(StepStatus.Done);
                }

                // Mark this group as running
                if (row.Status != StepStatus.Done)
                    row.SetStatus(StepStatus.Running);
            }
            else if (e.Outcome == StepOutcome.Failed || e.Outcome == StepOutcome.FailedTerminal)
            {
                row.SetStatus(StepStatus.Failed);
            }
            else
            {
                // Step succeeded/skipped — track it
                _completedSteps.Add(e.StepId);

                // If all steps in this group are done, mark group done
                if (group.StepIds.All(id => _completedSteps.Contains(id)))
                    row.SetStatus(StepStatus.Done);
            }
        });
    }

    private readonly HashSet<string> _completedSteps = new();

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

    private static List<SetupStep> BuildSteps(SetupConfig config)
        => SetupStepFactory.BuildDefaultSteps()
            .Where(step => step is not RunGatewayWizardStep)
            .Where(step => config.SkipWizard || step is not WindowsNodeBootstrapContextStep)
            .ToList();
}

// ─── Step Row UI Element ───

internal enum StepStatus { Idle, Running, Done, Failed }

internal sealed class StepRow
{
    public FrameworkElement Element { get; }
    public StepStatus Status { get; private set; }

    private readonly TextBlock _label;
    private readonly ProgressRing _spinner;
    private readonly Border _idleBadge;
    private readonly Border _checkBadge;
    private readonly Border _errorBadge;
    private readonly Border _rowBorder;

    public StepRow(string displayName)
    {
        _label = new TextBlock
        {
            Text = displayName,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Bare Windows spinner (no filled disc) — theme-neutral so it reads white
        // on the dark active row and dark on light, like a standard ProgressRing.
        _spinner = new ProgressRing
        {
            Width = 20, Height = 20,
            MinWidth = 20, MinHeight = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var spinnerFg) && spinnerFg is Brush spinnerBrush)
            _spinner.Foreground = spinnerBrush;

        _idleBadge = CreateEmptyBadge();

        _checkBadge = CreateIconBadge("\uE73E", ResolveColor("SystemFillColorSuccess", Color.FromArgb(255, 0x2B, 0xC3, 0x6F)), Colors.White);
        _checkBadge.Visibility = Visibility.Collapsed;

        _errorBadge = CreateIconBadge("\uE711", ResolveColor("SystemFillColorCritical", Color.FromArgb(255, 0xE8, 0x11, 0x23)), Colors.White);
        _errorBadge.Visibility = Visibility.Collapsed;

        var badgeContainer = new Grid
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeContainer.Children.Add(_idleBadge);
        badgeContainer.Children.Add(_spinner);
        badgeContainer.Children.Add(_checkBadge);
        badgeContainer.Children.Add(_errorBadge);

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(badgeContainer, 1);
        grid.Children.Add(_label);
        grid.Children.Add(badgeContainer);

        _rowBorder = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 5, 12, 5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        Element = _rowBorder;
    }

    public void SetStatus(StepStatus status)
    {
        Status = status;
        _spinner.IsActive = status == StepStatus.Running;
        _spinner.Visibility = status == StepStatus.Running ? Visibility.Visible : Visibility.Collapsed;
        _idleBadge.Visibility = status == StepStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
        _checkBadge.Visibility = status == StepStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        _errorBadge.Visibility = status == StepStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _label.Opacity = status == StepStatus.Idle ? 0.72 : 1.0;
        _label.FontWeight = status == StepStatus.Running
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

        // Highlight the active step with the setup accent while it is running.
        if (status == StepStatus.Running
            && Application.Current.Resources.TryGetValue("SetupIndicatorAccentBrush", out var accent)
            && accent is SolidColorBrush accentBrush)
        {
            var c = accentBrush.Color;
            _rowBorder.Background = new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B));
            _rowBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B));
        }
        else
        {
            _rowBorder.Background = new SolidColorBrush(Colors.Transparent);
            _rowBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
        }
    }

    private static Border CreateEmptyBadge()
    {
        // Use a theme-aware stroke so the pending-step ring stays visible in every theme.
        var border = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };

        if (Application.Current.Resources.TryGetValue("ControlStrongStrokeColorDefaultBrush", out var brush)
            && brush is Brush themed)
        {
            border.BorderBrush = themed;
        }
        else
        {
            border.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 128, 128, 128));
        }

        return border;
    }

    private static Border CreateIconBadge(string glyph, Color background, Color foreground)
    {
        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(background),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 11,
                FontFamily = IconFonts.SymbolThemeFontFamily,
                Foreground = new SolidColorBrush(foreground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }

    // Resolve a native Color theme resource (e.g. SystemFillColorSuccess) with a fallback.
    private static Color ResolveColor(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : fallback;
}
