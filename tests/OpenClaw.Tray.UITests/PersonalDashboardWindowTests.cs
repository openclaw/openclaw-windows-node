using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Connection;
using OpenClaw.TestSupport;
using OpenClawTray.Windows;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class PersonalDashboardWindowTests(UIThreadFixture ui)
{
    private const string Host = "native-fixture.example.ts.net";

    [Fact]
    public Task NativeBridgeDeliversCurrentReplyAndRejectsLeaseRetiredDuringAwait() => WithBrowserAsync(async (window, core, manager) =>
    {
        await core.ExecuteScriptAsync("OpenClawNativeGatewayAuth.postMessage({id:'current',nonce:'current-nonce',signedAt:Date.now()})");
        await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        manager.Response.SetResult("{\"fixture\":\"accepted\"}");
        await WaitForReplyAsync(core, "current");
        Assert.Equal("\"accepted\"", await core.ExecuteScriptAsync("fixtureReplies.find(x=>x.id==='current').result.fixture"));

        manager.PrepareRequest();
        await core.ExecuteScriptAsync("OpenClawNativeGatewayAuth.postMessage({id:'retired',nonce:'retired-nonce',signedAt:Date.now()})");
        await manager.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        manager.Lease = manager.Lease with { Generation = manager.Lease.Generation + 1 };
        manager.Response.SetResult("{\"fixture\":\"must-not-be-delivered\"}");
        await WaitForReplyAsync(core, "retired");
        Assert.Equal("false", await core.ExecuteScriptAsync("fixtureReplies.some(x=>x.id==='retired' && x.result)"));
    });

    [Fact]
    public Task CloseDeletesOwnedInPrivateProfile() => WithBrowserAsync(async (window, core, manager) =>
    {
        Assert.True(core.Profile.IsInPrivateModeEnabled);
        Assert.StartsWith("personal-", core.Profile.ProfileName);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Profile.Deleted += (_, _) => deleted.TrySetResult();
        await window.CloseAsync();
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    });

    private Task WithBrowserAsync(Func<PersonalDashboardWindow, CoreWebView2, ManagerProxy, Task> test) => ui.RunOnUIAsync(async () =>
    {
        using var directory = new TempDirectory("personal-browser-ui-");
        using var environment = new EnvironmentScope("OPENCLAW_TRAY_DATA_DIR", directory.Path);
        var page = directory.Combine("page");
        Directory.CreateDirectory(page);
        File.WriteAllText(Path.Combine(page, "index.html"), """
            <!doctype html><meta charset="utf-8"><title>Native authentication fixture</title>
            <script>
            window.fixtureReplies = [];
            window.OpenClawNativeGatewayAuth.onmessage = e => fixtureReplies.push(JSON.parse(e.data));
            </script>
            """);
        var contract = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        var manager = (ManagerProxy)(object)contract;
        var window = new PersonalDashboardWindow(contract, manager.Lease, "/index.html");
        var view = (WebView2)((FrameworkElement)window.Content).FindName("DashboardWebView");
        var ready = new TaskCompletionSource<CoreWebView2>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.CoreWebView2Initialized += (_, _) =>
        {
            var core = view.CoreWebView2;
            if (core is null) { ready.TrySetException(new InvalidOperationException("Native Core initialization failed.")); return; }
            core.SetVirtualHostNameToFolderMapping(Host, page, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationCompleted += (_, result) =>
            {
                if (result.IsSuccess) ready.TrySetResult(core);
                else ready.TrySetException(new InvalidOperationException("Native fixture page did not load."));
            };
        };
        window.Activate();
        try
        {
            await test(window, await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)), manager);
        }
        finally
        {
            manager.Response.TrySetResult(null);
            await window.CloseAsync();
        }
    });

    private static async Task WaitForReplyAsync(CoreWebView2 core, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await core.ExecuteScriptAsync($"fixtureReplies.some(x=>x.id==='{id}')") != "true")
        {
            Assert.True(DateTime.UtcNow < deadline, "Native browser reply did not arrive.");
            await Task.Delay(25);
        }
    }

    public class ManagerProxy : DispatchProxy
    {
        public PersonalOperatorLease Lease { get; set; } = new("fixture", "wss://" + Host, "person", "scope", "device", 1, 1);
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Response { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PrepareRequest()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_CurrentPersonalOperatorLease") return Lease;
            if (method?.Name == "CreatePersonalBrowserAuthResponseAsync")
            {
                Started.TrySetResult();
                return Response.Task;
            }
            return null;
        }
    }
}
