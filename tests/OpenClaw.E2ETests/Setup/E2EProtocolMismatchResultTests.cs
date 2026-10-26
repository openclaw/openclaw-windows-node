using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

public sealed class E2EProtocolMismatchResultTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"openclaw-protocol-result-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_directory, "openclaw-tray.log");
    private string ResultPath => Path.Combine(_directory, "protocol-result.json");

    public E2EProtocolMismatchResultTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(null, false, "app.status.nodeError")]
    [InlineData("[DEBUG] [NODE RX] ", true, "openclaw-tray.log.node_rx_error_code")]
    [InlineData("[INFO] [HANDSHAKE] Raw error response: ", true, "openclaw-tray.log.operator_handshake_error_code")]
    [InlineData("[DEBUG] [NODE RX] ", false, "openclaw-tray.log.node_rx_error_code")]
    [InlineData("[INFO] [HANDSHAKE] Raw error response: ", false, "openclaw-tray.log.operator_handshake_error_code")]
    public void ExactAppError_WritesReceiptAndThrowsAttributableFailure(
        string? logPrefix, bool useDetailCode, string expectedSource)
    {
        using var status = JsonDocument.Parse(logPrefix is null
            ? """{"nodeError":"PROTOCOL_MISMATCH"}"""
            : """{"nodeError":"Gateway protocol is incompatible"}""");
        if (logPrefix is not null)
        {
            // Current Gateway connect-admission emits INVALID_REQUEST with the
            // authoritative protocol code in details, not in the top-level code.
            var frame = useDetailCode
                ? """{"type":"res","id":"connect-1","ok":false,"error":{"code":"INVALID_REQUEST","message":"protocol mismatch","details":{"code":"PROTOCOL_MISMATCH","clientMinProtocol":3,"clientMaxProtocol":3,"expectedProtocol":4,"minimumProbeProtocol":3}}}"""
                : """{"type":"res","id":"connect-1","ok":false,"error":{"code":"PROTOCOL_MISMATCH","message":"connect rejected"}}""";
            File.WriteAllText(LogPath,
                $"[2026-10-22 12:00:00.000] {logPrefix}{frame}");
        }

        var error = Assert.Throws<E2EProtocolMismatchException>(() =>
            E2EProtocolMismatchResult.ThrowIfDetected(status.RootElement, LogPath, ResultPath));

        Assert.Equal("OPENCLAW_E2E_PROTOCOL_MISMATCH: fixture_initialization", error.Message);
        using var receipt = JsonDocument.Parse(File.ReadAllText(ResultPath));
        var root = receipt.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("protocol_mismatch", root.GetProperty("outcome").GetString());
        Assert.Equal("fixture_initialization", root.GetProperty("phase").GetString());
        Assert.Equal(expectedSource, root.GetProperty("source").GetString());
        Assert.Equal("PROTOCOL_MISMATCH", root.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{}", "")]
    [InlineData("{\"nodeError\":\"protocol_mismatch\"}", "")]
    [InlineData("{\"nodeError\":\"PROTOCOL_MISMATCH while reconnecting\"}", "")]
    [InlineData("{\"nodeError\":{\"code\":\"PROTOCOL_MISMATCH\"}}", "")]
    [InlineData("{}", "[INFO] [NODE] Gateway protocol mismatch: PROTOCOL_MISMATCH")]
    [InlineData("{}", "[INFO] quoted [DEBUG] [NODE RX] {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[DEBUG] [NODE RX] {\"type\":\"event\",\"ok\":false,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[DEBUG] [NODE RX] {\"type\":\"res\",\"ok\":true,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[DEBUG] [NODE RX] {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"AUTH_TOKEN_MISMATCH\",\"message\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[DEBUG] [NODE RX] {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"protocol_mismatch\"}}")]
    [InlineData("{}", "[DEBUG] [NODE RX] {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"")]
    [InlineData("{}", "[WARN] [HANDSHAKE] Raw error response: {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[INFO] quoted [HANDSHAKE] Raw error response: {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[INFO] [HANDSHAKE] Raw error response: {\"type\":\"res\",\"ok\":true,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[INFO] [HANDSHAKE] Raw error response: {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"AUTH_TOKEN_MISMATCH\",\"message\":\"PROTOCOL_MISMATCH\"}}")]
    [InlineData("{}", "[INFO] [HANDSHAKE] Raw error response: {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"PROTOCOL_MISMATCH\"...")]
    [InlineData("{}", "[INFO] [HANDSHAKE] Raw error response: {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"INVALID_REQUEST\",\"details\":{\"code\":\"AUTH_TOKEN_MISMATCH\",\"message\":\"PROTOCOL_MISMATCH\"}}}")]
    [InlineData("{}", "[INFO] [HANDSHAKE] Raw error response: {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"INVALID_REQUEST\",\"details\":{\"code\":\"protocol_mismatch\"}}}")]
    [InlineData("{}", "[DEBUG] [NODE RX] {\"type\":\"res\",\"ok\":false,\"error\":{\"code\":\"INVALID_REQUEST\",\"details\":\"PROTOCOL_MISMATCH\"}}")]
    public void UnrelatedOrUnstructuredError_DoesNotWriteReceipt(string statusJson, string logMessage)
    {
        using var status = JsonDocument.Parse(statusJson);
        File.WriteAllText(LogPath, $"[2026-10-22 12:00:00.000] {logMessage}");

        E2EProtocolMismatchResult.ThrowIfDetected(status.RootElement, LogPath, ResultPath);

        Assert.False(File.Exists(ResultPath));
    }

    [Fact]
    public void ReceiptWriteFailure_IsNotAProtocolMismatchFailure()
    {
        using var status = JsonDocument.Parse("""{"nodeError":"PROTOCOL_MISMATCH"}""");

        Assert.Throws<DirectoryNotFoundException>(() => E2EProtocolMismatchResult.ThrowIfDetected(
            status.RootElement, LogPath, Path.Combine(_directory, "missing", "result.json")));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
