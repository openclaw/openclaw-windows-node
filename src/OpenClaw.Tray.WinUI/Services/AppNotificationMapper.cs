using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Markdown;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClawTray.Services;

internal static class AppNotificationMapper
{
    public static AppNotification FromGatewayNotification(OpenClawNotification notification, string? chatActionLabel = null)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var title = NormalizeTitle(notification.Title);
        var sourceMessage = notification.IsChat && !string.IsNullOrWhiteSpace(notification.FullMessage)
            ? notification.FullMessage
            : notification.Message;
        var message = NormalizeMessage(sourceMessage, title, notification.IsChat);
        var category = NormalizeCategory(notification.Type);
        var hasChatAction = notification.IsChat && !string.IsNullOrWhiteSpace(chatActionLabel);

        return new AppNotification
        {
            Title = title,
            Message = message,
            Source = "gateway",
            Category = category,
            Severity = SeverityFromGatewayType(category),
            DedupeKey = BuildDedupeKey(
                "gateway",
                notification.Type,
                notification.Title,
                message,
                notification.SessionKey),
            ActionLabel = hasChatAction ? chatActionLabel : null,
            ActionRoute = hasChatAction ? GetChatActionRoute(notification.SessionKey) : null
        };
    }

    public static AppNotification FromNodeSystemNotification(SystemNotifyArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var title = NormalizeTitle(args.Title);
        var message = NormalizeMessage(args.Body, title);

        return new AppNotification
        {
            Title = title,
            Message = message,
            Source = "node",
            Category = "system.notify",
            Severity = SeverityFromText(title, args.Body ?? string.Empty),
            DedupeKey = BuildDedupeKey("node-system-notify", args.Title, args.Body)
        };
    }

    public static AppNotification FromNodeActivity(
        string title,
        string message,
        string category,
        AppNotificationSeverity severity,
        string dedupeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);

        var normalizedTitle = NormalizeTitle(title);
        return new AppNotification
        {
            Title = normalizedTitle,
            Message = NormalizeMessage(message, normalizedTitle),
            Source = "node",
            Category = category.Trim(),
            Severity = severity,
            DedupeKey = dedupeKey.Trim()
        };
    }

    private static AppNotificationSeverity SeverityFromGatewayType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "error" => AppNotificationSeverity.Error,
        "urgent" or "health" => AppNotificationSeverity.Warning,
        _ => AppNotificationSeverity.Informational
    };

    private static AppNotificationSeverity SeverityFromText(string title, string message)
    {
        var text = string.Concat(title, " ", message);
        return text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? AppNotificationSeverity.Error
                : AppNotificationSeverity.Informational;
    }

    private static string NormalizeTitle(string? title)
    {
        string plainText = NotificationPlainTextFormatter.Format(title);
        return string.IsNullOrWhiteSpace(plainText) ? "OpenClaw" : plainText;
    }

    private static string NormalizeMessage(
        string? message,
        string title,
        bool stripControlMarkers = false)
    {
        string plainText = NotificationPlainTextFormatter.Format(message, stripControlMarkers);
        return string.IsNullOrWhiteSpace(plainText) && !stripControlMarkers ? title : plainText;
    }

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "info" : category.Trim();

    private static string GetChatActionRoute(string? sessionKey) =>
        string.IsNullOrWhiteSpace(sessionKey)
            ? "chat"
            : AppNotificationActionRoutes.Chat(sessionKey);

    private static string BuildDedupeKey(string scope, params string?[] parts)
    {
        var raw = string.Join("\u001f", parts.Select(part => part?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"{scope}:{hash}";
    }
}

internal static class NotificationPlainTextFormatter
{
    private const int MaximumPreviewLength = 200;
    private const int MaximumInputLength = 4_096;

    private static readonly Regex s_blankLines = new(@"\n{2,}", RegexOptions.Compiled);
    private static readonly Regex s_controlMarker = new(
        @"^[ \t]*(?:DONE|NO_REPLY|HEARTBEAT_OK)[ \t]*(?:\n|$)|(?<=[a-z0-9.!?])DONE(?=$|[A-Z])",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static string Format(string? value, bool stripControlMarkers = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        int inputLength = Math.Min(value.Length, MaximumInputLength);
        if (inputLength < value.Length && char.IsHighSurrogate(value[inputLength - 1]))
            inputLength--;
        var document = new ChatMarkdownAstBuilder().Build(value[..inputLength]);
        var builder = new StringBuilder(inputLength);
        AppendBlocks(builder, document.Blocks, stripControlMarkers);
        string text = builder.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        text = s_blankLines.Replace(text, "\n").Trim();

        return Truncate(text);
    }

    private static void AppendBlocks(
        StringBuilder builder,
        IReadOnlyList<MdBlock> blocks,
        bool stripControlMarkers)
    {
        foreach (MdBlock block in blocks)
        {
            switch (block)
            {
                case MdHeading heading: AppendInlines(builder, heading.Inlines, stripControlMarkers); break;
                case MdParagraph paragraph: AppendInlines(builder, paragraph.Inlines, stripControlMarkers); break;
                case MdBlockQuote quote: AppendBlocks(builder, quote.Children, stripControlMarkers); break;
                case MdCodeBlock code: builder.Append(code.Code.TrimEnd()); break;
                case MdRawTextBlock raw: builder.Append(raw.Text); break;
                case MdList list:
                    foreach (MdListItem item in list.Items)
                        AppendBlocks(builder, item.Children, stripControlMarkers);
                    break;
                case MdListItem item: AppendBlocks(builder, item.Children, stripControlMarkers); break;
                case MdTable table:
                    foreach (MdTableRow row in table.HeaderRows.Concat(table.BodyRows))
                    {
                        for (int i = 0; i < row.Cells.Count; i++)
                        {
                            if (i > 0) builder.Append(" | ");
                            AppendInlines(builder, row.Cells[i].Inlines, stripControlMarkers);
                        }
                        builder.AppendLine();
                    }
                    break;
            }

            if (builder.Length > 0 && builder[^1] != '\n') builder.AppendLine();
        }
    }

    private static void AppendInlines(
        StringBuilder builder,
        IReadOnlyList<MdInline> inlines,
        bool stripControlMarkers)
    {
        foreach (MdInline inline in inlines)
        {
            if (inline is MdInlineText text)
            {
                builder.Append(stripControlMarkers && !text.IsCode
                    ? s_controlMarker.Replace(text.Text, " ")
                    : text.Text);
            }
            else if (inline is MdInlineLineBreak) builder.AppendLine();
        }
    }

    private static string Truncate(string text)
    {
        if (text.Length <= MaximumPreviewLength)
            return text;

        int length = MaximumPreviewLength - 1;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            length--;
        return string.Concat(text.AsSpan(0, length).TrimEnd(), "…");
    }
}
