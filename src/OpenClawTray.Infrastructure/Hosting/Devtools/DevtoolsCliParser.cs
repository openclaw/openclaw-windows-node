namespace OpenClawTray.Infrastructure.Hosting.Devtools;

internal enum DevtoolsSubverb
{
    Run,
    List,
    Screenshot,
    Tree,
    App,
}

/// <summary>
/// Parsed result of a <c>--devtools</c> / <c>--preview</c> invocation.
/// <see cref="Subverb"/> is <c>null</c> when neither flag is present.
/// </summary>
internal sealed record DevtoolsCliOptions(
    DevtoolsSubverb? Subverb,
    string? ComponentName,
    bool VsCodeMode,
    int Fps,
    string? ListOutputPath,
    string? ScreenshotOutputPath,
    int? McpPort,
    string? LogLevel,
    McpTransport Transport,
    bool UsedDeprecatedPreview,
    bool PreviewAndDevtoolsConflict,
    string? ProjectIdentifier = null,
    bool LogsDisabled = false,
    int? LogsCapacityMb = null);

/// <summary>
/// Pure command-line parser for the devtools entry point. Has no side effects so it is
/// unit-testable without spinning up a process. The runtime surfaces deprecation warnings
/// based on <see cref="DevtoolsCliOptions.UsedDeprecatedPreview"/>.
/// </summary>
internal static class DevtoolsCliParser
{
    public static DevtoolsCliOptions Parse(string[] args)
    {
        int devtoolsIdx = IndexOf(args, "--devtools");
        int previewIdx = IndexOf(args, "--preview");
        int previewListIdx = IndexOf(args, "--preview-list");
        int devtoolsListIdx = IndexOf(args, "--devtools-list");

        bool anyDevtools = devtoolsIdx >= 0 || devtoolsListIdx >= 0;
        bool anyPreview = previewIdx >= 0 || previewListIdx >= 0;

        if (anyDevtools && anyPreview)
        {
            return new DevtoolsCliOptions(
                Subverb: null,
                ComponentName: null,
                VsCodeMode: false,
                Fps: 10,
                ListOutputPath: null,
                ScreenshotOutputPath: null,
                McpPort: null,
                LogLevel: null,
                Transport: McpTransport.Http,
                UsedDeprecatedPreview: true,
                PreviewAndDevtoolsConflict: true,
                LogsDisabled: false,
                LogsCapacityMb: null);
        }

        if (!anyDevtools && !anyPreview)
        {
            return new DevtoolsCliOptions(
                Subverb: null,
                ComponentName: null,
                VsCodeMode: false,
                Fps: 10,
                ListOutputPath: null,
                ScreenshotOutputPath: null,
                McpPort: null,
                LogLevel: null,
                Transport: McpTransport.Http,
                UsedDeprecatedPreview: false,
                PreviewAndDevtoolsConflict: false,
                LogsDisabled: false,
                LogsCapacityMb: null);
        }

        DevtoolsSubverb subverb;
        int anchorIdx;
        bool deprecated;
        int trailingArgStart;

        if (devtoolsListIdx >= 0)
        {
            subverb = DevtoolsSubverb.List;
            anchorIdx = devtoolsListIdx;
            deprecated = false;
            trailingArgStart = devtoolsListIdx + 1;
        }
        else if (devtoolsIdx >= 0)
        {
            anchorIdx = devtoolsIdx;
            deprecated = false;
            (subverb, trailingArgStart) = ParseSubverbAfter(args, devtoolsIdx);
        }
        else if (previewListIdx >= 0)
        {
            subverb = DevtoolsSubverb.List;
            anchorIdx = previewListIdx;
            deprecated = true;
            trailingArgStart = previewListIdx + 1;
        }
        else
        {
            subverb = DevtoolsSubverb.Run;
            anchorIdx = previewIdx;
            deprecated = true;
            trailingArgStart = previewIdx + 1;
        }

        string? componentName = null;
        string? listOut = null;
        string? screenshotOut = null;

        if (subverb is DevtoolsSubverb.Run or DevtoolsSubverb.Screenshot or DevtoolsSubverb.Tree)
        {
            if (trailingArgStart < args.Length && !args[trailingArgStart].StartsWith("-"))
                componentName = args[trailingArgStart];
        }
        else if (subverb == DevtoolsSubverb.List)
        {
            if (trailingArgStart < args.Length && !args[trailingArgStart].StartsWith("-"))
                listOut = args[trailingArgStart];
        }

        bool vscode = args.Contains("--vscode");

        int fps = 10;
        int fpsIdx = IndexOf(args, "--fps");
        if (fpsIdx >= 0 && fpsIdx + 1 < args.Length && int.TryParse(args[fpsIdx + 1], out var parsedFps))
            fps = Math.Clamp(parsedFps, 1, 30);

        int? mcpPort = null;
        int mcpPortIdx = IndexOf(args, "--mcp-port");
        if (mcpPortIdx >= 0 && mcpPortIdx + 1 < args.Length && int.TryParse(args[mcpPortIdx + 1], out var parsedPort))
            mcpPort = parsedPort;

        string? logLevel = null;
        int logLevelIdx = IndexOf(args, "--devtools-log-level");
        if (logLevelIdx >= 0 && logLevelIdx + 1 < args.Length)
            logLevel = args[logLevelIdx + 1];

        var transport = McpTransport.Http;
        int transportIdx = IndexOf(args, "--mcp-transport");
        if (transportIdx >= 0 && transportIdx + 1 < args.Length)
        {
            if (string.Equals(args[transportIdx + 1], "stdio", StringComparison.OrdinalIgnoreCase))
                transport = McpTransport.Stdio;
        }

        int outIdx = IndexOf(args, "--out");
        if (outIdx >= 0 && outIdx + 1 < args.Length && subverb == DevtoolsSubverb.Screenshot)
            screenshotOut = args[outIdx + 1];

        string? projectIdentifier = null;
        int projIdx = IndexOf(args, "--devtools-project");
        if (projIdx >= 0 && projIdx + 1 < args.Length)
            projectIdentifier = args[projIdx + 1];

        bool logsDisabled = false;
        int logsIdx = IndexOf(args, "--devtools-logs");
        if (logsIdx >= 0 && logsIdx + 1 < args.Length
            && string.Equals(args[logsIdx + 1], "off", StringComparison.OrdinalIgnoreCase))
            logsDisabled = true;

        int? logsCapMb = null;
        int logsCapIdx = IndexOf(args, "--devtools-logs-capacity");
        if (logsCapIdx >= 0 && logsCapIdx + 1 < args.Length
            && int.TryParse(args[logsCapIdx + 1], out var parsedCap) && parsedCap > 0)
            logsCapMb = parsedCap;

        _ = anchorIdx;

        return new DevtoolsCliOptions(
            Subverb: subverb,
            ComponentName: componentName,
            VsCodeMode: vscode,
            Fps: fps,
            ListOutputPath: listOut,
            ScreenshotOutputPath: screenshotOut,
            McpPort: mcpPort,
            LogLevel: logLevel,
            Transport: transport,
            UsedDeprecatedPreview: deprecated,
            PreviewAndDevtoolsConflict: false,
            ProjectIdentifier: projectIdentifier,
            LogsDisabled: logsDisabled,
            LogsCapacityMb: logsCapMb);
    }

    private static (DevtoolsSubverb Subverb, int TrailingArgStart) ParseSubverbAfter(string[] args, int devtoolsIdx)
    {
        // `--devtools <verb> [...positional]`. If no verb is given, default to Run.
        if (devtoolsIdx + 1 >= args.Length) return (DevtoolsSubverb.Run, devtoolsIdx + 1);
        var next = args[devtoolsIdx + 1];
        return next.ToLowerInvariant() switch
        {
            "run" => (DevtoolsSubverb.Run, devtoolsIdx + 2),
            "list" => (DevtoolsSubverb.List, devtoolsIdx + 2),
            "screenshot" => (DevtoolsSubverb.Screenshot, devtoolsIdx + 2),
            "tree" => (DevtoolsSubverb.Tree, devtoolsIdx + 2),
            "app" => (DevtoolsSubverb.App, devtoolsIdx + 2),
            _ => (DevtoolsSubverb.Run, devtoolsIdx + 1),
        };
    }

    private static int IndexOf(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
                return i;
        return -1;
    }
}
