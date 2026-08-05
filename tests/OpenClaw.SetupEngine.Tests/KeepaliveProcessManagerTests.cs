using System.Text.Json;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// Characterization + new tests for <see cref="KeepaliveProcessManager"/>, the setup-time
/// keepalive owner extracted out of <see cref="StartKeepaliveStep"/> (see
/// setup-keepalive-process-manager in docs/ARCHITECTURE.md).
///
/// Uses <see cref="FakeKeepaliveProcessRuntime"/> — a narrow, setup-engine-internal fake for
/// <see cref="IKeepaliveProcessRuntime"/> — to deterministically control PID liveness,
/// command-line lookup, WSL process enumeration, process start (success/null/throw), and
/// kill-tree (success/throw) without touching any real process. Command-line *identity* policy
/// (whether a command line belongs to a given distro's keepalive) is exercised through the real
/// <see cref="OpenClaw.Shared.WslCommandLineMatcher"/> via
/// <see cref="KeepaliveProcessManager.IsKeepaliveCommandLine"/> — the fake never re-implements
/// that policy, only supplies raw command-line strings for the manager to evaluate.
/// </summary>
public class KeepaliveProcessManagerTests : IDisposable
{
    private const string Distro = "OpenClawGateway";
    private const string WslExePath = @"C:\Windows\System32\wsl.exe";
    private static string ValidKeepaliveCommandLine(string distro) => $@"{WslExePath} -d {distro} -- sleep infinity";

    private readonly TempDirectory _tempDirectory = new("keepalive-mgr-test-");
    private readonly string _tempDir;

    public KeepaliveProcessManagerTests()
    {
        _tempDir = _tempDirectory.Path;
    }

    public void Dispose() => _tempDirectory.Dispose();

    private static SetupLogger NewLogger() => new(filePath: null, LogLevel.Trace);

    private KeepaliveProcessManager NewManager(
        FakeKeepaliveProcessRuntime runtime,
        string? distroName = Distro,
        SetupLogger? logger = null)
        => new(distroName, _tempDir, WslExePath, logger ?? NewLogger(), runtime);

