using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.TestSupport;
using static OpenClaw.Shared.Tests.Mxc.MxcCommandRunnerTests;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcNamespaceProofCaptureTests
{
    [Theory]
    [InlineData("", 42, "EOF")]
    [InlineData("NOT_READY\r\ntrailing-output\r\n", 17, "line")]
    public async Task StartupFailureRetainsFirstLineStderrExitAndOriginalAssertion(string output, int exit, string observation)
    {
        using var directory = new TempDirectory();
        var process = new FakeProcess(exit, output, "startup-error\r\nsecond-error\r\n");
        var capture = Capture(directory);
        var failure = await Record.ExceptionAsync(() => capture.RunAsync(async evidence =>
        {
            await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(process));
            Assert.Equal("READY", await evidence.ReadReadinessAsync());
        }));

        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
        using var result = Read(capture, "result");
        var record = result.RootElement;
        Assert.Equal("readiness", record.GetProperty("FailurePhase").GetString());
        Assert.Equal(failure.GetType().FullName, record.GetProperty("Failure").GetProperty("Type").GetString());
        Assert.Contains(nameof(StartupFailureRetainsFirstLineStderrExitAndOriginalAssertion),
            record.GetProperty("Failure").GetProperty("StackTrace").GetString());
        Assert.Equal(observation, record.GetProperty("Readiness").GetProperty("Observation").GetString());
        Assert.Equal(output.Length == 0 ? null : "NOT_READY", record.GetProperty("Readiness").GetProperty("Value").GetString());
        Assert.Equal(output, record.GetProperty("Stdout").GetProperty("Text").GetString());
        Assert.Equal("startup-error\r\nsecond-error\r\n", record.GetProperty("Stderr").GetProperty("Text").GetString());
        Assert.Equal(exit, record.GetProperty("Finalization").GetProperty("Result").GetProperty("ExitCode").GetInt32());
        Assert.Equal("succeeded", record.GetProperty("Finalization").GetProperty("Status").GetString());
        Assert.Equal("disposed", record.GetProperty("Disposal").GetString());
        Assert.True(process.Disposed);
        Assert.Equal(0, process.ExitProbesAfterWait);
        Assert.True(File.Exists(capture.Prefix + "-before-cleanup.json"));
    }

    [Fact]
    public async Task PassingCasesKeepFirstLineAndUseDistinctArtifacts()
    {
        using var directory = new TempDirectory();
        var first = Capture(directory);
        var second = Capture(directory);
        foreach (var capture in new[] { first, second })
        {
            await capture.RunAsync(async evidence =>
            {
                evidence.Probe = new ProbeOutput
                {
                    Tier = IsolationTier.BaseContainer, NeedsDaclAugmentation = false,
                    Probes = new() { BaseContainerSupportsDenyPaths = true, UiCapabilities = new() },
                };
                await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(
                    new FakeProcess(0, "READY\nOWN_SCRATCH_OK\n", "synthetic-warning\n")));
                Assert.Equal("READY", await evidence.ReadReadinessAsync());
                Assert.Equal(0, (await evidence.FinishAsync()).ExitCode);
                Assert.Contains("OWN_SCRATCH_OK", evidence.Output);
            });
            using var result = Read(capture, "result");
            Assert.Equal("READY\nOWN_SCRATCH_OK\n", result.RootElement.GetProperty("Stdout").GetProperty("Text").GetString());
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("Failure").ValueKind);
            Assert.Empty(result.RootElement.GetProperty("CleanupFailures").EnumerateArray());
            Assert.Equal("synthetic-command", result.RootElement.GetProperty("Fixture").GetProperty("Command").GetString());
            Assert.Equal("synthetic-command", result.RootElement.GetProperty("Fixture").GetProperty("Request").GetProperty("command").GetString());
            Assert.True(result.RootElement.GetProperty("Probe").GetProperty("Probes").GetProperty("BaseContainerSupportsDenyPaths").GetBoolean());
        }
        Assert.NotEqual(first.Prefix, second.Prefix);
        Assert.Equal(8, Directory.GetFiles(directory.Path, "*.json").Length);
    }

    [Fact]
    public async Task BothPipesDrainToEofWithoutUnboundedRetention()
    {
        using var directory = new TempDirectory();
        var capture = Capture(directory);
        await capture.RunAsync(async evidence =>
        {
            await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(
                new FakeProcess(0, "READY\n" + new string('o', 70_000), new string('e', 70_000))));
            Assert.Equal("READY", await evidence.ReadReadinessAsync());
            await evidence.FinishAsync();
        });
        using var result = Read(capture, "result");
        foreach (var name in new[] { "Stdout", "Stderr" })
        {
            var pipe = result.RootElement.GetProperty(name);
            Assert.Equal(64 * 1024, pipe.GetProperty("Text").GetString()!.Length);
            Assert.True(pipe.GetProperty("Truncated").GetBoolean());
            Assert.Equal("EOF", pipe.GetProperty("State").GetString());
        }
    }

    [Fact]
    public async Task NonzeroExitRemainsAnAssertionFailureAndCaseFailureIsRecordedBeforeFixtureRemoval()
    {
        using var directory = new TempDirectory();
        var capture = Capture(directory);
        var failure = await Record.ExceptionAsync(() => capture.RunAsync(async evidence =>
        {
            await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(new FakeProcess(7, "READY\npayload\n")));
            Assert.Equal("READY", await evidence.ReadReadinessAsync());
            var result = await evidence.FinishAsync();
            evidence.Phase = "behavior assertions";
            Assert.Equal(0, result.ExitCode);
        }));
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
        using var result = Read(capture, "result");
        Assert.Equal(7, result.RootElement.GetProperty("Finalization").GetProperty("Result").GetProperty("ExitCode").GetInt32());
        Assert.Equal("behavior assertions", result.RootElement.GetProperty("FailurePhase").GetString());
        Assert.Empty(capture.RecordCase("before-fixture-cleanup", failure, []));
        using var caseResult = Read(capture, "before-fixture-cleanup");
        Assert.Equal("failed", caseResult.RootElement.GetProperty("CaseOutcome").GetProperty("Outcome").GetString());
    }

    [Fact]
    public async Task SpawnFailureRecordsUnreachedReadinessAndUnobservedExitWithoutReplacingException()
    {
        using var directory = new TempDirectory();
        var capture = Capture(directory);
        var spawnFailure = new IOException("synthetic spawn failure");
        var failure = await Record.ExceptionAsync(() => capture.RunAsync(evidence =>
            evidence.StartAsync(() => Task.FromException<IMxcProcess>(spawnFailure))));
        Assert.Same(spawnFailure, failure);
        using var result = Read(capture, "result");
        Assert.Equal("spawn", result.RootElement.GetProperty("FailurePhase").GetString());
        Assert.Equal("not reached", result.RootElement.GetProperty("Readiness").GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("ExitObservation").GetProperty("Code").ValueKind);
        Assert.Equal("not reached", result.RootElement.GetProperty("Finalization").GetProperty("Status").GetString());
        Assert.Equal("no process", result.RootElement.GetProperty("Disposal").GetString());
    }

    [Fact]
    public async Task ReadinessTimeoutClosesOwnedBlockedReaderAndRecordsPartialOutput()
    {
        using var directory = new TempDirectory();
        using var pipe = new InputPipe("partial", block: true);
        var process = new FakeProcess(9, stderr: "before-ready-error\n")
        {
            RootExited = false, StandardOutput = pipe, StandardOutputCloser = new Closer(pipe.Release),
        };
        var capture = Capture(directory, TimeSpan.FromMilliseconds(100));
        var failure = await Record.ExceptionAsync(() => capture.RunAsync(async evidence =>
        {
            await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(process));
            await pipe.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("READY", await evidence.ReadReadinessAsync());
        }));
        Assert.IsType<TimeoutException>(failure);
        using var result = Read(capture, "result");
        Assert.Equal("readiness", result.RootElement.GetProperty("FailurePhase").GetString());
        Assert.Equal("timed out", result.RootElement.GetProperty("Readiness").GetProperty("Status").GetString());
        Assert.Equal("partial", result.RootElement.GetProperty("Stdout").GetProperty("Text").GetString());
        Assert.Equal("before-ready-error\n", result.RootElement.GetProperty("Stderr").GetProperty("Text").GetString());
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task DrainFailureRetainsReadyLineAndPartialPayload()
    {
        using var directory = new TempDirectory();
        using var pipe = new InputPipe("READY\npartial-payload\n", fail: true);
        var capture = Capture(directory);
        var failure = await Record.ExceptionAsync(() => capture.RunAsync(async evidence =>
        {
            await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(new FakeProcess { StandardOutput = pipe }));
            Assert.Equal("READY", await evidence.ReadReadinessAsync());
            await evidence.FinishAsync();
        }));
        Assert.IsType<IOException>(failure);
        using var result = Read(capture, "result");
        Assert.Equal("drain", result.RootElement.GetProperty("FailurePhase").GetString());
        Assert.Equal("READY\npartial-payload\n", result.RootElement.GetProperty("Stdout").GetProperty("Text").GetString());
        Assert.Equal("read failed", result.RootElement.GetProperty("Stdout").GetProperty("State").GetString());
        Assert.Equal("synthetic pipe failure", result.RootElement.GetProperty("Failure").GetProperty("Message").GetString());
    }

    [Theory]
    [InlineData("close")]
    [InlineData("finalization")]
    [InlineData("kill")]
    public async Task CleanupOrFinalizationFailureIsSeparateFromOriginalReadinessAssertion(string step)
    {
        using var directory = new TempDirectory();
        var externalFailure = new IOException("synthetic " + step + " failure");
        FakeProcess process = null!;
        process = new FakeProcess(42, stderr: "startup-error")
        {
            RootExited = step != "kill",
            KillAction = step == "kill" ? () => { process.RootExited = true; throw externalFailure; } : null,
            StandardErrorCloser = step == "close" ? new Closer(() => throw externalFailure) : null,
            WaitCompletion = step == "finalization" ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null,
        };
        process.WaitCompletion?.SetException(externalFailure);
        var capture = Capture(directory);
        var failure = await Record.ExceptionAsync(() => capture.RunAsync(async evidence =>
        {
            await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(process));
            Assert.Equal("READY", await evidence.ReadReadinessAsync());
        }));
        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(aggregate.InnerExceptions[0]);
        Assert.Contains(externalFailure, aggregate.InnerExceptions);
        using var result = Read(capture, "result");
        Assert.Equal(aggregate.InnerExceptions[0].GetType().FullName,
            result.RootElement.GetProperty("Failure").GetProperty("Type").GetString());
        Assert.Equal(externalFailure.Message,
            Assert.Single(result.RootElement.GetProperty("CleanupFailures").EnumerateArray()).GetProperty("Message").GetString());
        Assert.Equal(step == "finalization" ? "failed" : "succeeded", result.RootElement.GetProperty("Finalization").GetProperty("Status").GetString());
        Assert.Equal(step == "finalization" ? JsonValueKind.Null : JsonValueKind.Object,
            result.RootElement.GetProperty("Finalization").GetProperty("Result").ValueKind);
    }

    [Fact]
    public async Task ReportWriteFailureDoesNotMaskPrimaryOrPreventProcessCleanup()
    {
        using var directory = new TempDirectory();
        var obstruction = directory.Combine("not-a-directory");
        File.WriteAllText(obstruction, "synthetic-obstruction");
        var capture = new MxcNamespaceProofCapture(obstruction, "fixture", 0, new { Command = "synthetic-command" });
        var process = new FakeProcess();
        var primary = new InvalidOperationException("synthetic primary");
        var failure = await Record.ExceptionAsync(() => capture.RunAsync(async evidence =>
        {
            await evidence.StartAsync(() => Task.FromResult<IMxcProcess>(process));
            throw primary;
        }));
        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.All(aggregate.InnerExceptions.Skip(1), ex => Assert.IsAssignableFrom<IOException>(ex));
        Assert.True(process.Disposed);
    }

    private static MxcNamespaceProofCapture Capture(TempDirectory directory, TimeSpan? readinessTimeout = null) =>
        new(directory.Path, "namespace-fixture", 0, new
        {
            RunId = "synthetic-run", Command = "synthetic-command",
            Request = new ContainerRequest("synthetic-command")
            {
                Containment = new Containment.ProcessContainer { LearningMode = false },
                Filesystem = new() { ReadonlyPaths = [@"C:\synthetic-code"], DeniedPaths = [@"C:\synthetic-denied"] },
            },
        }, readinessTimeout);

    private static JsonDocument Read(MxcNamespaceProofCapture capture, string stage) =>
        JsonDocument.Parse(File.ReadAllText(capture.Prefix + "-" + stage + ".json"));

    // External pipe input only. Capture, phase transitions, records and failure policy belong to the subject.
    private sealed class InputPipe(string text, bool block = false, bool fail = false) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        private readonly ManualResetEventSlim _release = new();
        private int _disposed;
        internal TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.Set();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position < Length) return base.Read(buffer, offset, count);
            Reading.TrySetResult();
            if (fail) throw new IOException("synthetic pipe failure");
            if (block) _release.Wait();
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            // Both the fixture owner and IMxcProcess can dispose this borrowed test input.
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _release.Set();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Closer(Action close) : IMxcStreamCloser
    {
        public void Close() => close();
        public void Dispose() { }
    }
}
