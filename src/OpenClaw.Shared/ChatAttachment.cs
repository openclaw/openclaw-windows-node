using System;
using System.IO;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// A file attachment to include with a chat message. Matches the gateway's
/// <c>chat.send</c> attachment schema: <c>{ type, mimeType, fileName, content }</c>.
/// </summary>
public sealed class ChatAttachment
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "file";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; init; } = "application/octet-stream";

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = "";

    [JsonPropertyName("content")]
    public string Content { get; init; } = "";

    /// <summary>Original file size in bytes (client-side only, not sent to gateway).</summary>
    [JsonIgnore]
    public long SizeBytes { get; init; }

    /// <summary>
    /// Creates a <see cref="ChatAttachment"/> by reading a file from disk,
    /// base64-encoding its contents, and inferring the MIME type from the extension.
    /// </summary>
    public static ChatAttachment FromFile(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
            throw new FileNotFoundException("Attachment file not found", filePath);

        var bytes = File.ReadAllBytes(filePath);
        var mime = InferMimeType(info.Extension);

        return new ChatAttachment
        {
            Type = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "file",
            MimeType = mime,
            FileName = info.Name,
            Content = Convert.ToBase64String(bytes),
            SizeBytes = info.Length
        };
    }

    private static string InferMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".ts" => "text/typescript",
            ".cs" => "text/x-csharp",
            ".py" => "text/x-python",
            ".yaml" or ".yml" => "text/yaml",
            ".zip" => "application/zip",
            ".log" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    /// <summary>Human-readable size string for UI display.</summary>
    public string FormatSize()
    {
        return SizeBytes switch
        {
            < 1024 => $"{SizeBytes} B",
            < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
            _ => $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
        };
    }
}