    private SetupContext NewContext(SetupLogger logger)
    {
        var config = new SetupConfig { DistroName = Distro };
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            dataDir: _tempDir,
            localDataDir: _tempDir);
    }

    private void WriteMarkerFile(string distro, int pid)
    {
        var markerPath = KeepaliveProcessManager.GetMarkerPath(_tempDir, distro);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, $$"""{"DistroName":"{{distro}}","Pid":{{pid}},"StartTimeUtc":"2020-01-01T00:00:00Z","ProcessName":"wsl"}""");
    }

    [Fact]
    public void GetMarkerPath_ReturnsExpectedShape()
    {
        var path = KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro);

        Assert.Equal(Path.Combine(_tempDir, "wsl-keepalive", $"{Distro}.json"), path);
    }

    [Fact]
    public void TryGetExisting_MissingMarkerFile_ReturnsFalse()
    {
        var markerPath = Path.Combine(_tempDir, "missing.json");
        var manager = NewManager(new FakeKeepaliveProcessRuntime());

        var result = manager.TryGetExisting(markerPath, Distro, out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void TryGetExisting_CorruptMarker_ReturnsFalse()
    {
        var markerPath = Path.Combine(_tempDir, "keepalive.json");
        File.WriteAllText(markerPath, "not json");
        var manager = NewManager(new FakeKeepaliveProcessRuntime());

        var result = manager.TryGetExisting(markerPath, Distro, out var pid);

        Assert.False(result);
        Assert.Equal(0, pid);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep infinity", "OpenClawGateway", true)]
    [InlineData(@"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep 60", "OpenClawGateway", false)]
    [InlineData(@"C:\Windows\System32\wsl.exe -d OtherGateway -- sleep infinity", "OpenClawGateway", false)]
    [InlineData(@"C:\Windows\System32\wsl.exe -d OpenClawGateway-Dev -- sleep infinity", "OpenClawGateway", false)]
    [InlineData("wsl.exe --distribution \"OpenClawGateway-Dev\" -- sleep infinity", "OpenClawGateway-Dev", true)]
    public void IsKeepaliveCommandLine_RequiresDistroAndSleepInfinity(string commandLine, string distro, bool expected)
    {
        Assert.Equal(expected, KeepaliveProcessManager.IsKeepaliveCommandLine(commandLine, distro));
    }

    // ---- EnsureStarted: identity / reuse / fresh-start decision tree ----

    [Fact]
    public void EnsureStarted_ValidMarkerWithMatchingLiveCommandLine_ReturnsAlreadyRunning_DoesNotStartNewProcess()
    {
        WriteMarkerFile(Distro, pid: 4242);
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.SetAlive(4242, ValidKeepaliveCommandLine(Distro));
        var manager = NewManager(runtime);

        var result = manager.EnsureStarted();

        var alreadyRunning = Assert.IsType<KeepaliveStartResult.AlreadyRunning>(result);
        Assert.Equal(4242, alreadyRunning.Pid);
        Assert.Equal(0, runtime.StartCallCount);
    }

    [Fact]
    public async Task StartKeepaliveStep_AlreadyRunning_MapsClosedResultToExactStepMessage()
    {
        WriteMarkerFile(Distro, pid: 4242);
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.SetAlive(4242, ValidKeepaliveCommandLine(Distro));
        using var logger = NewLogger();
        var step = new StartKeepaliveStep(runtime);

        var result = await step.ExecuteAsync(NewContext(logger), CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal("Keepalive already running", result.Message);
        Assert.Equal(0, runtime.StartCallCount);
    }

    [Fact]
    public void EnsureStarted_StaleOrExitedMarker_StartsFresh()
    {
        // Marker points at a PID the runtime reports as no longer alive.
        WriteMarkerFile(Distro, pid: 111);
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.NextStartedPid = 999;
        var manager = NewManager(runtime);

        var result = manager.EnsureStarted();

        var started = Assert.IsType<KeepaliveStartResult.Started>(result);
        Assert.Equal(999, started.Pid);
        Assert.Equal(1, runtime.StartCallCount);

        var markerJson = File.ReadAllText(KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro));
        using var marker = JsonDocument.Parse(markerJson);
        Assert.Equal(Distro, marker.RootElement.GetProperty("DistroName").GetString());
        Assert.Equal(999, marker.RootElement.GetProperty("Pid").GetInt32());
        Assert.Equal("wsl", marker.RootElement.GetProperty("ProcessName").GetString());
        Assert.True(marker.RootElement.GetProperty("StartTimeUtc").TryGetDateTimeOffset(out _));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\wsl.exe -d SomeOtherDistro -- sleep infinity")]
    [InlineData(@"C:\Windows\System32\wsl.exe -d OpenClawGateway -- sleep 60")]
    public void EnsureStarted_LivePidWithWrongDistroOrCommandLine_StartsFresh(string commandLine)
    {
        WriteMarkerFile(Distro, pid: 222);
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.SetAlive(222, commandLine);
        runtime.NextStartedPid = 333;
        var manager = NewManager(runtime);

        var result = manager.EnsureStarted();

        var started = Assert.IsType<KeepaliveStartResult.Started>(result);
        Assert.Equal(333, started.Pid);
        Assert.Equal(1, runtime.StartCallCount);
    }

    [Fact]
    public async Task StartKeepaliveStep_ProcessStartReturnsNull_SoftSucceedsWithExactLog()
    {
        var runtime = new FakeKeepaliveProcessRuntime { NextStartedPid = null };
        using var logger = NewLogger();
        var logs = new List<LogEntry>();
        logger.LogEmitted += (_, entry) => logs.Add(entry);
        var step = new StartKeepaliveStep(runtime);

        var result = await step.ExecuteAsync(NewContext(logger), CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Null(result.Message);
        Assert.Contains(logs, entry =>
            entry.Level == LogLevel.Warn &&
            entry.Message == "Failed to start keepalive process — tray will start its own");
        Assert.False(File.Exists(KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro)));
    }

    [Fact]
    public async Task StartKeepaliveStep_ProcessStartThrows_SoftSucceedsWithExactLog()
    {
        var runtime = new FakeKeepaliveProcessRuntime { StartException = new InvalidOperationException("boom") };
        using var logger = NewLogger();
        var logs = new List<LogEntry>();
        logger.LogEmitted += (_, entry) => logs.Add(entry);
        var step = new StartKeepaliveStep(runtime);

        var result = await step.ExecuteAsync(NewContext(logger), CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Null(result.Message);
        // Warning text must be byte-identical to the null-PID branch: callers observing the
        // warning contract cannot distinguish a thrown start failure from a null-PID one.
        Assert.Contains(logs, entry =>
            entry.Level == LogLevel.Warn &&
            entry.Message == "Failed to start keepalive process — tray will start its own");
        Assert.Contains(logs, entry =>
            entry.Level == LogLevel.Debug &&
            entry.Message.Contains("boom"));
        Assert.False(File.Exists(KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro)));
    }

    [Fact]
    public void EnsureStarted_UsesExactWslArgvOrder()
    {
        var runtime = new FakeKeepaliveProcessRuntime { NextStartedPid = 321 };
        var manager = NewManager(runtime);

        manager.EnsureStarted();

        var startSpec = Assert.Single(runtime.StartSpecs);
        Assert.Equal(WslExePath, startSpec.FileName);
        Assert.Equal(new[] { "-d", Distro, "--", "sleep", "infinity" }, startSpec.Arguments);
    }

    // ---- RollbackAsync: kill-identity, per-process failure isolation, marker cleanup ----

    [Fact]
    public async Task RollbackAsync_KillsOnlyMatchingDistroProcesses_LeavesOthersUntouched()
    {
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.SetWslProcesses(
            (1001, ValidKeepaliveCommandLine(Distro)),          // matches -> killed
            (1002, ValidKeepaliveCommandLine("OtherDistro")),   // wrong distro -> untouched
            (1003, @"C:\Windows\System32\wsl.exe -d " + Distro + " -- bash"), // right distro, not a keepalive shape -> untouched
            (1004, null));                                      // unreadable/unknown command line -> untouched
        var manager = NewManager(runtime);

        await manager.RollbackAsync(CancellationToken.None);

        Assert.Equal(new[] { 1001 }, runtime.KilledPids);
        Assert.DoesNotContain(1002, runtime.KilledPids);
        Assert.DoesNotContain(1003, runtime.KilledPids);
        Assert.DoesNotContain(1004, runtime.KilledPids);
        Assert.Equal(new[] { "wsl", "wsl.exe" }, runtime.EnumeratedProcessNames);
        Assert.Equal((1001, TimeSpan.FromSeconds(5)), Assert.Single(runtime.KillRequests));
    }

    [Fact]
    public async Task RollbackAsync_EnumerationFailure_StillCleansMarker()
    {
        var runtime = new FakeKeepaliveProcessRuntime
        {
            EnumerationException = new InvalidOperationException("enumeration failed")
        };
        WriteMarkerFile(Distro, pid: 1999);
        var manager = NewManager(runtime);

        await manager.RollbackAsync(CancellationToken.None);

        Assert.Empty(runtime.KilledPids);
        Assert.False(File.Exists(KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro)));
    }

    [Fact]
    public async Task RollbackAsync_InspectionFailureForOneProcess_ContinuesToLaterProcessesAndMarkerCleanup()
    {
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.SetWslProcesses(
            (2001, ValidKeepaliveCommandLine(Distro)),
            (2002, ValidKeepaliveCommandLine(Distro)));
        // Inspecting PID 2001's command line throws — must not stop 2002 from being inspected/killed.
        runtime.CommandLineExceptions[2001] = new InvalidOperationException("WMI query failed");
        WriteMarkerFile(Distro, pid: 2001);
        var manager = NewManager(runtime);

        await manager.RollbackAsync(CancellationToken.None);

        Assert.DoesNotContain(2001, runtime.KilledPids); // inspection failed, so it's never evaluated for a kill
        Assert.Contains(2002, runtime.KilledPids);        // later process still processed normally
        Assert.False(File.Exists(KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro))); // marker cleanup still runs
    }

    [Fact]
    public async Task RollbackAsync_KillFailureForOneProcess_ContinuesToLaterProcessesAndMarkerCleanup()
    {
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.SetWslProcesses(
            (3001, ValidKeepaliveCommandLine(Distro)),
            (3002, ValidKeepaliveCommandLine(Distro)));
        // Killing PID 3001 throws (e.g. it exited between enumeration and kill) — must not stop
        // 3002 from being killed, and must not stop marker cleanup afterward.
        runtime.KillExceptions[3001] = new InvalidOperationException("process already exited");
        WriteMarkerFile(Distro, pid: 3001);
        var manager = NewManager(runtime);

        await manager.RollbackAsync(CancellationToken.None);

        Assert.DoesNotContain(3001, runtime.KilledPids); // kill attempted but threw, not recorded as killed
        Assert.Contains(3002, runtime.KilledPids);
        Assert.False(File.Exists(KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro)));
    }

    [Fact]
    public async Task RollbackAsync_NoDistroName_NoOpsSafely()
    {
        var runtime = new FakeKeepaliveProcessRuntime();
        var manager = NewManager(runtime, distroName: null);

        await manager.RollbackAsync(CancellationToken.None);
        manager = NewManager(runtime, distroName: string.Empty);
        await manager.RollbackAsync(CancellationToken.None);

        // No distro name means no enumeration is even attempted, and no exception is thrown.
        Assert.Equal(0, runtime.EnumerateCallCount);
    }

    [Fact]
    public async Task RollbackAsync_NoMarkerFile_DoesNotThrow()
    {
        var manager = NewManager(
            new FakeKeepaliveProcessRuntime(),
            distroName: $"no-such-distro-{Guid.NewGuid():N}");

        await manager.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RollbackAsync_DeletesMarkerAndEmptyDirectory_ButNotNonEmptyDirectory()
    {
        var distro = $"test-distro-{Guid.NewGuid():N}";
        var markerPath = KeepaliveProcessManager.GetMarkerPath(_tempDir, distro);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "{}");

        var manager = NewManager(new FakeKeepaliveProcessRuntime(), distro);
        await manager.RollbackAsync(CancellationToken.None);

        Assert.False(File.Exists(markerPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(markerPath)));
    }

    [Fact]
    public async Task RollbackAsync_LeavesNonEmptyMarkerDirectoryInPlace()
    {
        var distroA = $"test-distro-a-{Guid.NewGuid():N}";
        var distroB = $"test-distro-b-{Guid.NewGuid():N}";
        var markerPathA = KeepaliveProcessManager.GetMarkerPath(_tempDir, distroA);
        var markerDir = Path.GetDirectoryName(markerPathA)!;
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(markerPathA, "{}");
        // Another distro's marker lives in the same shared wsl-keepalive directory.
        File.WriteAllText(KeepaliveProcessManager.GetMarkerPath(_tempDir, distroB), "{}");

        var manager = NewManager(new FakeKeepaliveProcessRuntime(), distroA);
        await manager.RollbackAsync(CancellationToken.None);

        Assert.False(File.Exists(markerPathA));
        Assert.True(Directory.Exists(markerDir));
        Assert.True(File.Exists(KeepaliveProcessManager.GetMarkerPath(_tempDir, distroB)));
    }

    [Fact]
    public async Task RollbackAsync_AlreadyCancelledToken_BehaviorUnchanged()
    {
        // RollbackAsync has never observed the CancellationToken (matches the pre-extraction
        // inline implementation) — an already-cancelled token must not change behavior or throw
        // an OperationCanceledException.
        var runtime = new FakeKeepaliveProcessRuntime();
        runtime.SetWslProcesses((4001, ValidKeepaliveCommandLine(Distro)));
        WriteMarkerFile(Distro, pid: 4001);
        var manager = NewManager(runtime);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await manager.RollbackAsync(cts.Token);

        Assert.Contains(4001, runtime.KilledPids);
        Assert.False(File.Exists(KeepaliveProcessManager.GetMarkerPath(_tempDir, Distro)));
    }
}

