using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

/// <summary>Independent observation only. No production completion path awaits this reader.</summary>
internal sealed class BrowserWslPersistentObserver : IAsyncDisposable
{
    internal sealed record Event(long ReceivedTicks, JsonElement Data);
    private readonly Process _process;
    private readonly Task _reader;
    private readonly Task _errors;
    private readonly Stopwatch _clock;
    private readonly ConcurrentQueue<Event> _events = new();
    private readonly TaskCompletionSource<Event> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Event>[] _challenges = [NewSignal(), NewSignal()];
    private static TaskCompletionSource<Event> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<Event> Ready => _ready.Task;
    internal Event[] Events => _events.ToArray();

    internal BrowserWslPersistentObserver(string distro, string root, Stopwatch clock)
    {
        _clock = clock;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var arg in new[] { "--distribution", distro, "--exec", "/usr/bin/python3", "-u", root + "/guest.py", "monitor" })
            start.ArgumentList.Add(arg);
        start.Environment.Remove("WSLENV");
        _process = Process.Start(start) ?? throw new IOException("Observer launch failed.");
        _errors = DrainErrors();
        _reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ReadLoop()
    {
        try
        {
            var bytes = _process.StandardOutput.BaseStream;
            var buffer = new byte[4096];
            var offset = 0;
            var count = 0;
            while (true)
            {
                var next = bytes.ReadByte();
                if (next < 0) break;
                if (next != 10)
                {
                    if (offset == buffer.Length) throw new InvalidDataException("Observer line bound.");
                    buffer[offset++] = (byte)next;
                    continue;
                }
                // Stamp immediately on the dedicated reader thread, before parsing, locks or continuations.
                var stamp = _clock.ElapsedTicks;
                if (++count > 8) throw new InvalidDataException("Observer event bound.");
                using var document = JsonDocument.Parse(buffer.AsMemory(0, offset));
                offset = 0;
                var item = new Event(stamp, document.RootElement.Clone());
                var type = item.Data.GetProperty("type").GetString();
                if (type is not ("ready" or "exit" or "challenge" or "stopped")) throw new InvalidDataException("Observer event schema.");
                _events.Enqueue(item);
                if (type == "ready") _ready.TrySetResult(item);
                if (type == "challenge")
                {
                    var id = item.Data.GetProperty("id").GetInt32();
                    if (id is < 1 or > 2) throw new InvalidDataException("Observer challenge id.");
                    _challenges[id - 1].TrySetResult(item);
                }
            }
            if (offset != 0) throw new InvalidDataException("Partial observer event.");
        }
        catch (Exception ex)
        {
            _ready.TrySetException(new IOException("Observer failed.", ex));
            foreach (var signal in _challenges) signal.TrySetException(new IOException("Observer failed.", ex));
            throw;
        }
    }

    internal async Task<(long SentTicks, Event Response)> Challenge(int id)
    {
        if (id is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(id));
        var sent = _clock.ElapsedTicks;
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { op = "challenge", id }));
        await _process.StandardInput.FlushAsync();
        var response = await _challenges[id - 1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (sent, response);
    }

    private async Task DrainErrors()
    {
        var bytes = new byte[1024];
        var total = 0;
        while (true)
        {
            var read = await _process.StandardError.BaseStream.ReadAsync(bytes);
            if (read == 0) return;
            total += read;
            if (total > 8192) throw new InvalidDataException("Observer error bound.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("{\"op\":\"stop\"}");
                _process.StandardInput.Close();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            try { await Task.WhenAll(_reader, _errors).WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { _process.Dispose(); }
        }
    }
}
