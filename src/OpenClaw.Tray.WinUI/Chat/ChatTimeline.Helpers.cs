using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using Windows.UI;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.Chat;

public partial class ChatTimeline
{
    // SECURITY (chat-rubber-duck HIGH 1 / MEDIUM 3): chat-bubble Markdown is
    // rendered as sanitized inert text that:
    //   1. Renders images as inert ``[Image: <alt>]`` text (no Uri fetch) —
    //      blocks SSRF / tracking-pixel beacons triggered by a compromised
    //      gateway, malicious tool output, or a prompt-injected model.
    //   2. Pre-strips inline link / image / ref-def syntax via
    //      <see cref="ChatMarkdownSanitizer.Sanitize(string?)"/> so explicit
    //      ``[text](url)`` syntax never reaches the parser.
    //   3. Renders raw HTML blocks as selectable plain text.
    //      Net effect: no click-to-navigate hyperlink or network-fetching
    //      image can be manufactured by untrusted Markdown inside a chat bubble.
    private static Element SafeMarkdownText(string? text)
    {
        if (!Markdown.ChatMarkdownRenderer.ContainsBlockMarkdown(text))
        {
            return TextBlock(string.Empty)
                .Set(t =>
                {
                    t.TextWrapping = TextWrapping.Wrap;
                    t.IsTextSelectionEnabled = true;
                    ApplySafeMarkdownInlines(t, text);
                });
        }

        return Markdown.ChatMarkdownRenderer.Render(text)
               ?? TextBlock(text ?? string.Empty)
                    .Set(t => { t.TextWrapping = TextWrapping.Wrap; t.IsTextSelectionEnabled = true; });
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, string>
        s_plainCache = new();

    private const string MonoFontFamilySource = "Cascadia Code, Cascadia Mono, Consolas";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue, FontFamily> s_monoFontByDispatcher = new();
    private const string ChatTextFontFamilySource = "Segoe UI Variable Text, Segoe UI";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue, FontFamily> s_chatTextFontByDispatcher = new();

    private static FontFamily s_monoFontFamily
    {
        get
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
                return new FontFamily(MonoFontFamilySource);

            if (!s_monoFontByDispatcher.TryGetValue(dispatcher, out var family))
            {
                family = new FontFamily(MonoFontFamilySource);
                s_monoFontByDispatcher.Add(dispatcher, family);
            }

            return family;
        }
    }

    private static FontFamily s_chatTextFontFamily
    {
        get
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
                return new FontFamily(ChatTextFontFamilySource);

            if (!s_chatTextFontByDispatcher.TryGetValue(dispatcher, out var family))
            {
                family = new FontFamily(ChatTextFontFamilySource);
                s_chatTextFontByDispatcher.Add(dispatcher, family);
            }

            return family;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue, SolidColorBrush> s_accentDarkByDispatcher = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue, SolidColorBrush> s_hcHighlightByDispatcher = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Microsoft.UI.Dispatching.DispatcherQueue,
        global::Windows.UI.ViewManagement.AccessibilitySettings> s_a11yByDispatcher = new();

    private static bool TryDetectHighContrast()
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            try { return new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
            catch { return false; }
        }

        if (!s_a11yByDispatcher.TryGetValue(dispatcher, out var settings))
        {
            try
            {
                settings = new global::Windows.UI.ViewManagement.AccessibilitySettings();
                s_a11yByDispatcher.Add(dispatcher, settings);
            }
            catch { return false; }
        }

        try { return settings.HighContrast; }
        catch
        {
            s_a11yByDispatcher.Remove(dispatcher);
            return false;
        }
    }

    private static SolidColorBrush GetUserBubbleSelectionBrush(bool isHighContrast)
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var table = isHighContrast ? s_hcHighlightByDispatcher : s_accentDarkByDispatcher;
        var color = isHighContrast
            ? TryGetThemeColor("SystemColorHighlightColor", Colors.Blue)
            : TryGetThemeColor("SystemAccentColorDark2", Colors.DarkBlue);

        if (dispatcher is null)
            return new SolidColorBrush(color);

        if (!table.TryGetValue(dispatcher, out var brush))
        {
            brush = new SolidColorBrush(color);
            table.Add(dispatcher, brush);
        }
        else if (brush.Color != color)
        {
            brush.Color = color;
        }

        return brush;
    }

    private static Color TryGetThemeColor(string key, Color fallback)
    {
        try
        {
            var app = Application.Current;
            if (app is null) return fallback;
            if (app.Resources.TryGetValue(key, out var v))
            {
                if (v is Color c) return c;
                if (v is SolidColorBrush brush) return brush.Color;
            }
        }
        catch (Exception ex) { OpenClawTray.Services.Logger.Debug($"ChatTimeline: resource brush lookup failed (unpackaged/test host?): {ex.Message}"); }

        return fallback;
    }

    private static void ApplyPlainSelectableInlines(TextBlock textBlock, string? text)
    {
        var normalized = text ?? string.Empty;
        if (textBlock.Inlines.Count > 0
            && s_plainCache.TryGetValue(textBlock, out var cached)
            && cached == normalized)
            return;

        s_plainCache.AddOrUpdate(textBlock, normalized);
        textBlock.Inlines.Clear();
        if (normalized.Length > 0)
            textBlock.Inlines.Add(new Run { Text = normalized });
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, string>
        s_markdownCache = new();

    private static void ApplySafeMarkdownInlines(TextBlock textBlock, string? text)
    {
        if (textBlock.Inlines.Count > 0
            && s_markdownCache.TryGetValue(textBlock, out var cached)
            && cached == text)
            return;

        s_markdownCache.AddOrUpdate(textBlock, text ?? "");
        textBlock.Inlines.Clear();

        foreach (var segment in ChatMarkdownSanitizer.SanitizeAndSplitStrongEmphasis(text))
        {
            if (segment.Text.Length == 0)
                continue;

            if (segment.IsStrong)
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = segment.Text });
                textBlock.Inlines.Add(bold);
            }
            else
            {
                textBlock.Inlines.Add(new Run { Text = segment.Text });
            }
        }
    }

    static string FormatToolLabel(ChatTimelineItem e)
    {
        var text = e.Text ?? "";
        return e.ToolName switch
        {
            "bash" or "powershell" => $"$ {text}",
            "read" or "view" => text,
            "edit" or "create" => text,
            "grep" => $"🔍 {text}",
            "glob" => $"📂 {text}",
            "web_fetch" => $"🌐 {text}",
            "web_search" => $"🔎 {text}",
            "task" => text,
            "report_intent" => text,
            _ => text == e.ToolName || string.IsNullOrEmpty(text) ? e.ToolName ?? "tool" : $"{e.ToolName}: {text}"
        };
    }

    static string CapitalizeFirst(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s[1..] : string.Empty);
    }

    static string TryFormatJsonForDisplay(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0) return text;
        var first = trimmed[0];
        if (first != '{' && first != '[') return text;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return text;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _bitmapCache = new();

    static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? TryDecodeBitmap(byte[] bytes)
    {
        if (_bitmapCache.TryGetValue(bytes, out var existing))
            return existing;
        try
        {
            var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new global::Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bmp.SetSource(stream);
            _bitmapCache.Add(bytes, bmp);
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