/// <summary>
/// Deterministic <see cref="IKeepaliveProcessRuntime"/> test double. Setup-engine-test-scoped
/// only — mirrors the raw OS primitives the interface exposes, with zero identity policy: the
/// caller (KeepaliveProcessManager) still runs every command line supplied here through the real
/// <see cref="OpenClaw.Shared.WslCommandLineMatcher"/>.
/// </summary>
internal sealed class FakeKeepaliveProcessRuntime : IKeepaliveProcessRuntime
{
    private readonly HashSet<int> _alivePids = new();
    private readonly Dictionary<int, string?> _commandLines = new();
    private List<int> _wslProcessIds = new();

    public Dictionary<int, Exception> CommandLineExceptions { get; } = new();
    public Dictionary<int, Exception> KillExceptions { get; } = new();
    public List<int> KilledPids { get; } = new();
    public List<(int Pid, TimeSpan WaitTimeout)> KillRequests { get; } = new();
    public List<string> EnumeratedProcessNames { get; } = new();
    public List<KeepaliveProcessStartSpec> StartSpecs { get; } = new();
    public int StartCallCount { get; private set; }
    public int EnumerateCallCount { get; private set; }
    public int? NextStartedPid { get; set; }
    public Exception? StartException { get; set; }
    public Exception? EnumerationException { get; set; }

