using OpenClaw.SetupEngine.UI;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests for <see cref="WizardConsoleTail.TryExtractConsoleMessage"/>, the
/// JSON-line filter used by the console-tail mitigation. Only lines emitted by
/// the root "openclaw" logger via console.log should be surfaced; everything
/// else is noise.
/// </summary>
public class WizardConsoleTailTests
{
    [Fact]
    public void ExtractsOAuthUrlFromUpstreamConsoleLogEntry()
    {
        // Verbatim shape of the gateway-side line that carries the OAuth URL
        // for the OpenAI Codex Browser path.
        var line = """{"0":"discarded","_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"\nOpen this URL in your LOCAL browser:\n\nhttps://auth.openai.com/oauth/authorize?response_type=code&client_id=app_EMoamEEZ73f0CkXaXp7hrann"}""";

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.NotNull(extracted);
        Assert.Contains("https://auth.openai.com/oauth/authorize", extracted);
    }

    [Fact]
    public void ExtractsCodexVersionFallbackMessage()
    {
        // Silent fallback during npm install of @openclaw/codex.
        var line = """{"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"Resolved @openclaw/codex to @openclaw/codex@2026.6.1, but that version is incompatible with this OpenClaw runtime; using newest compatible @openclaw/codex@2026.5.28"}""";

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.NotNull(extracted);
        Assert.Contains("incompatible", extracted);
    }

    [Fact]
    public void StripsAnsiSequencesFromQrConsoleOutput()
    {
        var line = """
            {"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"\u001b[47m\u001b[30m██  ▄▄  ██\u001b[0m"}
            """;

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.Equal("██  ▄▄  ██", extracted);
    }

    [Fact]
    public void PreservesUtf8QrBlockCharacters()
    {
        var line = """
            {"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"Open WhatsApp and scan:\n████ ▄▄ ████"}
            """;

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.NotNull(extracted);
        Assert.Contains("████ ▄▄ ████", extracted);
    }

    [Fact]
    public void DetectsTerminalQrArt()
    {
        var qr = string.Join('\n', Enumerable.Repeat(" ███████  ▄▄▄  ▄▄▄      ▄  ▄  ▄▄   ▄    ▄  ▄▄   ▄▄▄ ", 12));

        Assert.True(WizardConsoleTail.LooksLikeTerminalQrArt(qr));
    }

    [Fact]
    public void DoesNotTreatRegularMultilineConsoleOutputAsQrArt()
    {
        var message = """
            Waiting for WhatsApp connection...
            Open the WhatsApp app, go to Linked Devices, then scan this QR:
            Docs: https://docs.openclaw.ai/whatsapp
            """;

        Assert.False(WizardConsoleTail.LooksLikeTerminalQrArt(message));
    }

    [Fact]
    public void IgnoresStructuredSubsystemLogs()
    {
        // openclaw/auth, openclaw/ws, gateway/ws etc. write structured records
        // via Logger.info(); they go through the same log file but have a
        // different _meta.path.method (not console.log) and different name.
        var line = """{"_meta":{"name":"openclaw/auth","logLevelName":"INFO","path":{"method":"info"}},"message":"device token rotated"}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresNonOpenclawNamedConsoleLog()
    {
        // Defense in depth: only the root openclaw logger should surface to the
        // wizard banner. A console.log from some other subsystem stays internal.
        var line = """{"_meta":{"name":"openclaw/ws","logLevelName":"INFO","path":{"method":"console.log"}},"message":"internal noise"}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresMalformedJson()
    {
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage("{not json at all"));
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage("plain text line"));
    }

    [Fact]
    public void IgnoresNullEmptyOrWhitespace()
    {
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(null));
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(""));
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage("   "));
    }

    [Fact]
    public void IgnoresLineWithoutMessageField()
    {
        var line = """{"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}}}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresLineWithEmptyMessage()
    {
        var line = """{"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"   "}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresLineMissingMeta()
    {
        // A log file from a different process or a corrupted line should never
        // crash the filter.
        var line = """{"message":"console.log without _meta"}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void CheapRejectionFastPathStillAcceptsValidLines()
    {
        // Sanity: the fast-path string check looks for "console.log". Ensure a
        // valid line passes it.
        var line = """{"_meta":{"name":"openclaw","logLevelName":"WARN","path":{"method":"console.log"}},"message":"install failed: npm ENOSPC"}""";

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.Equal("install failed: npm ENOSPC", extracted);
    }
}
