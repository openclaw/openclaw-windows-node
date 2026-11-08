// Exercises the editor's live owner snapshot without a parent rerender or Gateway write.
// Run with dotnet test --filter FullyQualifiedName~AutomationsEditorSaveTests.
using System.Xml.Linq;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using OpenClawTray.Automations;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Queued Save actions validate the current root-owned draft, not stale render props.</summary>
[Collection(UICollection.Name)]
public sealed class AutomationsEditorSaveTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SaveReadsCurrentOwnerStateBeforeValidation(bool retired, bool alreadySaving)
    {
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT for mounted Automations tests.");
        var catalog = XDocument.Load(Path.Combine(repo,
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => (string)item.Element("value")!);
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        AutomationsStore? store = null;
        EditorSession? latest = null;
        var updates = 0;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                store = new AutomationsStore("main", () => null, action => action());
                var host = new AutomationsHost(store, new AutomationsText(key => catalog[key]),
                    () => null, () => Assert.Fail("Unexpected connection action."),
                    _ => Assert.Fail("Unexpected chat action."), _ => Assert.Fail("Unexpected web action."),
                    _ => false, () => ["main"]);
                var rendered = new EditorSession(EditorMode.Create, CronJobForm.CreateDefault("main"), null, 1);
                latest = rendered;
                renderer = new ReactorHostControl { Width = 560, Height = 640 };
                var root = renderer.Reconciler.Mount(Component<AutomationEditor, AutomationEditorProps>(
                    new(host, rendered, "main", true, () => latest,
                        change => { updates++; latest = change(latest!); },
                        _ => Assert.Fail("An invalid or retired editor must not close."))), static () => { });
                renderer.Content = root;
                ui.Container.Children.Add(renderer);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                updates = 0;
                // Model an input callback already accepted by the owner while its render is queued.
                // The rendered props remain blank; the still-missing prompt keeps this off the wire.
                latest = retired ? null : latest! with
                {
                    Form = latest!.Form with { Name = "Latest owner name" },
                    Saving = alreadySaving,
                };
                var root = Assert.IsAssignableFrom<Microsoft.UI.Xaml.DependencyObject>(renderer!.Content);
                var save = Assert.Single(FindLogical<Button>(root), button =>
                    AutomationProperties.GetAutomationId(button) == "AutomationEditorSave");
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(save)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                if (retired || alreadySaving)
                {
                    Assert.Equal(0, updates);
                    return;
                }
                Assert.NotNull(latest);
                Assert.True(latest.Submitted);
                Assert.DoesNotContain(CronFormField.Name, latest.Errors.Keys);
                Assert.Contains(CronFormField.PayloadText, latest.Errors.Keys);
                Assert.Equal("Latest owner name", latest.Form.Name);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (renderer is not null)
                {
                    ui.Container.Children.Remove(renderer);
                    renderer.Content = null;
                    renderer.Dispose();
                }
                store?.Dispose();
            });
        }
    }
}
