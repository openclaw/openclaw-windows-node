using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Runs commands inside a Windows AppContainer via wxc-exec.exe. Throws
/// <see cref="FileNotFoundException"/> on construction if the binary is absent.
/// </summary>
public sealed class MxcExecutor
{
    private const int DefaultStdoutCapBytes = 40_000;
    private const int DefaultStderrCapBytes = 5_000;
    internal const int ProcessTreeKillWorkerLimit = 8;
    private static readonly TimeSpan s_defaultCleanupTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly SemaphoreSlim s_processTreeKillWorkers = new(
        ProcessTreeKillWorkerLimit,
        ProcessTreeKillWorkerLimit);

    private readonly string _wxcExePath;
    private readonly int _stdoutCapBytes;
    private readonly int _stderrCapBytes;
    private readonly Func<ProcessStartInfo, Process> _processFactory;
    private readonly Action<Process> _processTreeKiller;
    private readonly TimeSpan _cleanupTimeout;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public MxcExecutor(string wxcExePath, int? stdoutCapBytes = null, int? stderrCapBytes = null)
        : this(
            wxcExePath,
            stdoutCapBytes,
            stderrCapBytes,
            static startInfo => new Process { StartInfo = startInfo },
            static process => process.Kill(entireProcessTree: true),
            s_defaultCleanupTimeout)
    {
    }

    internal MxcExecutor(
        string wxcExePath,
        int? stdoutCapBytes,
        int? stderrCapBytes,
        Func<ProcessStartInfo, Process> processFactory,
        Action<Process> processTreeKiller,
        TimeSpan cleanupTimeout)
    {
        if (string.IsNullOrEmpty(wxcExePath)) throw new ArgumentException("wxcExePath required", nameof(wxcExePath));
        if (!File.Exists(wxcExePath))
            throw new FileNotFoundException($"wxc-exec.exe not found at: {wxcExePath}", wxcExePath);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(processTreeKiller);
        if (cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout), "Cleanup timeout must be positive.");

