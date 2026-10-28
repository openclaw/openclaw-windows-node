using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OpenClawTray.Chat.Markdown;

internal sealed class ChatTextSurface
{
    private sealed record Leaf(
        FrameworkElement Owner, string Text, TextPointer Start, TextPointer End, int Offset, int NativeOffset);
    private sealed record State(bool SelectionEnabled, AccessibilityView Accessibility, TextHighlighter Highlight);
    private readonly List<Leaf> _leaves = [];
    private readonly Dictionary<FrameworkElement, State> _states = [];
    private readonly List<int> _paragraphStarts = [];
    internal IReadOnlyList<int> ParagraphStarts => _paragraphStarts;
    internal FrameworkElement Element { get; }
    internal string Text { get; }
    internal int Start { get; set; }
    internal int End => Start + Text.Length;
    internal string Separator { get; set; } = "\n\n";

    internal ChatTextSurface(FrameworkElement element, ChatTextSurface? previous)
    {
        Element = element;
        var text = new StringBuilder();
        Collect(element);
        Text = text.ToString();
        if (previous is not null)
            foreach (var state in previous._states.Where(pair => !_states.ContainsKey(pair.Key)))
                Restore(state.Key, state.Value);

        void Collect(FrameworkElement owner)
        {
            if (owner is not RichTextBlock and not TextBlock)
            {
                IEnumerable<FrameworkElement> children = owner switch
                {
                    Panel panel => panel.Children.OfType<FrameworkElement>(),
                    Border { Child: FrameworkElement child } => [child],
                    ScrollViewer { Content: FrameworkElement child } => [child],
                    ContentControl content when content is not ButtonBase && content.Content is FrameworkElement child => [child],
                    _ => [],
                };
                foreach (var child in children)
                    Collect(child);
                return;
            }
            _states.Add(owner, previous is not null && previous._states.TryGetValue(owner, out var state)
                ? state : new(Enabled(owner), AutomationProperties.GetAccessibilityView(owner), new TextHighlighter()));
            var nativeOffset = 0;
            if (owner is RichTextBlock rich)
            {
                var first = true;
                foreach (var paragraph in rich.Blocks.OfType<Paragraph>())
                {
                    if (!first)
                    {
                        text.Append('\n');
                        nativeOffset++;
                    }
                    first = false;
                    if (owner == Element)
                        _paragraphStarts.Add(text.Length);
                    Append(owner, paragraph.Inlines, ref nativeOffset);
                }
            }
            else if (owner is TextBlock block)
            {
                if (owner == Element)
                    _paragraphStarts.Add(text.Length);
                var before = text.Length;
                Append(owner, block.Inlines, ref nativeOffset);
                if (before == text.Length && block.Text.Length > 0)
                {
                    _leaves.Add(new(owner, block.Text, block.ContentStart, block.ContentEnd, text.Length, 0));
                    text.Append(block.Text);
                }
            }
        }

        void Append(FrameworkElement owner, InlineCollection inlines, ref int nativeOffset)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        _leaves.Add(new(owner, run.Text, run.ContentStart, run.ContentEnd, text.Length, nativeOffset));
                        text.Append(run.Text);
                        nativeOffset += run.Text.Length;
                        break;
                    case LineBreak line:
                        _leaves.Add(new(owner, "\n", line.ContentStart, line.ContentEnd, text.Length, nativeOffset));
                        text.Append('\n');
                        nativeOffset++;
                        break;
                    case Span span:
                        Append(owner, span.Inlines, ref nativeOffset);
                        break;
                    case InlineUIContainer { Child: FrameworkElement child }:
                        // The native parent has an object position, not the embedded child's
                        // text. Keep their native highlight offsets and logical copy text separate.
                        nativeOffset++;
                        Collect(child);
                        break;
                }
            }
        }
    }

    internal void Apply(int start, int end, Brush background, Brush foreground)
    {
        foreach (var pair in _states)
        {
            if (Enabled(pair.Key))
                Enable(pair.Key, false);
            if (AutomationProperties.GetAccessibilityView(pair.Key) != AccessibilityView.Raw)
                AutomationProperties.SetAccessibilityView(pair.Key, AccessibilityView.Raw);
            var collection = Highlights(pair.Key);
            pair.Value.Highlight.Background = background;
            pair.Value.Highlight.Foreground = foreground;
            var expected = new List<TextRange>();
            foreach (var leaf in _leaves.Where(leaf => leaf.Owner == pair.Key))
            {
                var first = Math.Max(start - Start, leaf.Offset);
                var last = Math.Min(end - Start, leaf.Offset + leaf.Text.Length);
                if (last > first)
                    expected.Add(new TextRange
                    {
                        StartIndex = leaf.NativeOffset + first - leaf.Offset,
                        Length = last - first,
                    });
            }
            var ranges = pair.Value.Highlight.Ranges;
            if (ranges.Count == expected.Count && ranges.Zip(expected).All(values =>
                values.First.StartIndex == values.Second.StartIndex && values.First.Length == values.Second.Length))
            {
                if (expected.Count > 0 && !collection.Contains(pair.Value.Highlight))
                    collection.Add(pair.Value.Highlight);
                continue;
            }
            // Publish populated ranges through the owner's native collection. Mutating an
            // already attached range vector can leave the cached text drawing unchanged.
            collection.Remove(pair.Value.Highlight);
            ranges.Clear();
            foreach (var range in expected)
                ranges.Add(range);
            if (expected.Count > 0)
                collection.Add(pair.Value.Highlight);
        }
    }

    internal void Restore()
    {
        foreach (var pair in _states)
            Restore(pair.Key, pair.Value);
    }

    private static void Restore(FrameworkElement element, State state)
    {
        Highlights(element).Remove(state.Highlight);
        Enable(element, state.SelectionEnabled);
        AutomationProperties.SetAccessibilityView(element, state.Accessibility);
    }

    internal int OffsetFromPoint(Point point)
    {
        var owner = _states.Keys.LastOrDefault(child => child != Element &&
            child.TransformToVisual(Element).TransformBounds(
                new Rect(0, 0, child.ActualWidth, child.ActualHeight)).Contains(point)) ?? Element;
        var local = Element.TransformToVisual(owner).TransformPoint(point);
        if (owner is RichTextBlock rich)
        {
            local.X = Math.Clamp(local.X, 0, Math.Max(0, owner.ActualWidth - 0.1));
            local.Y = Math.Clamp(local.Y, 0, Math.Max(0, owner.ActualHeight - 0.1));
            var pointer = rich.GetPositionFromPoint(local);
            if (pointer is not null)
            {
                var owned = _leaves.Where(leaf => leaf.Owner == owner).ToArray();
                foreach (var leaf in owned)
                {
                    if (pointer.Offset <= leaf.Start.Offset)
                        return leaf.Offset;
                    if (pointer.Offset <= leaf.End.Offset)
                        return leaf.Offset + Math.Clamp(pointer.Offset - leaf.Start.Offset, 0, leaf.Text.Length);
                }
                if (owned.Length > 0)
                    return owned[^1].Offset + owned[^1].Text.Length;
            }
        }
        // TextBlock has no GetPositionFromPoint API. These are small native list
        // markers or inert inline placeholders; hit-test their real caret geometry.
        var best = double.PositiveInfinity;
        var offset = 0;
        for (var index = 0; index <= Text.Length; index++)
        {
            var rectangle = CharacterRectangle(index);
            if (rectangle.IsEmpty || rectangle.Height <= 0)
                continue;
            var distance = Math.Pow(point.X - rectangle.X, 2) +
                Math.Pow(point.Y - (rectangle.Y + rectangle.Height / 2), 2);
            if (distance < best)
            {
                best = distance;
                offset = index;
            }
        }
        return offset;
    }

    internal Rect CharacterRectangle(int position)
    {
        position = Math.Clamp(position, 0, Text.Length);
        var leaf = _leaves.FirstOrDefault(item => position >= item.Offset &&
            position < item.Offset + item.Text.Length) ?? _leaves.LastOrDefault();
        if (leaf is null)
            return Rect.Empty;
        var pointer = leaf.Start.GetPositionAtOffset(Math.Clamp(position - leaf.Offset, 0, leaf.Text.Length),
            LogicalDirection.Forward);
        var rectangle = pointer?.GetCharacterRect(LogicalDirection.Forward) ?? Rect.Empty;
        return rectangle.IsEmpty ? Rect.Empty : leaf.Owner.TransformToVisual(Element).TransformBounds(rectangle);
    }

    internal Rect GlyphRectangle(int position)
    {
        var current = CharacterRectangle(position);
        if (current.IsEmpty)
            return current;
        var next = CharacterRectangle(Math.Min(position + 1, Text.Length));
        if (!next.IsEmpty && Math.Abs(current.Y - next.Y) < 0.5)
        {
            var difference = Math.Abs(next.X - current.X);
            current.X = Math.Min(current.X, next.X);
            current.Width = Math.Max(current.Width, difference);
        }
        current.Width = Math.Max(1, current.Width);
        return current;
    }

    private static bool Enabled(FrameworkElement element) => element is RichTextBlock rich
        ? rich.IsTextSelectionEnabled : ((TextBlock)element).IsTextSelectionEnabled;

    private static void Enable(FrameworkElement element, bool enabled)
    {
        if (element is RichTextBlock rich)
            rich.IsTextSelectionEnabled = enabled;
        else
            ((TextBlock)element).IsTextSelectionEnabled = enabled;
    }

    private static IList<TextHighlighter> Highlights(FrameworkElement element) => element is RichTextBlock rich
        ? rich.TextHighlighters : ((TextBlock)element).TextHighlighters;
}
