using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// JSON-RPC 2.0 request envelope. <c>params</c> and <c>id</c> are the only optional
/// members we parse — <c>jsonrpc</c> is required to be the string "2.0" per spec.
/// Ids may be a string, number, or null; we round-trip the raw token so the response
/// carries the same type back.
/// </summary>
internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
}

internal sealed class JsonRpcError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("data")] public object? Data { get; set; }
}

internal sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }
}

internal static class JsonRpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    // Application-defined codes use the -32000..-32099 range.
    public const int ToolExecution = -32000;
}
