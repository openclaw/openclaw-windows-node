// Exercises the editor's live owner snapshot without a parent rerender or Gateway write.
// Set OPENCLAW_UI_TEST_RESOURCE_PROFILE=Production, then run dotnet test --filter FullyQualifiedName~AutomationsEditorSaveTests.
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
[Trait("Category", "ProductionResources")]
public sealed class AutomationsEditorSaveTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SaveReadsCurrentOwnerStateBeforeValidation(bool retired, bool alreadySaving)
    {
        Assert.True(ui.UsesProductionResources,
            $"Run Category=ProductionResources with {TestAppResourceLoader.ProfileVariable}=Production.");
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
            await ui.RunOnUIAsync(async () =>
            {
                updates = 0;
                var root = Assert.IsAssignableFrom<Microsoft.UI.Xaml.DependencyObject>(renderer!.Content);
                var save = Assert.Single(FindLogical<Button>(root), button =>
                    AutomationProperties.GetAutomationId(button) == "AutomationEditorSave");
                void InvokeSave() => Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(save)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                if (retired || alreadySaving)
                {
                    latest = retired ? null : latest! with { Saving = true };
                    InvokeSave();
                    return;
                }

                // Run after Reactor's TextChanged handler, before any parent reconciliation.
                // The rendered props stay blank; the missing prompt keeps this off the wire.
                var input = Assert.Single(FindLogical<TextBox>(root), box =>
                    AutomationProperties.GetAutomationId(box) == "AutomationEditorName");
                var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TextChangedEventHandler? changed = null;
                changed = (_, _) =>
                {
                    if (input.Text != "Latest owner name") return;
                    input.TextChanged -= changed;
                    try
                    {
                        Assert.Equal("Latest owner name", latest!.Form.Name);
                        InvokeSave();
                        invoked.TrySetResult();
                    }
                    catch (Exception error) { invoked.TrySetException(error); }
                };
                input.TextChanged += changed;
                try
                {
                    input.Text = "Latest owner name";
                    await WaitForRenderedConditionAsync(() => invoked.Task.IsCompleted, "accepted editor TextChanged callback");
                    await invoked.Task;
                }
                finally { input.TextChanged -= changed; }
            });
            await ui.YieldToRenderAsync();
            if (!retired && !alreadySaving)
                await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() => latest?.Submitted == true,
                    "Save validated the accepted input"));
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
