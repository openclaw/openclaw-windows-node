using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ChannelPairing_ReviewCancelApproveAndDismissUseNativeConfirmedAccountContext()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "channels" });
            await WaitUiAsync(run, () => FindById(run, "ChannelPairingExpander") is not null, "native DM-access section");
            var expander = FindById(run, "ChannelPairingExpander")!;
            ((ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindById(run, "ChannelPairingApprove:0") is { Current.IsOffscreen: false, Current.IsEnabled: true },
                "visible pending sender approval");
            Assert.Contains("sender-approve", FindPairingIdentity(run, "approve").Current.Name, StringComparison.Ordinal);
            Assert.Contains("work", FindPairingIdentity(run, "approve").Current.Name, StringComparison.Ordinal);
            OwnedWindowCapture.Save(run, FindPairingWindow(run), "channels-pairing-pending.png", expander);

            Invoke(FindById(run, "ChannelPairingApprove:0")!);
            await WaitUiAsync(run, () => FindById(run, "ChannelPairingConfirmation") is not null, "approval confirmation");
            var dialog = FindById(run, "ChannelPairingConfirmation")!;
            Assert.Contains("sender-approve", dialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(row => row.Current.Name).Aggregate("", (a, b) => a + b),
                StringComparison.Ordinal);
            OwnedWindowCapture.Save(run, FindPairingWindow(run), "channels-pairing-confirm.png", dialog);
            Invoke(DialogButton(run, "Cancel"));
            await WaitUiAsync(run, () => FindById(run, "ChannelPairingConfirmation") is null, "cancelled consent");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "channels.pairing.approve");

            Invoke(FindById(run, "ChannelPairingApprove:0")!);
            await WaitUiAsync(run, () => FindById(run, "ChannelPairingConfirmation") is not null, "second explicit approval");
            Invoke(DialogButton(run, "Approve access"));
            await WaitUiAsync(run, () => FindById(run, "ChannelPairingDismiss:1") is null &&
                FindById(run, "ChannelPairingDismiss:0")?.Current.IsEnabled == true, "acknowledged approval and refreshed list");
            Invoke(FindById(run, "ChannelPairingDismiss:0")!);
            await WaitUiAsync(run, () => FindById(run, "ChannelPairingConfirmation") is not null, "dismiss confirmation");
            Invoke(DialogButton(run, "Dismiss request"));
            await WaitUiAsync(run, () => FindText(run, "No pending DM access requests.") is not null, "reconciled empty list");
            OwnedWindowCapture.Save(run, FindPairingWindow(run), "channels-pairing-completed.png", FindById(run, "ChannelPairingExpander")!);
            Assert.Single(run.Gateway.Requests, request => request.Method == "channels.pairing.approve" && request.Outcome == "ok");
            Assert.Single(run.Gateway.Requests, request => request.Method == "channels.pairing.dismiss" && request.Outcome == "ok");
        }, scenario: GatewayScenario.CreateChannelPairing());
    }

    private static AutomationElement DialogButton(GatewayFixtureRun run, string label) =>
        FindById(run, "ChannelPairingConfirmation")!.FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, label)));

    private static AutomationElement FindPairingIdentity(GatewayFixtureRun run, string requestId) =>
        FindById(run, "ChannelPairingPanel")!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Single(row => row.Current.Name.Contains($"Request: {requestId}", StringComparison.Ordinal));

    private static AutomationElement FindPairingWindow(GatewayFixtureRun run) =>
        AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChannelPairingPanel")) is not null);
}
