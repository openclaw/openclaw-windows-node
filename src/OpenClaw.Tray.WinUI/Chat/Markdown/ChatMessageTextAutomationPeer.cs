using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using OpenClawTray.Chat;
using Windows.Foundation;

namespace OpenClawTray.Chat.Markdown;

internal sealed class ChatMessageTextAutomationPeer : FrameworkElementAutomationPeer, ITextProvider
{
    private readonly NativeChatMessageText _owner;

    internal ChatMessageTextAutomationPeer(NativeChatMessageText owner) : base(owner)
    {
        _owner = owner;
    }

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Text ? this : base.GetPatternCore(patternInterface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetClassNameCore() => "ChatMessageText";

    internal void NotifyTextChanged() => RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
    internal void NotifySelectionChanged() => RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);

    public ITextRangeProvider DocumentRange => NewRange(0, _owner.Document.Text.Length);
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection() => [NewRange(_owner.Document.Start, _owner.Document.End)];

    public ITextRangeProvider[] GetVisibleRanges() =>
        _owner.VisibleTextRanges().Select(range => (ITextRangeProvider)NewRange(range.Start, range.End)).ToArray();

    public ITextRangeProvider RangeFromPoint(Point screenLocation)
    {
        var offset = _owner.OffsetFromScreenPoint(screenLocation);
        return NewRange(offset, offset);
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        ArgumentNullException.ThrowIfNull(childElement);
        if (PeerFromProvider(childElement) is FrameworkElementAutomationPeer childPeer
            && childPeer.Owner is FrameworkElement childOwner
            && _owner.RangeForElement(childOwner) is { } range)
        {
            return NewRange(range.Start, range.End);
        }

        throw new ArgumentException("The element does not belong to this message's text.", nameof(childElement));
    }

    private TextRange NewRange(int start, int end) => new(this, start, end);

    private sealed class TextRange : ITextRangeProvider
    {
        private readonly ChatMessageTextAutomationPeer _peer;
        private string _text;
        private int _start;
        private int _end;

        internal TextRange(ChatMessageTextAutomationPeer peer, int start, int end)
        {
            _peer = peer;
            _text = peer._owner.Document.Text;
            _start = Math.Clamp(start, 0, _text.Length);
            _end = Math.Clamp(end, _start, _text.Length);
        }

        private void Rebase()
        {
            var current = _peer._owner.Document.Text;
            if (string.Equals(_text, current, StringComparison.Ordinal))
            {
                return;
            }

            _start = Math.Clamp(ChatTextSelectionDocument.MapPosition(_text, current, _start), 0, current.Length);
            _end = Math.Clamp(ChatTextSelectionDocument.MapPosition(_text, current, _end), _start, current.Length);
            _text = current;
        }

        private TextRange RequireRange(ITextRangeProvider range)
        {
            if (range is not TextRange other || !ReferenceEquals(other._peer, _peer))
            {
                throw new ArgumentException("The text range belongs to a different message.", nameof(range));
            }

            Rebase();
            other.Rebase();
            return other;
        }

        public ITextRangeProvider Clone()
        {
            Rebase();
            return _peer.NewRange(_start, _end);
        }

        public bool Compare(ITextRangeProvider textRange)
        {
            var other = RequireRange(textRange);
            return _start == other._start && _end == other._end;
        }

        public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
            TextPatternRangeEndpoint targetEndpoint)
        {
            var other = RequireRange(targetRange);
            return Endpoint(endpoint) - other.Endpoint(targetEndpoint);
        }

        private int Endpoint(TextPatternRangeEndpoint endpoint) => endpoint switch
        {
            TextPatternRangeEndpoint.Start => _start,
            TextPatternRangeEndpoint.End => _end,
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };

