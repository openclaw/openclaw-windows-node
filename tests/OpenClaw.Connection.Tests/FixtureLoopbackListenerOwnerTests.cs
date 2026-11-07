using System.Net;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class FixtureLoopbackListenerOwnerTests
{
    [Fact]
    public void OrdinaryProcess_DoesNotOwnTheListener()
    {
        Assert.NotEqual("1", Environment.GetEnvironmentVariable(GatewayFixtureIsolation.ModeEnvironmentVariable));
        Assert.False(FixtureLoopbackListenerOwner.IsOwnedByCurrentProcessParent(1));
    }

    [Fact]
    public void LiveParent_MatchesOnlyThatListener()
    {
        var parentStart = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var childStart = parentStart.AddSeconds(1);
        var listener = new WindowsTcpListenerInfo(
            IPAddress.Loopback, 51697, 40, "dotnet", null, parentStart);

        Assert.True(FixtureLoopbackListenerOwner.IsLiveParentListener(
            parentHasExited: false, parentStart, childStart, parentId: 40, listener));
    }

    [Fact]
    public void UnrelatedListener_IsRejected()
    {
        var parentStart = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var listener = new WindowsTcpListenerInfo(
            IPAddress.Loopback, 51697, 99, "other", null, parentStart);

        Assert.False(FixtureLoopbackListenerOwner.IsLiveParentListener(
            parentHasExited: false, parentStart, parentStart.AddSeconds(1), parentId: 40, listener));
    }

    [Fact]
    public void ReusedParentId_IsRejectedWhenTheReplacementStartsLater()
    {
        var childStart = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var reusedStart = childStart.AddMinutes(1);
        var listener = new WindowsTcpListenerInfo(
            IPAddress.Loopback, 51697, 40, "dotnet", null, reusedStart);

        Assert.False(FixtureLoopbackListenerOwner.IsLiveParentListener(
            parentHasExited: false, reusedStart, childStart, parentId: 40, listener));
    }

    [Fact]
    public void ReassignedParent_IsRejectedWhenTheListenerStartDiffers()
    {
        var parentStart = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var listener = new WindowsTcpListenerInfo(
            IPAddress.Loopback, 51697, 40, "dotnet", null, parentStart.AddSeconds(5));

        Assert.False(FixtureLoopbackListenerOwner.IsLiveParentListener(
            parentHasExited: false, parentStart, parentStart.AddSeconds(1), parentId: 40, listener));
    }

    [Fact]
    public void ExitedParent_IsRejected()
    {
        var parentStart = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var listener = new WindowsTcpListenerInfo(
            IPAddress.Loopback, 51697, 40, "dotnet", null, parentStart);

        Assert.False(FixtureLoopbackListenerOwner.IsLiveParentListener(
            parentHasExited: true, parentStart, parentStart.AddSeconds(1), parentId: 40, listener));
    }
}
