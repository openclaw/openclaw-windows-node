// Guards the staged single owner of chat transcript scrolling (ARCHITECTURE row chat-scroll-owner).
//
// Until every scroll decision family has moved into the scroll policy, the native executor
// (ReactorItemsViewScrollController.cs) is frozen against a reviewed allowance
// (ChatScrollOwnerAllowance.txt). The allowance is built from C# syntax trees, so comments and
// formatting do not count. It records:
// - for the executor: types, record parameters, fields and properties, every write and invocation per
//   member, method-group references, subscriptions, preprocessor directives, and a token hash of each
//   member, so any logic edit needs review;
// - for every chat file: movement, focus and bring calls or delegates, anchor writes and anchor
//   dependency properties, BringIntoViewOptions and bring-target writes, bring/anchor event hooks,
//   tail-jump triggers (ScrollToBottomToken, JumpRevision and their setters), every use of the tail
//   request key, and token hashes of the request key, its controller binding and the transcript fade's
//   bring handler. Names inside #if branches that no scanned symbol set enables still count.
//
// Usage: a reviewed executor change regenerates the allowance locally with
//   $env:OPENCLAW_UPDATE_SCROLL_OWNER_ALLOWANCE = '1'; dotnet test --filter ChatScrollOwnerGuardTests
// and requests chat-scroll-owner review (CODEOWNERS). A new scroll decision belongs in the scroll
// policy, not in the executor or another chat file.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenClaw.Tray.Tests;

public sealed class ChatScrollOwnerGuardTests
{
    private const string OwnerFile = "ReactorItemsViewScrollController.cs";
    private const string AllowanceFile = "ChatScrollOwnerAllowance.txt";
    private const string UpdateVariable = "OPENCLAW_UPDATE_SCROLL_OWNER_ALLOWANCE";

    // Calls (or delegates) that move a ScrollView, bring content into view, or focus an element,
    // which brings it into view.
    private static readonly HashSet<string> MovementNames = new(StringComparer.Ordinal)
    {
        "ScrollTo", "ScrollBy", "ScrollFrom", "ChangeView", "ZoomTo", "ZoomBy", "ZoomFrom",
        "StartBringIntoView", "StartBringItemIntoView", "TryStartScroll", "AddScrollVelocity", "ScrollIntoView",
        "Focus", "TryFocusAsync", "TryMoveFocus", "TryMoveFocusAsync",
    };

    // Writes that choose an anchor or change where a bring lands.
    private static readonly HashSet<string> AnchorWrites = new(StringComparer.Ordinal)
    {
        "VerticalAnchorRatio", "HorizontalAnchorRatio", "AnchorElement", "TargetRect",
    };

    // Names whose any use sets an anchor or shapes a bring. Alignment ratios count only through
    // BringIntoViewOptions, because composition brushes reuse those property names.
    private static readonly HashSet<string> AnchorNames = new(StringComparer.Ordinal)
    {
        "VerticalAnchorRatioProperty", "HorizontalAnchorRatioProperty", "BringIntoViewOptions",
    };

    private static readonly HashSet<string> MovementEvents = new(StringComparer.Ordinal)
    {
        "BringingIntoView", "AnchorRequested", "BringIntoViewRequested", "BringIntoViewRequestedEvent",
    };

    // Reactor state and request inputs that force a tail jump.
    private static readonly HashSet<string> TailTriggers = new(StringComparer.Ordinal)
    {
        "setScrollToBottomToken", "setJumpRevision", "ScrollToBottomToken", "JumpRevision",
    };

    // Sites whose full text decides when the owner re-runs its initial tail request; every use is
    // counted and the declaration or binding is pinned by token hash.
    private static readonly HashSet<string> TailRequestSites = new(StringComparer.Ordinal)
    {
        "initialTailRequestKey", "BindVerticalScrollController",
    };

    // Handlers outside the owner that reshape a transcript bring; pinned by token hash.
    private static readonly HashSet<string> BringAdjusterSites = new(StringComparer.Ordinal)
    {
        "s_keepBringAboveBand",
    };

