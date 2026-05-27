using OpenClaw.Shared;
using System.Text;

namespace OpenClawTray.Services;

internal sealed record DiagnosticsTailOptions(
    int MaxLines = 200,
    int MaxLineChars = 8_000,
    int MaxSectionChars = 256_000);

internal static class DiagnosticsLogTailReader
{
    public static string BuildSection(string title, string? path, DiagnosticsTailOptions? options = null)
    {
        options ??= new DiagnosticsTailOptions();
        var builder = new StringBuilder();
        builder.AppendLine($"## {title}");
        builder.AppendLine($"Source: {FormatPath(path)}");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            builder.AppendLine("Status: not found");
            builder.AppendLine();
            return builder.ToString();
        }

        try
        {
            var lines = ReadTail(path, options.MaxLines);
            builder.AppendLine($"Lines: last {lines.Count} of up to {options.MaxLines}");
            builder.AppendLine();

            var writtenChars = 0;
            foreach (var rawLine in lines)
            {
                var line = DiagnosticsExportRedactor.Sanitize(TruncateLine(rawLine, options.MaxLineChars));
                if (writtenChars + line.Length > options.MaxSectionChars)
                {
                    builder.AppendLine($"[truncated section at {options.MaxSectionChars} chars]");
                    break;
                }

                builder.AppendLine(line);
                writtenChars += line.Length + Environment.NewLine.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            builder.AppendLine($"Status: failed to read ({DiagnosticsExportRedactor.Sanitize(ex.Message)})");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    public static IReadOnlyList<string> ReadTail(string path, int maxLines)
    {
        if (maxLines <= 0)
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var queue = new Queue<string>(maxLines);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (queue.Count == maxLines)
                queue.Dequeue();
            queue.Enqueue(line);
        }
        return queue.ToArray();
    }

    private static string FormatPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "not configured"
            : DiagnosticsExportRedactor.RedactPath(path);

    private static string TruncateLine(string line, int maxChars)
    {
        if (maxChars <= 0 || line.Length <= maxChars)
            return line;

        return line[..maxChars] + $"... [truncated {line.Length - maxChars} chars]";
    }
}
