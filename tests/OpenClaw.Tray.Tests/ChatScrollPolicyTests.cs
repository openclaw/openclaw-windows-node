using System.Text.RegularExpressions;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatScrollPolicyTests
{
    private const ChatScrollAction None = ChatScrollAction.None;
    private const ChatScrollAction Cancel = ChatScrollAction.Cancel;
    private const ChatScrollAction Land = ChatScrollAction.LandOnTail;
    private const ChatScrollAction ToTail = ChatScrollAction.ScrollToTail;
    private const ChatScrollAction Pin = ChatScrollAction.PinToBottom;

    private static ChatScrollGeometry At(double offset, double scrollableHeight = 1000) => new(offset, scrollableHeight);

    private static ChatScrollPolicy Following(double offset = 1000)
    {
        var policy = new ChatScrollPolicy();
        policy.Request(ChatScrollRequest.Open, hasRows: true);
        policy.LandingCompleted(At(1000));
        policy.ViewChanged(At(offset));
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        return policy;
    }

    private static ChatScrollPolicy Reading(double offset = 0)
    {
        var policy = Following();
        policy.UserInput(At(1000));
        policy.ViewChanged(At(offset));
        policy.Settled(At(offset));
        Assert.Equal(ChatScrollMode.Reading, policy.Mode);
        return policy;
    }

    [Theory]
    [InlineData(ChatScrollRequest.Open, false)]
    [InlineData(ChatScrollRequest.JumpToLatest, true)]
    public void OpenSendAndJump_LandOnTheTailOnce(object request, bool afterRender)
    {
        var policy = Reading();

        var command = policy.Request((ChatScrollRequest)request, hasRows: true);

        Assert.Equal(new ChatScrollCommand(Land, ChatScrollAnchor.Bottom), command);
        Assert.Equal(ChatScrollMode.Landing, policy.Mode);
        Assert.True(policy.IsLanding);
        Assert.Equal(afterRender, policy.LandAfterRender);
        Assert.True(policy.Permits(Land));
        Assert.False(policy.Permits(ToTail));
    }

    [Fact]
    public void Landing_WaitsForRowsAndRearmsWhenClearedRowsReturn()
    {
        var policy = new ChatScrollPolicy();
        Assert.Equal(Cancel, policy.Request(ChatScrollRequest.JumpToLatest, hasRows: false).Action);
        Assert.False(policy.IsLanding);
        Assert.False(policy.LandAfterRender);
        Assert.Equal(Land, policy.RowsChanged(hasRows: true, tailChanged: true).Action);

        Assert.Equal(Cancel, policy.RowsChanged(hasRows: false, tailChanged: true).Action);
        Assert.False(policy.IsLanding);
        Assert.Equal(None, policy.RowsChanged(hasRows: false, tailChanged: false).Action);
        Assert.Equal(Land, policy.RowsChanged(hasRows: true, tailChanged: true).Action);
        Assert.True(policy.IsLanding);
    }

    [Fact]
    public void Landing_TailChangesDoNotInterruptItAndViewChangesCannotEndIt()
    {
        var policy = new ChatScrollPolicy();
        policy.Request(ChatScrollRequest.Open, hasRows: true);

        Assert.Equal(None, policy.RowsChanged(hasRows: true, tailChanged: true).Action);
        Assert.Equal(None, policy.ViewChanged(At(0)).Action);
        Assert.Equal(None, policy.ViewChanged(At(900)).Action);
        Assert.Equal(None, policy.ContentChanged(At(0, 4000)).Action);
        Assert.Equal(None, policy.ViewportResized().Action);
        Assert.Equal(None, policy.ReaderNavigationStarting(500).Action);
        Assert.Equal(ChatScrollMode.Landing, policy.Mode);
    }

    [Theory]
    [InlineData(1000, None)]
    [InlineData(700, Pin)]
    public void LandingCompletion_AlwaysFollowsAndPinsAnyGapLeftByAGiveUp(double offset, object expected)
    {
        var policy = new ChatScrollPolicy();
        policy.Request(ChatScrollRequest.Open, hasRows: true);

        Assert.Equal(expected, policy.LandingCompleted(At(offset)).Action);
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.False(policy.LandAfterRender);
    }

    [Theory]
    [InlineData(ChatScrollMode.Following, ToTail)]
    [InlineData(ChatScrollMode.Reading, None)]
    public void TailAppended_ScrollsToTheNewRowOnlyWhileFollowing(object mode, object expected)
    {
        var policy = (ChatScrollMode)mode == ChatScrollMode.Following ? Following() : Reading();

        Assert.Equal(expected, policy.RowsChanged(hasRows: true, tailChanged: true).Action);
        Assert.Equal(None, policy.RowsChanged(hasRows: true, tailChanged: false).Action);
    }

    [Fact]
    public void Streaming_WhileNativeAnchoringHoldsTheBottom_IssuesNoNavigation()
    {
        var policy = Following();

        for (var extent = 1000; extent < 3000; extent += 250)
        {
            Assert.Equal(None, policy.ContentChanged(At(extent, extent)).Action);
            Assert.Equal(None, policy.ViewChanged(At(extent, extent)).Action);
        }
    }

    // Release undershoot: a fractional offset leaves the native edge anchor, so streamed
    // growth extends below the viewport while the offset stays put.
    [Fact]
    public void ReleaseStreamingUndershoot_IsPinnedBackToTheBottom()
    {
        var policy = Following();
        policy.ViewChanged(At(999.5));

        Assert.Equal(Pin, policy.ContentChanged(At(999.5, 1250)).Action);
        Assert.True(policy.BeginPin(At(999.5, 1250)));
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.Equal(None, policy.ViewChanged(At(1250, 1250)).Action);
        // A large streamed delta never leaves Following: growth does not move the offset up.
        Assert.Equal(Pin, policy.ContentChanged(At(1250, 1900)).Action);
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.False(policy.ShowsJumpToLatest);
    }

    // Debug overshoot: after a resize/reflow and jump, the offset can sit past the shrunken extent.
    [Fact]
    public void ReflowOvershoot_IsPinnedBackToTheBottom()
    {
        var policy = Following();

        Assert.Equal(Pin, policy.ViewportResized().Action);
        Assert.Equal(Pin, policy.ContentChanged(At(1000, 940)).Action);
        Assert.True(policy.BeginPin(At(1000, 940)));
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.Equal(Pin, policy.ViewChanged(At(1000, 940)).Action);
        Assert.False(policy.ShowsJumpToLatest);
    }

    [Theory]
    [InlineData(ChatScrollMode.Following, Pin)]
    [InlineData(ChatScrollMode.Reading, None)]
    public void ViewportResize_PinsOnlyWhileFollowing(object mode, object expected)
    {
        var policy = (ChatScrollMode)mode == ChatScrollMode.Following ? Following() : Reading();

        Assert.Equal(expected, policy.ViewportResized().Action);
    }

    [Fact]
    public void UserScrollUp_LeavesFollowingAsSoonAsItLeavesTheBottom()
    {
        var policy = Following();

        Assert.Equal(Cancel, policy.UserInput(At(1000)).Action);
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.Equal(None, policy.ViewChanged(At(998)).Action);

        Assert.Equal(ChatScrollMode.Reading, policy.Mode);
        Assert.True(policy.ShowsJumpToLatest);
        Assert.Equal(None, policy.ContentChanged(At(998, 1600)).Action);
        Assert.Equal(None, policy.RowsChanged(hasRows: true, tailChanged: true).Action);
        Assert.Equal(None, policy.Settled(At(998, 1600)).Action);
        Assert.False(policy.Permits(ToTail));
        Assert.False(policy.BeginPin(At(998, 1600)));
    }

    [Fact]
    public void InputWithoutMovement_KeepsFollowing()
    {
        var policy = Following();

        Assert.Equal(Cancel, policy.UserInput(At(1000)).Action);
        policy.Settled(At(1000));

        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.Equal(ToTail, policy.RowsChanged(hasRows: true, tailChanged: true).Action);
    }

    // Debug overshoot trace: a virtualized extent re-estimate left the offset past the bottom,
    // then the pin's own layout moved the offset far up without user input.
    [Theory]
    [InlineData(960)]
    [InlineData(100)]
    public void MovementWithoutUserInput_IsDriftAndNeverLeavesFollowing(double offset)
    {
        var policy = Following();

        Assert.Equal(Pin, policy.ViewChanged(At(offset)).Action);
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.False(policy.ShowsJumpToLatest);
    }

    [Fact]
    public void AdmittedPin_ClearsStaleUserIntent()
    {
        var policy = Following();
        policy.UserInput(At(1000));
        Assert.True(policy.BeginPin(At(1000, 1200)));

        Assert.Equal(Pin, policy.ViewChanged(At(300, 1200)).Action);
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
    }

    [Fact]
    public void ReachingTheBottom_ReentersFollowing()
    {
        var scrolled = Reading(500);
        scrolled.UserInput(At(500));
        Assert.Equal(Pin, scrolled.ViewChanged(At(950)).Action);
        Assert.Equal(ChatScrollMode.Following, scrolled.Mode);
        Assert.False(scrolled.ShowsJumpToLatest);

        var collapsed = Reading(500);
        Assert.Equal(None, collapsed.ContentChanged(At(500, 500)).Action);
        Assert.Equal(ChatScrollMode.Following, collapsed.Mode);
    }

    [Fact]
    public void DisclosureToggle_KeepsItsHeaderInPlaceUntilTheReaderMoves()
    {
        var policy = Following();

        var command = policy.DisclosureToggled(At(1000));

        Assert.Equal(new ChatScrollCommand(Cancel, ChatScrollAnchor.Top), command);
        Assert.Equal(ChatScrollMode.Reading, policy.Mode);
        // Details grow downward; nothing pulls the header up.
        Assert.Equal(None, policy.ContentChanged(At(1000, 1400)).Action);
        Assert.Equal(None, policy.ViewportResized().Action);
        Assert.Equal(None, policy.RowsChanged(hasRows: true, tailChanged: true).Action);
        Assert.Equal(None, policy.ViewChanged(At(1360, 1400)).Action);
        Assert.Equal(ChatScrollAnchor.Top, policy.Anchor);

        Assert.Equal(ChatScrollAnchor.Bottom, policy.UserInput(At(1360, 1400)).Anchor);
    }

    [Fact]
    public void KeyboardReaderNavigation_LeavesTheTailBeforeTheNativeMoveAndReturnsNearIt()
    {
        var policy = Following();
        policy.UserInput(At(1000));

        Assert.Equal(new ChatScrollCommand(None, ChatScrollAnchor.Top), policy.ReaderNavigationStarting(600));
        Assert.True(policy.ReaderNavigationPending);
        Assert.Equal(ChatScrollMode.Reading, policy.Mode);
        Assert.Equal(None, policy.ViewChanged(At(990)).Action);
        Assert.Equal(ChatScrollMode.Reading, policy.Mode);

        var command = policy.ReaderNavigationCompleted(At(960));
        Assert.Equal(new ChatScrollCommand(Pin, ChatScrollAnchor.Bottom), command);
        Assert.Equal(ChatScrollMode.Following, policy.Mode);
        Assert.False(policy.ReaderNavigationPending);
    }

    [Fact]
    public void KeyboardReaderNavigation_FarAwayStaysReadingAndNearbyDoesNotLeave()
    {
        var far = Following();
        far.ReaderNavigationStarting(600);
        Assert.Equal(None, far.ReaderNavigationCompleted(At(400)).Action);
        Assert.Equal(ChatScrollMode.Reading, far.Mode);
        Assert.Equal(ChatScrollAnchor.Top, far.Anchor);

        var near = Following();
        near.ReaderNavigationStarting(ChatScrollPolicy.FollowThreshold);
        Assert.False(near.ReaderNavigationPending);
        Assert.Equal(ChatScrollMode.Following, near.Mode);
    }

    [Theory]
    [InlineData(980, ChatScrollMode.Following)]
    [InlineData(100, ChatScrollMode.Reading)]
    public void InputDuringLanding_CancelsItAndKeepsTheReaderWhereTheyAre(double offset, object expected)
    {
        var policy = new ChatScrollPolicy();
        policy.Request(ChatScrollRequest.JumpToLatest, hasRows: true);

        Assert.Equal(Cancel, policy.UserInput(At(offset)).Action);

        Assert.Equal(expected, policy.Mode);
        Assert.False(policy.IsLanding);
        Assert.False(policy.LandAfterRender);
        Assert.False(policy.Permits(Land));
        Assert.Equal(None, policy.LandingCompleted(At(offset)).Action);
    }

    [Fact]
    public void Loaded_RelandsAndKeepsAPendingJumpsRenderWait()
    {
        var policy = new ChatScrollPolicy();
        policy.Request(ChatScrollRequest.JumpToLatest, hasRows: true);
        Assert.Equal(None, policy.Loaded(hasRows: false).Action);

        Assert.Equal(Land, policy.Loaded(hasRows: true).Action);
        Assert.True(policy.LandAfterRender);

        var reader = Reading();
        Assert.Equal(new ChatScrollCommand(Land, ChatScrollAnchor.Bottom), reader.Loaded(hasRows: true));
        Assert.False(reader.LandAfterRender);
    }

    [Fact]
    public void PinBudget_StopsChasingAnExtentThatNeverReachesTheBottom()
    {
        var policy = Following();
        for (var pin = 0; pin < ChatScrollPolicy.MaxPinsWithoutReachingBottom; pin++)
            Assert.True(policy.BeginPin(At(900 + pin, 1000 + pin)));
        Assert.False(policy.BeginPin(At(910, 1010)));

        policy.ContentChanged(At(1010, 1010));
        Assert.True(policy.BeginPin(At(1010, 1050)));
    }

    // Folded from #2117's extent-growth re-pin: a correction can remeasure virtual rows, so only a
    // new Entries revision (or a resize), never another native extent event or re-render, rearms it.
    [Fact]
    public void PinBudget_RearmsOnlyForANewContentRevisionOrResize()
    {
        var policy = Following();
        for (var pin = 0; pin < ChatScrollPolicy.MaxPinsWithoutReachingBottom; pin++)
            policy.BeginPin(At(900, 1000));
        Assert.False(policy.BeginPin(At(900, 1000)));

        Assert.Equal(Pin, policy.ContentChanged(At(900, 1100)).Action);
        policy.RowsChanged(hasRows: true, tailChanged: false);
        Assert.False(policy.BeginPin(At(900, 1100)));

        policy.ContentRevisionChanged();
        Assert.True(policy.BeginPin(At(900, 1100)));

        for (var pin = 1; pin < ChatScrollPolicy.MaxPinsWithoutReachingBottom; pin++)
            policy.BeginPin(At(900, 1000));
        Assert.False(policy.BeginPin(At(900, 1000)));
        policy.ViewportResized();
        Assert.True(policy.BeginPin(At(900, 1000)));
    }

    // #2117's first-delta failure: same-key streaming grew the extent 46.29 px below an anchored tail.
    [Theory]
    [InlineData(ChatScrollMode.Following, true)]
    [InlineData(ChatScrollMode.Reading, false)]
    public void SameKeyStreamingGrowth_PinsWhileFollowingButNeverMovesAReaderOrDisclosure(object mode, bool pins)
    {
        var policy = (ChatScrollMode)mode == ChatScrollMode.Following ? Following(2673.71) : Reading(500);
        var growth = At((ChatScrollMode)mode == ChatScrollMode.Following ? 2673.71 : 500, 2720);
        policy.ContentRevisionChanged();
        Assert.Equal(None, policy.RowsChanged(hasRows: true, tailChanged: false).Action);

        Assert.Equal(pins ? Pin : None, policy.ContentChanged(growth).Action);
        Assert.Equal(pins, policy.BeginPin(growth));

        var disclosure = Following();
        disclosure.DisclosureToggled(At(1000));
        disclosure.ContentRevisionChanged();
        Assert.Equal(None, disclosure.ContentChanged(At(1000, 1400)).Action);
        Assert.False(disclosure.BeginPin(At(1000, 1400)));

        var landing = new ChatScrollPolicy();
        landing.Request(ChatScrollRequest.Open, hasRows: true);
        landing.ContentRevisionChanged();
        Assert.Equal(None, landing.ContentChanged(At(0, 2720)).Action);
        Assert.False(landing.BeginPin(At(0, 2720)));
    }

    // A reload that is already at the bottom raises no ViewChanged before the first wheel.
    [Fact]
    public void LandingAlreadyAtTheBottom_FirstUserScrollUpLeavesFollowing()
    {
        var policy = new ChatScrollPolicy();
        policy.Request(ChatScrollRequest.Open, hasRows: true);
        policy.LandingCompleted(At(1000));
        policy.Loaded(hasRows: true);
        policy.LandingCompleted(At(1000));

        policy.UserInput(At(1000));
        Assert.Equal(None, policy.ViewChanged(At(952)).Action);

        Assert.Equal(ChatScrollMode.Reading, policy.Mode);
        Assert.Equal(None, policy.Settled(At(952)).Action);
    }

    // From the #2048 review: an absolute at-bottom predicate made Jump to latest flicker
    // during overshoot. The control follows the mode instead.
    [Fact]
    public void JumpToLatest_IsShownOnlyWhileReading_NotFromTheRawOffset()
    {
        var policy = new ChatScrollPolicy();
        policy.Request(ChatScrollRequest.Open, hasRows: true);
        policy.ViewChanged(At(0));
        Assert.False(policy.ShowsJumpToLatest);

        policy.LandingCompleted(At(400));
        Assert.False(policy.ShowsJumpToLatest);
        policy.ViewChanged(At(1000));
        policy.ContentChanged(At(1000, 940));
        policy.ContentChanged(At(700, 1000));
        Assert.False(policy.ShowsJumpToLatest);

        policy.UserInput(At(1000));
        policy.ViewChanged(At(200));
        Assert.True(policy.ShowsJumpToLatest);

        policy.Request(ChatScrollRequest.JumpToLatest, hasRows: true);
        Assert.False(policy.ShowsJumpToLatest);
    }

    [Fact]
    public void ConversationScrolling_HasOneNativeOwner()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var chat = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Chat");
        var scrolling = new Regex(
            @"\.(ScrollTo|ScrollBy|ChangeView|StartBringIntoView|StartBringItemIntoView|TryStartScroll|AddScrollVelocity|ScrollIntoView)\(|VerticalAnchorRatio|AnchorRequested",
            RegexOptions.CultureInvariant);
        // Separate scrollers inside popups. Each is pinned to its exact call count.
        var allowed = new Dictionary<string, (string Line, int Count)>
        {
            ["ReactorChatComposer.cs"] = ("element.StartBringIntoView(", 2), // slash-command menu list
            ["NativeChatModelPicker.cs"] = ("_list.ScrollIntoView(", 1),     // model picker list
            ["NativeChatPlacementPicker.cs"] = ("_list.ScrollIntoView(", 1), // placement picker list
        };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(chat, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name == "ReactorItemsViewScrollController.cs")
                continue;
            var lines = File.ReadAllLines(file).Where(line => scrolling.IsMatch(line)).ToArray();
            if (allowed.TryGetValue(name, out var allowance)
                && lines.Length == allowance.Count
                && lines.All(line => line.Contains(allowance.Line, StringComparison.Ordinal)))
                continue;
            offenders.AddRange(lines.Select(line => $"{name}: {line.Trim()}"));
        }

        Assert.True(offenders.Count == 0, "Only ChatScrollPolicy's adapter may scroll the conversation:\n" + string.Join("\n", offenders));

        var owner = File.ReadAllText(Path.Combine(chat, "ReactorItemsViewScrollController.cs"));
        Assert.DoesNotContain("_following", owner);
        Assert.DoesNotContain("_initialPending", owner);
        Assert.DoesNotContain("IsNearBottom", owner);
        Assert.Contains("private void Apply(ChatScrollCommand command)", owner);
    }
}