    private static readonly ConcurrentDictionary<string, string[]> s_directiveSymbols = new();
    private static readonly ConcurrentDictionary<(string File, string Source, string Symbols), Dictionary<string, int>> s_fileInventories = new();

    // Build symbols always scanned in addition to every symbol used by a chat-file directive.
    private static readonly string[] BuildSymbols = ["DEBUG", "RELEASE", "TRACE", "WINDOWS", "DEV_BUILD", "OPENCLAW_TRAY_TESTS"];

    [Fact]
    public void ConversationScrolling_HasOneNativeOwnerAndNoNewDecisionState()
    {
        var sources = ReadChatSources();
        Assert.True(sources.ContainsKey(OwnerFile), $"{OwnerFile} moved; update the chat-scroll-owner guard and ledger row.");
        var actual = Inventory(sources);
        Assert.Contains(actual, line => line.StartsWith("state\t", StringComparison.Ordinal));
        Assert.All(actual, line => Assert.Equal(3, line.Split('\t').Length));

        var path = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "tests", "OpenClaw.Tray.Tests", AllowanceFile);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")), $"{UpdateVariable} must not be set in CI.");
            File.WriteAllLines(path, actual);
        }

        var differences = Compare(File.ReadAllLines(path), actual);
        var outsideOwner = differences.Any(line => FileOf(line) != OwnerFile);
        Assert.True(differences.Count == 0,
            "Chat transcript scrolling no longer matches its reviewed allowance (ARCHITECTURE chat-scroll-owner).\n"
            + (outsideOwner
                ? $"A chat file other than {OwnerFile} gained or lost a transcript movement, focus, anchor, bring or tail-jump site. "
                  + "Route transcript movement through the scroll owner; popup lists that scroll themselves are pinned here after review.\n"
                : "")
            + $"A new scroll decision belongs in the scroll policy, not in {OwnerFile}. If this is a reviewed executor or popup "
            + $"change, regenerate {AllowanceFile} with {UpdateVariable}=1 and request chat-scroll-owner review.\n"
            + string.Join("\n", differences));
    }

    // Each mutation of the executor must change the inventory.
    [Theory]
    [InlineData("qualified write", "write", "        _initialPending = true;\n        AwaitLayout(renderAfterLayout: _explicitJumpPending);",
        "        _initialPending = true;\n        this._following = true;\n        AwaitLayout(renderAfterLayout: _explicitJumpPending);")]
    [InlineData("null-coalescing write", "write", OnLoaded, OnLoaded + "\n        _historyRequest ??= default;")]
    [InlineData("tuple write", "write", OnLoaded, OnLoaded + "\n        (_following, _awaitingIdle) = (true, false);")]
    [InlineData("ref write", "write", OnLoaded, OnLoaded + "\n        System.Threading.Volatile.Write(ref _following, true);")]
    [InlineData("non-decision state write", "write", OnLoaded, OnLoaded + "\n        _awaitingIdle = true;")]
    [InlineData("field initializer", "write", Following, "    private bool _following = true;")]
    [InlineData("field without underscore", "state", Following, Following + "\n    private bool pinned;")]
    [InlineData("static field", "state", Following, Following + "\n    private static bool s_pin;")]
    [InlineData("internal field", "state", Following, "    internal bool _following;")]
    [InlineData("tuple field", "state", Following, Following + "\n    private (bool Pin, int Id) _pin;")]
    [InlineData("multiple declarators", "state", Following, "    private bool _following, _pinPending;")]
    [InlineData("enum field", "state", Following, Following + "\n    private ScrollingInteractionState _phase;")]
    [InlineData("helper object field", "state", Following, Following + "\n    private readonly object _pinOwner = new();")]
    [InlineData("auto-property", "state", Following, Following + "\n    private bool Pinned { get; set; }")]
    [InlineData("nested type", "type", Following, Following + "\n    private sealed class PinState { public bool Pending; }")]
    [InlineData("conditional state", "directive", Following, Following + "\n#if WINDOWS\n    private bool _pin;\n#endif")]
    [InlineData("record parameter", "state", "    IReadOnlyList<ChatHistoryAnchor> HistoryAnchors) : Element",
        "    IReadOnlyList<ChatHistoryAnchor> HistoryAnchors,\n    bool PinToTail = false) : Element")]
    [InlineData("threshold", "body", "    private const double FollowThreshold = 60;", "    private const double FollowThreshold = 120;")]
    [InlineData("condition edit", "body", "        _following = _readerBringCorrelationId is null && IsNearBottom(sender) && _historyRequest is null;",
        "        _following = _readerBringCorrelationId is null && IsNearBottom(sender);")]
    [InlineData("implicit-private writer", "write", ReportTail, "    void Pin() { _following = true; }\n\n" + ReportTail)]
    [InlineData("generic writer", "write", ReportTail, "    private void Pin<T>() => _following = true;\n\n" + ReportTail)]
    [InlineData("spaced movement", "move", OnLoaded, OnLoaded + "\n        _scrollView?.ScrollBy (0, 100);")]
    [InlineData("wrapped movement", "move", OnLoaded, OnLoaded + "\n        _scrollView?.ChangeView\n            (null, 0, null);")]
    [InlineData("deferred movement", "move", OnLoaded, OnLoaded + "\n        itemsView.DispatcherQueue.TryEnqueue(itemsView.StartBringIntoView);")]
    [InlineData("focus-driven bring", "move", OnLoaded, OnLoaded + "\n        itemsView.Focus(FocusState.Programmatic);")]
    [InlineData("anchor ratio", "anchor", OnLoaded, OnLoaded + "\n        if (_scrollView is not null) _scrollView.VerticalAnchorRatio = 0;")]
    [InlineData("anchor selection", "anchor", "                        args.AnchorElement = target;",
        "                        args.AnchorElement = target;\n                        args.AnchorElement = repeater;")]
    [InlineData("cancelled bring", "write", OnViewChanged, OnViewChanged + "\n        if (_disposed) ((BringingIntoViewEventArgs)args).Cancel = true;")]
    [InlineData("helper object call", "call", OnViewChanged, OnViewChanged + "\n        _reflowCorrection.Arm();")]
    [InlineData("new call edge", "call", OnLoaded, OnLoaded + "\n        CancelPendingNavigation();")]
    [InlineData("new subscription", "subscribe", OnLoaded, OnLoaded + "\n        itemsView.GotFocus += (_, _) => { };")]
    [InlineData("routed handler", "handler", OnLoaded, OnLoaded + "\n        itemsView.AddHandler(UIElement.KeyDownEvent, _readerPointerPressed, true);")]
    public void ConversationScrolling_AllowanceRejectsExecutorMutations(string control, string kind, string original, string mutated)
    {
        var sources = ReadChatSources();
        var baseline = Inventory(sources);
        Mutate(sources, OwnerFile, original, mutated, control);
        AssertDetected(Compare(baseline, Inventory(sources)), kind);
    }

    // A write moved from one member to another keeps every count but changes its owner member.
    [Fact]
    public void ConversationScrolling_AllowanceRejectsARelocatedWrite()
    {
        var sources = ReadChatSources();
        var baseline = Inventory(sources);
        Mutate(sources, OwnerFile,
            "        _following = true;\n        _initialPending = false;\n        _explicitJumpPending = explicitJump;",
            "        _initialPending = false;\n        _explicitJumpPending = explicitJump;", "relocated write");
        Mutate(sources, OwnerFile, OnLoaded, OnLoaded + "\n        _following = true;", "relocated write");
        Assert.NotEmpty(Compare(baseline, Inventory(sources)));
    }

    // Comments, line breaks and literal text do not change the inventory.
    [Theory]
    [InlineData("    private void OnLoaded(object sender, RoutedEventArgs args)",
        "    private void OnLoaded(\n        object sender, // ScrollBy(0, 1)\n        RoutedEventArgs args) /* _following = true; */")]
    [InlineData("    private TypedEventHandler<WinUIScrollView, ScrollingAnchorRequestedEventArgs>? _offsetCorrectionAnchorRequested;",
        "    private /* anchor */ TypedEventHandler<\n        WinUIScrollView,\n        ScrollingAnchorRequestedEventArgs>? _offsetCorrectionAnchorRequested;")]
    public void ConversationScrolling_AllowanceIgnoresExecutorFormatting(string original, string equivalent)
    {
        var sources = ReadChatSources();
        var baseline = Inventory(sources);
        Mutate(sources, OwnerFile, original, equivalent, "formatting");
        var actual = Inventory(sources);
        Assert.Empty(Compare(baseline, actual));
        Assert.All(actual, line => Assert.Equal(3, line.Split('\t').Length));
    }

    // Literal text, comments and brush alignment outside the owner do not change the inventory.
    [Theory]
    [InlineData("ReactorChatTimeline.cs", "    private const string Note = \"ScrollTo(0, 0) Focus() VerticalAnchorRatio = 0\"; // row.StartBringIntoView();")]
    [InlineData("ChatTranscriptFade.cs", "    private static void Align(CompositionSurfaceBrush brush) { brush.HorizontalAlignmentRatio = 0; brush.VerticalAlignmentRatio = 1; }")]
    public void ConversationScrolling_NonMovementCodeOutsideTheOwnerIsIgnored(string file, string member)
    {
        var sources = ReadChatSources();
        var baseline = Inventory(sources);
        AddMember(sources, file, member);
        Assert.Empty(Compare(baseline, Inventory(sources)));
    }

    [Theory]
    [InlineData("ReactorChatTimeline.cs", "move", "    private static void Pin(ScrollView scroll) => scroll.ScrollTo(0, scroll.ScrollableHeight);")]
    [InlineData("ReactorChatTimeline.cs", "move", "    private static void Pin(Control row) => row.Focus(FocusState.Programmatic);")]
    [InlineData("ReactorChatTimeline.cs", "move", "    private static void Pin(ScrollView scroll) => scroll.ScrollFrom(new(0, 10), null);")]
    [InlineData("ReactorChatTimeline.cs", "move", "    private static void Pin(FrameworkElement row) => row.DispatcherQueue.TryEnqueue(row.StartBringIntoView);")]
    [InlineData("ReactorChatTimeline.cs", "event", "    private static void Hook(ScrollView scroll) => scroll.BringingIntoView += (_, args) => args.Cancel = true;")]
    [InlineData("ReactorChatTimeline.cs", "event", "    private static void Hook(UIElement row) => row.BringIntoViewRequested += (_, args) => args.TargetRect = default;")]
    [InlineData("ReactorChatTimeline.cs", "event", "    private static void Hook(UIElement row, object handler) => row.AddHandler(UIElement.BringIntoViewRequestedEvent, handler, true);")]
    [InlineData("ReactorChatTimeline.cs", "move", "#if WINDOWS\n    private static void Pin(ScrollView scroll) => scroll.ScrollTo(0, 0);\n#endif")]
    [InlineData("ReactorChatTimeline.cs", "move", "#if DEBUG && !OPENCLAW_TRAY_TESTS\n    private static void Pin(ScrollView scroll) => scroll.ScrollTo(0, 0);\n#endif")]
    [InlineData("ReactorChatTimeline.cs", "move", "#if WINDOWS && !DEBUG\n    private static void Pin(ScrollView scroll) => scroll.ScrollTo(0, 0);\n#endif")]
    [InlineData("ReactorChatTimeline.cs", "move", "#if DEBUG\n    private static void Other() { }\n#elif WINDOWS\n    private static void Pin(ScrollView scroll) => scroll.ScrollTo(0, 0);\n#endif")]
    [InlineData("ReactorChatTimeline.cs", "anchor", "    private static void Pin(ScrollView scroll) => (scroll.VerticalAnchorRatio, scroll.HorizontalAnchorRatio) = (0, 0);")]
    [InlineData("ReactorChatTimeline.cs", "anchor", "    private static void Pin(ScrollView scroll) => scroll.SetValue(ScrollView.VerticalAnchorRatioProperty, 0.0);")]
    [InlineData("ReactorChatTimeline.cs", "anchor", "    private static BringIntoViewOptions Bottom() => new() { VerticalAlignmentRatio = 1 };")]
    [InlineData("ReactorChatComposer.cs", "move", "    private static void Pin(FrameworkElement element) => element.StartBringIntoView();")]
    [InlineData("ChatTranscriptFade.cs", "anchor", "    private static void Pad(BringIntoViewRequestedEventArgs args) => args.TargetRect = default;")]
    [InlineData("OpenClawReactorChatRoot.cs", "trigger", "    private static void Bump(Action<int> setScrollToBottomToken) => setScrollToBottomToken(1);")]
    [InlineData("ReactorChatTimeline.cs", "trigger", "    private static ChatTimelinePresentationContext Jump(ChatTimelinePresentationContext context) => context with { ScrollToBottomToken = 1 };")]
    public void ConversationScrolling_MovementOutsideTheOwnerIsRejected(string file, string kind, string member)
    {
        var sources = ReadChatSources();
        var baseline = Inventory(sources);
        AddMember(sources, file, member);
        AssertDetected(Compare(baseline, Inventory(sources)), kind);
    }

    // A new input to the tail request key re-lands the transcript without any new movement call, and
    // a changed fade handler moves where every bring lands.
    [Theory]
    [InlineData("ReactorChatTimeline.cs", "trigger", "|{props.Timeline.ScrollToBottomToken}|{jumpRevision}\";",
        "|{props.Timeline.ScrollToBottomToken}|{jumpRevision}|{props.Timeline.SessionId}\";")]
    [InlineData("ReactorChatTimeline.cs", "trigger", "|{props.Timeline.ScrollToBottomToken}|{jumpRevision}\";",
        "|{props.Timeline.ScrollToBottomToken}|{jumpRevision}\";\n        initialTailRequestKey += $\"|{props.Timeline.SessionId}\";")]
    [InlineData("ReactorChatTimeline.cs", "trigger", "|{props.Timeline.ScrollToBottomToken}|{jumpRevision}\";",
        "|{props.Timeline.ScrollToBottomToken}|{jumpRevision}\";\n        initialTailRequestKey = initialTailRequestKey + \"|wide\";")]
    [InlineData("ChatTranscriptFade.cs", "anchor", "rect.Height + ChatVisuals.TranscriptFadeHeight",
        "rect.Height + 2 * ChatVisuals.TranscriptFadeHeight")]
    public void ConversationScrolling_TailRequestAndBringAdjusterChangesAreRejected(string file, string kind, string original, string mutated)
    {
        var sources = ReadChatSources();
        var baseline = Inventory(sources);
        Mutate(sources, file, original, mutated, kind);
        AssertDetected(Compare(baseline, Inventory(sources)), kind);
    }

    [Theory]
    [InlineData("ChatNewRow.cs", "namespace OpenClawTray.Chat; internal sealed class ChatNewRow { void M(Microsoft.UI.Xaml.Controls.Control row) => row.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }")]
    [InlineData("Nested/" + OwnerFile, "namespace X; file sealed class Copy { void M(Microsoft.UI.Xaml.Controls.ScrollView s) => s.ScrollTo(0, 0); }")]
    public void ConversationScrolling_NewChatFilesAreScanned(string file, string source)
    {
        var sources = ReadChatSources();
        var baseline = Inventory(sources);
        sources[file] = source;
        AssertParses(file, source);
        AssertDetected(Compare(baseline, Inventory(sources)), "move");
    }

    private static void AssertDetected(IReadOnlyList<string> differences, string kind) =>
        Assert.True(differences.Any(line => line.StartsWith($"+ {kind}\t", StringComparison.Ordinal)),
            $"Expected a new '{kind}' entry; differences:\n{string.Join("\n", differences)}");

    private const string OnLoaded = "    private void OnLoaded(object sender, RoutedEventArgs args)\n    {";
    private const string OnViewChanged = "    private void OnViewChanged(WinUIScrollView sender, object args)\n    {";
    private const string Following = "    private bool _following;";
    private const string ReportTail = "    private void ReportTailVisibility(bool atTail)";

    private static Dictionary<string, string> ReadChatSources()
    {
        var chat = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Chat");
        return Directory.EnumerateFiles(chat, "*.cs", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(chat, file).Replace('\\', '/'),
                file => File.ReadAllText(file).ReplaceLineEndings("\n"), StringComparer.Ordinal);
    }

    // Sorted "kind<TAB>file|key<TAB>count" lines, scanned with no build symbols and with every known
    // symbol so code under #if is visible; a key keeps its larger count. Names in branches that neither
    // pass enables are still counted from their disabled text. Per-file results are cached by content.
    private static List<string> Inventory(IReadOnlyDictionary<string, string> sources)
    {
        var symbols = BuildSymbols.Concat(sources.Values.SelectMany(source => s_directiveSymbols.GetOrAdd(source, DirectiveSymbols)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var defined in new[] { Array.Empty<string>(), symbols })
        {
            var pass = new Dictionary<string, int>(StringComparer.Ordinal);
            var symbolKey = string.Join(";", defined);
            foreach (var (file, source) in sources)
            {
                var fileCounts = s_fileInventories.GetOrAdd((file, source, symbolKey), key =>
                {
                    var result = new Dictionary<string, int>(StringComparer.Ordinal);
                    var options = CSharpParseOptions.Default.WithPreprocessorSymbols(defined);
                    InventoryFile(key.File, CSharpSyntaxTree.ParseText(key.Source, options).GetCompilationUnitRoot(), result);
                    return result;
                });
                foreach (var (key, count) in fileCounts)
                    pass[key] = pass.GetValueOrDefault(key) + count;
            }
            foreach (var (key, count) in pass)
                counts[key] = Math.Max(count, counts.GetValueOrDefault(key));
        }

        return counts.Select(entry => $"{entry.Key}\t{entry.Value}").ToList();
    }

    private static void InventoryFile(string file, CompilationUnitSyntax root, Dictionary<string, int> counts)
    {
        var isOwner = file == OwnerFile;
        void Add(string kind, string key) => counts[$"{kind}\t{Line(key)}"] = counts.GetValueOrDefault($"{kind}\t{Line(key)}") + 1;
        string Site(SyntaxNode node, string what) => isOwner ? $"{file}|{Member(node)}|{what}" : $"{file}|{what}";

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var text = name.Identifier.Text;
            if (MovementNames.Contains(text))
                Add("move", Site(name, text));
            else if (TailTriggers.Contains(text) || TailRequestSites.Contains(text))
                Add("trigger", Site(name, text));
            else if (MovementEvents.Contains(text))
                Add("event", Site(name, text + SubscriptionOperator(name)));
            else if (AnchorNames.Contains(text))
                Add("anchor", Site(name, text));
        }

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            foreach (var target in Targets(assignment.Left).Where(AnchorWrites.Contains))
                Add("anchor", Site(assignment, target));

        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            var hashed = $"{declarator.Identifier.Text}:{Hash(Canon(declarator))}";
            if (TailRequestSites.Contains(declarator.Identifier.Text))
                Add("trigger", Site(declarator, hashed));
            else if (BringAdjusterSites.Contains(declarator.Identifier.Text))
                Add("anchor", Site(declarator, hashed));
        }
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                     .Where(invocation => TailRequestSites.Contains(InvokedName(invocation.Expression))))
            Add("trigger", Site(invocation, $"{InvokedName(invocation.Expression)}:{Hash(Canon(invocation.ArgumentList))}"));

        foreach (var disabled in root.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
            foreach (var token in SyntaxFactory.ParseTokens(disabled.ToString()).Where(token => token.IsKind(SyntaxKind.IdentifierToken)))
            {
                var kind = MovementNames.Contains(token.Text) ? "move"
                    : TailTriggers.Contains(token.Text) || TailRequestSites.Contains(token.Text) ? "trigger"
                    : MovementEvents.Contains(token.Text) ? "event"
                    : AnchorWrites.Contains(token.Text) || AnchorNames.Contains(token.Text) ? "anchor"
                    : null;
                if (kind is not null)
                    Add(kind, Site(disabled.Token.Parent ?? root, "disabled:" + token.Text));
            }

        if (!isOwner)
            return;

        foreach (var directive in root.DescendantTrivia().Where(trivia => trivia.IsDirective))
            Add("directive", $"{file}|{Canon(directive.GetStructure()!)}");
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            Add("type", $"{file}|{TypePath(type)}:{Canon(type.Modifiers)} {Canon(type.BaseList)}");
            if (type is TypeDeclarationSyntax { ParameterList: { } parameters })
                foreach (var parameter in parameters.Parameters)
                    Add("state", $"{file}|{TypePath(type)}({parameter.Identifier.Text}):{Canon(parameter)}");
        }
        foreach (var field in root.DescendantNodes().OfType<BaseFieldDeclarationSyntax>())
            foreach (var variable in field.Declaration.Variables)
            {
                Add("state", $"{file}|{TypePath(field)}.{variable.Identifier.Text}:{Canon(field.Modifiers)} {Canon(field.Declaration.Type)}");
                if (variable.Initializer is not null)
                    Add("write", $"{file}|{Member(field)}|{variable.Identifier.Text}");
            }
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            Add("state", $"{file}|{TypePath(property)}.{property.Identifier.Text}:{Canon(property.Modifiers)} {Canon(property.Type)} {Canon(property.AccessorList)}");
            if (property.Initializer is not null)
                Add("write", $"{file}|{Member(property)}|{property.Identifier.Text}");
        }

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(method => method.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var node in root.DescendantNodes())
        {
            foreach (var written in WrittenNames(node))
                Add("write", $"{file}|{Member(node)}|{written}");
            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    Add("call", $"{file}|{Member(node)}|{Receiver(invocation.Expression)}.{InvokedName(invocation.Expression)}");
                    if (InvokedName(invocation.Expression) is "AddHandler" or "RemoveHandler")
                        Add("handler", $"{file}|{Member(node)}|{Canon(invocation.ArgumentList)}");
                    break;
                case SimpleNameSyntax simple when methods.Contains(simple.Identifier.Text) && !IsInvoked(simple):
                    Add("ref", $"{file}|{Member(node)}|{simple.Identifier.Text}");
                    break;
                case AssignmentExpressionSyntax subscription
                    when subscription.Kind() is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression:
                    Add("subscribe", $"{file}|{Member(node)}|{LastName(subscription.Left)}{subscription.OperatorToken.Text}");
                    break;
            }
        }

        // Token hash of every member: any logic edit, including thresholds and conditions, needs review.
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                     .Where(member => member.Parent is BaseTypeDeclarationSyntax && member is not BaseTypeDeclarationSyntax))
            Add("body", $"{file}|{Member(member)}|{Hash(Canon(member))}");
    }

    private static string[] DirectiveSymbols(string source) =>
        CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantTrivia()
            .Select(trivia => trivia.GetStructure())
            .SelectMany(structure => structure switch
            {
                IfDirectiveTriviaSyntax directive => directive.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>(),
                ElifDirectiveTriviaSyntax directive => directive.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>(),
                _ => [],
            })
            .Select(identifier => identifier.Identifier.Text).ToArray();

    private static IEnumerable<string> WrittenNames(SyntaxNode node) => node switch
    {
        AssignmentExpressionSyntax assignment => Targets(assignment.Left),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression } prefix
            => Targets(prefix.Operand),
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression } postfix
            => Targets(postfix.Operand),
        ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or (int)SyntaxKind.OutKeyword } argument
            => Targets(argument.Expression),
        _ => [],
    };

    private static IEnumerable<string> Targets(ExpressionSyntax expression) => expression switch
    {
        TupleExpressionSyntax tuple => tuple.Arguments.SelectMany(argument => Targets(argument.Expression)),
        ParenthesizedExpressionSyntax parenthesized => Targets(parenthesized.Expression),
        _ when LastName(expression) is { } name => [name],
        _ => [],
    };

    private static string? LastName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
        ConditionalAccessExpressionSyntax conditional => LastName(conditional.WhenNotNull),
        _ => null,
    };

    private static string InvokedName(ExpressionSyntax expression) => LastName(expression) ?? expression.Kind().ToString();

    // The last name of the object a method is invoked on: "-" for an unqualified call.
    private static string Receiver(ExpressionSyntax invoked) => invoked switch
    {
        MemberAccessExpressionSyntax access => ReceiverName(access.Expression),
        MemberBindingExpressionSyntax binding when binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional
            => ReceiverName(conditional.Expression),
        _ => "-",
    };

    private static string ReceiverName(ExpressionSyntax expression) => expression switch
    {
        ThisExpressionSyntax => "this",
        InvocationExpressionSyntax invocation => InvokedName(invocation.Expression) + "()",
        _ => LastName(expression) ?? expression.Kind().ToString(),
    };

    private static bool IsInvoked(SimpleNameSyntax name) =>
        name.Parent is InvocationExpressionSyntax
        || (name.Parent is MemberAccessExpressionSyntax access && access.Name == name && access.Parent is InvocationExpressionSyntax)
        || (name.Parent is MemberBindingExpressionSyntax binding && binding.Parent is InvocationExpressionSyntax);

    private static string SubscriptionOperator(SimpleNameSyntax name) =>
        name.FirstAncestorOrSelf<AssignmentExpressionSyntax>() is { } assignment
        && assignment.Kind() is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression
        && assignment.Left.Span.Contains(name.Span)
            ? assignment.OperatorToken.Text
            : "";

    // The member that contains a node: Type.Member`arity/parameters, or Type for type-level code.
    private static string Member(SyntaxNode node)
    {
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Parent is BaseTypeDeclarationSyntax);
        if (member is null)
            return node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() is { } type ? TypePath(type) : "<file>";
        var name = member switch
        {
            MethodDeclarationSyntax method => $"{method.Identifier.Text}`{method.TypeParameterList?.Parameters.Count ?? 0}/{method.ParameterList.Parameters.Count}",
            ConstructorDeclarationSyntax constructor => $"{(constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor")}/{constructor.ParameterList.Parameters.Count}",
            DestructorDeclarationSyntax => "~",
            PropertyDeclarationSyntax property => property.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.Text,
            BaseFieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(variable => variable.Identifier.Text)),
            OperatorDeclarationSyntax op => $"op_{op.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax conversion => $"op_{Canon(conversion.Type)}",
            BaseTypeDeclarationSyntax nested => nested.Identifier.Text,
            _ => member.Kind().ToString(),
        };
        return $"{TypePath(member)}.{name}";
    }

    private static string TypePath(SyntaxNode node) => string.Join(".",
        node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(type => type.Identifier.Text));

    // Tokens without trivia, so comments, whitespace and line breaks do not matter.
    private static string Canon(SyntaxNode? node) =>
        node is null ? "" : string.Join(" ", node.DescendantTokens().Select(token => token.Text));

    private static string Canon(SyntaxTokenList tokens) => string.Join(" ", tokens.Select(token => token.Text));

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static string Line(string key) => key.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string FileOf(string difference) => difference.Split('\t')[1].Split('|')[0];

    private static List<string> Compare(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedSet = expected.Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        return expectedSet.Except(actualSet).Select(line => "- " + line)
            .Concat(actualSet.Except(expectedSet).Select(line => "+ " + line))
            .Order(StringComparer.Ordinal).ToList();
    }

    private static void Mutate(Dictionary<string, string> sources, string file, string original, string replacement, string control)
    {
        var source = sources[file];
        var index = source.IndexOf(original, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{control}: control anchor moved; update it.");
        sources[file] = source[..index] + replacement + source[(index + original.Length)..];
        AssertParses(file, sources[file]);
    }

    // Inserts a member before the closing brace of the file's first class.
    private static void AddMember(Dictionary<string, string> sources, string file, string member)
    {
        var source = sources[file];
        var type = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var at = type.CloseBraceToken.SpanStart;
        sources[file] = source[..at] + member + "\n" + source[at..];
        AssertParses(file, sources[file]);
    }

    private static void AssertParses(string file, string source)
    {
        var options = CSharpParseOptions.Default.WithPreprocessorSymbols(BuildSymbols);
        var errors = CSharpSyntaxTree.ParseText(source, options).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, $"{file}: mutated source does not parse: {string.Join("; ", errors)}");
    }
}
