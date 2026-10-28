using System.Globalization;
using System.Text;

namespace OpenClawTray.Chat;

internal sealed record ChatTextFragment(string Text, string SeparatorBefore = "\n\n");

/// <summary>A single visible message body. Offsets are UTF-16, as in WinUI text ranges.</summary>
internal sealed class ChatTextSelectionDocument
{
    private int[] _characters = [0];
    internal string Text { get; private set; } = string.Empty;
    internal int Anchor { get; private set; }
    internal int Focus { get; private set; }
    internal int Start => Math.Min(Anchor, Focus);
    internal int End => Math.Max(Anchor, Focus);
    internal string SelectedText => Text[Start..End];
    internal bool HasSelection => Start != End;

    internal bool Update(IReadOnlyList<ChatTextFragment> fragments)
    {
        var builder = new StringBuilder();
        foreach (var fragment in fragments)
        {
            if (builder.Length > 0)
                builder.Append(fragment.SeparatorBefore);
            builder.Append(fragment.Text);
        }
        var next = builder.ToString();
        if (next == Text)
            return false;
        Anchor = MapPosition(Text, next, Anchor);
        Focus = MapPosition(Text, next, Focus);
        Text = next;
        _characters = [.. StringInfo.ParseCombiningCharacters(Text), Text.Length];
        return true;
    }

    internal void Select(int anchor, int focus)
    {
        Anchor = Math.Clamp(anchor, 0, Text.Length);
        Focus = Math.Clamp(focus, 0, Text.Length);
    }

    internal void MoveTo(int position, bool extend) =>
        Select(extend ? Anchor : position, position);

    internal void MoveHorizontal(int direction, bool word, bool extend)
    {
        if (!extend && HasSelection)
        {
            MoveTo(direction < 0 ? Start : End, extend: false);
            return;
        }
        MoveTo(word ? WordBoundary(Focus, direction) : CharacterBoundary(Focus, direction), extend);
    }

    internal int CharacterBoundary(int position, int direction)
    {
        var index = Array.BinarySearch(_characters, Math.Clamp(position, 0, Text.Length));
        if (index < 0)
            index = ~index - (direction < 0 ? 1 : 0);
        else
            index += Math.Sign(direction);
        return _characters[Math.Clamp(index, 0, _characters.Length - 1)];
    }

    internal int WordBoundary(int position, int direction)
    {
        position = Math.Clamp(position, 0, Text.Length);
        if (direction < 0)
        {
            while (position > 0 && char.IsWhiteSpace(Text, CharacterBoundary(position, -1)))
                position = CharacterBoundary(position, -1);
            if (position == 0)
                return 0;
            var word = IsWord(CharacterBoundary(position, -1));
            while (position > 0 && !char.IsWhiteSpace(Text, CharacterBoundary(position, -1)) &&
                IsWord(CharacterBoundary(position, -1)) == word)
                position = CharacterBoundary(position, -1);
            return position;
        }
        if (position < Text.Length && !char.IsWhiteSpace(Text[position]))
        {
            var word = IsWord(position);
            while (position < Text.Length && !char.IsWhiteSpace(Text[position]) &&
                IsWord(position) == word)
                position = CharacterBoundary(position, 1);
        }
        while (position < Text.Length && char.IsWhiteSpace(Text[position]))
            position = CharacterBoundary(position, 1);
        return position;
    }

    internal void SelectWord(int position)
    {
        position = Math.Clamp(position, 0, Text.Length);
        if (position == Text.Length && position > 0)
            position = CharacterBoundary(position, -1);
        if (Text.Length == 0)
            return;
        var boundary = Array.BinarySearch(_characters, position);
        if (boundary < 0)
            position = _characters[Math.Max(0, ~boundary - 1)];
        var start = position;
        var end = CharacterBoundary(position, 1);
        var word = IsWord(position);
        if (!char.IsWhiteSpace(Text[position]))
        {
            while (start > 0 && !char.IsWhiteSpace(Text, CharacterBoundary(start, -1)) &&
                IsWord(CharacterBoundary(start, -1)) == word)
                start = CharacterBoundary(start, -1);
            while (end < Text.Length && !char.IsWhiteSpace(Text[end]) && IsWord(end) == word)
                end = CharacterBoundary(end, 1);
        }
        Select(start, end);
    }

    internal static int MapPosition(string previous, string current, int position)
    {
        position = Math.Clamp(position, 0, previous.Length);
        var prefix = 0;
        while (prefix < previous.Length && prefix < current.Length && previous[prefix] == current[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < previous.Length - prefix && suffix < current.Length - prefix &&
            previous[previous.Length - suffix - 1] == current[current.Length - suffix - 1])
            suffix++;
        if (position <= prefix)
            return position;
        if (position >= previous.Length - suffix)
            return current.Length - (previous.Length - position);
        return prefix + Math.Min(position - prefix, current.Length - prefix - suffix);
    }

    private bool IsWord(int position) => char.IsLetterOrDigit(Text, position) || Text[position] == '_';
}