        _wxcExePath = wxcExePath;
        _stdoutCapBytes = stdoutCapBytes is > 0 ? stdoutCapBytes.Value : DefaultStdoutCapBytes;
        _stderrCapBytes = stderrCapBytes is > 0 ? stderrCapBytes.Value : DefaultStderrCapBytes;
        _processFactory = processFactory;
        _processTreeKiller = processTreeKiller;
        _cleanupTimeout = cleanupTimeout;
    }

    public async Task<MxcResult> RunAsync(
        MxcConfig config,
        CancellationToken ct = default,
        bool experimental = false,
        string? workingDirectory = null)
    {
        var json = JsonSerializer.Serialize(config, s_jsonOptions);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var args = new List<string>();
        if (experimental) args.Add("--experimental");
        args.Add("--config-base64");
        args.Add(base64);
        return await RunWithArgumentsAsync(args, ct, workingDirectory);
    }

    /// <summary>
    /// Additive (OpenClaw): runs wxc-exec with <c>--config &lt;file&gt;</c> instead of
    /// <c>--config-base64</c>. Use when the serialized config approaches the Windows
    /// command-line limit (~32k chars). Caller owns the file lifetime.
    /// </summary>
    public Task<MxcResult> RunWithConfigFileAsync(
        string configFilePath,
        CancellationToken ct = default,
        bool experimental = false,
        string? workingDirectory = null)
    {
        if (string.IsNullOrEmpty(configFilePath)) throw new ArgumentException("configFilePath required", nameof(configFilePath));
        // Reject embedded quotes to avoid any argv-parsing ambiguity. NTFS allows
        // names with most punctuation but disallows '"', so this is also a
        // guard against malformed input rather than a real-world rejection.
        if (configFilePath.IndexOf('"') >= 0)
            throw new ArgumentException("configFilePath must not contain quote characters", nameof(configFilePath));
        var args = new List<string>();
        if (experimental) args.Add("--experimental");
        args.Add("--config");
        args.Add(configFilePath);
        return RunWithArgumentsAsync(args, ct, workingDirectory);
    }

    private async Task<MxcResult> RunWithArgumentsAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _wxcExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        // ArgumentList avoids the manual-quoting trap that bites Process.Arguments
        // (each entry is escaped per Win32 CommandLineToArgvW rules by the BCL).
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        var process = _processFactory(startInfo)
            ?? throw new InvalidOperationException("Process factory returned null.");
        var processOwnedByKillWorker = false;
        var processRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processStarted = false;
        RedirectedOutputCapture? stdout = null;
        RedirectedOutputCapture? stderr = null;

        var processExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => processExited.TrySetResult();

        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
            processStarted = true;
            var processId = process.Id;
            stdout = new RedirectedOutputCapture(
                GetRedirectedPipe(process.StandardOutput),
                process.StandardOutput.CurrentEncoding,
                GetCaptureLimit(_stdoutCapBytes));
            stderr = new RedirectedOutputCapture(
                GetRedirectedPipe(process.StandardError),
                process.StandardError.CurrentEncoding,
                GetCaptureLimit(_stderrCapBytes));
            if (process.HasExited)
                processExited.TrySetResult();

            bool completed;
            try
            {
                await processExited.Task.WaitAsync(ct);
                completed = true;
            }
            catch (OperationCanceledException)
            {
                completed = false;
            }

            if (!completed)
            {
                if (!await KillProcessTreeWithTimeoutAsync(
                        process,
                        processRelease.Task,
                        () => processOwnedByKillWorker = true))
                {
                    Trace.WriteLine($"MxcExecutor: process kill (cancellation path) timed out or failed (pid={processId}).");
                }
                var cleanupCompleted = await WaitForCleanupAsync(
                        processExited.Task,
                        stdout.Completion,
                        stderr.Completion,
                        _cleanupTimeout);
                if (!cleanupCompleted)
                {
                    Trace.WriteLine($"MxcExecutor: cancellation cleanup timed out (pid={processId}).");
                }

                var cancelledOutput = cleanupCompleted
                    ? stdout.Output
                    : (await StopOutputCapturesAsync(stdout, stderr, _cleanupTimeout)).Stdout;
                sw.Stop();
                return new MxcResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = Truncate(cancelledOutput, _stdoutCapBytes),
                    Error = "Execution was cancelled.",
                    TimedOut = true,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }

            // The launcher exited, but a descendant may still hold an inherited
            // stdout/stderr write handle. Bound the drain so a completed launcher
            // cannot pin the node invocation slot indefinitely.
            var outputDrained = await WaitForCleanupAsync(
                    processExited.Task,
                    stdout.Completion,
                    stderr.Completion,
                    _cleanupTimeout);
            if (!outputDrained)
            {
                Trace.WriteLine($"MxcExecutor: post-exit output drain timed out (pid={processId}).");
            }

            var output = outputDrained
                ? (Stdout: stdout.Output, Stderr: stderr.Output)
                : await StopOutputCapturesAsync(stdout, stderr, _cleanupTimeout);
            sw.Stop();
            var capturedOut = Truncate(output.Stdout.Trim(), _stdoutCapBytes);
            var capturedError = Truncate(output.Stderr.Trim(), _stderrCapBytes);

            return new MxcResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = string.IsNullOrEmpty(capturedOut) ? null : capturedOut,
                Error = string.IsNullOrEmpty(capturedError) ? null : capturedError,
                TimedOut = false,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            if (processStarted)
            {
                try
                {
                    if (!processOwnedByKillWorker)
                    {
                        _ = await KillProcessTreeWithTimeoutAsync(
                            process,
                            processRelease.Task,
                            () => processOwnedByKillWorker = true);
                    }
                    if (stdout is not null)
                        _ = await stdout.StopAndSnapshotAsync(_cleanupTimeout);
                    if (stderr is not null)
                        _ = await stderr.StopAndSnapshotAsync(_cleanupTimeout);
                }
                catch (Exception cleanupException)
                {
                    Trace.WriteLine(
                        $"MxcExecutor: post-failure cleanup failed: {cleanupException.Message}");
                }
            }

            sw.Stop();
            return new MxcResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Failed to launch wxc-exec.exe: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        finally
        {
            processRelease.TrySetResult();
            if (!processOwnedByKillWorker)
                process.Dispose();
        }
    }

    private static FileStream GetRedirectedPipe(StreamReader reader) =>
        reader.BaseStream as FileStream
        ?? throw new InvalidOperationException("Redirected process stream is not a file-backed pipe.");

    private static int GetCaptureLimit(int resultLimit) =>
        resultLimit > int.MaxValue / 2 ? int.MaxValue : resultLimit * 2;

    private static async Task<(string Stdout, string Stderr)> StopOutputCapturesAsync(
        RedirectedOutputCapture stdout,
        RedirectedOutputCapture stderr,
        TimeSpan timeout)
    {
        var stdoutTask = stdout.StopAndSnapshotAsync(timeout);
        var stderrTask = stderr.StopAndSnapshotAsync(timeout);
        await Task.WhenAll(stdoutTask, stderrTask);
        return (await stdoutTask, await stderrTask);
    }

    internal async Task<bool> KillProcessTreeWithTimeoutAsync(
        Process process,
        Task? processRelease = null,
        Action? transferProcessOwnership = null)
    {
        var processId = process.Id;
        var timeoutStarted = Stopwatch.GetTimestamp();
        if (!await s_processTreeKillWorkers.WaitAsync(_cleanupTimeout))
        {
            Trace.WriteLine(
                $"MxcExecutor: timed out waiting for process kill worker capacity " +
                $"(limit={ProcessTreeKillWorkerLimit}, pid={processId}).");
            return false;
        }

        Task<Exception?> killTask;
        try
        {
            killTask = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        _processTreeKiller(process);
                        return (Exception?)null;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                    finally
                    {
                        s_processTreeKillWorkers.Release();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            s_processTreeKillWorkers.Release();
            Trace.WriteLine($"MxcExecutor: process kill worker failed to start: {ex.Message}");
            return false;
        }

        if (transferProcessOwnership is not null)
        {
            transferProcessOwnership();
            ObserveFault(DisposeProcessAfterReleaseAsync(
                process,
                killTask,
                processRelease ?? Task.CompletedTask));
        }
        var remainingTimeout = _cleanupTimeout - Stopwatch.GetElapsedTime(timeoutStarted);
        try
        {
            if (!killTask.IsCompleted && remainingTimeout <= TimeSpan.Zero)
                return false;

            var error = killTask.IsCompleted
                ? await killTask
                : await killTask.WaitAsync(remainingTimeout);
            if (error is null)
                return true;

            Trace.WriteLine($"MxcExecutor: process kill (cancellation path) failed: {error.Message}");
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task DisposeProcessAfterReleaseAsync(
        Process process,
        Task killTask,
        Task processRelease)
    {
        try
        {
            await Task.WhenAll(killTask, processRelease).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    internal static async Task<bool> WaitForCleanupAsync(
        Task processExited,
        Task stdoutClosed,
        Task stderrClosed,
        TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(processExited, stdoutClosed, stderrClosed).WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + $"\n\n... [TRUNCATED — showing first {maxLength} of {text.Length} chars]";
    }

    private sealed class RedirectedOutputCapture
    {
        private const uint ThreadTerminate = 0x0001;
        private const int ErrorNotFound = 1168;
        private readonly FileStream _stream;
        private readonly Encoding _configuredEncoding;
        private readonly int _maxChars;
        private readonly object _lock = new();
        private readonly object _readerThreadLock = new();
        private readonly StringBuilder _output = new();
        private readonly TaskCompletionSource _readerThreadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint _readerThread;
        private int _closing;

        internal RedirectedOutputCapture(
            FileStream stream,
            Encoding configuredEncoding,
            int maxChars)
        {
            _stream = stream;
            _configuredEncoding = configuredEncoding;
            _maxChars = maxChars;
            Completion = Task.Factory.StartNew(
                Capture,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        internal Task Completion { get; }

        internal string Output
        {
            get
            {
                lock (_lock)
                    return _output.ToString();
            }
        }

        internal async Task<string> StopAndSnapshotAsync(TimeSpan timeout)
        {
            var timeoutStarted = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _closing, 1);
            try
            {
                await _readerThreadStarted.Task.WaitAsync(timeout);
                while (!Completion.IsCompleted)
                {
                    CancelPendingRead();
                    var remainingTimeout = timeout - Stopwatch.GetElapsedTime(timeoutStarted);
                    if (remainingTimeout <= TimeSpan.Zero)
                        break;

                    var retryDelay = remainingTimeout < TimeSpan.FromMilliseconds(25)
                        ? remainingTimeout
                        : TimeSpan.FromMilliseconds(25);
                    try
                    {
                        await Completion.WaitAsync(retryDelay).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                    }
                }
            }
            catch (TimeoutException)
            {
                Trace.WriteLine("MxcExecutor: redirected output reader thread did not start in time.");
                _stream.Dispose();
            }

            if (!Completion.IsCompleted)
            {
                Trace.WriteLine("MxcExecutor: redirected output reader did not stop in time.");
                _stream.Dispose();
            }

            lock (_lock)
            {
                return _output.ToString();
            }
        }

        private void CancelPendingRead()
        {
            lock (_readerThreadLock)
            {
                if (_readerThread == 0
                    || Completion.IsCompleted
                    || CancelSynchronousIo(_readerThread))
                {
                    return;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound && !Completion.IsCompleted)
                {
                    Trace.WriteLine(
                        $"MxcExecutor: redirected output cancellation failed ({error}).");
                }
            }
        }

        private void Capture()
        {
            try
            {
                var readerThread = OpenThread(
                    ThreadTerminate,
                    bInheritHandle: false,
                    GetCurrentThreadId());
                if (readerThread == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                lock (_readerThreadLock)
                {
                    _readerThread = readerThread;
                    _readerThreadStarted.TrySetResult();
                }
                CaptureCore();
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _closing) != 0)
            {
            }
            catch (IOException) when (Volatile.Read(ref _closing) != 0)
            {
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _closing) != 0)
            {
            }
            catch (Exception ex)
            {
                _readerThreadStarted.TrySetException(ex);
                throw;
            }
            finally
            {
                _stream.Dispose();
                lock (_readerThreadLock)
                {
                    if (_readerThread != 0)
                    {
                        _ = CloseHandle(_readerThread);
                        _readerThread = 0;
                    }
                }
            }
        }

        private void CaptureCore()
        {
            Decoder? decoder = null;
            using var undecodedPrefix = new MemoryStream();
            var byteBuffer = new byte[4_096];
            var charBuffer = new char[GetMaxCharCount(byteBuffer.Length + 4)];
            try
            {
                while (true)
                {
                    var byteCount = _stream.Read(byteBuffer, 0, byteBuffer.Length);
                    if (byteCount == 0)
                        break;

                    if (decoder is null)
                    {
                        undecodedPrefix.Write(byteBuffer, 0, byteCount);
                        var prefix = undecodedPrefix.GetBuffer()
                            .AsSpan(0, checked((int)undecodedPrefix.Length));
                        if (!TrySelectEncoding(prefix, final: false, out var encoding, out var preambleLength))
                            continue;

                        decoder = encoding.GetDecoder();
                        AppendDecoded(
                            decoder,
                            prefix[preambleLength..],
                            charBuffer,
                            flush: false);
                        undecodedPrefix.SetLength(0);
                    }
                    else
                    {
                        AppendDecoded(
                            decoder,
                            byteBuffer.AsSpan(0, byteCount),
                            charBuffer,
                            flush: false);
                    }
                }
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _closing) != 0)
            {
            }
            catch (IOException) when (Volatile.Read(ref _closing) != 0)
            {
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _closing) != 0)
            {
            }

            if (decoder is null)
            {
                var prefix = undecodedPrefix.GetBuffer()
                    .AsSpan(0, checked((int)undecodedPrefix.Length));
                _ = TrySelectEncoding(
                    prefix,
                    final: true,
                    out var encoding,
                    out var preambleLength);
                decoder = encoding.GetDecoder();
                AppendDecoded(
                    decoder,
                    prefix[preambleLength..],
                    charBuffer,
                    flush: true);
            }
            else
            {
                AppendDecoded(
                    decoder,
                    ReadOnlySpan<byte>.Empty,
                    charBuffer,
                    flush: true);
            }
        }

        private int GetMaxCharCount(int byteCount) =>
            new[]
            {
                _configuredEncoding.GetMaxCharCount(byteCount),
                Encoding.UTF8.GetMaxCharCount(byteCount),
                Encoding.Unicode.GetMaxCharCount(byteCount),
                Encoding.BigEndianUnicode.GetMaxCharCount(byteCount),
                Encoding.UTF32.GetMaxCharCount(byteCount),
            }.Max();

        private bool TrySelectEncoding(
            ReadOnlySpan<byte> bytes,
            bool final,
            out Encoding encoding,
            out int preambleLength)
        {
            if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            {
                encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
                preambleLength = 4;
                return true;
            }

            if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            {
                encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
                preambleLength = 4;
                return true;
            }

            if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                encoding = Encoding.UTF8;
                preambleLength = 3;
                return true;
            }

            if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })
                && (final || bytes.Length >= 4 || (bytes.Length >= 3 && bytes[2] != 0)))
            {
                encoding = Encoding.Unicode;
                preambleLength = 2;
                return true;
            }

            if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            {
                encoding = Encoding.BigEndianUnicode;
                preambleLength = 2;
                return true;
            }

            if (!final && IsPossiblePreamblePrefix(bytes))
            {
                encoding = null!;
                preambleLength = 0;
                return false;
            }

            encoding = _configuredEncoding;
            preambleLength = 0;
            return true;
        }

        private static bool IsPossiblePreamblePrefix(ReadOnlySpan<byte> bytes) =>
            IsPrefix(bytes, new byte[] { 0xFF, 0xFE, 0x00, 0x00 })
            || IsPrefix(bytes, new byte[] { 0x00, 0x00, 0xFE, 0xFF })
            || IsPrefix(bytes, new byte[] { 0xEF, 0xBB, 0xBF })
            || IsPrefix(bytes, new byte[] { 0xFE, 0xFF });

        private static bool IsPrefix(ReadOnlySpan<byte> value, ReadOnlySpan<byte> candidate) =>
            value.Length < candidate.Length && candidate[..value.Length].SequenceEqual(value);

        private void AppendDecoded(
            Decoder decoder,
            ReadOnlySpan<byte> bytes,
            char[] charBuffer,
            bool flush)
        {
            var charCount = decoder.GetChars(bytes, charBuffer, flush);
            Append(charBuffer, charCount);
        }

        private void Append(char[] buffer, int count)
        {
            if (count == 0)
                return;

            lock (_lock)
            {
                var remaining = _maxChars - _output.Length;
                if (remaining > 0)
                    _output.Append(buffer, 0, Math.Min(count, remaining));
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenThread(
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelSynchronousIo(nint hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint hObject);
    }
}
