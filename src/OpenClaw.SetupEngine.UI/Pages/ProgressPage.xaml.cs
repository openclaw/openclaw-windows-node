using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class ProgressPage : Page
{
    private SetupConfig? _config;
    private SetupPipeline? _pipeline;
    private SetupLogger? _logger;
    private CancellationTokenSource? _runCts;
    private readonly Dictionary<string, StepRow> _rows = new();
    private int _logLineCount;
    private bool _pipelineFinished;
    private const int MaxLogLines = 200;

    // Map pipeline step IDs to display groups (N:1)
    private static readonly (string GroupId, string DisplayName, string[] StepIds)[] StepGroups =
    [
        ("preflight", "Check system", ["preflight-os", "preflight-wsl"]),
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
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        SubtitleText.Text = $"Creating {_config.DistroName} WSL instance";

        BuildStepRows();
        if (SetupPreview.IsActive)
        {
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray", "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

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
            var ctx = new SetupContext(config, _logger, journal, commands, cts.Token);

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
                        finishRow.SetStatus(StepStatus.Running);
                    SubtitleText.Text = "Opening gateway setup...";
                    await Task.Delay(900);
                    finishRow?.SetStatus(StepStatus.Done);
                    SetupWindow.Active?.NavigateToWizard();
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

    private static List<SetupStep> BuildSteps(SetupConfig config)
        => SetupStepFactory.BuildDefaultSteps()
            .Where(step => step is not RunGatewayWizardStep)
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

    public StepRow(string displayName)
    {
        _label = new TextBlock
        {
            Text = displayName,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _spinner = new ProgressRing
        {
            Width = 22, Height = 22,
            MinWidth = 22, MinHeight = 22,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };

        _idleBadge = CreateEmptyBadge();

        _checkBadge = CreateIconBadge("\uE73E", Color.FromArgb(255, 0x2B, 0xC3, 0x6F), Color.FromArgb(255, 255, 255, 255));
        _checkBadge.Visibility = Visibility.Collapsed;

        _errorBadge = CreateIconBadge("\uE711", Color.FromArgb(255, 0xE8, 0x11, 0x23), Color.FromArgb(255, 255, 255, 255));
        _errorBadge.Visibility = Visibility.Collapsed;

        var badgeContainer = new Grid
        {
            Width = 28,
            Height = 28,
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

        Element = grid;
    }

    public void SetStatus(StepStatus status)
    {
        Status = status;
        _spinner.IsActive = status == StepStatus.Running;
        _spinner.Visibility = status == StepStatus.Running ? Visibility.Visible : Visibility.Collapsed;
        if (status == StepStatus.Running)
        {
            // Brand-red spinner. The ProgressRing foreground resolves to the
            // app-level system accent, so the setup window's element-scoped
            // red override doesn't reach it; pick the themed red explicitly.
            _spinner.Foreground = new SolidColorBrush(
                _spinner.ActualTheme == ElementTheme.Light
                    ? Color.FromArgb(255, 0xC8, 0x1E, 0x1E)
                    : Color.FromArgb(255, 0xD8, 0x1E, 0x34));
        }
        _idleBadge.Visibility = status == StepStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
        _checkBadge.Visibility = status == StepStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        _errorBadge.Visibility = status == StepStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _label.Opacity = status == StepStatus.Idle ? 0.72 : 1.0;
        _label.FontWeight = status == StepStatus.Running
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private static Border CreateEmptyBadge()
    {
        // Use a theme-aware stroke brush so the pending-step ring is visible
        // in both light and dark mode. The previous hard-coded translucent
        // white was invisible against light backgrounds.
        var border = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
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
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(background),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                FontFamily = IconFonts.SymbolThemeFontFamily,
                Foreground = new SolidColorBrush(foreground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }
}
