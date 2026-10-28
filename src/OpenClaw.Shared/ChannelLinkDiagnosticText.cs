using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>Selectable linking diagnostics must not contain the pairing image.</summary>
public static class ChannelLinkDiagnosticText
{
    private const int MaxResponseLength = 1000;

    public static string Format(string method, string channelId, string? parametersJson, string? error, string? rawResponse,
        string? qrDataUrl = null)
    {
        var parts = new List<string>
        {
            $"Method:   {method}",
            $"Channel:  {channelId}",
            $"Params:   {Redact(parametersJson, qrDataUrl)}",
        };
        if (!string.IsNullOrEmpty(error))
            parts.Add($"Error:    {Redact(error, qrDataUrl)}");
        if (!string.IsNullOrEmpty(rawResponse))
        {
            var redacted = Redact(rawResponse, qrDataUrl);
            var trimmed = redacted.Length > MaxResponseLength
                ? redacted[..MaxResponseLength] + "…"
                : redacted;
            parts.Add($"Response: {trimmed}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>Removes image fields and echoed data URLs before display, logging or truncation.</summary>
    public static string Redact(string? text, string? qrDataUrl = null)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        if (!string.IsNullOrEmpty(qrDataUrl))
        {
            text = text.Replace(qrDataUrl, "<omitted>", StringComparison.Ordinal);
            var jsonImage = JsonSerializer.Serialize(qrDataUrl);
            text = text.Replace(jsonImage[1..^1], "<omitted>", StringComparison.Ordinal);
        }

        var withoutFields = Regex.Replace(
            text,
            @"(""(?i:qrDataUrl|currentQrDataUrl)""\s*:\s*"")(?:\\.|[^""\\])*("")",
            "$1<omitted>$2",
            RegexOptions.NonBacktracking);
        // The known image is removed exactly, preserving surrounding errors.
        // For an unknown/encoded image, fail closed rather than guess where
        // whitespace-wrapped payload ends and diagnostic prose begins.
        var imageStart = withoutFields.IndexOf("data:image", StringComparison.OrdinalIgnoreCase);
        return imageStart < 0 ? withoutFields : withoutFields[..imageStart] + "<omitted>";
    }
}
