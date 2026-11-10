// Adds inert, theme-bound syntax runs to tool input without changing its text or executing it.
// Usage: ChatToolInputSyntax.Apply(richText, displayedInput, toolEntry).
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ColorCode;
using ColorCode.Common;
using ColorCode.Compilation;
using ColorCode.Parsing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using OpenClaw.Chat;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>Presentation-only PowerShell and JSON coloring for already-sanitized tool inputs.</summary>
internal static class ChatToolInputSyntax
{
    private const int DefaultMatchTimeoutMilliseconds = 100;
    private static readonly TimeSpan MatchTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_CHAT_SYNTAX_TIMEOUT_MS"), out var timeout) && timeout > 0
            ? TimeSpan.FromMilliseconds(Math.Min(timeout, 1000))
            : TimeSpan.FromMilliseconds(DefaultMatchTimeoutMilliseconds);
    private static readonly ILanguage JsonLanguage = new ColorCode.Compilation.Languages.Json();
    private static readonly LanguageParser Parser = new(
        new BoundedCompiler(),
        new LanguageRepository(new Dictionary<string, ILanguage>
        {
            [Languages.PowerShell.Id] = Languages.PowerShell,
            [JsonLanguage.Id] = JsonLanguage,
        }));
    private sealed record RenderedInput(string Text, string Language, Paragraph Paragraph);
    private static readonly ConditionalWeakTable<RichTextBlock, RenderedInput> Rendered = new();

    /// <summary>Colors supported input while preserving every character and the native selection surface.</summary>
    internal static void Apply(RichTextBlock target, string source, ChatTimelineItem? entry)
    {
        var language = entry is null ? null : InputLanguage(source, entry);
        var languageId = language?.Id ?? "";
        if (Rendered.TryGetValue(target, out var previous) && previous.Text == source &&
            previous.Language == languageId && target.Blocks.Count == 1 &&
            ReferenceEquals(target.Blocks[0], previous.Paragraph))
            return;

        var roles = new string?[source.Length];
        var position = 0;
        try
        {
            if (language is not null)
                Parser.Parse(source, language, (fragment, scopes) =>
                {
                    foreach (var scope in scopes)
                        Mark(scope, position, roles);
                    position += fragment.Length;
                });
        }
        catch (RegexMatchTimeoutException)
        {
            Logger.Warn($"[ChatToolInputSyntax] Highlighting exceeded the match deadline ({languageId}); keeping literal input.");
            Array.Clear(roles);
        }

        var paragraph = new Paragraph();
        for (var start = 0; start < source.Length;)
        {
            var end = start + 1;
            while (end < source.Length && roles[end] == roles[start])
                end++;
            var run = roles[start] is { } resource
                ? (Run)XamlReader.Load(
                    $"<Run xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Foreground=\"{{ThemeResource {resource}}}\" />")
                : new Run();
            run.Text = source[start..end];
            paragraph.Inlines.Add(run);
            start = end;
        }
        target.Blocks.Clear();
        target.Blocks.Add(paragraph);
        Rendered.AddOrUpdate(target, new(source, languageId, paragraph));
    }

    private static ILanguage? InputLanguage(string source, ChatTimelineItem entry)
    {
        if (entry.ToolInputDetails?.Unavailable == true)
            return null;
        var name = NativeToolProjector.CanonicalizeToolIdentity(
            entry.ToolName ?? "", ChatToolIdentityStrength.Explicit).Name;
        if (name == "PowerShell")
            return Languages.PowerShell;
        var command = entry.ToolInputDetails?.Command;
        if (command is null && entry.ToolArgs?["command"] is System.Text.Json.Nodes.JsonValue value)
            value.TryGetValue(out command);
        var executable = command?.TrimStart().Split([' ', '\t', '\r', '\n'], 2)[0];
        if (executable is not null &&
            (executable.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
             executable.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)))
            return Languages.PowerShell;
        var text = source.AsSpan().TrimStart();
        return !text.IsEmpty && text[0] is '{' or '[' ? JsonLanguage : null;
    }

    private static void Mark(Scope scope, int position, string?[] roles)
    {
        var resource = scope.Name switch
        {
            ScopeName.PowerShellCommand or ScopeName.PowerShellVariable or ScopeName.PowerShellType or
                ScopeName.PowerShellAttribute or ScopeName.PowerShellParameter or ScopeName.JsonKey or
                ScopeName.JsonNumber => "ChatSyntaxIdentifierBrush",
            ScopeName.Keyword or ScopeName.PowerShellOperator or ScopeName.JsonConst => "ChatSyntaxKeywordBrush",
            ScopeName.String or ScopeName.StringCSharpVerbatim or ScopeName.JsonString => "ChatSyntaxStringBrush",
            ScopeName.Comment => "ChatSyntaxCommentBrush",
            _ => null,
        };
        if (resource is not null)
            Array.Fill(roles, resource, position + scope.Index, scope.Length);
        foreach (var child in scope.Children)
            Mark(child, position, roles);
    }

    private sealed class BoundedCompiler : ILanguageCompiler
    {
        private readonly LanguageCompiler _compiler = new(new(), new());
        private readonly Dictionary<string, CompiledLanguage> _compiled = new();

        public CompiledLanguage Compile(ILanguage language)
        {
            lock (_compiled)
            {
                if (_compiled.TryGetValue(language.Id, out var cached))
                    return cached;
                var compiled = _compiler.Compile(language);
                var bounded = new CompiledLanguage(compiled.Id, compiled.Name,
                    new Regex(compiled.Regex.ToString(), compiled.Regex.Options, MatchTimeout), compiled.Captures);
                _compiled.Add(language.Id, bounded);
                return bounded;
            }
        }
    }
}
