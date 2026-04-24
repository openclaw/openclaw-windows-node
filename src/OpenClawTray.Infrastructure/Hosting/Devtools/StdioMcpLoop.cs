using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Which MCP transport the devtools surface uses. HTTP stays the default in
/// v1 (spec §16 open question #1); stdio is additive for agents that prefer
/// framed-line JSON-RPC.
/// </summary>
internal enum McpTransport
{
    Http,
    Stdio,
}

/// <summary>
/// Newline-delimited JSON-RPC 2.0 read/write loop. One request per line in,
/// one response per line out. Reuses the same <see cref="McpDispatcher"/> as
/// the HTTP transport so tool handlers and logging are shared.
///
/// <para>
/// The loop terminates cleanly when stdin reaches EOF (the agent closed the
/// pipe) or when <see cref="Stop"/> is called. Reload still exits the process
/// with sentinel code 42 — the supervisor treats it identically to the HTTP
/// transport's teardown.
/// </para>
/// </summary>
internal sealed class StdioMcpLoop : IDisposable
{
    private readonly McpDispatcher _dispatcher;
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public StdioMcpLoop(McpDispatcher dispatcher, TextReader reader, TextWriter writer)
    {
        _dispatcher = dispatcher;
        _reader = reader;
        _writer = writer;
    }

    /// <summary>
    /// Processes a single request and returns the response line. Kept public
    /// so tests can exercise the request/response contract without spawning a
    /// background thread.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "JSON serialization for stdio MCP response.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "JSON serialization for stdio MCP response.")]
    public string ProcessLine(string line)
    {
        var response = _dispatcher.Dispatch(line);
        return JsonSerializer.Serialize(response, DevtoolsMcpServer.JsonOpts);
    }

    /// <summary>Runs the read loop synchronously until stdin closes or cancellation fires.</summary>
    public void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = _reader.ReadLine();
            }
            catch (IOException) { break; }
            if (line is null) break; // EOF — agent closed the pipe
            if (line.Length == 0) continue;

            var response = ProcessLine(line);
            lock (_writeLock)
            {
                _writer.WriteLine(response);
                _writer.Flush();
            }
        }
    }

    /// <summary>Starts the loop on a background thread. Safe to call once per instance.</summary>
    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("Stdio loop is already running.");
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _thread = new Thread(() => Run(ct))
        {
            IsBackground = true,
            Name = "devtools-stdio",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }
}
