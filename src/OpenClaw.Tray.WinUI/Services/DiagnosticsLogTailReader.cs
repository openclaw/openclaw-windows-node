using System.Text;

namespace OpenClawTray.Services;

internal sealed record DiagnosticsTailOptions(
    int MaxLines = 200,
    int MaxLineChars = 8_000,
    int MaxSectionChars = 256_000,
    int MaxReadBytes = 512_000,
    DiagnosticsExportLineKind LineKind = DiagnosticsExportLineKind.Text,
    int SanitizationContextLines = 20);

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
            var lines = ReadSanitizedTail(path, options);
            builder.AppendLine($"Lines: last {lines.Count} of up to {options.MaxLines}");
            builder.AppendLine("Sanitization: applied during export; source log was not modified.");
            builder.AppendLine();

            var writtenChars = 0;
            foreach (var rawLine in lines)
            {
                var line = TruncateLine(rawLine, options.MaxLineChars);
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
            builder.AppendLine($"Status: failed to read ({ex.GetType().Name})");
        }

        builder.AppendLine();
        return builder.ToString();
    }

    public static IReadOnlyList<string> ReadTail(string path, int maxLines)
        => ReadTail(path, maxLines, maxReadBytes: 512_000);

    public static IReadOnlyList<string> ReadTail(string path, int maxLines, int maxReadBytes)
    {
        if (maxLines <= 0)
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
            return [];

        const int ChunkSize = 8192;
        var chunks = new Stack<byte[]>();
        var buffer = new byte[ChunkSize];
        var position = stream.Length;
        var newlineCount = 0;
        var bytesCollected = 0;
        maxReadBytes = Math.Max(1, maxReadBytes);

        while (position > 0 && newlineCount <= maxLines && bytesCollected < maxReadBytes)
        {
            var bytesToRead = (int)Math.Min(Math.Min(buffer.Length, position), maxReadBytes - bytesCollected);
            position -= bytesToRead;
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(buffer.AsSpan(0, bytesToRead));
            bytesCollected += bytesToRead;

            var chunk = buffer.AsSpan(0, bytesToRead).ToArray();
            chunks.Push(chunk);

            for (var i = bytesToRead - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                    newlineCount++;
            }
        }

        using var tailBytes = new MemoryStream();
        foreach (var chunk in chunks)
            tailBytes.Write(chunk);

        var text = Encoding.UTF8.GetString(tailBytes.ToArray())
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines.TakeLast(maxLines).ToArray();
    }

    public static IReadOnlyList<string> ReadSanitizedTail(string path, DiagnosticsTailOptions options)
    {
        var contextLineCount = Math.Max(0, options.SanitizationContextLines);
        var lines = ReadTail(path, options.MaxLines + contextLineCount, options.MaxReadBytes);
        return SanitizeTailLines(lines, options.LineKind)
            .TakeLast(options.MaxLines)
            .ToArray();
    }

    private static IReadOnlyList<string> SanitizeTailLines(IReadOnlyList<string> lines, DiagnosticsExportLineKind kind)
    {
        if (lines.Count == 0)
            return [];

        var sanitized = DiagnosticsExportSanitizer.SanitizeTextBlock(string.Join('\n', lines), kind)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (var i = 0; i < sanitized.Length; i++)
            sanitized[i] = DiagnosticsExportSanitizer.SanitizeLine(sanitized[i], kind);

        return sanitized;
    }

    private static string FormatPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? "not configured"
            : FormatSourcePath(path);

    private static string TruncateLine(string line, int maxChars)
    {
        if (maxChars <= 0 || line.Length <= maxChars)
            return line;

        return line[..maxChars] + $"... [truncated {line.Length - maxChars} chars]";
    }

    private static string FormatSourcePath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "configured" : fileName;
    }
}
