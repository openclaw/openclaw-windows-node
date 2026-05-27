using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System.Text;

namespace OpenClawTray.Services;

internal sealed record DiagnosticsBundlePaths(
    string? TrayLogPath,
    string? TrayLogArchivePath,
    string? DiagnosticsJsonlPath,
    string? CrashLogPath,
    string? SetupLogDirectory)
{
    public static DiagnosticsBundlePaths Default()
    {
        var trayLog = Logger.LogFilePath;
        var logDirectory = Path.GetDirectoryName(trayLog);
        var diagnosticsJsonl = DiagnosticsJsonlService.FilePath ??
            Path.Combine(logDirectory ?? "", "Logs", "diagnostics.jsonl");
        return new DiagnosticsBundlePaths(
            TrayLogPath: trayLog,
            TrayLogArchivePath: string.IsNullOrWhiteSpace(logDirectory)
                ? null
                : Path.Combine(logDirectory, "openclaw-tray.log.old"),
            DiagnosticsJsonlPath: diagnosticsJsonl,
            CrashLogPath: string.IsNullOrWhiteSpace(logDirectory)
                ? null
                : Path.Combine(logDirectory, "crash.log"),
            SetupLogDirectory: Path.Combine(SettingsManager.SettingsDirectoryPath, "Logs", "Setup"));
    }
}

internal static class DiagnosticsBundleBuilder
{
    private static readonly DiagnosticsTailOptions StandardTail = new(MaxLines: 200);
    private static readonly DiagnosticsTailOptions ShortTail = new(MaxLines: 120);

    public static string Build(
        GatewayCommandCenterState state,
        IReadOnlyList<ConnectionDiagnosticEvent>? connectionEvents = null,
        DiagnosticsBundlePaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        paths ??= DiagnosticsBundlePaths.Default();

        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw Windows Tray Diagnostics Bundle");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine();
        builder.AppendLine("## Manifest");
        builder.AppendLine("Included:");
        builder.AppendLine("- Generated support/debug summaries");
        builder.AppendLine("- Connection event timeline");
        builder.AppendLine("- Tray log tail");
        builder.AppendLine("- Structured diagnostics JSONL tail");
        builder.AppendLine("- Crash log tail");
        builder.AppendLine("- Latest setup log tails");
        builder.AppendLine();
        builder.AppendLine("Redaction:");
        builder.AppendLine("- Tokens, bootstrap/shared credentials, bearer headers, API keys, passwords, setup codes, DPAPI blobs, private keys, URLs, emails, IPs, and user paths are sanitized.");
        builder.AppendLine("- Raw settings.json, gateways.json, mcp-token.txt, device-key-ed25519.json, screenshots, recordings, chat payloads, camera data, and microphone data are not included.");
        builder.AppendLine("- Long sections are truncated and marked inline.");
        builder.AppendLine();
        builder.AppendLine("Sources:");
        AppendSource(builder, "Tray log", paths.TrayLogPath);
        AppendSource(builder, "Tray log archive", paths.TrayLogArchivePath);
        AppendSource(builder, "Diagnostics JSONL", paths.DiagnosticsJsonlPath);
        AppendSource(builder, "Crash log", paths.CrashLogPath);
        AppendSource(builder, "Setup logs", paths.SetupLogDirectory);
        builder.AppendLine();

        AppendSection(builder, "Generated Debug Summary", CommandCenterTextHelper.BuildDebugBundle(state));
        AppendSection(builder, "Connection Event Timeline", BuildConnectionTimeline(connectionEvents));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Tray Log Tail", paths.TrayLogPath, StandardTail));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Tray Log Archive Tail", paths.TrayLogArchivePath, ShortTail));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Structured Diagnostics JSONL Tail", paths.DiagnosticsJsonlPath, StandardTail));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Crash Log Tail", paths.CrashLogPath, ShortTail));
        AppendLatestSetupLogs(builder, paths.SetupLogDirectory);

        return DiagnosticsExportRedactor.Sanitize(builder.ToString());
    }

    private static void AppendSource(StringBuilder builder, string label, string? path)
    {
        builder.Append("- ");
        builder.Append(label);
        builder.Append(": ");
        builder.AppendLine(string.IsNullOrWhiteSpace(path)
            ? "not configured"
            : DiagnosticsExportRedactor.RedactPath(path));
    }

    private static void AppendSection(StringBuilder builder, string title, string content)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine(DiagnosticsExportRedactor.Sanitize(content).TrimEnd());
        builder.AppendLine();
    }

    private static string BuildConnectionTimeline(IReadOnlyList<ConnectionDiagnosticEvent>? events)
    {
        if (events is not { Count: > 0 })
            return "No connection diagnostic events recorded.";

        var builder = new StringBuilder();
        foreach (var evt in events.TakeLast(200))
        {
            builder.Append(evt.Timestamp.ToUniversalTime().ToString("O"));
            builder.Append(" [");
            builder.Append(evt.Category);
            builder.Append("] ");
            builder.Append(evt.Message);
            if (!string.IsNullOrWhiteSpace(evt.Detail))
            {
                builder.Append(" — ");
                builder.Append(evt.Detail);
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static void AppendLatestSetupLogs(StringBuilder builder, string? setupLogDirectory)
    {
        builder.AppendLine("## Latest Setup Log Tails");
        builder.AppendLine($"Source: {FormatPath(setupLogDirectory)}");

        if (string.IsNullOrWhiteSpace(setupLogDirectory) || !Directory.Exists(setupLogDirectory))
        {
            builder.AppendLine("Status: not found");
            builder.AppendLine();
            return;
        }

        var latestLogs = Directory.EnumerateFiles(setupLogDirectory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(4)
            .ToList();

        if (latestLogs.Count == 0)
        {
            builder.AppendLine("Status: no setup logs found");
            builder.AppendLine();
            return;
        }

        builder.AppendLine();
        foreach (var file in latestLogs)
        {
            builder.Append(DiagnosticsLogTailReader.BuildSection(
                $"Setup Log Tail: {file.Name}",
                file.FullName,
                ShortTail));
        }
    }

    private static string FormatPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "not configured"
            : DiagnosticsExportRedactor.RedactPath(path);
}
