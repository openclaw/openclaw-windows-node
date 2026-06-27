using OpenClaw.Shared.Sessions;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class SessionActionPlannerTests
{
    [Theory]
    [InlineData(SessionActionKind.Reset, true)]
    [InlineData(SessionActionKind.Compact, true)]
    [InlineData(SessionActionKind.Delete, true)]
    [InlineData(SessionActionKind.Restore, true)]
    [InlineData(SessionActionKind.Export, false)]
    [InlineData(SessionActionKind.OpenChat, false)]
    [InlineData(SessionActionKind.Branch, false)]
    public void RequiresConfirmation_MatchesPolicy(SessionActionKind kind, bool expected)
    {
        Assert.Equal(expected, SessionActionPlanner.RequiresConfirmation(kind));
    }

    [Theory]
    [InlineData(SessionActionKind.Reset, true)]
    [InlineData(SessionActionKind.Delete, true)]
    [InlineData(SessionActionKind.Restore, true)]
    [InlineData(SessionActionKind.Compact, false)]
    [InlineData(SessionActionKind.Export, false)]
    [InlineData(SessionActionKind.OpenChat, false)]
    public void IsDestructive_MatchesPolicy(SessionActionKind kind, bool expected)
    {
        Assert.Equal(expected, SessionActionPlanner.IsDestructive(kind));
    }

    [Fact]
    public void IsAllowed_BlocksDeleteOfMainSession_WithReason()
    {
        var allowed = SessionActionPlanner.IsAllowed(SessionActionKind.Delete, isMainSession: true, out var reason);

        Assert.False(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("main session", reason!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAllowed_PermitsDeleteOfNonMainSession()
    {
        var allowed = SessionActionPlanner.IsAllowed(SessionActionKind.Delete, isMainSession: false, out var reason);

        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public void IsAllowed_BlocksRestoreOfMainSession_WithReason()
    {
        var allowed = SessionActionPlanner.IsAllowed(SessionActionKind.Restore, isMainSession: true, out var reason);

        Assert.False(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void IsAllowed_PermitsRestoreOfNonMain_AndBranchOfMain()
    {
        Assert.True(SessionActionPlanner.IsAllowed(SessionActionKind.Restore, isMainSession: false, out var r1));
        Assert.Null(r1);
        Assert.True(SessionActionPlanner.IsAllowed(SessionActionKind.Branch, isMainSession: true, out var r2));
        Assert.Null(r2);
    }

    [Theory]
    [InlineData(SessionActionKind.Delete)]
    [InlineData(SessionActionKind.Restore)]
    public void IsAllowed_BlocksDestructiveActions_WhenMainStateUnknown(SessionActionKind kind)
    {
        var allowed = SessionActionPlanner.IsAllowed(kind, SessionMainState.Unknown, out var reason);

        Assert.False(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("agent:main")]
    [InlineData("agent:main:main")]
    public void ResolveMainState_ProtectsCanonicalMainKeyShapes(string key)
    {
        Assert.Equal(SessionMainState.Main, SessionActionPlanner.ResolveMainState(key));
    }

    [Fact]
    public void ResolveMainState_UsesMainSessionKey_WhenAvailable()
    {
        Assert.Equal(
            SessionMainState.Main,
            SessionActionPlanner.ResolveMainState("agent:abc", mainSessionKey: "agent:abc"));
        Assert.Equal(
            SessionMainState.NotMain,
            SessionActionPlanner.ResolveMainState("agent:abc", mainSessionKey: "agent:def"));
    }

    [Fact]
    public void ResolveMainState_UsesSessionList_WhenMainKeyUnavailable()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "mainish", IsMain = true },
            new SessionInfo { Key = "other", IsMain = false },
        };

        Assert.Equal(SessionMainState.Main, SessionActionPlanner.ResolveMainState("mainish", sessions: sessions));
        Assert.Equal(SessionMainState.NotMain, SessionActionPlanner.ResolveMainState("other", sessions: sessions));
        Assert.Equal(SessionMainState.Unknown, SessionActionPlanner.ResolveMainState("missing", sessions: sessions));
    }

    [Theory]
    [InlineData(SessionActionKind.Reset)]
    [InlineData(SessionActionKind.Compact)]
    public void IsAllowed_PermitsResetAndCompactOfMain(SessionActionKind kind)
    {
        Assert.True(SessionActionPlanner.IsAllowed(kind, isMainSession: true, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void BuildPrompt_ReturnsNull_ForNonConfirmingActions()
    {
        Assert.Null(SessionActionPlanner.BuildPrompt(SessionActionKind.Export, "k", null, false));
        Assert.Null(SessionActionPlanner.BuildPrompt(SessionActionKind.OpenChat, "k", null, false));
        Assert.Null(SessionActionPlanner.BuildPrompt(SessionActionKind.Branch, "k", null, false));
    }

    [Fact]
    public void BuildPrompt_Delete_IsDestructive_AndUsesDisplayName()
    {
        var prompt = SessionActionPlanner.BuildPrompt(
            SessionActionKind.Delete, "agent:main:wa", "WhatsApp · Bob", isMainSession: false);

        Assert.NotNull(prompt);
        Assert.Equal(SessionActionKind.Delete, prompt!.Kind);
        Assert.Equal("WhatsApp \u00B7 Bob", prompt.SessionName);
        Assert.True(prompt!.IsDestructive);
        Assert.Equal("Delete", prompt.ConfirmLabel);
        Assert.Contains("WhatsApp \u00B7 Bob", prompt.Body);
        Assert.DoesNotContain("agent:main:wa", prompt.Body);
    }

    [Fact]
    public void BuildPrompt_Compact_IsNotDestructive_AndMentionsCheckpoint()
    {
        var prompt = SessionActionPlanner.BuildPrompt(
            SessionActionKind.Compact, "main", null, isMainSession: true);

        Assert.NotNull(prompt);
        Assert.False(prompt!.IsDestructive);
        Assert.Equal("Compact", prompt.ConfirmLabel);
        Assert.Contains("checkpoint", prompt.Body, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPrompt_FallsBackToKey_WhenNoDisplayName()
    {
        var prompt = SessionActionPlanner.BuildPrompt(
            SessionActionKind.Reset, "agent:main:main", null, isMainSession: true);

        Assert.NotNull(prompt);
        Assert.Contains("agent:main:main", prompt!.Body);
    }

    [Fact]
    public void Describe_PrefersDisplayName_FallsBackToKey()
    {
        Assert.Equal("Pretty", SessionActionPlanner.Describe("key", "Pretty"));
        Assert.Equal("key", SessionActionPlanner.Describe("key", null));
        Assert.Equal("key", SessionActionPlanner.Describe("key", "   "));
    }
}
