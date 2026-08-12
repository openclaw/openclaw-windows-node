using System.Text.Json;

namespace OpenClaw.Shared.Codex;

public static class CodexAppServerProtocol
{
    public const string ThreadListMethod = "thread/list";
    public const string ThreadTurnsListMethod = "thread/turns/list";

    internal const string InitializeMethod = "initialize";
    internal const string InitializedMethod = "initialized";

    internal static byte[] CreateInitializeRequest(long id) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method = InitializeMethod,
            @params = new
            {
                clientInfo = new
                {
                    name = "openclaw-windows-node",
                    title = "OpenClaw Windows Node",
                    version = "1",
                },
                capabilities = new
                {
                    experimentalApi = true,
                    requestAttestation = false,
                    mcpServerOpenaiFormElicitation = false,
                },
            },
        });

    internal static byte[] CreateInitializedNotification() =>
        JsonSerializer.SerializeToUtf8Bytes(new { method = InitializedMethod });

    internal static byte[] CreateRequest(long id, string method, JsonElement parameters) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters,
        });

    internal static byte[] CreateServerRequestRefusal(long id) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            error = new
            {
                code = -32601,
                message = "OpenClaw read-only client refuses server requests.",
            },
        });

    internal static CodexAppServerMessage ParseMessage(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException exception)
        {
            throw new CodexAppServerProtocolException("Malformed App Server JSONL message.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CodexAppServerProtocolException("Malformed App Server message: expected an object.");

            var hasId = root.TryGetProperty("id", out var idElement);
            var hasMethod = root.TryGetProperty("method", out var methodElement);
            var hasResult = root.TryGetProperty("result", out var resultElement);
            var hasError = root.TryGetProperty("error", out var errorElement);

            if (hasMethod)
            {
                RejectUnknownFields(
                    root,
                    hasId
                        ? ["id", "method", "params", "trace"]
                        : ["method", "params", "emittedAtMs"]);
                if (methodElement.ValueKind != JsonValueKind.String)
                    throw new CodexAppServerProtocolException("Malformed App Server message: method must be a string.");

                var method = methodElement.GetString()!;
                if (!hasId)
                {
                    if (root.TryGetProperty("emittedAtMs", out var emittedAtMs)
                        && (emittedAtMs.ValueKind != JsonValueKind.Number
                            || !emittedAtMs.TryGetInt64(out _)))
                    {
                        throw new CodexAppServerProtocolException(
                            "Malformed App Server message: emittedAtMs must be an integer.");
                    }

                    return CodexAppServerMessage.ForNotification(method);
                }

                return CodexAppServerMessage.ForServerRequest(ReadNumericId(idElement), method);
            }

            if (!hasId || hasResult == hasError)
                throw new CodexAppServerProtocolException("Malformed App Server response envelope.");

            var id = ReadNumericId(idElement);
            if (hasResult)
            {
                RejectUnknownFields(root, ["id", "result"]);
                return CodexAppServerMessage.ForResult(id, resultElement.Clone());
            }

            RejectUnknownFields(root, ["id", "error"]);
            if (errorElement.ValueKind != JsonValueKind.Object)
                throw new CodexAppServerProtocolException("Malformed App Server error response.");

            RejectUnknownFields(errorElement, ["code", "message", "data"]);
            if (!errorElement.TryGetProperty("code", out var codeElement)
                || !codeElement.TryGetInt64(out var code)
                || !errorElement.TryGetProperty("message", out var errorMessageElement)
                || errorMessageElement.ValueKind != JsonValueKind.String)
            {
                throw new CodexAppServerProtocolException("Malformed App Server error response.");
            }

            return CodexAppServerMessage.ForError(
                id,
                code,
                errorMessageElement.GetString()!,
                errorElement.TryGetProperty("data", out var data) ? data.Clone() : null);
        }
    }

    private static long ReadNumericId(JsonElement id)
    {
        if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var value))
            throw new CodexAppServerProtocolException("App Server response id must be numeric.");
        return value;
    }

    private static void RejectUnknownFields(JsonElement value, IReadOnlyCollection<string> allowed)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new CodexAppServerProtocolException(
                    $"Unknown App Server message field '{property.Name}'.");
        }
    }
}

internal enum CodexAppServerMessageKind
{
    Result,
    Error,
    Notification,
    ServerRequest,
}

