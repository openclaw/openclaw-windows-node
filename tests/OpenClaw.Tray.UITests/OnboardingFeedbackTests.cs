using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class OnboardingFeedbackTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task PhaseStatus_UsesDistinctIconsAndKeepsLocalizedAccessibleText(ElementTheme theme)
    {
        await ui.RunOnUIAsync(() =>
        {
            var control = new SetupPhaseStatus { RequestedTheme = theme };
            ui.Container.Children.Add(control);
            try
            {
                foreach (var status in Enum.GetValues<SetupInstallationStatus>())
                {
                    control.Apply(status);
                    var text = Assert.IsType<TextBlock>(control.FindName("StatusText"));
                    Assert.False(string.IsNullOrWhiteSpace(text.Text));
                    Assert.DoesNotContain("Onboarding_", text.Text);
                    Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                    var running = Assert.IsType<ProgressRing>(control.FindName("RunningIcon"));
                    Assert.Equal(status == SetupInstallationStatus.Running, running.IsActive);
                    Assert.Equal(status == SetupInstallationStatus.Running ? Visibility.Visible : Visibility.Collapsed, running.Visibility);
                    foreach (var (name, expected) in new[] {
                        ("CompleteIcon", SetupInstallationStatus.Complete),
                        ("FailedIcon", SetupInstallationStatus.Failed),
                        ("CancelledIcon", SetupInstallationStatus.Cancelled) })
                        Assert.Equal(status == expected ? Visibility.Visible : Visibility.Collapsed,
                            Assert.IsType<FontIcon>(control.FindName(name)).Visibility);
                }
            }
            finally { ui.Container.Children.Remove(control); }
        });
    }

    [Fact]
    public async Task Tailscale_OffDoesNotProbeAndRebindingRejectsLateResults()
    {
        await ui.RunOnUIAsync(async () =>
        {
            var pending = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tokens = new List<CancellationToken>();
            var control = new TailscaleSetupControl(token => { tokens.Add(token); return pending.Task; });
            var first = new SetupAccessDraft(new SetupConfig());
            var second = new SetupAccessDraft(new SetupConfig());
            ui.Container.Children.Add(control);
            try
            {
                control.Initialize(first);
                Assert.Empty(tokens);
                var toggle = Assert.IsType<ToggleSwitch>(control.FindName("TailscaleToggle"));
                toggle.IsOn = true;
                Assert.Single(tokens);
                Assert.True(first.Config.Tailscale.Enabled);
                Assert.False(first.TailscaleReady);
                Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(control.FindName("TailscaleOptions")).Visibility);
                control.Initialize(second);
                Assert.True(tokens[0].IsCancellationRequested);
                Assert.False(toggle.IsOn);
                var changes = 0;
                control.StateChanged += (_, _) => changes++;
                pending.SetResult((0, """{"BackendState":"Running","Self":{"DNSName":"pc.test.ts.net"},"CurrentTailnet":{"MagicDNSEnabled":true}}"""));
                await TestSupport.WaitForRenderedConditionAsync(() => pending.Task.IsCompleted, "synthetic probe completion");
                await OnboardingNativeProof.NextCompositionAsync();
                Assert.Equal(0, changes);
                Assert.False(first.TailscaleReady);
                Assert.False(second.TailscaleReady);
                Assert.Null(first.Config.Tailscale.TailnetDnsSuffix);
                Assert.Null(second.Config.Tailscale.TailnetDnsSuffix);
                control.Deactivate();
                toggle.IsOn = true;
                Assert.Single(tokens);
                Assert.False(second.Config.Tailscale.Enabled);
            }
            finally
            {
                control.Deactivate();
                ui.Container.Children.Remove(control);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tailscale_ReadinessRequiresMagicDnsAndRetainsExplicitAuthChoices(bool magicDns)
    {
        await ui.RunOnUIAsync(() =>
        {
            var json = """{"BackendState":"Running","Self":{"DNSName":"pc.test.ts.net"},"CurrentTailnet":{"MagicDNSEnabled":MAGIC}}"""
                .Replace("MAGIC", magicDns ? "true" : "false", StringComparison.Ordinal);
            var control = new TailscaleSetupControl(_ => Task.FromResult((0, json)));
            var draft = new SetupAccessDraft(new SetupConfig());
            ui.Container.Children.Add(control);
            try
            {
                control.Initialize(draft);
                Assert.IsType<ToggleSwitch>(control.FindName("TailscaleToggle")).IsOn = true;
                Assert.Equal(magicDns, draft.TailscaleReady);
                Assert.Equal(magicDns ? "test.ts.net" : null, draft.Config.Tailscale.TailnetDnsSuffix);
                Assert.False(draft.Config.Tailscale.TrustTailscaleAuth);
                Assert.IsType<RadioButtons>(control.FindName("TailscaleAuthModeSelector")).SelectedIndex = 1;
                var key = Assert.IsType<PasswordBox>(control.FindName("TailscaleAuthKeyBox"));
                Assert.Equal(Visibility.Visible, key.Visibility);
                key.Password = "synthetic-test-key";
                Assert.Equal(TailscaleAuthMode.AuthKey, draft.Config.Tailscale.AuthMode);
                Assert.Equal("synthetic-test-key", draft.Config.Tailscale.AuthKey);
                control.Deactivate();
                Assert.Empty(key.Password);
                Assert.Equal("synthetic-test-key", draft.Config.Tailscale.AuthKey);
            }
            finally
            {
                control.Deactivate();
                ui.Container.Children.Remove(control);
            }
        });
    }
}
