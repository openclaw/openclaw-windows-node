using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClaw.Tray.IntegrationTests;

/// <summary>
/// Tiny JSON-RPC over HTTP client for the MCP endpoint. Exposes the two methods
/// these tests need (tools/list, tools/call) plus a generic Send for anything
/// else. Each call is a fresh POST — the MCP HTTP transport is request/response,
/// not session-based.
/// </summary>
public sealed class McpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private int _id;

    public McpClient(string endpoint) : this(endpoint, authToken: null) { }

    public McpClient(string endpoint, string? authToken)
    {
        _endpoint = endpoint;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(authToken))
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);
        }
    }

    public Task<JsonDocument> InitializeAsync()
        => SendAsync("initialize", parameters: null);

    public Task<JsonDocument> ListToolsAsync()
        => SendAsync("tools/list", parameters: null);

    /// <summary>
    /// Call a tool. Returns the parsed JSON-RPC response. Caller checks
    /// result.isError and result.content[0].text per MCP spec.
    /// </summary>
    public Task<JsonDocument> CallToolAsync(string name, object? arguments = null)
    {
        var parameters = arguments is null
            ? (object)new { name }
            : new { name, arguments };
        return SendAsync("tools/call", parameters);
    }

    public async Task<JsonDocument> SendAsync(string method, object? parameters)
    {
        var id = System.Threading.Interlocked.Increment(ref _id);
        var requestObj = parameters is null
            ? (object)new { jsonrpc = "2.0", id, method }
            : new { jsonrpc = "2.0", id, method, @params = parameters };
        var requestJson = JsonSerializer.Serialize(requestObj);

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_endpoint, content).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MCP HTTP {(int)resp.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Convenience: call a tool, assert the JSON-RPC envelope is well-formed,
    /// and return result.content[0].text parsed as JSON. Throws if the tool
    /// returned isError=true.
    /// </summary>
    public async Task<JsonDocument> CallToolExpectSuccessAsync(string name, object? arguments = null)
    {
        using var doc = await CallToolAsync(name, arguments).ConfigureAwait(false);
        var result = doc.RootElement.GetProperty("result");
        var isError = result.GetProperty("isError").GetBoolean();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        if (isError)
        {
            throw new InvalidOperationException($"Tool {name} returned isError=true: {text}");
        }
        return JsonDocument.Parse(text!);
    }

    /// <summary>
    /// Call a tool and return a tuple of (isError, payloadText). Use when the
    /// test should accept either success or a tool error (e.g. hardware-gated
    /// commands like canvas.eval without an active canvas window).
    /// </summary>
    public async Task<(bool IsError, string Text)> CallToolAcceptingFailureAsync(string name, object? arguments = null)
    {
        using var doc = await CallToolAsync(name, arguments).ConfigureAwait(false);
        var result = doc.RootElement.GetProperty("result");
        var isError = result.GetProperty("isError").GetBoolean();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        return (isError, text);
    }

    public void Dispose() => _http.Dispose();
}
