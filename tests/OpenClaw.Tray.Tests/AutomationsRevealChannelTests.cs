using OpenClaw.Shared.Cron;
using OpenClawTray.Automations;

namespace OpenClaw.Tray.Tests;

public sealed class AutomationsRevealChannelTests
{
    private static Func<string, Task<AutomationRevealOutcome>> Serving(List<string> seen, AutomationRevealOutcome outcome) =>
        id =>
        {
            seen.Add(id);
            return Task.FromResult(outcome);
        };

    [Fact]
    public async Task AnAttachedRootServesRequestsDirectly()
    {
        using var channel = new AutomationsRevealChannel();
        var seen = new List<string>();
        using var attached = channel.Attach(Serving(seen, AutomationRevealOutcome.Revealed));

        Assert.Equal(AutomationRevealOutcome.Revealed, await channel.RevealAsync("morning-brief"));
        Assert.Equal(AutomationRevealOutcome.Revealed, await channel.RevealAsync("weekly-digest"));
        Assert.Equal(["morning-brief", "weekly-digest"], seen);
    }

    [Fact]
    public async Task ARequestBeforeTheRootAttachesWaitsForItAndIsServedOnce()
    {
        using var channel = new AutomationsRevealChannel();
        var waiting = channel.RevealAsync("early");
        Assert.False(waiting.IsCompleted);

        var seen = new List<string>();
        var attached = channel.Attach(Serving(seen, AutomationRevealOutcome.NotFound));
        Assert.Equal(AutomationRevealOutcome.NotFound, await waiting);
        Assert.Equal(["early"], seen);

        // A remount does not replay a request that was already answered.
        attached.Dispose();
        using var remounted = channel.Attach(Serving(seen, AutomationRevealOutcome.Revealed));
        Assert.Equal(["early"], seen);
    }

    [Fact]
    public async Task ANewerWaitingRequestSupersedesTheOlderOne()
    {
        using var channel = new AutomationsRevealChannel();
        var older = channel.RevealAsync("older");
        var newer = channel.RevealAsync("newer");
        Assert.Equal(AutomationRevealOutcome.Superseded, await older);
        Assert.False(newer.IsCompleted);

        var seen = new List<string>();
        using var attached = channel.Attach(Serving(seen, AutomationRevealOutcome.Revealed));
        Assert.Equal(AutomationRevealOutcome.Revealed, await newer);
        Assert.Equal(["newer"], seen);
    }

    [Fact]
    public async Task DisposingTheChannelSupersedesAWaitingRequestAndRefusesLaterOnes()
    {
        var channel = new AutomationsRevealChannel();
        var waiting = channel.RevealAsync("a");
        channel.Dispose();
        Assert.Equal(AutomationRevealOutcome.Superseded, await waiting);

        var seen = new List<string>();
        using var attached = channel.Attach(Serving(seen, AutomationRevealOutcome.Revealed));
        Assert.Equal(AutomationRevealOutcome.Superseded, await channel.RevealAsync("b"));
        Assert.Empty(seen);
    }

    [Fact]
    public async Task ARegistrationDetachesOnlyItsOwnHandler()
    {
        using var channel = new AutomationsRevealChannel();
        var stale = channel.Attach(Serving([], AutomationRevealOutcome.NotFound));
        var seen = new List<string>();
        var current = channel.Attach(Serving(seen, AutomationRevealOutcome.Revealed));

        // The earlier root's cleanup runs after the newer root attached; it must not take the newer one away.
        stale.Dispose();
        Assert.Equal(AutomationRevealOutcome.Revealed, await channel.RevealAsync("a"));

        current.Dispose();
        var waiting = channel.RevealAsync("b");
        Assert.False(waiting.IsCompleted);
        Assert.Equal(["a"], seen);
    }

    [Fact]
    public async Task AHandlerFailureWhileServingAWaitingRequestFaultsThatRequest()
    {
        using var channel = new AutomationsRevealChannel();
        var waiting = channel.RevealAsync("a");
        using var attached = channel.Attach(_ => throw new InvalidOperationException("The inventory could not be read."));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Equal("The inventory could not be read.", failure.Message);
    }
}
