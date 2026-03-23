using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services.Voice;

namespace OpenClaw.Tray.Tests;

public class VoiceServiceTransportTests
{
    [Fact]
    public void GetOrCreateTransportReadySource_ReusesExistingTaskWhileConnecting()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Connecting, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.Same(existing, result);
        Assert.False((bool)arguments[2]!);
    }

    [Fact]
    public void GetOrCreateTransportReadySource_CreatesFreshTaskWhenDisconnected()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Disconnected, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.NotSame(existing, result);
        Assert.True((bool)arguments[2]!);
    }

    [Fact]
    public void GetOrCreateTransportReadySource_CreatesFreshTaskAfterError()
    {
        var method = GetMethod();
        var existing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arguments = new object?[] { ConnectionStatus.Error, existing, null };

        var result = (TaskCompletionSource<bool>)method.Invoke(null, arguments)!;

        Assert.NotSame(existing, result);
        Assert.True((bool)arguments[2]!);
    }

    private static MethodInfo GetMethod()
    {
        return typeof(VoiceService).GetMethod(
            "GetOrCreateTransportReadySource",
            BindingFlags.NonPublic | BindingFlags.Static)!;
    }
}
