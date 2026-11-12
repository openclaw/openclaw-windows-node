// Exercises the real widget HTTP loader, native WebView relay and local connection lifetime.
// Run with the DashboardWidgetCellTests filter in the existing native UI lane.
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OpenClaw.Shared.Dashboards;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Dashboards;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed partial class DashboardWidgetCellTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedTicket_NeverRendersBeforeAnAuthorizedReplacement(bool revoked)
    {
        var scenario = GatewayScenario.CreateDashboards();
        await using var server = await FixtureGatewayServer.StartAsync(scenario, "synthetic-widget-proof");
        var boards = scenario.Boards!;
        var origin = new UriBuilder(server.Endpoint) { Scheme = "http" }.Uri;
        var valid = ReadHtmlBoard(boards).Widgets[0];
        var invalid = revoked ? valid : valid with
        {
            FrameUrl = valid.FrameUrl!.Replace(Uri.EscapeDataString(FixtureBoards.OpsSessionKey),
                Uri.EscapeDataString(FixtureBoards.ResearchSessionKey), StringComparison.Ordinal)
        };
        if (revoked)
            Assert.True(boards.RevokeViewTicket(valid.ViewTicket!));
        var request = new BoardWidgetWebHostRequest(invalid, origin);
        var states = new List<BoardWidgetWebHostStatus>();
        await WithMountedAsync(() => BoardWidgetWebHostElement.BoardWidgetWebHost().Set(control =>
            {
                control.StateChanged = next => states.Add(next.Status);
                control.Update(request);
            }), _ => { }, afterMounted: async root =>
            {
                await WaitForWebHostAsync(root, () => states.Contains(BoardWidgetWebHostStatus.Unauthorized));
                await ui.RunOnUIAsync(() =>
                {
                    Assert.DoesNotContain(BoardWidgetWebHostStatus.Ready, states);
                    var host = Assert.Single(FindDescendants<BoardWidgetWebHost>(root));
                    Assert.Null(HostField<string>(host, "_pendingHtml"));
                    Assert.Equal(1, boards.DocumentRequests);
                    request = new BoardWidgetWebHostRequest(ReadHtmlBoard(boards).Widgets[0], origin, Attempt: 1);
                    host.Update(request);
                });
                await WaitForWebHostAsync(root, () => states.Contains(BoardWidgetWebHostStatus.Ready));
                Assert.Equal(2, boards.DocumentRequests);
                Assert.Empty(server.Requests);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionChange_CancelsUndeliveredHtmlButRetainsCompletedDocumentsAndTabs(bool completed)
    {
        var scenario = GatewayScenario.CreateDashboards();
        var boards = scenario.Boards!;
        var sandboxGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandboxRequests = 0;
        if (!completed)
            boards.BeforeSandboxResponse = cancellation =>
            {
                Interlocked.Increment(ref sandboxRequests);
                return sandboxGate.Task.WaitAsync(cancellation);
            };
        await using var server = await FixtureGatewayServer.StartAsync(scenario, "synthetic-widget-owner");
        var origin = new UriBuilder(server.Endpoint) { Scheme = "http" }.Uri;
        var board = ReadHtmlBoard(boards);
        var first = new SnapshotBoardClient(board) { Origin = origin };
        var replacement = new TaskCompletionSource<BoardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new SnapshotBoardClient(board) { Origin = origin, BoardResponse = replacement.Task };
        var heldViewUpdates = new Queue<Action>();
        var holdViewUpdates = false;
        var state = new DashboardsPageState
        {
            Post = action =>
            {
                if (holdViewUpdates)
                    heldViewUpdates.Enqueue(action);
                else
                    ui.Container.DispatcherQueue.TryEnqueue(() => action());
            },
            Schedule = (_, _) => new NoopLease(),
        };
        state.SetConnection(first, true);
        state.Open(new BoardSessionTarget(FixtureBoards.OpsSessionKey, "main"));
        try
        {
            await WithMountedAsync(() => Component<ReactorDashboardsRoot, DashboardsRootProps>(new(state)),
                _ => { }, ready: root => FindDescendants<SelectorBar>(root).Any(), afterMounted: async root =>
                {
                    await ui.RunOnUIAsync(() =>
                    {
                        var tabs = Assert.Single(FindDescendants<SelectorBar>(root));
                        tabs.SelectedItem = tabs.Items[1];
                    });
                    await WaitForWebHostAsync(root, () =>
                    {
                        var hosts = FindDescendants<BoardWidgetWebHost>(root).ToArray();
                        return hosts.Length == 2 && (completed || Volatile.Read(ref sandboxRequests) == 2) &&
                            hosts.All(host => completed
                            ? HostField<BoardWidgetWebHostState>(host, "_state")?.Status == BoardWidgetWebHostStatus.Ready
                            : HostField<string>(host, "_pendingHtml") is not null);
                    });
                    BoardWidgetWebHost[] retained = [];
                    WebView2?[] views = [];
                    var proxyReady = 0;
                    var resourceLoaded = 0;
                    void Observe(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
                    {
                        switch (BoardWidgetHostPage.Parse(args.WebMessageAsJson))
                        {
                            case BoardWidgetHostMessage.ProxyReady: proxyReady++; break;
                            case BoardWidgetHostMessage.ResourceLoaded: resourceLoaded++; break;
                        }
                    }
                    await ui.RunOnUIAsync(() =>
                    {
                        retained = FindDescendants<BoardWidgetWebHost>(root).ToArray();
                        views = retained.Select(host => HostField<WebView2>(host, "_webView")).ToArray();
                        Assert.All(views, view => Assert.NotNull(view));
                        Assert.Equal(2, boards.DocumentRequests);
                        if (!completed)
                        {
                            foreach (var view in views)
                                view!.CoreWebView2.WebMessageReceived += Observe;
                            holdViewUpdates = true;
                        }
                        state.SetConnection(second, true);
                        sandboxGate.TrySetResult();
                    });
                    if (!completed)
                    {
                        try
                        {
                            // Deliver actual proxy readiness while Reactor has not yet applied the owner change.
                            await WaitForWebHostAsync(root, () => proxyReady == 2);
                            await ui.RunOnUIAsync(() =>
                            {
                                Assert.Equal(0, resourceLoaded);
                                Assert.All(retained, host =>
                                {
                                    Assert.NotNull(HostField<string>(host, "_pendingHtml"));
                                    Assert.NotEqual(BoardWidgetWebHostStatus.Ready,
                                        HostField<BoardWidgetWebHostState>(host, "_state")?.Status);
                                });
                            });
                        }
                        finally
                        {
                            await ui.RunOnUIAsync(() =>
                            {
                                foreach (var view in views)
                                    view!.CoreWebView2.WebMessageReceived -= Observe;
                                holdViewUpdates = false;
                                while (heldViewUpdates.TryDequeue(out var update))
                                    ui.Container.DispatcherQueue.TryEnqueue(() => update());
                            });
                        }
                    }
                    await WaitForWebHostAsync(root, () => second.BoardLoads == 1 &&
                        retained.All(host => HostField<BoardWidgetWebHostRequest>(host, "_request") is
                            { DocumentCurrent: false } request && ReferenceEquals(request.ConnectionOwner, second) &&
                            (completed
                                ? HostField<BoardWidgetWebHostState>(host, "_state")?.Status == BoardWidgetWebHostStatus.Ready
                                : HostField<WebView2>(host, "_webView") is null)));
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.Equal(retained, FindDescendants<BoardWidgetWebHost>(root));
                        var tabs = Assert.Single(FindDescendants<SelectorBar>(root));
                        Assert.Same(tabs.Items[1], tabs.SelectedItem);
                        for (var index = 0; index < retained.Length; index++)
                        {
                            Assert.Null(HostField<string>(retained[index], "_pendingHtml"));
                            if (completed)
                                Assert.Same(views[index], HostField<WebView2>(retained[index], "_webView"));
                        }
                        foreach (var widget in board.Widgets)
                            Assert.True(boards.RevokeViewTicket(widget.ViewTicket!));
                        replacement.SetResult(ReadHtmlBoard(boards));
                        sandboxGate.TrySetResult();
                    });
                    await WaitForWebHostAsync(root, () => retained.All(host =>
                        HostField<BoardWidgetWebHostRequest>(host, "_request")?.DocumentCurrent == true &&
                        HostField<BoardWidgetWebHostState>(host, "_state")?.Status == BoardWidgetWebHostStatus.Ready));
                    await ui.RunOnUIAsync(() =>
                    {
                        Assert.Equal(retained, FindDescendants<BoardWidgetWebHost>(root));
                        var tabs = Assert.Single(FindDescendants<SelectorBar>(root));
                        Assert.Same(tabs.Items[1], tabs.SelectedItem);
                        Assert.Equal(completed ? 2 : 4, boards.DocumentRequests);
                        for (var index = 0; index < retained.Length; index++)
                        {
                            if (completed)
                                Assert.Same(views[index], HostField<WebView2>(retained[index], "_webView"));
                            else
                                Assert.NotSame(views[index], HostField<WebView2>(retained[index], "_webView"));
                        }
                    });
                });
        }
        finally
        {
            sandboxGate.TrySetResult();
            replacement.TrySetCanceled();
        }
    }

    [Fact]
    public async Task HostileWidgetBridge_ClosesRealTransferredPortsWithoutHostActions()
    {
        var scenario = GatewayScenario.CreateDashboards();
        scenario.Boards!.SetWidgetDocument(FixtureBoards.OpsSessionKey, "status-card", """
            <p>Untrusted fixture document</p><script>
            addEventListener("message", event => {
              if (event.data?.type !== "fixture:attack") return;
              for (const [index, type] of ["openclaw:widget-bridge-port-offer", "openclaw:widget-prompt-offer"].entries()) {
                const channel = new MessageChannel();
                channel.port2.onmessage = () => parent.postMessage({type:"fixture:unexpected-host-reply"}, "*");
                parent.postMessage({type}, "*", [channel.port1]);
                channel.port2.postMessage({type:"openclaw:widget-bridge-request", id:"private-"+index,
                  method:"prompt.send", params:{text:"Must not execute"}});
              }
              for (const method of ["prompt.send", "action.invoke", "cron.run", "data.read"]) {
                parent.postMessage({type:"openclaw:widget-bridge-request", id:method, method, params:{}}, "*");
              }
              parent.postMessage({type:"fixture:attack-sent"}, "*");
            });
            </script>
            """);
        await using var server = await FixtureGatewayServer.StartAsync(scenario, "synthetic-widget-bridge");
        var origin = new UriBuilder(server.Endpoint) { Scheme = "http" }.Uri;
        var request = new BoardWidgetWebHostRequest(ReadHtmlBoard(scenario.Boards).Widgets[0], origin);
        var states = new List<BoardWidgetWebHostStatus>();
        await WithMountedAsync(() => BoardWidgetWebHostElement.BoardWidgetWebHost().Set(control =>
            {
                control.StateChanged = next => states.Add(next.Status);
                control.Update(request);
            }), _ => { }, afterMounted: async root =>
            {
                await WaitForWebHostAsync(root, () => states.LastOrDefault() == BoardWidgetWebHostStatus.Ready);
                var attacks = 0;
                var replies = 0;
                var requestMessages = 0;
                CoreWebView2? core = null;
                void Observe(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
                {
                    using var json = JsonDocument.Parse(args.WebMessageAsJson);
                    if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                        !data.TryGetProperty("type", out var type))
                        return;
                    switch (type.GetString())
                    {
                        case "fixture:attack-sent": attacks++; break;
                        case "fixture:unexpected-host-reply": replies++; break;
                        case "openclaw:widget-bridge-request": requestMessages++; break;
                    }
                }
                try
                {
                    var stateCount = states.Count;
                    await ui.RunOnUIAsync(async () =>
                    {
                        core = Assert.Single(FindDescendants<WebView2>(root)).CoreWebView2;
                        core.WebMessageReceived += Observe;
                        // Observe real port disposal without replacing the production relay handler.
                        await core.ExecuteScriptAsync("""
                            window.fixtureClosedPorts = 0;
                            const nativeClose = MessagePort.prototype.close;
                            MessagePort.prototype.close = function() {
                              window.fixtureClosedPorts++;
                              return nativeClose.call(this);
                            };
                            """);
                        core.PostWebMessageAsJson("""{"channel":"sandbox","data":{"type":"fixture:attack"}}""");
                    });
                    await WaitForWebHostAsync(root, () => attacks == 1);
                    await ui.RunOnUIAsync(async () =>
                    {
                        Assert.Equal(2, JsonSerializer.Deserialize<int>(
                            await core!.ExecuteScriptAsync("window.fixtureClosedPorts")));
                        Assert.Equal(4, requestMessages);
                        Assert.Equal(0, replies);
                        Assert.Equal(stateCount, states.Count);
                        Assert.Equal(BoardWidgetWebHostStatus.Ready, states[^1]);
                        Assert.Equal(1, scenario.Boards.DocumentRequests);
                        Assert.Empty(server.Requests);
                    });
                }
                finally
                {
                    if (core is not null)
                        await ui.RunOnUIAsync(() => core.WebMessageReceived -= Observe);
                }
            });
    }

    private static BoardSnapshot ReadHtmlBoard(FixtureBoards boards)
    {
        var snapshot = DashboardProtocol.ParseBoardSnapshot(JsonSerializer.SerializeToElement(
            boards.GetBoard(JsonSerializer.SerializeToElement(new { sessionKey = FixtureBoards.OpsSessionKey }))));
        return snapshot with
        {
            Tabs = [new("overview", "Overview", 0, "right"), new("metrics", "Metrics", 1, "right")],
            Widgets = [
                snapshot.Widgets.Single(widget => widget.Name == "status-card"),
                snapshot.Widgets.Single(widget => widget.Name == "deploy-chart") with { TabId = "metrics" }],
        };
    }

    // Reflection only gates the exact fetch-to-proxy window; outcomes use real HTTP and WebView messages.
    private static T? HostField<T>(BoardWidgetWebHost host, string name) where T : class =>
        (T?)typeof(BoardWidgetWebHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host);

    private async Task WaitForWebHostAsync(ReactorHostControl root, Func<bool> ready)
    {
        var deadline = DateTime.UtcNow + BoardWidgetDocumentLoader.DefaultTimeout;
        while (!await ui.RunOnUIAsync(() => Task.FromResult(ready())))
        {
            Assert.True(DateTime.UtcNow < deadline, "Native widget HTTP/relay boundary did not settle.");
            await ui.YieldToRenderAsync();
        }
    }
}
