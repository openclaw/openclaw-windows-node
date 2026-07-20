using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Helper-level coverage for the node-pairing cancellation fix. The durable end-to-end
/// contract (Cancelled outcome + no retry warning + cancellation journal) is asserted at the
/// pipeline level in <c>SetupStepsTests.SetupPipeline_CallerCancel_*</c>; these tests pin the
/// two catch-filter boundaries directly: a caller cancel must propagate as an
/// <see cref="OperationCanceledException"/>, while a genuine internal timeout must still map to
/// <see cref="NodeConnectionOutcome.Timeout"/>.
/// </summary>
public sealed class SetupNodeConnectCancellationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dataDir;

    public SetupNodeConnectCancellationTests(ITestOutputHelper output)
    {
        _output = output;
        _dataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "setup-cancel-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    // Bind by full signature (not just name) so a signature change fails loudly here rather than
    // silently matching the wrong overload — per reviewer feedback on brittle reflection.
    private static MethodInfo ResolveWaitForNodeConnection()
    {
        var candidates = typeof(SetupStep).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
            .Where(m => m.Name == "WaitForNodeConnection")
            .Where(m =>
            {
                var p = m.GetParameters();
                return p.Length == 4
                    && p[0].ParameterType == typeof(WindowsNodeClient)
                    && p[1].ParameterType == typeof(SetupContext)
                    && p[2].ParameterType == typeof(TimeSpan)
                    && p[3].ParameterType == typeof(CancellationToken);
            })
            .ToList();
        Assert.True(candidates.Count == 1,
            $"Expected exactly one private static WaitForNodeConnection(WindowsNodeClient, SetupContext, TimeSpan, " +
            $"CancellationToken); found {candidates.Count}. If the signature moved, update this test.");
        return candidates[0];
    }

    private SetupContext MakeContext()
    {
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            new SetupConfig(), logger, new TransactionJournal(filePath: null),
            new CommandRunner(logger), CancellationToken.None,
            dataDir: _dataDir, localDataDir: _dataDir);
    }

    [Fact]
    public async Task WaitForNodeConnection_CallerCancellation_PropagatesInsteadOfTimeout()
    {
        using var server = new SilentWebSocketServer();
        var ctx = MakeContext();
        using var client = new WindowsNodeClient($"ws://127.0.0.1:{server.Port}", "test-token-placeholder", _dataDir);

        using var callerCts = new CancellationTokenSource();
        // 15s internal timeout window; the caller cancels only AFTER the WS upgrade barrier, so
        // the method is provably in its wait — not a 300ms guess about handshake timing.
        var task = (Task)ResolveWaitForNodeConnection().Invoke(null,
            new object[] { client, ctx, TimeSpan.FromSeconds(15), callerCts.Token })!;
        await server.UpgradeCompleted;
        callerCts.Cancel();

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30));
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var outcome = result.GetType().GetProperty("Outcome")!.GetValue(result)!.ToString();
            Assert.Fail($"Caller cancellation was misclassified: returned Outcome={outcome} " +
                        "(the 15s internal timeout never elapsed). Expected OperationCanceledException.");
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine("caller cancellation propagated as OperationCanceledException");
        }
    }

    [Fact]
    public async Task WaitForNodeConnection_GenuineInternalTimeout_StillMapsToTimeout()
    {
        using var server = new SilentWebSocketServer();
        var ctx = MakeContext();
        using var client = new WindowsNodeClient($"ws://127.0.0.1:{server.Port}", "test-token-placeholder", _dataDir);

        // No caller cancellation — the internal CancelAfter(timeout) is the only thing that fires.
        // This must still be classified as Timeout (the fix only diverts caller-driven cancels).
        var task = (Task)ResolveWaitForNodeConnection().Invoke(null,
            new object[] { client, ctx, TimeSpan.FromMilliseconds(200), CancellationToken.None })!;

        await task.WaitAsync(TimeSpan.FromSeconds(30));
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var outcome = result.GetType().GetProperty("Outcome")!.GetValue(result)!.ToString();
        _output.WriteLine($"genuine internal timeout → Outcome={outcome}");
        Assert.Equal("Timeout", outcome);
    }
}
