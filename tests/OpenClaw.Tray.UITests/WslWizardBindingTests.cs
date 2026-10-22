using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WslWizardBindingTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassicFallbackRetainsReviewedWslTargetAndRejectsChangedActiveBeforeConnecting(bool sameId)
    {
        using var temp = new TempDirectory();
        await using var server = await FixtureGatewayServer.StartAsync(
            GatewayScenario.CreateNativeSetup((_, _) =>
                throw new InvalidOperationException("A replacement must never receive wizard requests.")), "fixture-token");
        var selected = new GatewayRecord
        {
            Id = "selected", Url = "wss://selected.example.test",
            IsLocal = true, SetupManagedDistroName = "SelectedGateway",
        };
        var pin = new ExistingWslGatewaySetup(selected);
        var registry = new GatewayRegistry(temp.Path);
        registry.AddOrUpdate(selected);
        var changed = selected with
        {
            Id = sameId ? selected.Id : "replacement",
            Url = server.Endpoint.ToString(), SharedGatewayToken = "fixture-token",
        };
        registry.AddOrUpdate(changed);
        registry.SetActive(changed.Id);
        registry.Save();
        using var environment = new EnvironmentScope("OPENCLAW_TRAY_DATA_DIR", temp.Path);

        await ui.RunOnUIAsync(async () =>
        {
            Assert.Null(SetupWindow.Active);
            var resources = OnboardingWindowsFlowTests.LoadProgressResources(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!);
            Application.Current.Resources.MergedDictionaries.Add(resources);
            var frame = new Frame();
            WizardPage? wizard = null;
            ui.Container.Children.Add(frame);
            try
            {
                Assert.True(frame.Navigate(typeof(WizardPage), new WizardPageArgs(new SetupConfig(), pin.Record.Id, pin.Binding)));
                wizard = Assert.IsType<WizardPage>(frame.Content);
                await TestSupport.WaitForRenderedConditionAsync(() =>
                    Assert.IsType<TextBlock>(wizard.FindName("ErrorText")).Visibility == Visibility.Visible,
                    "changed WSL target rejected by mounted classic wizard");
                Assert.Null(typeof(WizardPage).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wizard));
                Assert.Equal(0, server.ConnectionCount);
                Assert.Empty(server.Requests);

                // Explicit retry must retain the original target, not capture the replacement.
                await Assert.IsAssignableFrom<Task>(typeof(WizardPage).GetMethod("StartOverAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, null));
                Assert.Equal(0, server.ConnectionCount);
                Assert.Empty(server.Requests);
            }
            finally
            {
                if (wizard is not null)
                    await Assert.IsAssignableFrom<Task>(typeof(WizardPage).GetMethod("CancelAndWaitAsync",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(wizard, null));
                ui.Container.Children.Remove(frame);
                Application.Current.Resources.MergedDictionaries.Remove(resources);
            }
        });
        registry.Load();
        Assert.Equal(changed, registry.GetActive());
    }
}
