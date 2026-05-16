using System;
using Xunit;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class NonFatalActionTests
{
    [Fact]
    public void Run_ThrowingAction_DoesNotPropagate()
    {
        var warned = false;
        NonFatalAction.Run(() => throw new InvalidOperationException("boom"), _ => warned = true);
        Assert.True(warned);
    }

    [Fact]
    public void Run_ThrowingAction_PassesExceptionMessageToOnError()
    {
        string? received = null;
        NonFatalAction.Run(() => throw new InvalidOperationException("boom"), msg => received = msg);
        Assert.Equal("boom", received);
    }

    [Fact]
    public void Run_SuccessfulAction_DoesNotCallOnError()
    {
        var errorCalled = false;
        NonFatalAction.Run(() => { }, _ => errorCalled = true);
        Assert.False(errorCalled);
    }
}