    public void SetAlive(int pid, string? commandLine)
    {
        _alivePids.Add(pid);
        _commandLines[pid] = commandLine;
    }

    public void SetWslProcesses(params (int Pid, string? CommandLine)[] processes)
    {
        _wslProcessIds = processes.Select(p => p.Pid).ToList();
        foreach (var (pid, commandLine) in processes)
        {
            _alivePids.Add(pid);
            _commandLines[pid] = commandLine;
        }
    }

    public bool IsProcessAlive(int pid) => _alivePids.Contains(pid);

    public string? GetCommandLine(int pid)
    {
        if (CommandLineExceptions.TryGetValue(pid, out var ex))
            throw ex;

        return _commandLines.TryGetValue(pid, out var commandLine) ? commandLine : null;
    }

    public IReadOnlyList<int> EnumerateProcessIds(string processName)
    {
        EnumerateCallCount++;
        EnumeratedProcessNames.Add(processName);
        if (EnumerationException != null)
            throw EnumerationException;

        return processName == "wsl" ? _wslProcessIds : [];
    }

    public int? StartDetached(KeepaliveProcessStartSpec startSpec)
    {
        StartCallCount++;
        StartSpecs.Add(startSpec);
        if (StartException != null)
            throw StartException;

        return NextStartedPid;
    }

    public void KillProcessTree(int pid, TimeSpan waitTimeout)
    {
        KillRequests.Add((pid, waitTimeout));
        if (KillExceptions.TryGetValue(pid, out var ex))
            throw ex;

        KilledPids.Add(pid);
    }
}
