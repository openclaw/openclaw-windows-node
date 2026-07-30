using System.Net;

namespace OpenClaw.Shared.Mcp;

internal sealed class McpHttpServerOptions
{
    public static McpHttpServerOptions Default { get; } = new(
        maxConcurrentHandlers: 16,
        handlerAdmissionTimeout: TimeSpan.FromMilliseconds(50),
        requestTimeout: TimeSpan.FromMinutes(6),
        drainTimeout: TimeSpan.FromSeconds(5));

    public McpHttpServerOptions(
        int maxConcurrentHandlers,
        TimeSpan handlerAdmissionTimeout,
        TimeSpan requestTimeout,
        TimeSpan drainTimeout,
        Action? handlerAdmissionStarted = null,
        Func<HttpListenerRequest, long, CancellationToken, Task<string>>? requestBodyReader = null,
        Action? handlerExecutionStarting = null)
    {
        if (maxConcurrentHandlers <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentHandlers));
        if (handlerAdmissionTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(handlerAdmissionTimeout));
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (drainTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));

        MaxConcurrentHandlers = maxConcurrentHandlers;
        HandlerAdmissionTimeout = handlerAdmissionTimeout;
        RequestTimeout = requestTimeout;
        DrainTimeout = drainTimeout;
        HandlerAdmissionStarted = handlerAdmissionStarted;
        RequestBodyReader = requestBodyReader;
        HandlerExecutionStarting = handlerExecutionStarting;
    }

    public int MaxConcurrentHandlers { get; }
    public TimeSpan HandlerAdmissionTimeout { get; }
    public TimeSpan RequestTimeout { get; }
    public TimeSpan DrainTimeout { get; }
    public Action? HandlerAdmissionStarted { get; }
    public Func<HttpListenerRequest, long, CancellationToken, Task<string>>? RequestBodyReader { get; }
    public Action? HandlerExecutionStarting { get; }
}

internal interface IMcpHttpListener
{
    bool IsListening { get; }
    void Start();
    Task<HttpListenerContext> GetContextAsync();
    void Stop();
    void Close();
}

internal sealed class SystemMcpHttpListener : IMcpHttpListener
{
    private readonly HttpListener _listener = new();

    public SystemMcpHttpListener(int port)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public bool IsListening => _listener.IsListening;
    public void Start() => _listener.Start();
    public Task<HttpListenerContext> GetContextAsync() => _listener.GetContextAsync();
    public void Stop() => _listener.Stop();
    public void Close() => _listener.Close();
}
