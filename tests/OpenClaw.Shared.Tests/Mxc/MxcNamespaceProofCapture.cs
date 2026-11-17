using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;

namespace OpenClaw.Shared.Tests.Mxc;

// Test-only ownership and evidence for the namespace probe, including failures before READY.
internal sealed class MxcNamespaceProofCapture(
    string artifactRoot, string caseName, int index, object fixture,
    TimeSpan? readinessTimeout = null)
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(500);
    private readonly List<Exception> _cleanupFailures = [];
    private readonly List<Exception> _reportFailures = [];
    private IMxcProcess? _process;
    private Stream? _input, _outputStream, _errorStream;
    private IMxcStreamCloser? _outputCloser, _errorCloser;
    private readonly PipeCapture _output = new(), _error = new();
    private Task _stdout = Task.CompletedTask, _stderr = Task.CompletedTask;
    private Task? _kill, _closeOutput, _closeError;
    private Task<WaitResult>? _wait;
    private Exception? _failure;
    private string? _failurePhase, _startTimeUtc;
    private object? _identityFailure;
    private uint? _pid;
    private int? _observedExitCode;
    private string _readiness = "not reached", _finalization = "not reached", _disposal = "not reached";

    internal string Prefix { get; } = Path.Combine(artifactRoot, $"{caseName}-{index}-{Guid.NewGuid():N}");
    internal string Phase { get; set; } = "created";
    internal object? Probe { get; set; }
    internal Stream StandardInput => _input ?? throw new InvalidOperationException("No probe input pipe.");
    internal string Output => _output.Text;
    internal WaitResult? Result { get; private set; }
    internal bool Settled => _process is null || (_disposal == "disposed" && _finalization == "succeeded");

    internal async Task RunAsync(Func<MxcNamespaceProofCapture, Task> body)
    {
        Write("started");
        try { await body(this); }
        catch (Exception ex) { _failure = ex; _failurePhase = Phase; }
        Write("before-cleanup");
        await CleanupAsync();
        Write("result");
        ThrowFailures(_failure, _cleanupFailures.Concat(_reportFailures));
    }

    internal async Task StartAsync(Func<Task<IMxcProcess>> spawn)
    {
        Phase = "spawn";
        _process = await spawn();
        // Start both readers before querying an OS identity that a fast-exiting child may already have lost.
        Phase = "pipe binding";
        _input = _process.StandardInput;
        _outputStream = _process.StandardOutput;
        _errorStream = _process.StandardError;
        _outputCloser = _process.StandardOutputCloser;
        _errorCloser = _process.StandardErrorCloser;
        _stdout = Task.Run(() => _output.Read(_outputStream));
        _stderr = Task.Run(() => _error.Read(_errorStream));
        Phase = "identity";
        try
        {
            _pid = _process.Id;
            using var os = Process.GetProcessById(checked((int)_pid.Value));
            _startTimeUtc = os.StartTime.ToUniversalTime().ToString("O");
        }
        catch (Exception ex) { _identityFailure = Describe(ex); }
        Write("process");
    }

    internal async Task<string?> ReadReadinessAsync()
    {
        Phase = "readiness";
        _readiness = "waiting";
        try
        {
            var value = await _output.FirstLine.Task.WaitAsync(readinessTimeout ?? ReadinessTimeout);
            _readiness = value is null ? "EOF" : "line";
            return value;
        }
        catch (Exception ex)
        {
            _readiness = ex is TimeoutException ? "timed out" : "read failed";
            throw;
        }
    }

    internal async Task<WaitResult> FinishAsync()
    {
        Phase = "exit observation";
        // Do not race SDK finalization's control lock with TryGetExitCode or Kill.
        using (var stop = new CancellationTokenSource(ExecutionTimeout))
        {
            var pollMs = 1;
            while (!ObserveExit())
            {
                try { await Task.Delay(pollMs, stop.Token); }
                catch (OperationCanceledException ex) { throw new TimeoutException("Namespace probe did not exit.", ex); }
                pollMs = Math.Min(pollMs * 2, 50);
            }
        }
        Phase = "finalization";
        await FinalizeAsync();
        Phase = "drain";
        await Task.WhenAll(_stdout, _stderr).WaitAsync(CleanupTimeout);
        return Result!.Value;
    }

    private bool ObserveExit()
    {
        if (!_process!.TryGetExitCode(out var code)) return false;
        _observedExitCode = code;
        return true;
    }

    private async Task FinalizeAsync()
    {
        _finalization = "started";
        try
        {
            _wait ??= _process!.WaitAsync(CancellationToken.None);
            Result = await _wait.WaitAsync(CleanupTimeout);
            _finalization = "succeeded";
        }
        catch (Exception ex)
        {
            _finalization = ex is TimeoutException ? "timed out" : "failed";
            throw;
        }
    }

    private async Task CleanupAsync()
    {
        if (_process is null) { _disposal = "no process"; return; }
        await Attempt("close input", () => { _input?.Dispose(); return Task.CompletedTask; });
        if (_wait is null)
        {
            var exited = false;
            await Attempt("observe exit", () => { exited = ObserveExit(); return Task.CompletedTask; });
            if (!exited)
                await Attempt("kill", () => (_kill = Task.Run(_process.Kill)).WaitAsync(CleanupTimeout));
        }
        // Give already-written startup diagnostics a bounded chance to reach EOF before interrupting.
        // A missing READY must not cause us to close stderr before its reader has collected it.
        try { await Task.WhenAll(_stdout, _stderr).WaitAsync(DrainGrace); }
        catch (TimeoutException) { _output.Interrupt(); _error.Interrupt(); }
        catch (Exception) when (_stdout.IsFaulted || _stderr.IsFaulted)
        {
            // The drain attempt below records the actual reader error separately from the primary.
        }
        // Closing the SDK's owned native readers interrupts synchronous reads. Disposing a borrowed
        // stream or canceling ReadAsync does not interrupt the SDK pipe implementation.
        await Task.WhenAll(
            Attempt("close stdout", () => (_closeOutput = Task.Run(() => _outputCloser?.Close())).WaitAsync(CleanupTimeout)),
            Attempt("close stderr", () => (_closeError = Task.Run(() => _errorCloser?.Close())).WaitAsync(CleanupTimeout)));
        if (_kill?.IsCompleted != false)
            await Attempt("finalization", FinalizeAsync);
        await Task.WhenAll(
            Attempt("drain stdout", () => _stdout.WaitAsync(CleanupTimeout)),
            Attempt("drain stderr", () => _stderr.WaitAsync(CleanupTimeout)));
        if (_wait?.IsCompleted != false && _kill?.IsCompleted != false &&
            _closeOutput?.IsCompleted != false && _closeError?.IsCompleted != false &&
            _stdout.IsCompleted && _stderr.IsCompleted)
        {
            var disposed = await Attempt("dispose stdout reader", () => { _output.Dispose(); return Task.CompletedTask; });
            disposed &= await Attempt("dispose stderr reader", () => { _error.Dispose(); return Task.CompletedTask; });
            disposed &= await Attempt("dispose stdout", () => { _outputStream?.Dispose(); return Task.CompletedTask; });
            disposed &= await Attempt("dispose stderr", () => { _errorStream?.Dispose(); return Task.CompletedTask; });
            disposed &= await Attempt("dispose stdout closer", () => { _outputCloser?.Dispose(); return Task.CompletedTask; });
            disposed &= await Attempt("dispose stderr closer", () => { _errorCloser?.Dispose(); return Task.CompletedTask; });
            disposed &= await Attempt("dispose process", () => { _process.Dispose(); return Task.CompletedTask; });
            _disposal = disposed ? "disposed" : "failed";
        }
        else
        {
            _disposal = "retained: process control or pipe worker did not settle";
            _cleanupFailures.Add(new InvalidOperationException(_disposal));
        }

        async Task<bool> Attempt(string step, Func<Task> action)
        {
            try { await action(); return true; }
            catch (Exception ex)
            {
                lock (_cleanupFailures)
                {
                    if (!ReferenceEquals(ex, _failure) && !_cleanupFailures.Contains(ex))
                    {
                        ex.Data["NamespaceCleanupStep"] = step;
                        _cleanupFailures.Add(ex);
                    }
                }
                return false;
            }
        }
    }

    internal IReadOnlyList<Exception> RecordCase(string stage, Exception? failure, IReadOnlyList<Exception> cleanup)
    {
        var previous = _reportFailures.Count;
        Write(stage, new
        {
            Outcome = failure is null && cleanup.Count == 0 ? "passed" : "failed",
            Failure = failure is null ? null : Describe(failure),
            CleanupFailures = cleanup.Select(Describe).ToArray(),
        });
        return _reportFailures.Skip(previous).ToArray();
    }

    private void Write(string stage, object? caseOutcome = null)
    {
        try
        {
            Directory.CreateDirectory(artifactRoot);
            File.WriteAllText(Prefix + "-" + stage + ".json", JsonSerializer.Serialize(new
            {
                Case = caseName, Index = index, EvidenceId = Path.GetFileName(Prefix), Fixture = fixture, Probe,
                Phase, FailurePhase = _failurePhase,
                Identity = new { Status = _startTimeUtc is not null ? "observed" : _identityFailure is not null ? "unavailable" : "unobserved",
                    Pid = _pid, StartTimeUtc = _startTimeUtc, Failure = _identityFailure },
                Readiness = new { Status = _readiness, Value = _output.FirstLineValue, Observation = _output.LineObservation },
                Stdout = _output.Snapshot(), Stderr = _error.Snapshot(),
                ExitObservation = new { Status = _observedExitCode.HasValue ? "observed" : "unobserved", Code = _observedExitCode },
                Finalization = new { Status = _finalization, Result },
                Kill = _kill?.Status.ToString() ?? "not reached", Disposal = _disposal,
                Failure = _failure is null ? null : Describe(_failure),
                CleanupFailures = _cleanupFailures.Select(Describe).ToArray(),
                ReportFailures = _reportFailures.Select(Describe).ToArray(), CaseOutcome = caseOutcome,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _reportFailures.Add(ex); }
    }

    internal static object Describe(Exception ex) => new
    {
        Type = ex.GetType().FullName, ex.Message, ex.StackTrace,
        CleanupStep = ex.Data["NamespaceCleanupStep"],
    };

    internal static void ThrowFailures(Exception? primary, IEnumerable<Exception> additional)
    {
        var failures = additional.Distinct().Where(ex => !ReferenceEquals(ex, primary)).ToList();
        if (failures.Count > 0)
            throw new AggregateException("Namespace proof failed. The primary failure, when present, is first; cleanup/report failures follow.",
                primary is null ? failures : new[] { primary }.Concat(failures));
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private sealed class PipeCapture : IDisposable
    {
        private const int CharacterLimit = 64 * 1024;
        private readonly object _gate = new();
        private readonly StringBuilder _text = new(), _line = new();
        private StreamReader? _reader;
        private bool _truncated, _lineDone, _interrupted;
        private string _state = "not reached";
        private Exception? _error;
        internal TaskCompletionSource<string?> FirstLine { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? FirstLineValue { get { lock (_gate) return FirstLine.Task.IsCompletedSuccessfully ? FirstLine.Task.Result : null; } }
        internal string LineObservation { get { lock (_gate) return FirstLine.Task.IsCompletedSuccessfully
            ? FirstLine.Task.Result is null ? "EOF" : "line" : FirstLine.Task.IsFaulted ? "read failed" : "unobserved"; } }
        internal string Text { get { lock (_gate) return _text.ToString(); } }
        internal object Snapshot()
        {
            lock (_gate) return new { Text = _text.ToString(), State = _state, Truncated = _truncated, Interrupted = _interrupted,
                Failure = _error is null ? null : Describe(_error) };
        }
        internal void Read(Stream? stream)
        {
            try
            {
                lock (_gate) _state = "reading";
                _reader = new StreamReader(stream ?? throw new IOException("Missing namespace proof output pipe."),
                    Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                var buffer = new char[1024];
                int count;
                while ((count = _reader.Read(buffer, 0, buffer.Length)) != 0)
                {
                    lock (_gate)
                    {
                        var retain = Math.Min(count, CharacterLimit - _text.Length);
                        _text.Append(buffer, 0, retain);
                        _truncated |= retain < count;
                        for (var i = 0; i < count && !_lineDone; i++)
                        {
                            if (buffer[i] is '\r' or '\n')
                            { _lineDone = true; FirstLine.TrySetResult(_line.ToString()); }
                            else if (_line.Length < CharacterLimit) _line.Append(buffer[i]);
                            else _truncated = true;
                        }
                    }
                }
                lock (_gate)
                {
                    _state = "EOF";
                    FirstLine.TrySetResult(_line.Length == 0 ? null : _line.ToString());
                }
            }
            catch (Exception ex)
            {
                lock (_gate) { _state = "read failed"; _error = ex; FirstLine.TrySetException(ex); }
                throw;
            }
        }
        internal void Interrupt() { lock (_gate) { if (_state == "reading") _interrupted = true; } }
        public void Dispose()
        {
            if (FirstLine.Task.IsFaulted) _ = FirstLine.Task.Exception;
            _reader?.Dispose();
        }
    }
}
