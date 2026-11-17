// Exercises the editor's live owner snapshot without a parent rerender or Gateway write.
// Set OPENCLAW_UI_TEST_RESOURCE_PROFILE=Production, then run dotnet test --filter FullyQualifiedName~AutomationsEditorSaveTests.
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Cron;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Automations;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>Queued Save actions validate the current root-owned draft, not stale render props.</summary>
[Collection(UICollection.Name)]
[Trait("Category", "ProductionResources")]
public sealed class AutomationsEditorSaveTests(UIThreadFixture ui)
{
    [Fact]
    public async Task RootHostedDialogKeepsNativeTextBindingAfterRequiredValidation()
    {
        Assert.True(ui.UsesProductionResources);
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT.");
        var catalog = XDocument.Load(Path.Combine(repo, "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => (string)item.Element("value")!);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        using var identity = new TempDirectory("automation-dialog-binding-");
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateAutomations(), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: identity.Path, ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => connected.TrySetResult();
        await client.ConnectAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await ui.ResetContainerAsync();
        AutomationsStore? store = null;
        ReactorHostControl? renderer = null;
        Window? window = null;
        ContentDialog? dialog = null;
        FrameworkElement Find(DependencyObject root, string id) => Assert.Single(
            FindDescendants<FrameworkElement>(root), element => AutomationProperties.GetAutomationId(element) == id);
        void Invoke(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(
            new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                store = new AutomationsStore("main", () => client, action => ui.Dispatcher.TryEnqueue(() => action()));
                var host = new AutomationsHost(store, new AutomationsText(key => catalog[key]), () => client,
                    () => Assert.Fail("Unexpected connection"), _ => Assert.Fail("Unexpected chat"),
                    _ => Assert.Fail("Unexpected web"), _ => false, () => ["main"]);
                renderer = new ReactorHostControl();
                renderer.Mount(_ => Component<AutomationsRoot, AutomationsRootProps>(new(host)));
                window = new Window { Content = renderer };
                window.AppWindow.ResizeClient(new global::Windows.Graphics.SizeInt32(1100, 900));
                window.AppWindow.Move(new global::Windows.Graphics.PointInt32(-32000, -32000));
                window.Activate();
                store.Refresh();
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                FindDescendants<Button>(renderer!).Any(button => AutomationProperties.GetAutomationId(button) == "AutomationsNew"),
                "production root create action"));
            await ui.RunOnUIAsync(() => Invoke(Assert.IsType<Button>(Find(renderer!, "AutomationsNew"))));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
            {
                dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(renderer!.XamlRoot)
                    .SelectMany(popup => popup.Child is ContentDialog direct
                        ? new[] { direct } : FindDescendants<ContentDialog>(popup.Child)).SingleOrDefault();
                return dialog is not null;
            }, "root-owned editor dialog"));
            await ui.RunOnUIAsync(() => Invoke(Assert.IsType<Button>(Find(dialog!, "AutomationEditorSave"))));
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                AutomationProperties.GetItemStatus(Find(dialog!, "AutomationEditorName")) == "Enter a name.",
                "required validation applied"));
            await ui.RunOnUIAsync(async () =>
            {
                var input = Assert.IsType<TextBox>(Find(dialog!, "AutomationEditorName"));
                await NativeTextValueInput.SetAsync(window!, input, "Dialog-owned name");
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                AutomationProperties.GetItemStatus(Find(dialog!, "AutomationEditorName")) == "",
                "root-owned dialog draft accepts native name"));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                dialog?.Hide();
                renderer?.Dispose();
                if (window is not null) { window.Content = null; window.Close(); }
                store?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDeliveryIsWebManagedWithoutChangingTheDraft(bool existingOwner)
    {
        Assert.True(ui.UsesProductionResources);
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("Set OPENCLAW_REPO_ROOT for mounted Automations tests.");
        var catalog = XDocument.Load(Path.Combine(repo,
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"))
            .Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => (string)item.Element("value")!);
        var text = new AutomationsText(key => catalog[key]);
        var source = existingOwner ? CronJsonReader.ParseJob(System.Text.Json.JsonDocument.Parse("""
            {"id":"owner-job","name":"Saved owner job","enabled":true,"configRevision":"rev-1",
             "schedule":{"kind":"every","everyMs":600000},"payload":{"kind":"agentTurn","message":"Keep my prompt"},
             "delivery":{"mode":"announce","target":"owner","channel":"slack","accountId":"work"}}
            """).RootElement) : null;
        var form = source is null
            ? CronJobForm.CreateDefault("main") with { DeliveryMode = "announce", DeliveryTo = "#daily" }
            : CronJobForm.FromJob(source);
        var session = new EditorSession(existingOwner ? EditorMode.Edit : EditorMode.Create, form, source, 1);
        var opened = new List<string?>();
        await ui.ResetContainerAsync();
        ReactorHostControl? renderer = null;
        AutomationsStore? store = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                store = new AutomationsStore("main", () => null, action => action());
                var host = new AutomationsHost(store, text, () => null, () => { }, _ => { },
                    id => opened.Add(id), _ => false, () => ["main"]);
                renderer = new ReactorHostControl { Width = 560, Height = 640 };
                var root = renderer.Reconciler.Mount(Component<AutomationEditor, AutomationEditorProps>(
                    new(host, session, "main", true, () => session,
                        change => session = change(session), _ => { })), static () => { });
                renderer.Content = root;
                ui.Container.Children.Add(renderer);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var root = Assert.IsAssignableFrom<Microsoft.UI.Xaml.DependencyObject>(renderer!.Content);
                var notice = Assert.Single(FindLogical<InfoBar>(root), item =>
                    AutomationProperties.GetAutomationId(item) == "AutomationEditorDeliveryWebManaged");
                Assert.Equal(text.FormError(CronFormError.DeliveryWebManaged), notice.Message);
                Assert.DoesNotContain(FindLogical<RadioButtons>(root), item =>
                    AutomationProperties.GetAutomationId(item) == "AutomationEditorDestination");
                Assert.Equal(!existingOwner, FindLogical<ComboBox>(root).Any(item =>
                    AutomationProperties.GetAutomationId(item) == "AutomationEditorDeliveryMode"));
                var action = Assert.IsType<Button>(notice.ActionButton);
                Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(action)
                    .GetPattern(PatternInterface.Invoke)).Invoke();
                Assert.Equal(source?.Id, Assert.Single(opened));
                Assert.Equal(form, session.Form);
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

                // Observe native TextChanged after the property binding, before any parent reconciliation.
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
                    await NativeTextValueInput.SetAsync(ui.TestWindow, input, "Latest owner name");
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
