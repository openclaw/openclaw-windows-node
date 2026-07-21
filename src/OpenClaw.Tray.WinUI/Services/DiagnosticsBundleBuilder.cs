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
    private const int MaxSetupLogFiles = 4;
    private const int MaxTotalBundleChars = 1_000_000;
    private static readonly DiagnosticsTailOptions StandardTail = new(MaxLines: 200, MaxLineChars: 8_000, MaxSectionChars: 256_000, MaxReadBytes: 512_000);
    private static readonly DiagnosticsTailOptions ShortTail = new(MaxLines: 120, MaxLineChars: 8_000, MaxSectionChars: 128_000, MaxReadBytes: 256_000);
    private static readonly DiagnosticsTailOptions JsonlTail = StandardTail with { LineKind = DiagnosticsExportLineKind.Jsonl };
    private static readonly DiagnosticsTailOptions ShortJsonlTail = ShortTail with { LineKind = DiagnosticsExportLineKind.Jsonl };

    public static string BuildCached(
        GatewayCommandCenterState state,
        IReadOnlyList<ConnectionDiagnosticEvent>? connectionEvents = null,
        DiagnosticsBundlePaths? paths = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Build(state, connectionEvents, paths);
    }

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
        builder.AppendLine("Privacy boundary:");
        builder.AppendLine("- Export is read-only: source logs are tail-read and are never rewritten by preview/copy/save.");
        builder.AppendLine("- Log tails are bounded first, then sanitized into this bundle as a defense-in-depth export boundary.");
        builder.AppendLine("- Tokens, bootstrap/shared credentials, bearer headers, API keys, passwords, setup codes, DPAPI blobs, private keys, URLs, emails, IPs, and user paths are redacted before emission.");
        builder.AppendLine("- Raw settings.json, gateways.json, mcp-token.txt, device-key-ed25519.json, screenshots, recordings, chat payloads, camera data, and microphone data are not included.");
        builder.AppendLine("- Files, lines, bytes per line, bytes per section, and total bundle size are capped and marked inline when truncated.");
        builder.AppendLine();
        builder.AppendLine("Sources:");
        AppendSource(builder, "Tray log", paths.TrayLogPath);
        AppendSource(builder, "Tray log archive", paths.TrayLogArchivePath);
        AppendSource(builder, "Diagnostics JSONL", paths.DiagnosticsJsonlPath);
        AppendSource(builder, "Crash log", paths.CrashLogPath);
        AppendSource(builder, "Setup logs", paths.SetupLogDirectory);
        builder.AppendLine();

        AppendSanitizedSection(builder, "Generated Debug Summary", CommandCenterTextHelper.BuildDebugBundle(state));
        AppendPreSanitizedSection(builder, "Connection Event Timeline", BuildConnectionTimeline(connectionEvents));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Tray Log Tail", paths.TrayLogPath, StandardTail));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Tray Log Archive Tail", paths.TrayLogArchivePath, ShortTail));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Structured Diagnostics JSONL Tail", paths.DiagnosticsJsonlPath, JsonlTail));
        builder.Append(DiagnosticsLogTailReader.BuildSection("Crash Log Tail", paths.CrashLogPath, ShortTail));
        AppendLatestSetupLogs(builder, paths.SetupLogDirectory);

        return TruncateBundle(builder.ToString());
    }

    private static void AppendSource(StringBuilder builder, string label, string? path)
    {
        builder.Append("- ");
        builder.Append(label);
        builder.Append(": ");
        builder.AppendLine(string.IsNullOrWhiteSpace(path)
            ? "not configured"
            : FormatPath(path));
    }

    private static void AppendSanitizedSection(StringBuilder builder, string title, string content)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine(DiagnosticsExportSanitizer.SanitizeTextBlock(content).TrimEnd());
        builder.AppendLine();
    }

    private static void AppendPreSanitizedSection(StringBuilder builder, string title, string content)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine(content.TrimEnd());
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
            builder.Append(DiagnosticsExportSanitizer.SanitizeTextBlock(evt.Category));
            builder.Append("] ");
            builder.Append(DiagnosticsExportSanitizer.SanitizeTextBlock(evt.Message));
            if (!string.IsNullOrWhiteSpace(evt.Detail))
            {
                builder.Append(": ");
                builder.Append(DiagnosticsExportSanitizer.SanitizeTextBlock(evt.Detail));
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

        IReadOnlyList<FileInfo> latestLogs;
        try
        {
            latestLogs = EnumerateLatestSetupLogFiles(setupLogDirectory, MaxSetupLogFiles + 1);
        }
        catch (Exception ex) when (IsSetupLogEnumerationException(ex))
        {
            Logger.Warn($"Diagnostics bundle setup log enumeration failed: {ex.Message}");
            builder.AppendLine($"Status: setup logs unavailable ({ex.GetType().Name})");
            builder.AppendLine();
            return;
        }

        if (latestLogs.Count == 0)
        {
            builder.AppendLine("Status: no setup logs found");
            builder.AppendLine();
            return;
        }

        builder.AppendLine();
        foreach (var file in latestLogs.Take(MaxSetupLogFiles))
        {
            builder.Append(DiagnosticsLogTailReader.BuildSection(
                $"Setup Log Tail: {file.Name}",
                file.FullName,
                ShortJsonlTail));
        }
        if (latestLogs.Count > MaxSetupLogFiles)
            builder.AppendLine($"[truncated setup logs at {MaxSetupLogFiles} files]");
    }

    private static string FormatPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "not configured"
            : FormatSourcePath(path);

    private static string FormatSourcePath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "configured" : fileName;
    }

    internal static void ClearBundleCacheForTest()
    {
        // Preserved for existing tests; bundle generation is intentionally uncached.
    }

    private static IReadOnlyList<FileInfo> EnumerateLatestSetupLogFiles(string setupLogDirectory, int maxFiles) =>
        Directory.EnumerateFiles(setupLogDirectory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(maxFiles)
            .ToList();

    private static IEnumerable<string> EnumerateLatestSetupLogPaths(string setupLogDirectory)
    {
        IReadOnlyList<FileInfo> latestLogs;
        try
        {
            latestLogs = EnumerateLatestSetupLogFiles(setupLogDirectory, MaxSetupLogFiles);
        }
        catch (Exception ex) when (IsSetupLogEnumerationException(ex))
        {
            Logger.Warn($"Diagnostics bundle setup log cache signature failed: {ex.Message}");
            yield break;
        }

        foreach (var file in latestLogs)
            yield return file.FullName;
    }

    private static bool IsSetupLogEnumerationException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException;

    private static string TruncateBundle(string text)
    {
        if (text.Length <= MaxTotalBundleChars)
            return text;

        return text[..MaxTotalBundleChars] + Environment.NewLine + $"[truncated bundle at {MaxTotalBundleChars} chars]";
    }
}