internal sealed record CodexAppServerMessage(
    CodexAppServerMessageKind Kind,
    long? Id,
    string? Method,
    JsonElement? Result,
    long? ErrorCode,
    string? ErrorMessage,
    JsonElement? ErrorData)
{
    public static CodexAppServerMessage ForResult(long id, JsonElement result) =>
        new(CodexAppServerMessageKind.Result, id, null, result, null, null, null);

    public static CodexAppServerMessage ForError(
        long id,
        long code,
        string message,
        JsonElement? data) =>
        new(CodexAppServerMessageKind.Error, id, null, null, code, message, data);

    public static CodexAppServerMessage ForNotification(string method) =>
        new(CodexAppServerMessageKind.Notification, null, method, null, null, null, null);

    public static CodexAppServerMessage ForServerRequest(long id, string method) =>
        new(CodexAppServerMessageKind.ServerRequest, id, method, null, null, null, null);
}

public class CodexAppServerException : Exception
{
    public CodexAppServerException(string message)
        : base(message)
    {
    }

    public CodexAppServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CodexAppServerProtocolException : CodexAppServerException
{
    public CodexAppServerProtocolException(string message)
        : base(message)
    {
    }

    public CodexAppServerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CodexAppServerRemoteException : CodexAppServerException
{
    internal CodexAppServerRemoteException(long code, string message, JsonElement? data)
        : base($"Codex App Server request failed ({code}): {message}")
    {
        Code = code;
        DataValue = data;
    }

    public long Code { get; }

    public JsonElement? DataValue { get; }
}

public sealed class CodexAppServerTransportException : CodexAppServerException
{
    internal CodexAppServerTransportException(
        string message,
        bool responseBytesObserved,
        Exception? innerException = null)
        : base(message, innerException ?? new IOException(message))
    {
        ResponseBytesObserved = responseBytesObserved;
    }

    public bool ResponseBytesObserved { get; }
}

public enum CodexAppServerTimeoutKind
{
    Request,
    Idle,
}

public sealed class CodexAppServerTimeoutException : CodexAppServerException
{
    internal CodexAppServerTimeoutException(CodexAppServerTimeoutKind kind)
        : base(kind == CodexAppServerTimeoutKind.Request
            ? "Codex App Server request timed out."
            : "Codex App Server request exceeded the idle timeout.")
    {
        Kind = kind;
    }

    public CodexAppServerTimeoutKind Kind { get; }
}

public sealed class CodexAppServerCleanupException : CodexAppServerException
{
    internal CodexAppServerCleanupException(string message)
        : base(message)
    {
    }

    internal CodexAppServerCleanupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record CodexAppServerLimits
{
    public static CodexAppServerLimits Default { get; } = new(
        maxLineBytes: 1_048_576,
        maxResponseBytes: 1_048_576,
        maxOperationBytes: 8_388_608,
        maxStandardErrorBytes: 16_384,
        requestTimeout: TimeSpan.FromSeconds(20),
        idleTimeout: TimeSpan.FromSeconds(5),
        cleanupTimeout: TimeSpan.FromSeconds(2));

    public static CodexAppServerLimits Catalog { get; } = Default with
    {
        MaxLineBytes = CodexSessionCatalogService.MaxTranscriptPageBytes
            + CodexSessionCatalogService.MaxJsonRpcEnvelopeBytes,
        MaxResponseBytes = CodexSessionCatalogService.MaxTranscriptPageBytes
            + CodexSessionCatalogService.MaxJsonRpcEnvelopeBytes,
        MaxOperationBytes = CodexSessionCatalogService.MaxTranscriptPageBytes
            + CodexSessionCatalogService.MaxJsonRpcEnvelopeBytes
            + CodexSessionCatalogService.MaxCatalogOperationOverheadBytes,
    };

    public CodexAppServerLimits(
        int maxLineBytes,
        int maxResponseBytes,
        int maxOperationBytes,
        int maxStandardErrorBytes,
        TimeSpan requestTimeout,
        TimeSpan idleTimeout,
        TimeSpan cleanupTimeout)
    {
        if (maxLineBytes <= 0
            || maxResponseBytes <= 0
            || maxOperationBytes <= 0
            || maxStandardErrorBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
        }

        if (requestTimeout <= TimeSpan.Zero
            || idleTimeout <= TimeSpan.Zero
            || cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));

        MaxLineBytes = maxLineBytes;
        MaxResponseBytes = maxResponseBytes;
        MaxOperationBytes = maxOperationBytes;
        MaxStandardErrorBytes = maxStandardErrorBytes;
        RequestTimeout = requestTimeout;
        IdleTimeout = idleTimeout;
        CleanupTimeout = cleanupTimeout;
    }

    public int MaxLineBytes { get; init; }

    public int MaxResponseBytes { get; init; }

    public int MaxOperationBytes { get; init; }

    public int MaxStandardErrorBytes { get; init; }

    public TimeSpan RequestTimeout { get; init; }

    public TimeSpan IdleTimeout { get; init; }

    public TimeSpan CleanupTimeout { get; init; }
}
