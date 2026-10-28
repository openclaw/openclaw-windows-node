using System.Runtime.InteropServices;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class NativeTestFailureTraceTests
{
    private const string PrivateMarker = "private-value-must-not-be-exported";

    [Fact]
    public void Describe_ReportsTypeAndCodeFramesWithoutMessagesDataOrFilePaths()
    {
        Exception error;
        try { ThrowPrivateFailure(); throw new InvalidOperationException("Expected fixture failure."); }
        catch (IOException failure) { error = failure; }
        var lines = NativeTestFailureTrace.Describe(error);
        Assert.Contains(lines, line => line.Contains("type=System.IO.IOException", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(
            "native-ui-frame OpenClaw.Shared.Tests.NativeTestFailureTraceTests.", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(PrivateMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(".cs:", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_RetainsInnerHresultWithoutReadingItsMessage()
    {
        var inner = new COMException(PrivateMarker, unchecked((int)0x80000013));
        var error = new InvalidOperationException(PrivateMarker, inner);
        var lines = NativeTestFailureTrace.Describe(error);
        Assert.Contains(lines, line => line.Contains("hresult=0x80000013", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(PrivateMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_BoundsNestedExceptionOutput()
    {
        Exception error = new Exception(PrivateMarker);
        for (var index = 0; index < 8; index++)
            error = new InvalidOperationException(PrivateMarker, error);
        Assert.Equal(4, NativeTestFailureTrace.Describe(error).Count);
    }

    private static void ThrowPrivateFailure()
    {
        var error = new IOException(PrivateMarker);
        error.Data["credential"] = PrivateMarker;
        throw error;
    }
}
