using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public class TrayMenuWindowMarkupTests
{
    [Fact]
    public void CanvasWindow_BridgeValidatesOriginAndPostsOnDispatcher()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Windows", "CanvasWindow.xaml.cs");

        Assert.Contains("BridgeMessageReceived", source);
        Assert.Contains("IsTrustedBridgeSource(e.Source)", source);
        Assert.Contains("openclaw-canvas.local", source);
        Assert.Contains("DispatcherQueue", source);
        Assert.Contains("TryEnqueue(() => PostBridgeMessageOnUiThread", source);
        Assert.Contains("PostWebMessageAsJson(json)", source);
        Assert.Contains("SanitizeBridgeLogValue", source);
        Assert.Contains("WebMessageReceived -= _webMessageReceivedHandler", source);
    }

    [Fact]
    public void CanvasWindow_CleansUpGatewayAuthWebResourceHandler()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Windows", "CanvasWindow.xaml.cs");

        Assert.Contains("_webResourceRequestedHandler = OnGatewayWebResourceRequested", source);
        Assert.Contains("WebResourceRequested += _webResourceRequestedHandler", source);
        Assert.Contains("WebResourceRequested -= _webResourceRequestedHandler", source);
        Assert.Contains("RemoveWebResourceRequestedFilter", source);
        Assert.Contains("_gatewayToken = null", source);
        Assert.Contains("_trustedGatewayOrigin = null", source);
        Assert.Contains("IsUriForOrigin(args.Request.Uri, trustedOrigin)", source);
        Assert.DoesNotContain("WebResourceRequested += (", source);
    }

    [Fact]
    public void CanvasWindow_RewritesOnlyGatewayUrls()
    {
        var source = Read(
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "CanvasWindow.xaml.cs");

        Assert.Contains("_configuredGatewayOrigin", source);
        Assert.Contains("CanvasGatewayUrlRewriter.Rewrite(url", source);
        Assert.DoesNotContain("if (!urlOrigin.Equals(_gatewayOriginForRewrite", source);
    }

    [Fact]
    public void CanvasNavigate_UsesCanvasWindow()
    {
        var source = Read(
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "NodeService.cs");

        Assert.Contains("request inside the WebView canvas", source);
        Assert.Contains("HttpUrlRiskEvaluator.Evaluate(canonical!)", source);
        Assert.Contains("EnrichWithDnsRiskAsync", source);
        Assert.Contains("risk.RequiresConfirmation", source);
        Assert.Contains("return \"unsupported_in_canvas\"", source);
        Assert.DoesNotContain("ShouldLaunchAfterPromptAsync(risk)", source);
        Assert.Contains("_canvasWindow.Navigate(canonical!)", source);
        Assert.Contains("tcs.TrySetResult(\"canvas\")", source);
        Assert.Contains("Canvas navigate -> canvas", source);
    }

    [Fact]
    public void CanvasGatewayOrigin_ComesFromActiveGatewayRecord()
    {
        var appSource = Read(
            "src",
            "OpenClaw.Tray.WinUI",
            "App.xaml.cs");
        var nodeServiceSource = Read(
            "src",
            "OpenClaw.Tray.WinUI",
            "Services",
            "NodeService.cs");

        Assert.Contains("activeGatewayUrlResolver: () => _gatewayRegistry?.GetActive()?.Url", appSource);
        Assert.Contains("private string? GetConfiguredGatewayUrl() => _activeGatewayUrlResolver?.Invoke();", nodeServiceSource);
        Assert.DoesNotContain("private string? GetConfiguredGatewayUrl() => _settings?.UseSshTunnel", nodeServiceSource);
    }

    [Fact]
    public void Source_DoesNotDeclareAsyncVoidHandlers()
    {
        var sourceRoot = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file =>
            {
                var source = File.ReadAllText(file);
                return Regex.Matches(
                        source,
                        @"(?m)^\s*(?:(?:public|private|protected|internal)\s+)?(?:override\s+)?async\s+void\s+[A-Za-z_][A-Za-z0-9_]*\s*\(")
                    .Select(match => $"{Path.GetRelativePath(sourceRoot, file)}:{LineNumber(source, match.Index)}");
            })
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Async event handlers must delegate through AsyncEventHandlerGuard instead of declaring async void: " +
            string.Join(", ", offenders));
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));

    private static int LineNumber(string source, int index) =>
        source.AsSpan(0, index).Count('\n') + 1;
}
