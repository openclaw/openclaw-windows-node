// Renders the shared recent-result row used by AutomationDetail.
// Usage: AutomationRunResult.Render(host, run, expanded, toggleExpanded, runOrder).
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Markdown;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using OpenClawTray.Chat;
using OpenClawTray.Services;
using static Microsoft.UI.Reactor.Factories;
using static OpenClawTray.Automations.AutomationsVisuals;

namespace OpenClawTray.Automations;

/// <summary>Recent results retain safe rich success text, selectable errors and run-specific chat links.</summary>
internal static class AutomationRunResult
{
    internal static Element Render(
        AutomationsHost host, CronRunEntry run, bool expanded, Action toggleExpanded, int runOrder = 0)
    {
        var t = host.Text;
        var (brush, glyph) = RunTone(run.Status);
        var isError = run.Status == "error";
        var display = CronRunHistoryDisplay.ExtractText(run.Raw, isError);
        var text = string.IsNullOrWhiteSpace(display.PreviewText) ? t["Runs_NoSummary"] : display.PreviewText;
        var full = expanded ? display.FullText ?? text : text;

        var at = run.EffectiveRunAtMs;
        var statusLine = HStack(Gap,
            Glyph(glyph, 14, brush),
            TextBlock(t.RunStatus(run.Status)).FontWeight(FontWeights.SemiBold).Foreground(brush),
            at is { } ms
                ? Secondary(t.RunTimestamp(ms), 12).VAlign(VerticalAlignment.Center).ToolTip(t.Relative(ms))
                : Secondary(t.Format("Designer_RunOrder", runOrder + 1), 12).VAlign(VerticalAlignment.Center));
        var headline = Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            statusLine.Grid(column: 0),
            (!string.IsNullOrWhiteSpace(run.SessionKey)
                ? ChatButton(host, run.SessionKey!, run.JobName ?? run.JobId, $"AutomationRunChat:{run.Key}")
                : Empty()).VAlign(VerticalAlignment.Center).Grid(column: 1));

        var chips = new List<Element>();
        if (run.DurationMs is { } duration)
            chips.Add(Pill(t.Duration(duration), Theme.SecondaryText, Theme.SystemNeutralBackground, GlyphClock));
        if (run.DeliveryStatus is { } delivery && !(run.Status != "ok" && delivery == "delivered"))
            chips.Add(Pill(t.DeliveryStatus(delivery),
                delivery == "delivered" ? Theme.SystemSuccess : delivery == "not-delivered" ? Theme.SystemCritical : Theme.SecondaryText,
                delivery == "delivered" ? Theme.SystemSuccessBackground : delivery == "not-delivered" ? Theme.SystemCriticalBackground : Theme.SystemNeutralBackground,
                GlyphSend).AutomationId($"AutomationRunDelivery:{run.Key}"));
        if (!string.IsNullOrWhiteSpace(run.Model))
            chips.Add(Pill(string.IsNullOrWhiteSpace(run.Provider) ? run.Model! : $"{run.Provider}/{run.Model}", Theme.SecondaryText, Theme.SystemNeutralBackground, GlyphSparkle));
        if (run.InputTokens is not null || run.OutputTokens is not null)
            chips.Add(Pill(t.Format("Runs_Tokens", (object?)run.InputTokens ?? AutomationsText.Unavailable, (object?)run.OutputTokens ?? AutomationsText.Unavailable),
                Theme.SecondaryText, Theme.SystemNeutralBackground));
        if (run.TriggerFired == true)
            chips.Add(Pill(t["Runs_TriggerFired"], Theme.AccentText, Theme.SystemAttentionBackground, GlyphBranch));

        var details = new List<Element?>
        {
            expanded && run.Status == "ok"
                ? InertMarkdown(full, host.CopyText).AutomationId($"AutomationRunSummary:{run.Key}")
                : TextBlock(full).FontSize(13).TextWrapping(TextWrapping.WrapWholeWords).IsTextSelectionEnabled(true)
                .Foreground(isError ? Theme.SystemCritical : Theme.PrimaryText)
                .MaxLines(expanded ? 0 : 3)
                .AutomationId($"AutomationRunSummary:{run.Key}"),
            run.DiagnosticsSummary is { Length: > 0 } diag ? Secondary(diag) : null,
            run.DeliverySuppressionReason is { Length: > 0 } reason ? Secondary(t.Format("Runs_Suppressed", reason)) : null,
            run.DeliveryError is { Length: > 0 } deliveryError ? ErrorText(t.Format("Runs_DeliveryError", deliveryError), $"AutomationRunDeliveryError:{run.Key}") : null,
        };

        var actions = new List<Element>();
        var canExpand = !string.IsNullOrWhiteSpace(display.FullText ?? text);
        if (canExpand)
            actions.Add(HyperlinkButton(!expanded ? t["Redesign_Response"] : t["Runs_ShowLess"], null, toggleExpanded)
                .AutomationId($"AutomationRunExpand:{run.Key}"));

        var body = VStack(Gap,
                headline,
                chips.Count == 0 ? Empty() : (FlexRow(chips.ToArray()) with { Wrap = Microsoft.UI.Reactor.Layout.FlexWrap.Wrap, ColumnGap = 6, RowGap = 6 }),
                VStack(4, details.ToArray()),
                actions.Count == 0 ? Empty() : HStack(4, actions.ToArray()));
        return Border(body).Padding(0, 8, 0, 12).BorderBrush(Theme.CardStroke).BorderThickness(0, 0, 0, 1);
    }

    private static Element InertMarkdown(string text, Func<string, bool> tryCopy)
    {
        // Run results retain a read-only link policy independently of chat navigation.
        Element Literal(string value) => TextBlock(value).FontSize(ChatVisuals.BodySize)
            .TextWrapping(TextWrapping.WrapWholeWords).IsTextSelectionEnabled(true).Foreground(Theme.PrimaryText);
        var options = new MarkdownOptions
        {
            Heading = ChatMarkdownPresentation.Heading,
            Paragraph = ChatMarkdownPresentation.Paragraph,
            CodeBlock = (code, language) => ChatMarkdownPresentation.CodeBlock(code, language, tryCopy: tryCopy),
            CodeFontFamily = ChatVisuals.CodeFontFamily,
            ParserFlags = MarkdownParserFlags.Tables | MarkdownParserFlags.NoHtml,
            LinkBuilder = link => HStack(link.Label),
            Image = (alt, _) => Literal(string.IsNullOrWhiteSpace(alt) ? "[Image]" : $"[Image: {alt}]"),
            HtmlBlock = raw => Literal(ChatMarkdownSanitizer.FlattenRawHtmlBlockToInertText(raw)),
        };
        return ChatMarkdownPresentation.WithInlineStyling(ChatMarkdownPresentation.MessageBlocks(
            Microsoft.UI.Reactor.Advanced.Factories.Markdown(ChatMarkdownSanitizer.Sanitize(text), options)));
    }
}
