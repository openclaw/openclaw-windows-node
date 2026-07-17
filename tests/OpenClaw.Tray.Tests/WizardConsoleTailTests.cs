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
    public void NativeTailReadsWindowsTempLogsWithoutLaunchingWsl()
    {
        var startInfo = WizardConsoleTail.BuildStartInfo(
            nativeWindows: true,
            distroName: "PreservedWslDistro",
            nativeLogPath: @"C:\Users\me\.openclaw-companion\logs\wizard-console.log");

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.Equal(
            @"C:\Users\me\.openclaw-companion\logs\wizard-console.log",
            startInfo.Environment["OPENCLAW_SETUP_WIZARD_LOG"]);
        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.Contains("PreservedWslDistro", StringComparison.Ordinal));
        var command = startInfo.ArgumentList[^1];
        Assert.Contains("Get-Item -LiteralPath $logPath", command);
        Assert.Contains("CreationTimeUtc.Ticks", command);
        Assert.Contains("$latest.Length -lt $position", command);
        Assert.Contains("[IO.File]::Open", command);
        Assert.Contains("[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)", command);
        Assert.Contains("} catch {", command);
        Assert.DoesNotContain("Get-Content", command);
    }

    [Fact]
    public void NativeTailRequiresManagedProfileLogPath()
    {
        Assert.Throws<ArgumentException>(() => WizardConsoleTail.BuildStartInfo(
            nativeWindows: true,
            distroName: "OpenClawGateway"));
    }

    [Fact]
    public async Task NativeTailReadsOnlyManagedProfileAndFollowsRecreation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"wizard-tail-{Guid.NewGuid():N}");
        var managedLog = Path.Combine(directory, "managed", "wizard-console.log");
        var unrelatedLog = Path.Combine(directory, "unrelated.log");
        Directory.CreateDirectory(Path.GetDirectoryName(managedLog)!);
        await File.WriteAllTextAsync(managedLog, "managed-history\n");
        await File.WriteAllTextAsync(unrelatedLog, "unrelated-history\n");
        using var process = new System.Diagnostics.Process
        {
            StartInfo = WizardConsoleTail.BuildStartInfo(
                nativeWindows: true,
                distroName: "PreservedWslDistro",
                nativeLogPath: managedLog),
        };

        try
        {
            Assert.True(process.Start());
            await Task.Delay(750);
            await File.AppendAllTextAsync(unrelatedLog, "unrelated-secret\n");
            await File.AppendAllTextAsync(managedLog, "managed-one\n");

            Assert.Equal(
                "managed-one",
                await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

            await File.AppendAllTextAsync(managedLog, "managed-");
            await Task.Delay(750);
            await File.AppendAllTextAsync(managedLog, "partial\n");
            Assert.Equal(
                "managed-partial",
                await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));

            File.Move(managedLog, managedLog + ".1");
            await File.WriteAllTextAsync(managedLog, "managed-two\n");
            Assert.Equal(
                "managed-two",
                await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WslTailTargetsSelectedDistro()
    {
        var startInfo = WizardConsoleTail.BuildStartInfo(
            nativeWindows: false,
            distroName: "OpenClawGateway-Test");

        Assert.Equal("wsl.exe", startInfo.FileName);
        Assert.Contains("OpenClawGateway-Test", startInfo.ArgumentList);
        Assert.Contains(startInfo.ArgumentList, argument => argument.Contains("tail -F", StringComparison.Ordinal));
    }

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
    public void DetectsTerminalQrArtWithSideBlockGlyphs()
    {
        var qr = string.Join('\n', Enumerable.Repeat("▌██  ▐▌ ▄▄ ▐▌ ██▐▌  ▀▀ ▐▌", 8));

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
