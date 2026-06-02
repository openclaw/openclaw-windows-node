using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for <see cref="AsyncEventHandlerGuard"/>.
///
/// The guard is a fault boundary: fire-and-forget async work must not
/// propagate exceptions to the caller's synchronisation context.  These
/// tests verify that all exit paths (success, cancellation, unexpected
/// exception) are handled correctly and that the optional callbacks fire.
/// </summary>
public class AsyncEventHandlerGuardTests
{
    // ─── Run: null guard ──────────────────────────────────────────────────────

    [Fact]
    public void Run_NullWork_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AsyncEventHandlerGuard.Run(null!));
    }

    // ─── Run: happy path ─────────────────────────────────────────────────────

    [Fact]
    public async Task Run_SuccessfulWork_CompletesWithoutError()
    {
        var completed = new TaskCompletionSource<bool>();

        AsyncEventHandlerGuard.Run(async () =>
        {
            await Task.Yield();
            completed.SetResult(true);
        });

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result);
    }

    // ─── Run: OperationCanceledException is silently swallowed ───────────────

    [Fact]
    public async Task Run_CancelledException_DoesNotInvokeOnError()
    {
        var onErrorInvoked = false;
        var finished = new TaskCompletionSource<bool>();

        AsyncEventHandlerGuard.Run(
            async () =>
            {
                await Task.Yield();
                finished.SetResult(true);
                throw new OperationCanceledException("test cancel");
            },
            onError: _ => { onErrorInvoked = true; });

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Brief yield to let RunCoreAsync complete its catch block.
        await Task.Delay(50);

        Assert.False(onErrorInvoked, "OperationCanceledException should be swallowed, not forwarded to onError");
    }

    [Fact]
    public async Task Run_CancelledException_LogsDebugMessage()
    {
        string? debugMessage = null;
        var finished = new TaskCompletionSource<bool>();
        var logger = new CapturingLogger(debug: m => debugMessage = m);

        AsyncEventHandlerGuard.Run(
            async () =>
            {
                await Task.Yield();
                finished.SetResult(true);
                throw new OperationCanceledException("user cancel");
            },
            logger: logger);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.NotNull(debugMessage);
        Assert.Contains("user cancel", debugMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Run: unexpected exception calls onError ──────────────────────────────

    [Fact]
    public async Task Run_UnexpectedException_InvokesOnError()
    {
        Exception? captured = null;
        var errorSignal = new TaskCompletionSource<bool>();

        AsyncEventHandlerGuard.Run(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            },
            onError: ex =>
            {
                captured = ex;
                errorSignal.TrySetResult(true);
            });

        await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<InvalidOperationException>(captured);
        Assert.Equal("boom", captured!.Message);
    }

    [Fact]
    public async Task Run_UnexpectedException_LogsErrorWithException()
    {
        string? loggedError = null;
        Exception? loggedException = null;
        var errorSignal = new TaskCompletionSource<bool>();
        var logger = new CapturingLogger(error: (m, ex) =>
        {
            loggedError = m;
            loggedException = ex;
            errorSignal.TrySetResult(true);
        });

        AsyncEventHandlerGuard.Run(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("something bad");
            },
            logger: logger);

        await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(loggedError);
        Assert.Contains("failed", loggedError, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(loggedException);
        Assert.Equal("something bad", loggedException!.Message);
    }

    // ─── Run: operationName shows in logged messages ──────────────────────────

    [Fact]
    public async Task Run_OperationName_AppearsInLogMessages()
    {
        string? loggedError = null;
        var errorSignal = new TaskCompletionSource<bool>();
        var logger = new CapturingLogger(error: (m, _) =>
        {
            loggedError = m;
            errorSignal.TrySetResult(true);
        });

        AsyncEventHandlerGuard.Run(
            async () =>
            {
                await Task.Yield();
                throw new Exception("oops");
            },
            logger: logger,
            operationName: "MySpecialOperation");

        await errorSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(loggedError);
        Assert.Contains("MySpecialOperation", loggedError, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Run: no logger / no onError — must not throw ─────────────────────────

    [Fact]
    public async Task Run_NoCallbacks_ExceptionIsSwallowedCleanly()
    {
        var finished = new TaskCompletionSource<bool>();

        AsyncEventHandlerGuard.Run(async () =>
        {
            await Task.Yield();
            finished.SetResult(true);
            throw new Exception("silent failure");
        });

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Give the async continuation time to reach the catch block.
        await Task.Delay(50);
        // If we got here the unobserved exception did not tear down the process.
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private sealed class CapturingLogger : IOpenClawLogger
    {
        private readonly Action<string>? _debug;
        private readonly Action<string, Exception?>? _error;

        public CapturingLogger(
            Action<string>? debug = null,
            Action<string, Exception?>? error = null)
        {
            _debug = debug;
            _error = error;
        }

        public void Debug(string message) => _debug?.Invoke(message);
        public void Error(string message, Exception? ex = null) => _error?.Invoke(message, ex);
        public void Info(string message) { }
        public void Warn(string message) { }
    }
}
