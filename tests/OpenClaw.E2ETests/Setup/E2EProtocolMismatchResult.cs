using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

internal sealed class E2EProtocolMismatchException() : Exception(
    "OPENCLAW_E2E_PROTOCOL_MISMATCH: fixture_initialization");

internal static class E2EProtocolMismatchResult
{
    internal static void ThrowIfDetected(JsonElement status, string logPath, string resultPath)
    {
        var source = HasString(status, "nodeError", "PROTOCOL_MISMATCH")
            ? "app.status.nodeError"
            : ReadProtocolMismatchSource(logPath);
        if (source is null)
            return;

        // The marker is thrown only after writing a receipt from the app under
        // test. Workflow admission also checks each TRX failure for this marker.
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            outcome = "protocol_mismatch",
            phase = "fixture_initialization",
            source,
            code = "PROTOCOL_MISMATCH"
        }));
        throw new E2EProtocolMismatchException();
    }

    private static string? ReadProtocolMismatchSource(string logPath)
    {
        if (!File.Exists(logPath))
            return null;

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            // Logger's prefix is [timestamp] [level]. The operator-only raw error
            // prefix is emitted for connect, before the node connection can start.
            // Quoted prefixes and human-readable mismatch messages are not evidence.
            const string nodePrefix = "[DEBUG] [NODE RX] ";
            const string operatorPrefix = "[INFO] [HANDSHAKE] Raw error response: ";
            var timestampEnd = line.IndexOf("] ", StringComparison.Ordinal);
            if (!line.StartsWith('[') || timestampEnd < 0)
                continue;
            var message = line.AsSpan(timestampEnd + 2);
            var prefix = message.StartsWith(nodePrefix, StringComparison.Ordinal)
                ? nodePrefix
                : message.StartsWith(operatorPrefix, StringComparison.Ordinal)
                    ? operatorPrefix
                    : null;
            if (prefix is null)
                continue;

            try
            {
                using var document = JsonDocument.Parse(line[(timestampEnd + 2 + prefix.Length)..]);
                var frame = document.RootElement;
                if (HasString(frame, "type", "res") &&
                    frame.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False &&
                    frame.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    (HasString(error, "code", "PROTOCOL_MISMATCH") ||
                     error.TryGetProperty("details", out var details) &&
                     HasString(details, "code", "PROTOCOL_MISMATCH")))
                    return prefix == nodePrefix
                        ? "openclaw-tray.log.node_rx_error_code"
                        : "openclaw-tray.log.operator_handshake_error_code";
            }
            catch (JsonException)
            {
                // A concurrent logger write may leave the last line incomplete.
                // Malformed frames are never evidence of protocol incompatibility.
            }
        }

        return null;
    }

    private static bool HasString(JsonElement value, string property, string expected) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var actual) &&
        actual.ValueKind == JsonValueKind.String &&
        actual.GetString() == expected;
}
