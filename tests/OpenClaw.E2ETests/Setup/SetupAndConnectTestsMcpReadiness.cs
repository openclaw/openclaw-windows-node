// Fixture-free coverage of the actual poll used by the setup and recovery readiness loop.
using System.Net;

namespace OpenClaw.E2ETests.Setup;

// Keep these regressions in the hosted setup/connect filter without launching its fixture.
public sealed class SetupAndConnectTestsMcpReadiness : IDisposable
{
    private const string Token = "private-test-token";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openclaw-mcp-readiness-{Guid.NewGuid():N}");
    private string TokenPath => Path.Combine(_directory, "mcp-token.txt");

    public SetupAndConnectTestsMcpReadiness() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task MissingToken_ReportsMissingWithoutProbingHttp()
    {
        using var handler = new ProbeHandler(_ => new(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);

        Assert.False(await probe.PollAsync(processExitCode: null));

        Assert.Equal("MCP token file is missing.", probe.LastObservation);
        Assert.Null(probe.Token);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task EmptyToken_ReportsEmptyWithoutProbingHttp(string contents)
    {
        await File.WriteAllTextAsync(TokenPath, contents);
        using var handler = new ProbeHandler(_ => new(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);

        Assert.False(await probe.PollAsync(processExitCode: null));

        Assert.Equal("MCP token file is empty.", probe.LastObservation);
        Assert.Null(probe.Token);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "MCP HTTP readiness returned HTTP 401.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "MCP HTTP readiness returned HTTP 503.")]
    public async Task NonOkStatus_ReportsOnlyNumericStatus(HttpStatusCode status, string expected)
    {
        await File.WriteAllTextAsync(TokenPath, Token);
        using var handler = new ProbeHandler(_ => new(status)
        {
            ReasonPhrase = "private-http-reason",
            Content = new StringContent("private-http-body")
        });
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);

        Assert.False(await probe.PollAsync(processExitCode: null));

        Assert.Equal(expected, probe.LastObservation);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(Token, handler.AuthorizationToken);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(false, "MCP HTTP readiness transport failed (HttpRequestException).")]
    [InlineData(true, "MCP HTTP readiness transport failed (TaskCanceledException).")]
    public async Task TransportFailure_ReportsTypeWithoutExceptionDetails(bool canceled, string expected)
    {
        await File.WriteAllTextAsync(TokenPath, Token);
        using var handler = new ProbeHandler(_ =>
        {
            if (canceled)
                throw new TaskCanceledException("private-cancellation-detail");
            throw new HttpRequestException($"private-error-detail {Token}");
        });
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);

        Assert.False(await probe.PollAsync(processExitCode: null));

        Assert.Equal(expected, probe.LastObservation);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(0, "Tray process exited before MCP server became ready (exit code 0).")]
    [InlineData(23, "Tray process exited before MCP server became ready (exit code 23).")]
    public async Task ExitedProcess_ReportsExitBeforeTokenOrHttpWork(int exitCode, string expected)
    {
        await File.WriteAllTextAsync(TokenPath, Token);
        using var handler = new ProbeHandler(_ => new(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);

        Assert.False(await probe.PollAsync(exitCode));

        Assert.Equal(expected, probe.LastObservation);
        Assert.Null(probe.Token);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TokenReadFailure_DoesNotIncludePrivatePathOrExceptionMessage()
    {
        await File.WriteAllTextAsync(TokenPath, Token);
        using var heldToken = File.Open(TokenPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var handler = new ProbeHandler(_ => new(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);

        Assert.False(await probe.PollAsync(processExitCode: null));

        Assert.Equal("MCP token acquisition failed (IOException).", probe.LastObservation);
        Assert.Null(probe.Token);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DisposedClient_IsNotHiddenAsTransientReadinessFailure()
    {
        await File.WriteAllTextAsync(TokenPath, Token);
        using var handler = new ProbeHandler(_ => new(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);
        http.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => probe.PollAsync(processExitCode: null));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LaterPolls_ReplaceStaleFailuresAndSucceedWithTheAcquiredToken()
    {
        var attempts = 0;
        using var handler = new ProbeHandler(request =>
        {
            Assert.Equal(new Uri("http://127.0.0.1:12345/"), request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            attempts++;
            return attempts switch
            {
                1 => throw new HttpRequestException($"private-error-detail {Token}"),
                2 => new(HttpStatusCode.ServiceUnavailable),
                _ => new(HttpStatusCode.OK)
            };
        });
        using var http = new HttpClient(handler);
        var probe = CreateProbe(http);

        Assert.False(await probe.PollAsync(processExitCode: null));
        Assert.Equal("MCP token file is missing.", probe.LastObservation);
        await File.WriteAllTextAsync(TokenPath, " \r\n");
        Assert.False(await probe.PollAsync(processExitCode: null));
        Assert.Equal("MCP token file is empty.", probe.LastObservation);
        await File.WriteAllTextAsync(TokenPath, $" \r\n{Token}\r\n");

        Assert.False(await probe.PollAsync(processExitCode: null));
        Assert.Equal("MCP HTTP readiness transport failed (HttpRequestException).", probe.LastObservation);
        Assert.Equal(Token, probe.Token);
        await File.WriteAllTextAsync(TokenPath, "replacement-token");
        Assert.False(await probe.PollAsync(processExitCode: null));
        Assert.Equal("MCP HTTP readiness returned HTTP 503.", probe.LastObservation);
        Assert.True(await probe.PollAsync(processExitCode: null));
        Assert.Equal("MCP HTTP readiness returned HTTP 200.", probe.LastObservation);
        Assert.Equal(Token, probe.Token);
        Assert.Equal(3, handler.RequestCount);
    }

    private McpReadinessProbe CreateProbe(HttpClient http) =>
        new(http, TokenPath, new Uri("http://127.0.0.1:12345/"));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class ProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationToken = request.Headers.Authorization?.Parameter;
            return Task.FromResult(respond(request));
        }
    }
}
