using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using OpenClawTray.Helpers;
using Xunit.Abstractions;

namespace OpenClaw.Tray.Tests;

public class ChatReadinessClientTests(ITestOutputHelper output)
{
    private const string TokenQuery = "token" + "=" + "test-auth-token";
    private static readonly TimeSpan RedirectFixtureBudget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("http://127.0.0.1:18789/")]
    [InlineData("http://localhost:18789/")]
    [InlineData("http://[::1]:18789/")]
    public void CreateHandler_disables_the_proxy_for_loopback(string url)
    {
        using var handler = ChatReadinessClient.CreateHandler(new Uri(url));
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void CreateHandler_keeps_the_proxy_for_a_remote_url()
    {
        using var handler = ChatReadinessClient.CreateHandler(new Uri("https://gateway.example/"));
        Assert.True(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    [InlineData(299, true)]
    [InlineData(300, true)]
    [InlineData(399, true)]
    [InlineData(400, false)]
    [InlineData(500, false)]
    public void Readiness_preserves_the_original_200_to_399_range(int statusCode, bool expected)
    {
        Assert.Equal(expected, ChatReadinessClient.IsReadyStatusCode((HttpStatusCode)statusCode));
    }

    [Fact]
    public async Task Loopback_probe_sends_the_token_only_to_the_origin_even_with_a_proxy()
    {
        using var origin = StartListener();
        using var proxy = StartListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var url = $"{ListenerUrl(origin)}chat?{TokenQuery}";
        using var handler = ChatReadinessClient.CreateHandler(new Uri(url));
        handler.Proxy = new WebProxy(ListenerUrl(proxy), false);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var received = RespondAsync(origin, "200 OK", null, deadline.Token);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(ChatReadinessClient.IsReadyStatusCode(response.StatusCode));
        Assert.Contains($"GET /chat?{TokenQuery} HTTP/1.1", await received);
        Assert.False(proxy.Pending());
        output.WriteLine("Loopback status: 200. Origin query present: True. Proxy connected: False.");
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirect_is_ready_without_contacting_the_other_host(int statusCode)
    {
        using var origin = StartListener();
        using var destination = StartListener();
        var observation = new RedirectObservation(statusCode);
        using var deadline = new CancellationTokenSource(RedirectFixtureBudget);
        var url = $"{ListenerUrl(origin)}chat?{TokenQuery}";
        var location = $"http://localhost:{((IPEndPoint)destination.LocalEndpoint).Port}/chat?{TokenQuery}";
        var received = RespondAsync(origin, $"{statusCode} Redirect", location, deadline.Token, observation);
        using var http = ChatReadinessClient.Create(url);
        ExceptionDispatchInfo? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            HttpResponseMessage response;
            observation.Record(RedirectPhase.RequestStarted);
            try
            {
                response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                observation.Record(RedirectPhase.ResponseHeadersReceived);
            }
            catch (Exception error)
            {
                observation.Record(error is OperationCanceledException
                    ? RedirectPhase.RequestCanceled : RedirectPhase.RequestFailed, error);
                throw;
            }
            using (response)
            {
                Assert.Equal(statusCode, (int)response.StatusCode);
                Assert.True(ChatReadinessClient.IsReadyStatusCode(response.StatusCode));
                Assert.Contains($"GET /chat?{TokenQuery} HTTP/1.1", await received);
                Assert.False(destination.Pending());
                output.WriteLine($"REDIRECT_STATUS={statusCode} READY=True DESTINATION_CONNECTED=False");
            }
        }
        catch (Exception error) { primary = ExceptionDispatchInfo.Capture(error); }
        finally
        {
            try { cleanupFailure = await FinishRedirectResponderAsync(received, origin, deadline, observation); }
            catch (Exception error) { cleanupFailure = error; }
            try { output.WriteLine("REDIRECT_PHASES=" + observation.ToJson(received.Status, destination.Pending())); }
            catch (Exception error) { cleanupFailure ??= error; }
        }
        // Diagnostic cleanup must never replace the original HTTP or security assertion failure.
        if (primary is not null) primary.Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    [Fact]
    public async Task Remote_https_probe_uses_proxy_connect_without_disclosing_the_token()
    {
        using var proxy = StartListener();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string url = "https://gateway.example/chat?" + TokenQuery;
        using var handler = ChatReadinessClient.CreateHandler(new Uri(url));
        handler.Proxy = new WebProxy(ListenerUrl(proxy), false);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var received = RespondAsync(proxy, "502 Bad Gateway", null, deadline.Token);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token));

        var request = await received;
        Assert.StartsWith("CONNECT gateway.example:443 HTTP/1.1", request);
        Assert.DoesNotContain("token" + "=", request);
        Assert.DoesNotContain("test-auth-token", request);
        output.WriteLine("REMOTE_PROXY_LINE=CONNECT gateway.example:443 HTTP/1.1 PROXY_HAS_TOKEN=False");
    }

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static string ListenerUrl(TcpListener listener) =>
        $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

    private static async Task<string> RespondAsync(
        TcpListener listener, string status, string? location, CancellationToken cancellationToken,
        RedirectObservation? observation = null)
    {
        try
        {
            observation?.Record(RedirectPhase.AcceptPending);
            using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
            observation?.Record(RedirectPhase.Accepted);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
                lines.Add(line);
            observation?.Record(RedirectPhase.RequestHeadersComplete);

            var headers = location is null ? "" : $"Location: {location}\r\n";
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n{headers}Content-Length: 0\r\nConnection: close\r\n\r\n");
            observation?.Record(RedirectPhase.ResponseWriteStarted);
            await stream.WriteAsync(response, cancellationToken);
            observation?.Record(RedirectPhase.ResponseWriteCompleted);
            return string.Join("\r\n", lines);
        }
        catch (Exception error)
        {
            observation?.Record(error is OperationCanceledException
                ? RedirectPhase.ResponderWorkCanceled : RedirectPhase.ResponderWorkFailed, error);
            throw;
        }
    }

    private static async Task<Exception?> FinishRedirectResponderAsync(Task<string> received, TcpListener origin,
        CancellationTokenSource deadline, RedirectObservation observation)
    {
        observation.Record(RedirectPhase.CleanupStarted);
        deadline.Cancel();
        origin.Stop();
        try
        {
            // Cancel owned I/O, then join only within what remains of the original fixture budget.
            var remaining = RedirectFixtureBudget - observation.Elapsed;
            await received.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            observation.Record(RedirectPhase.ResponderSucceeded);
            return null;
        }
        catch (OperationCanceledException) when (received.IsCanceled)
        {
            observation.Record(RedirectPhase.ResponderCanceled);
            return null;
        }
        catch (Exception error)
        {
            // This error belongs to the join, not necessarily to the responder: it can fault
            // after WaitAsync times out. Observe faults unconditionally, including that race.
            observation.Record(RedirectPhase.ResponderJoinFailed, error);
            _ = received.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return error;
        }
    }

    private enum RedirectPhase
    {
        AcceptPending, Accepted, RequestHeadersComplete, ResponseWriteStarted, ResponseWriteCompleted,
        RequestStarted, ResponseHeadersReceived, RequestCanceled, RequestFailed, CleanupStarted,
        ResponderSucceeded, ResponderCanceled, ResponderJoinFailed,
        ResponderWorkCanceled, ResponderWorkFailed,
    }

    // Test-only, fixed-phase observations. No URLs, headers, response content or exception messages.
    private sealed class RedirectObservation(int statusCode)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly object _gate = new();
        private readonly List<(RedirectPhase Phase, double ElapsedMs, string? ErrorType)> _phases = [];
        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

        public void Record(RedirectPhase phase, Exception? error = null)
        {
            lock (_gate) _phases.Add((phase, Elapsed.TotalMilliseconds, error?.GetType().Name));
        }

        public string ToJson(TaskStatus responderStatus, bool? destinationPending = null)
        {
            lock (_gate) return JsonSerializer.Serialize(new
            {
                StatusCode = statusCode,
                ResponderStatus = responderStatus.ToString(),
                DestinationPending = destinationPending,
                Phases = _phases.Select(item => new { Phase = item.Phase.ToString(), item.ElapsedMs, item.ErrorType }).ToArray(),
            });
        }
    }

    [Fact]
    public void Redirect_diagnostics_keep_monotonic_phases_without_exception_content()
    {
        var observation = new RedirectObservation(303);
        observation.Record(RedirectPhase.RequestStarted);
        observation.Record(RedirectPhase.RequestFailed,
            new HttpRequestException("http://example.invalid/chat?" + TokenQuery + " Authorization: private-value"));
        var json = observation.ToJson(TaskStatus.Faulted);
        Assert.DoesNotContain("example.invalid", json);
        Assert.DoesNotContain("test-auth-token", json);
        Assert.DoesNotContain("Authorization", json);
        Assert.DoesNotContain("private-value", json);
        using var document = JsonDocument.Parse(json);
        var phases = document.RootElement.GetProperty("Phases").EnumerateArray().ToArray();
        Assert.Equal(2, phases.Length);
        Assert.Equal("RequestStarted", phases[0].GetProperty("Phase").GetString());
        Assert.Equal("RequestFailed", phases[1].GetProperty("Phase").GetString());
        Assert.Equal("HttpRequestException", phases[1].GetProperty("ErrorType").GetString());
        Assert.True(phases[0].GetProperty("ElapsedMs").GetDouble() >= 0);
        Assert.True(phases[1].GetProperty("ElapsedMs").GetDouble() >= phases[0].GetProperty("ElapsedMs").GetDouble());
    }

    [Fact]
    public async Task Redirect_diagnostics_cancel_and_observe_owned_responder()
    {
        using var origin = StartListener();
        using var deadline = new CancellationTokenSource(RedirectFixtureBudget);
        var observation = new RedirectObservation(303);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = deadline.Token.Register(() => received.TrySetCanceled(deadline.Token));
        Assert.Null(await FinishRedirectResponderAsync(received.Task, origin, deadline, observation));
        Assert.True(deadline.IsCancellationRequested);
        Assert.True(received.Task.IsCanceled);
        Assert.Contains("ResponderCanceled", observation.ToJson(received.Task.Status));
    }

    [Fact]
    public async Task Redirect_diagnostics_return_responder_fault_without_logging_its_message()
    {
        using var origin = StartListener();
        using var deadline = new CancellationTokenSource(RedirectFixtureBudget);
        var observation = new RedirectObservation(303);
        var error = new IOException("private response body");
        var received = Task.FromException<string>(error);
        Assert.Same(error, await FinishRedirectResponderAsync(received, origin, deadline, observation));
        var json = observation.ToJson(received.Status);
        Assert.Contains("ResponderJoinFailed", json);
        Assert.Contains("IOException", json);
        Assert.DoesNotContain("private response body", json);
    }
}
