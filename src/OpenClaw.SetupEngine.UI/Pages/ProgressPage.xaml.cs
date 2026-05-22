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
    private readonly Dictionary<string, StepRow> _rows = new();
    private bool _logExpanded;
    private int _logLineCount;
    private const int MaxLogLines = 200;

    // Map pipeline step IDs to display groups (N:1)
    private static readonly (string GroupId, string DisplayName, string[] StepIds)[] StepGroups =
    [
        ("cleanup", "Removing existing gateway", ["cleanup-distro", "cleanup-gateway"]),
        ("preflight", "Check system", ["preflight-os", "preflight-wsl", "preflight-port"]),
        ("wsl-create", "Installing Ubuntu", ["wsl-create"]),
        ("wsl-configure", "Configuring instance", ["wsl-configure"]),
        ("install-cli", "Installing OpenClaw", ["install-cli"]),
        ("configure", "Preparing gateway", ["configure-gateway", "install-service"]),
        ("start", "Starting gateway", ["start-gateway", "mint-token"]),
        ("pairing", "Pairing device", ["pair-operator", "pair-node", "verify-e2e", "start-keepalive"]),
    ];

    public ProgressPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        SubtitleText.Text = $"Creating {_config.DistroName} WSL instance";

        BuildStepRows();
        StartPipeline();
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
        config.LogPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OpenClawTray", "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        _logger = new SetupLogger(config.LogPath,
            Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);

        _logger.LogEmitted += OnLogEmitted;

        var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
        using var journal = new TransactionJournal(journalPath);
        var commands = new CommandRunner(_logger);
        using var cts = new CancellationTokenSource();
        var ctx = new SetupContext(config, _logger, journal, commands, cts.Token);

        var steps = BuildSteps(config);
        _pipeline = new SetupPipeline(steps);
        _pipeline.StepProgress += OnStepProgress;

        var sw = Stopwatch.StartNew();
        var result = await Task.Run(() => _pipeline.RunAsync(ctx));
        sw.Stop();

        _logger.LogEmitted -= OnLogEmitted;
        _pipeline.StepProgress -= OnStepProgress;
        _logger.Dispose();

        App.MainWindow?.NavigateToComplete(result.Outcome == PipelineOutcome.Success, sw.Elapsed, config.LogPath);
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
        LogToggleButton.Content = _logExpanded ? "Hide logs ▲" : "Show logs ▼";

        var isDark = ActualTheme == ElementTheme.Dark;
        LogPanel.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x1A, 0x1A, 0x1A)
            : Color.FromArgb(255, 0xF8, 0xF8, 0xF8));
    }

    private static List<SetupStep> BuildSteps(SetupConfig config)
    {
        return
        [
            new CleanupStaleDistroStep(),
            new CleanupStaleGatewayStep(),
            new PreflightOsStep(),
            new PreflightWslStep(),
            new PreflightPortStep(),
            new CreateWslInstanceStep(),
            new ConfigureWslInstanceStep(),
            new InstallCliStep(),
            new ConfigureGatewayStep(),
            new InstallGatewayServiceStep(),
            new StartGatewayStep(),
            new MintBootstrapTokenStep(),
            new PairOperatorStep(),
            new PairNodeStep(),
            new VerifyEndToEndStep(),
            new StartKeepaliveStep(),
        ];
    }
}

// ─── Step Row UI Element ───

internal enum StepStatus { Idle, Running, Done, Failed }

internal sealed class StepRow
{
    public FrameworkElement Element { get; }
    public StepStatus Status { get; private set; }

    private readonly TextBlock _label;
    private readonly ProgressRing _spinner;
    private readonly Border _checkBadge;
    private readonly Border _errorBadge;

    public StepRow(string displayName)
    {
        _label = new TextBlock
        {
            Text = displayName,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _spinner = new ProgressRing
        {
            Width = 24, Height = 24,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };

        _checkBadge = CreateBadge("✓", Color.FromArgb(255, 0x2B, 0xC3, 0x6F), Color.FromArgb(255, 255, 255, 255));
        _checkBadge.Visibility = Visibility.Collapsed;

        _errorBadge = CreateBadge("✕", Color.FromArgb(255, 0xF4, 0xA6, 0xB0), Color.FromArgb(255, 0, 0, 0));
        _errorBadge.Visibility = Visibility.Collapsed;

        var badgeContainer = new Grid { Width = 24, Height = 24 };
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
        _checkBadge.Visibility = status == StepStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        _errorBadge.Visibility = status == StepStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Border CreateBadge(string symbol, Color background, Color foreground)
    {
        return new Border
        {
            Width = 24, Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(background),
            Child = new TextBlock
            {
                Text = symbol,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(foreground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }
}
