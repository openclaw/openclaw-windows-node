// Guards the remaining WinUI-only URL display boundaries until mounted-control tests cover them.
// Run with: dotnet test --filter FullyQualifiedName~GatewayUrlDisplayWiringTests
using OpenClaw.Connection;
using OpenClaw.Shared;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

/// <summary>Display wiring and input preservation for saved and decoded Gateway URLs.</summary>
public sealed class GatewayUrlDisplayWiringTests
{
    private const string RawUrl = "wss://user:synthetic-password@gateway.example:18789/proxy/chat?token=synthetic-query#synthetic-fragment";
    private const string DisplayUrl = "wss://gateway.example:18789/proxy/chat";

    [Fact]
    public void SavedGatewayDisplay_DoesNotReplaceTheOriginalRecordOrCredentials()
    {
        var record = new GatewayRecord
        {
            Url = RawUrl,
            SharedGatewayToken = "synthetic-shared-token",
            BootstrapToken = "synthetic-bootstrap-token",
        };

        Assert.Equal(DisplayUrl, GatewayUrlHelper.SanitizeForDisplay(record.Url));
        Assert.Equal(RawUrl, record.Url);
        Assert.Equal("synthetic-shared-token", record.SharedGatewayToken);
        Assert.Equal("synthetic-bootstrap-token", record.BootstrapToken);

        // Retirement: replace this wiring guard when a mounted Connection Status test
        // checks the rendered saved row and rereads its isolated registry record.
        var source = ReadSource("Windows", "ConnectionStatusWindow.xaml.cs");
        Assert.Contains("Text = $\"{statusIcon} {GatewayUrlHelper.SanitizeForDisplay(gw.Url)}\"", source);
        Assert.DoesNotContain("Text = $\"{statusIcon} {gw.Url}\"", source);
    }

    [Fact]
    public void SetupPreviews_SanitizeDecodedUrlWithoutChangingConnectionInputOrTokenHint()
    {
        var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            url = RawUrl,
            bootstrapToken = "synthetic-bootstrap-token",
        })));
        var decoded = SetupCodeDecoder.Decode(code);

        Assert.True(decoded.Success);
        Assert.Equal(DisplayUrl, GatewayUrlHelper.SanitizeForDisplay(decoded.Url));
        Assert.Equal(RawUrl, decoded.Url);
        Assert.Equal("synthetic-bootstrap-token", decoded.Token);

        // Retirement: replace these guards when mounted tests drive both TextChanged
        // handlers, assert their preview and token hint, and observe the original target.
        var status = ReadSource("Windows", "ConnectionStatusWindow.xaml.cs");
        var page = ReadSource("Pages", "ConnectionPage.xaml.cs");
        Assert.Contains("SetupCodePreview.Text = $\"→ {(decoded.Url is null ? \"?\" : GatewayUrlHelper.SanitizeForDisplay(decoded.Url))}", status);
        Assert.Contains("decoded.Token?[..Math.Min(8, decoded.Token?.Length ?? 0)]", status);
        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(decoded.Url)", page);
        Assert.Contains("AddSetupCodePreviewUrl.Text = string.Format(LocalizationHelper.GetString(\"ConnectionPage_PreviewGateway\"), previewUrl)", page);
        Assert.Contains("decoded.Token!.Substring(0, Math.Min(8, decoded.Token.Length))", page);
        Assert.Contains("ScheduleConnectivityTest(decoded.Url)", page);
        Assert.DoesNotContain("ConnectionPage_PreviewGateway\"), decoded.Url", page);
    }

    [Fact]
    public void ChatConnectionFailure_SanitizesOnlyErrorTextAndKeepsActiveBuilderDelegation()
    {
        // Retirement: a mounted WebView navigation-failure test checks the visible error
        // and target. Invalid-URL behavior is covered through GatewayChatUrlBuilder.
        var source = ReadSource("Pages", "ChatPage.xaml.cs");
        Assert.Contains("GatewayChatHelper.TryBuildChatUrl(credential.GatewayUrl, credential.Token", source);
        Assert.Contains("GatewayUrlHelper.SanitizeForDisplay(credential.GatewayUrl)", source);
        Assert.DoesNotContain("ChatPage_CannotConnectToGateway\"), credential.GatewayUrl)", source);
    }

    private static string ReadSource(string directory, string filename) => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", directory, filename));
}
