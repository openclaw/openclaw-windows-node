using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenClawTray.Helpers;
using Xunit.Abstractions;

namespace OpenClaw.Tray.Tests;

public class ChatReadinessClientTests(ITestOutputHelper output)
{
    private const string TokenQuery = "token" + "=" + "test-auth-token";

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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var url = $"{ListenerUrl(origin)}chat?{TokenQuery}";
        var location = $"http://localhost:{((IPEndPoint)destination.LocalEndpoint).Port}/chat?{TokenQuery}";
        var received = RespondAsync(origin, $"{statusCode} Redirect", location, deadline.Token);
        using var http = ChatReadinessClient.Create(url);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

        Assert.Equal(statusCode, (int)response.StatusCode);
        Assert.True(ChatReadinessClient.IsReadyStatusCode(response.StatusCode));
        Assert.Contains($"GET /chat?{TokenQuery} HTTP/1.1", await received);
        Assert.False(destination.Pending());
        output.WriteLine($"REDIRECT_STATUS={statusCode} READY=True DESTINATION_CONNECTED=False");
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
        TcpListener listener, string status, string? location, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
            lines.Add(line);

        var headers = location is null ? "" : $"Location: {location}\r\n";
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n{headers}Content-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        return string.Join("\r\n", lines);
    }
}
