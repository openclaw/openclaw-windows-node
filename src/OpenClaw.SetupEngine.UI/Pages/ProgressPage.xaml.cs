using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class ProgressPage : Page
{
    private SetupConfig? _config;
    private SetupPipeline? _pipeline;
    private SetupLogger? _logger;
    private CancellationTokenSource? _runCts;
    private readonly Dictionary<string, StepRow> _rows = new();
    private bool _logExpanded;
    private int _logLineCount;
    private bool _pipelineFinished;
    private const int MaxLogLines = 200;

    // Map pipeline step IDs to display groups (N:1)
    private static readonly (string GroupId, string DisplayName, string[] StepIds)[] StepGroups =
    [
        ("cleanup", "Removing existing gateway", ["cleanup-distro", "cleanup-gateway"]),
        ("preflight", "Check system", ["preflight-os", "preflight-wsl", "preflight-port"]),
        ("wsl-create", "Installing Ubuntu", ["wsl-create"]),
        ("wsl-configure", "Configuring instance", ["wsl-configure", "validate-wsl-lockdown"]),
        ("install-cli", "Installing OpenClaw", ["install-cli"]),
        ("configure", "Preparing gateway", ["configure-gateway", "install-service"]),
        ("start", "Starting gateway", ["start-gateway", "mint-token"]),
        ("pairing", "Pairing device", ["pair-operator", "pair-node", "verify-e2e"]),
        ("finish", "Finishing setup", ["run-wizard", "start-keepalive"]),
    ];

    public ProgressPage()
    {
        Program.WriteStartupBreadcrumb("ProgressPage.ctor.begin");
        BuildPageShell();
        Program.WriteStartupBreadcrumb("ProgressPage.ctor.afterBuildPageShell");
        Unloaded += (_, _) => CancelPipeline();
    }

    private void BuildPageShell()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel
        {
            Padding = new Thickness(56, 24, 56, 0),
            Spacing = 4
        };
        Grid.SetRow(header, 0);
        header.Children.Add(new TextBlock
        {
            Text = "Setting up locally",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        SubtitleText = new TextBlock
        {
            FontSize = 13,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        header.Children.Add(SubtitleText);

        StepsPanel = new StackPanel
        {
            Padding = new Thickness(56, 24, 56, 12),
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Top
        };
        var stepsScroller = new ScrollViewer
        {
            Content = StepsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(stepsScroller, 1);

        var logRoot = new Grid
        {
            Padding = new Thickness(24, 0, 24, 16)
        };
        logRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        logRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(logRoot, 2);

        LogText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true
        };
        LogScroller = new ScrollViewer
        {
            Content = LogText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        LogPanel = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Height = 200,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8),
            Child = LogScroller
        };
        Grid.SetRow(LogPanel, 0);

        var logButtons = new Grid
        {
            ColumnSpacing = 8
        };
        logButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        logButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(logButtons, 1);

        OpenLogButton = new Button
        {
            Content = "Open log",
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(8, 4, 8, 4)
        };
        OpenLogButton.Click += OpenLog_Click;
        Grid.SetColumn(OpenLogButton, 1);

        LogToggleButton = new Button
        {
            Content = "Show logs ▲",
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4)
        };
        LogToggleButton.Click += LogToggle_Click;
        Grid.SetColumn(LogToggleButton, 2);

        logButtons.Children.Add(OpenLogButton);
        logButtons.Children.Add(LogToggleButton);
        logRoot.Children.Add(LogPanel);
        logRoot.Children.Add(logButtons);

        root.Children.Add(header);
        root.Children.Add(stepsScroller);
        root.Children.Add(logRoot);
        Content = root;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Program.WriteStartupBreadcrumb("ProgressPage.OnNavigatedTo.begin");
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        SubtitleText.Text = $"Creating {_config.DistroName} WSL instance";

        BuildStepRows();
        Program.WriteStartupBreadcrumb("ProgressPage.OnNavigatedTo.afterBuildStepRows");
        StartPipeline();
        Program.WriteStartupBreadcrumb("ProgressPage.OnNavigatedTo.afterStartPipeline");
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

    private async void StartPipeline()
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
                    App.MainWindow?.NavigateToWizard();
                }
                else if (config.SkipPermissions)
                    App.MainWindow?.NavigateToComplete(true, sw.Elapsed, config.LogPath);
                else
                    App.MainWindow?.NavigateToPermissions();
            }
            else
            {
                var errorMsg = result.Outcome == PipelineOutcome.Cancelled
                    ? "Setup was cancelled."
                    : result.FailedStepId != null
                        ? $"Step '{result.FailedStepId}' failed: {result.Message}"
                        : result.Message;
                App.MainWindow?.NavigateToComplete(false, sw.Elapsed, config.LogPath, errorMsg);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            _pipelineFinished = true;
            App.MainWindow?.NavigateToComplete(false, sw.Elapsed, config.LogPath, "Setup was cancelled.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _pipelineFinished = true;
            _logger?.Error($"Setup UI pipeline failed: {ex.Message}");
            App.MainWindow?.NavigateToComplete(false, sw.Elapsed, config.LogPath, $"Setup crashed: {ex.Message}");
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

    private void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        _logExpanded = !_logExpanded;
        LogPanel.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        OpenLogButton.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        LogToggleButton.Content = _logExpanded ? "Hide logs ▼" : "Show logs ▲";

        var isDark = ActualTheme == ElementTheme.Dark;
        LogPanel.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A)
            : Color.FromArgb(255, 0xF8, 0xF8, 0xF8));
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = _config?.LogPath;
        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
        }
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
    private readonly TextBlock _statusText;

    public StepRow(string displayName)
    {
        _label = new TextBlock
        {
            Text = displayName,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _statusText = new TextBlock
        {
            Text = "[ ]",
            Width = 40,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_statusText, 1);
        grid.Children.Add(_label);
        grid.Children.Add(_statusText);

        Element = grid;
    }

    public void SetStatus(StepStatus status)
    {
        Status = status;
        _statusText.Text = status switch
        {
            StepStatus.Running => "...",
            StepStatus.Done => "OK",
            StepStatus.Failed => "!",
            _ => "[ ]"
        };
        _statusText.Foreground = new SolidColorBrush(status switch
        {
            StepStatus.Done => Color.FromArgb(255, 0x2B, 0xC3, 0x6F),
            StepStatus.Failed => Color.FromArgb(255, 0xE8, 0x11, 0x23),
            _ => Color.FromArgb(255, 255, 255, 255)
        });
        _label.Opacity = status == StepStatus.Idle ? 0.72 : 1.0;
        _label.FontWeight = status == StepStatus.Running
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }
}
