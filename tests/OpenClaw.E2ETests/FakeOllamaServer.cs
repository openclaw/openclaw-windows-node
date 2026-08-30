using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenClaw.E2ETests;

internal sealed class FakeOllamaServer : IAsyncDisposable
{
    public const string Model = "proof-model";
    public const string ExpectedPrompt = "Reply with exactly: WINDOWS_GATEWAY_OLLAMA_OK";
    public const string ExpectedResponse = "WINDOWS_GATEWAY_OLLAMA_OK";
    private const int OllamaPort = 11_434;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private int _requestCount;
    private int _chatRequestCount;
    private string? _lastChatBody;

    private FakeOllamaServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, OllamaPort);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int RequestCount => Volatile.Read(ref _requestCount);
    public int ChatRequestCount => Volatile.Read(ref _chatRequestCount);
    public string? LastChatBody => Volatile.Read(ref _lastChatBody);

    public static FakeOllamaServer Start() => new();

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using TcpClient client =
                    await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                await HandleClientAsync(client, _shutdown.Token).ConfigureAwait(false);
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
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        string requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Ollama proof server received an empty request.");
        string[] requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2)
            throw new InvalidDataException("Ollama proof server received a malformed request line.");

        int contentLength = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } header &&
               header.Length > 0)
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(header["Content-Length:".Length..].Trim(), out int parsed))
            {
                contentLength = parsed;
            }
        }

        var bodyBuffer = new char[contentLength];
        int bodyRead = 0;
        while (bodyRead < contentLength)
        {
            int read = await reader.ReadAsync(
                    bodyBuffer.AsMemory(bodyRead, contentLength - bodyRead),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Ollama proof request body ended early.");
            bodyRead += read;
        }

        string path = requestParts[1];
        string body = new(bodyBuffer);
        Interlocked.Increment(ref _requestCount);
        string response = path switch
        {
            "/api/tags" =>
                """{"models":[{"name":"proof-model","capabilities":["completion"],"details":{"context_length":4096}}]}""",
            "/api/ps" => """{"models":[]}""",
            "/api/show" => BuildShowResponse(body),
            "/api/chat" => BuildChatResponse(body),
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

    private static string BuildShowResponse(string body)
    {
        using JsonDocument request = JsonDocument.Parse(body);
        if (request.RootElement.GetProperty("name").GetString() != Model)
            throw new InvalidDataException("Ollama proof server received an unexpected model.");
        return """{"capabilities":["completion"],"model_info":{"proof.context_length":4096}}""";
    }

    private string BuildChatResponse(string body)
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
        if (prompt != ExpectedPrompt)
            throw new InvalidDataException("Ollama proof server received an unexpected prompt.");

        Volatile.Write(ref _lastChatBody, body);
        Interlocked.Increment(ref _chatRequestCount);
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
