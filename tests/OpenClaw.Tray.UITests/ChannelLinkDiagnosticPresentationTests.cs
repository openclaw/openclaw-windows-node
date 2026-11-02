using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using OpenClawTray.Pages;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ChannelLinkDiagnosticPresentationTests(UIThreadFixture ui)
{
    // A synthetic one-pixel PNG, not a pairing credential.
    private const string ImageUrl = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedQrWait_OmitsPairingImageFromSelectableDiagnosticAndKeepsPicture(bool force)
    {
        await ui.RunOnUIAsync(async () =>
        {
            var client = DispatchProxy.Create<IOperatorGatewayClient, LinkingGateway>();
            var gateway = (LinkingGateway)client;
            var page = new ChannelsPage();
            var build = typeof(ChannelsPage).GetMethod("BuildLinkingPlaceholder",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var surface = Assert.IsType<StackPanel>(build.Invoke(page,
                [new ChannelRecord { Id = "whatsapp" }]));
            var picture = Assert.Single(surface.Children.OfType<Image>());
            var message = surface.Children.OfType<TextBlock>().Last();
            var disclosure = Assert.Single(surface.Children.OfType<Expander>());
            var diagnostic = Assert.IsType<TextBlock>(disclosure.Content);
            var recovery = Assert.Single(surface.Children.OfType<StackPanel>(),
                panel => panel.Visibility == Visibility.Collapsed);
            ui.Container.Children.Add(surface);
            try
            {
                var linking = typeof(ChannelsPage).GetMethod("StartLinkingAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                await Assert.IsAssignableFrom<Task>(linking.Invoke(page,
                    [client, picture, message, disclosure, diagnostic, recovery, "whatsapp", force]));

                Assert.Equal(force, gateway.Force);
                Assert.True(gateway.WaitImage == ImageUrl, "The wait request must retain the synthetic QR.");
                Assert.Equal(30000, gateway.WaitTimeout);
                Assert.Equal(Visibility.Visible, picture.Visibility);
                Assert.IsType<BitmapImage>(picture.Source);
                Assert.Equal("Link wait failed for whatsapp. See details below.", message.Text);
                Assert.Equal(Visibility.Visible, disclosure.Visibility);
                Assert.True(diagnostic.IsTextSelectionEnabled);
                Assert.Contains("Method:   web.login.wait", diagnostic.Text);
                Assert.Contains("Channel:  whatsapp", diagnostic.Text);
                Assert.Contains("\"timeoutMs\":30000", diagnostic.Text);
                Assert.Contains("synthetic wait failure", diagnostic.Text);
                Assert.False(diagnostic.Text.Contains("data:image", StringComparison.OrdinalIgnoreCase),
                    "Selectable diagnostics must omit the image URL.");
                Assert.False(diagnostic.Text.Contains("iVBORw0KGgo", StringComparison.Ordinal),
                    "Selectable diagnostics must omit even a truncated image payload.");
                Assert.Contains("<omitted>", diagnostic.Text);
                disclosure.IsExpanded = true;
                await ui.PauseAsync("Synthetic QR wait failure: image omitted from selectable text");
            }
            finally
            {
                ui.Container.Children.Clear();
            }
        });
    }

    public class LinkingGateway : DispatchProxy
    {
        public bool? Force { get; private set; }
        public string? WaitImage { get; private set; }
        public int? WaitTimeout { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IOperatorGatewayClient.WebLoginStartAsync):
                    Force = (bool)args![0]!;
                    return Task.FromResult<WebLoginStartResult?>(new() { QrDataUrl = ImageUrl });
                case nameof(IOperatorGatewayClient.WebLoginWaitAsync):
                    WaitImage = (string?)args![0];
                    WaitTimeout = (int)args[1]!;
                    return Task.FromResult<WebLoginWaitResult?>(new()
                    {
                        Error = "synthetic wait failure " + ImageUrl,
                        RawResponse = "{\"qrDataUrl\":\"" + ImageUrl + "\",\"error\":\"synthetic wait failure\"}",
                    });
                default:
                    throw new InvalidOperationException($"Unexpected linking call: {targetMethod?.Name}");
            }
        }
    }
}