        private void SetEndpoint(TextPatternRangeEndpoint endpoint, int position)
        {
            position = Math.Clamp(position, 0, _text.Length);
            switch (endpoint)
            {
                case TextPatternRangeEndpoint.Start:
                    _start = position;
                    _end = Math.Max(_end, _start);
                    break;
                case TextPatternRangeEndpoint.End:
                    _end = position;
                    _start = Math.Min(_start, _end);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(endpoint));
            }
        }

        public void ExpandToEnclosingUnit(TextUnit unit)
        {
            Rebase();
            var boundaries = Boundaries(unit);
            var index = UnitIndex(boundaries, _start);
            _start = boundaries[index];
            _end = boundaries[Math.Min(index + 1, boundaries.Length - 1)];
        }

        public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
        {
            ArgumentException.ThrowIfNullOrEmpty(text);
            Rebase();
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var content = _text.Substring(_start, _end - _start);
            var index = backward ? content.LastIndexOf(text, comparison) : content.IndexOf(text, comparison);
            return index < 0 ? null! : _peer.NewRange(_start + index, _start + index + text.Length);
        }

        // WinUI's native wrapper maps E_NOT_SUPPORTED (0x80131515, the HRESULT of
        // NotSupportedException) to UIA's reserved unsupported value and S_OK.
        public object GetAttributeValue(int attributeId) =>
            throw new NotSupportedException("This text provider does not expose formatting attributes.");

        public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) =>
            throw new NotSupportedException("This text provider does not expose formatting attributes.");

        public void GetBoundingRectangles(out double[] returnValue)
        {
            Rebase();
            returnValue = _peer._owner.ScreenRectangles(_start, _end);
        }

        public IRawElementProviderSimple GetEnclosingElement() => _peer.ProviderFromPeer(_peer);
        public IRawElementProviderSimple[] GetChildren() => [];

        public string GetText(int maxLength)
        {
            if (maxLength < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            }

            Rebase();
            var length = _end - _start;
            return _text.Substring(_start, maxLength == -1 ? length : Math.Min(maxLength, length));
        }

        public void Select()
        {
            Rebase();
            _peer._owner.SelectRange(_start, _end);
        }

        public void AddToSelection()
        {
            Rebase();
            var document = _peer._owner.Document;
            if (document.Start != document.End && (document.Start != _start || document.End != _end))
            {
                throw new InvalidOperationException("This message supports only one selected range.");
            }

            Select();
        }

        public void RemoveFromSelection()
        {
            Rebase();
            var document = _peer._owner.Document;
            if (document.Start == _start && document.End == _end)
            {
                _peer._owner.SelectRange(_start, _start);
            }
        }

        public void ScrollIntoView(bool alignToTop)
        {
            Rebase();
            _peer._owner.ScrollRangeIntoView(_start, _end, alignToTop);
        }

        public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange,
            TextPatternRangeEndpoint targetEndpoint)
        {
            var other = RequireRange(targetRange);
            SetEndpoint(endpoint, other.Endpoint(targetEndpoint));
        }

        public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
        {
            Rebase();
            var position = Endpoint(endpoint);
            var boundaries = Boundaries(unit);
            if (count == 0)
            {
                return 0;
            }

            var index = Array.BinarySearch(boundaries, position);
            // An endpoint inside a unit reaches that unit's boundary in one step.
            var origin = index >= 0 ? index : count > 0 ? ~index - 1 : ~index;
            var destination = (int)Math.Clamp((long)origin + count, 0, boundaries.Length - 1);
            SetEndpoint(endpoint, boundaries[destination]);
            return destination - origin;
        }

        public int Move(TextUnit unit, int count)
        {
            Rebase();
            var boundaries = Boundaries(unit);
            if (count == 0)
            {
                return 0;
            }

            if (_start == _end)
            {
                var moved = MoveEndpointByUnit(TextPatternRangeEndpoint.Start, unit, count);
                _end = _start;
                return moved;
            }

            var origin = UnitIndex(boundaries, _start);
            var destination = (int)Math.Clamp((long)origin + count, 0, Math.Max(0, boundaries.Length - 2));
            _start = boundaries[destination];
            _end = boundaries[Math.Min(destination + 1, boundaries.Length - 1)];
            return destination - origin;
        }

        private static int UnitIndex(int[] boundaries, int position)
        {
            var index = Array.BinarySearch(boundaries, position);
            index = index >= 0 ? index : ~index - 1;
            return Math.Clamp(index, 0, Math.Max(0, boundaries.Length - 2));
        }

        private int[] Boundaries(TextUnit unit)
        {
            switch (unit)
            {
                case TextUnit.Document:
                case TextUnit.Page:
                    return _text.Length == 0 ? [0] : [0, _text.Length];
                case TextUnit.Format:
                    // Word is the next supported unit above Format in UIA's hierarchy.
                    return WordBoundaries();
                case TextUnit.Character:
                    return StringInfo.ParseCombiningCharacters(_text).Append(_text.Length).ToArray();
                case TextUnit.Word:
                    return WordBoundaries();
                case TextUnit.Line:
                    return _peer._owner.VisualLineBoundaries() ?? _peer._owner.ParagraphBoundaries();
                case TextUnit.Paragraph:
                    return _peer._owner.ParagraphBoundaries();
                default:
                    throw new ArgumentOutOfRangeException(nameof(unit));
            }
        }

        private int[] WordBoundaries()
        {
            var characters = StringInfo.ParseCombiningCharacters(_text);
            var boundaries = new List<int> { 0 };
            var previousKind = -1;
            foreach (var position in characters)
            {
                var kind = char.IsWhiteSpace(_text, position) ? 0
                    : char.IsLetterOrDigit(_text, position) || _text[position] == '_' ? 1 : 2;
                // Trailing whitespace belongs to the preceding word, as in native text controls.
                if (position > 0 && kind != 0 && (kind != previousKind || kind == 2))
                {
                    boundaries.Add(position);
                }

                previousKind = kind;
            }

            if (boundaries[^1] != _text.Length)
            {
                boundaries.Add(_text.Length);
            }

            return boundaries.ToArray();
        }
    }
}
