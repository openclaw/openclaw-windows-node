using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenClaw.E2ETests;

internal sealed class FakeOllamaServer : IAsyncDisposable
{
    public const string Model = "proof-model";
    public const string ExpectedPrompt = "Reply with exactly: WINDOWS_GATEWAY_OLLAMA_OK";
    public const string CapturedPrompt = "Reply with exactly: THIS_RESPONSE_MUST_BE_REVOKED";
    public const string ExpectedResponse = "WINDOWS_GATEWAY_OLLAMA_OK";
    private const int DefaultOllamaPort = 11_434;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private int _requestCount;
    private int _chatRequestCount;
    private string? _lastChatBody;
    private TaskCompletionSource? _pausedChatReached;
    private TaskCompletionSource? _pausedChatRelease;

    private FakeOllamaServer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int RequestCount => Volatile.Read(ref _requestCount);
    public int ChatRequestCount => Volatile.Read(ref _chatRequestCount);
    public string? LastChatBody => Volatile.Read(ref _lastChatBody);

    public static FakeOllamaServer Start(int port = DefaultOllamaPort) => new(port);

    public void PauseNextChatResponse()
    {
        _pausedChatReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedChatRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitForPausedChatAsync(TimeSpan timeout) =>
        (_pausedChatReached?.Task ??
         throw new InvalidOperationException("No paused chat was configured."))
        .WaitAsync(timeout);

    public void ReleasePausedChat() => _pausedChatRelease?.TrySetResult();

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using TcpClient client =
                    await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    await HandleClientAsync(client, _shutdown.Token).ConfigureAwait(false);
                }
                catch (IOException) when (!_shutdown.IsCancellationRequested)
                {
                    // A revoked MCP request closes its HTTP connection before
                    // the controlled response is released.
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        string headerBlock = await ReadHeaderBlockAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        string[] headerLines = headerBlock.Split("\r\n", StringSplitOptions.None);
        string requestLine = headerLines[0];
        string[] requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2)
            throw new InvalidDataException("Ollama proof server received a malformed request line.");

        int contentLength = 0;
        bool chunked = false;
        foreach (string header in headerLines.Skip(1))
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(header["Content-Length:".Length..].Trim(), out int parsed))
            {
                contentLength = parsed;
            }
            else if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                     header["Transfer-Encoding:".Length..]
                         .Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }

        string path = requestParts[1];
        string body = chunked
            ? await ReadChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false)
            : Encoding.UTF8.GetString(
                await ReadExactBytesAsync(stream, contentLength, cancellationToken)
                    .ConfigureAwait(false));
        Interlocked.Increment(ref _requestCount);
        string response = path switch
        {
            "/api/tags" =>
                """{"models":[{"name":"proof-model","capabilities":["completion"],"details":{"context_length":4096}}]}""",
            "/api/ps" => """{"models":[]}""",
            "/api/show" => BuildShowResponse(body),
            "/api/chat" => await BuildChatResponseAsync(body).ConfigureAwait(false),
            _ => throw new InvalidDataException($"Ollama proof server received unexpected path '{path}'."),
        };

        byte[] payload = Encoding.UTF8.GetBytes(response);
        byte[] headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadHeaderBlockAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        const int maxHeaderBytes = 16 * 1024;
        using var buffer = new MemoryStream();
        var single = new byte[1];
        int matched = 0;
        byte[] terminator = [13, 10, 13, 10];
        while (buffer.Length < maxHeaderBytes)
        {
            int read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Ollama proof request headers ended early.");
            buffer.WriteByte(single[0]);
            matched = single[0] == terminator[matched]
                ? matched + 1
                : single[0] == terminator[0] ? 1 : 0;
            if (matched == terminator.Length)
            {
                byte[] bytes = buffer.ToArray();
                return Encoding.ASCII.GetString(bytes, 0, bytes.Length - terminator.Length);
            }
        }

        throw new InvalidDataException("Ollama proof request headers exceed the size limit.");
    }

    private static async Task<string> ReadChunkedBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        while (true)
        {
            string sizeLine = await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false);
            string sizeToken = sizeLine.Split(';', 2)[0].Trim();
            if (!int.TryParse(
                    sizeToken,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int size) ||
                size < 0)
            {
                throw new InvalidDataException("Ollama proof request has an invalid chunk size.");
            }

            if (size == 0)
            {
                while ((await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false)).Length > 0)
                {
                }
                return Encoding.UTF8.GetString(body.ToArray());
            }

            byte[] chunk = await ReadExactBytesAsync(stream, size, cancellationToken)
                .ConfigureAwait(false);
            body.Write(chunk);
            byte[] terminator = await ReadExactBytesAsync(stream, 2, cancellationToken)
                .ConfigureAwait(false);
            if (terminator[0] != 13 || terminator[1] != 10)
                throw new InvalidDataException("Ollama proof chunk terminator is malformed.");
        }
    }

    private static async Task<string> ReadAsciiLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        const int maxLineBytes = 1024;
        using var line = new MemoryStream();
        var single = new byte[1];
        bool sawCarriageReturn = false;
        while (line.Length < maxLineBytes)
        {
            int read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Ollama proof chunk line ended early.");
            if (sawCarriageReturn)
            {
                if (single[0] != 10)
                    throw new InvalidDataException("Ollama proof chunk line is malformed.");
                return Encoding.ASCII.GetString(line.ToArray());
            }
            if (single[0] == 13)
            {
                sawCarriageReturn = true;
            }
            else
            {
                line.WriteByte(single[0]);
            }
        }

        throw new InvalidDataException("Ollama proof chunk line exceeds the size limit.");
    }

    private static async Task<byte[]> ReadExactBytesAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(
                    buffer.AsMemory(offset, length - offset),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Ollama proof request body ended early.");
            offset += read;
        }

        return buffer;
    }

    private static string BuildShowResponse(string body)
    {
        using JsonDocument request = JsonDocument.Parse(body);
        if (request.RootElement.GetProperty("name").GetString() != Model)
            throw new InvalidDataException("Ollama proof server received an unexpected model.");
        return """{"capabilities":["completion"],"model_info":{"proof.context_length":4096}}""";
    }

    private async Task<string> BuildChatResponseAsync(string body)
    {
        using JsonDocument request = JsonDocument.Parse(body);
        if (request.RootElement.GetProperty("model").GetString() != Model)
            throw new InvalidDataException("Ollama proof server received an unexpected chat model.");
        string? prompt = request.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Last()
            .GetProperty("content")
            .GetString();
        if (prompt is not (ExpectedPrompt or CapturedPrompt))
            throw new InvalidDataException("Ollama proof server received an unexpected prompt.");

        Volatile.Write(ref _lastChatBody, body);
        Interlocked.Increment(ref _chatRequestCount);
        if (_pausedChatReached is { } reached &&
            _pausedChatRelease is { } release)
        {
            reached.TrySetResult();
            try
            {
                await release.Task.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            finally
            {
                _pausedChatReached = null;
                _pausedChatRelease = null;
            }
        }
        return
            """{"model":"proof-model","message":{"role":"assistant","content":"WINDOWS_GATEWAY_OLLAMA_OK"},"prompt_eval_count":7,"eval_count":4,"load_duration":1000000,"total_duration":5000000}""";
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
