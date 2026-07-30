using System;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class DesktopCanPresentEvaluatorTests
{
    [Fact]
    public void PresentsWhenDesktopInteractive()
        => Assert.True(new DesktopCanPresentEvaluator(() => true).CanPresent("session-1"));

    [Fact]
    public void DoesNotPresentWhenDesktopNotInteractive()
        => Assert.False(new DesktopCanPresentEvaluator(() => false).CanPresent("session-1"));

    [Fact]
    public void FailsClosedWhenProbeThrows()
        => Assert.False(new DesktopCanPresentEvaluator(() => throw new InvalidOperationException())
            .CanPresent("session-1"));
}
