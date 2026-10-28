using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SessionsHubRegressionTests(UIThreadFixture ui)
{
    [Fact]
    public async Task FailedNewScope_DoesNotLeavePreviousRowsActionable()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(() =>
        {
            harness.Gateway.List = _ => Task.FromException<SessionListPage>(new TimeoutException("List unavailable"));
            harness.Controller.SetFilters(harness.Controller.State.Filters with { Status = SessionArchiveFilter.Archived });

            Assert.Equal(SessionsHubLoad.Failed, harness.Controller.State.Load);
            Assert.Empty(harness.Controller.State.Rows);
            Assert.Null(harness.Controller.State.DetailsRow);
            Assert.Empty(harness.Controller.State.Selection.Keys);
        });
    }

    [Fact]
    public async Task FailedRefresh_ShowsErrorAndRetryWhileKeepingPreviousRows()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.List = _ => Task.FromException<SessionListPage>(new TimeoutException("List unavailable"));
            await harness.Controller.ReloadAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Single(harness.Controller.State.Rows);
            var error = harness.Find<InfoBar>("SessionsHubReloadFailed");
            Assert.True(error.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, error.Severity);
            Assert.Equal("List unavailable", error.Message);
            harness.Gateway.List = null;
        });
        await ui.RunOnUIAsync(() => Invoke(Assert.IsType<Button>(harness.Find<InfoBar>("SessionsHubReloadFailed").ActionButton)));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => Assert.Equal(SessionsHubLoad.Loaded, harness.Controller.State.Load));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWorktreeRefresh_ShowsItsErrorWithOrWithoutPreviousRows(bool populated)
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Worktrees = Task.FromResult<IReadOnlyList<WorktreeRecord>?>(
                populated ? [new WorktreeRecord("one", "one", "C:\\fixture", null, "main", null, "manual", null, 0, 0, null)] : []);
            harness.Controller.SetTab(SessionsHubTab.Worktrees);
            harness.Gateway.Worktrees = Task.FromException<IReadOnlyList<WorktreeRecord>?>(new TimeoutException("Worktrees unavailable"));
            await harness.Controller.LoadWorktreesAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            if (populated)
                Assert.Equal("Worktrees unavailable", harness.Find<InfoBar>("SessionsHubWorktreesReloadFailed").Message);
            else
                Assert.Contains(TestSupport.FindDescendants<TextBlock>(harness.Root!), text => text.Text == "Worktrees unavailable");
        });
    }

    [Theory]
    [InlineData("query")]
    [InlineData("empty")]
    [InlineData("agent")]
    [InlineData("route")]
    [InlineData("global")]
    public async Task EditingTranscriptInputs_RejectsAnInFlightOldResponse(string change)
    {
        await using var harness = await Harness.CreateAsync(ui);
        var response = new TaskCompletionSource<SessionSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task search = Task.CompletedTask;
        await ui.RunOnUIAsync(() =>
        {
            harness.Gateway.Search = response.Task;
            harness.Controller.SetTranscriptQuery("old query");
            search = harness.Controller.RunTranscriptSearchAsync();
            switch (change)
            {
                case "query":
                    harness.Controller.SetTranscriptQuery("new query");
                    break;
                case "empty":
                    harness.Controller.SetTranscriptQuery("");
                    _ = harness.Controller.RunTranscriptSearchAsync();
                    break;
                case "agent":
                    harness.Controller.SetFilters(harness.Controller.State.Filters with { AgentId = "research" });
                    break;
                case "route":
                    harness.Controller.Activate("research");
                    break;
                case "global":
                    harness.Controller.SetFilters(harness.Controller.State.Filters with { IncludeGlobal = false });
                    break;
            }
        });
        response.SetResult(new SessionSearchResult { Indexing = true });
        await search.WaitAsync(TimeSpan.FromSeconds(10));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Null(harness.Controller.State.Transcript.Result);
            Assert.Null(harness.Controller.State.Transcript.Error);
            Assert.False(harness.Controller.State.Transcript.IsSearching);
        });
    }

    [Fact]
    public async Task EditingTranscriptQuery_ClearsAlreadyDisplayedOldResults()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Search = Task.FromResult(new SessionSearchResult { Indexing = true });
            harness.Controller.SetTranscriptQuery("old query");
            await harness.Controller.RunTranscriptSearchAsync();
            Assert.NotNull(harness.Controller.State.Transcript.Result);

            harness.Controller.SetTranscriptQuery("new query");
            Assert.Null(harness.Controller.State.Transcript.Result);
        });
    }

    [Fact]
    public async Task SavedLabel_DoesNotBecomeAnUnsavedDraftAfterAnotherRename()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(() => harness.Find<TextBox>("SessionsHubLabel").Text = "Saved name");
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            harness.Gateway.Page = Page("Saved name");
            Invoke(harness.Find<Button>("SessionsHubLabelSave"));
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            Assert.Single(harness.Gateway.Patches);
            harness.Gateway.Page = Page("Later rename");
            await harness.Controller.ReloadAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal("Later rename", harness.Find<TextBox>("SessionsHubLabel").Text);
            Assert.False(harness.Find<Button>("SessionsHubLabelSave").IsEnabled);
        });
    }

    [Theory]
    [InlineData("data:image/svg+xml,<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("\U0001F680")]
    public async Task DefaultIcon_ClearsAStoredCustomIcon(string icon)
    {
        await using var harness = await Harness.CreateAsync(ui, icon, mount: true);
        await ui.RunOnUIAsync(() => Invoke(harness.Find<Button>("SessionsHubIcon:default")));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            var patch = Assert.Single(harness.Gateway.Patches);
            Assert.True(patch.Icon.IsSpecified);
            Assert.True(patch.Icon.IsClear);
        });
    }

    [Fact]
    public async Task LabelAcknowledgement_PreservesANewerDraft()
    {
        await using var harness = await Harness.CreateAsync(ui);
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task save = Task.CompletedTask;
        await ui.RunOnUIAsync(() =>
        {
            harness.Gateway.PatchAcknowledgement = acknowledgement.Task;
            harness.Controller.SetLabelDraft(Harness.Key, "Submitted");
            save = harness.Controller.SaveLabelAsync(harness.Controller.State.Rows.Single(), "Submitted");
            harness.Controller.SetLabelDraft(Harness.Key, "Newer edit");
            harness.Gateway.Page = Page("Submitted");
        });
        acknowledgement.SetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(10));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal("Submitted", harness.Controller.State.Rows.Single().Label);
            Assert.Equal((Harness.Key, "Newer edit", "session-one"), harness.Controller.State.LabelDraft);
        });
    }

    [Fact]
    public async Task LegacyGateway_DisablesCreationWithoutBlockingExistingGroupMoves()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Methods = ["sessions.list", "sessions.patch", "sessions.groups.list", "sessions.groups.put"];
            harness.Controller.RefreshConnection();
            await harness.Controller.NewGroupAsync();
            Assert.Null(harness.Controller.State.Dialog);
            Assert.NotNull(harness.Controller.State.Notice);
            await harness.Controller.MoveToGroupAsync(harness.Controller.State.Rows.Single(), "Release");

            Assert.Equal("Release", Assert.Single(harness.Gateway.Patches).Category.Value);
            Assert.Equal(0, harness.Gateway.CatalogReplacements);
            Assert.Empty(harness.Gateway.CreatedGroups);
        });
    }

    [Fact]
    public async Task CreatingGroup_DoesNotReplaceACachedCatalog()
    {
        await using var harness = await Harness.CreateAsync(ui);
        Task create = Task.CompletedTask;
        await ui.RunOnUIAsync(() =>
        {
            create = harness.Controller.NewGroupAsync();
            harness.Controller.UpdateDialog(dialog => dialog with { Text = "Mine" });
            harness.Controller.CloseDialog(true);
        });
        await create.WaitAsync(TimeSpan.FromSeconds(10));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal(0, harness.Gateway.CatalogReplacements);
            Assert.Equal("Mine", Assert.Single(harness.Gateway.CreatedGroups));
            Assert.Contains("Other client's group", harness.Controller.State.GroupCatalog);
        });
    }

    [Fact]
    public async Task QueryChanges_PreserveAnUnsavedRenameUntilTheSameSessionReturns()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(() =>
        {
            harness.Controller.SetLabelDraft(Harness.Key, "Unsaved name");
            harness.Controller.SetFilters(harness.Controller.State.Filters with { Limit = "500" });
            Assert.Equal("Unsaved name", harness.Controller.State.LabelDraft.Text);
            Assert.Equal(Harness.Key, harness.Controller.State.DetailsRow?.Key);

            harness.Gateway.Page = new SessionListPage();
            harness.Controller.SetFilters(harness.Controller.State.Filters with { AgentId = "other" });
            Assert.Null(harness.Controller.State.DetailsRow);
            Assert.Equal("Unsaved name", harness.Controller.State.LabelDraft.Text);

            harness.Gateway.Page = Page();
            harness.Controller.SetFilters(harness.Controller.State.Filters with { AgentId = null });
            Assert.Equal(Harness.Key, harness.Controller.State.DetailsRow?.Key);
            Assert.Equal("Unsaved name", harness.Controller.State.LabelDraft.Text);
        });
    }

    [Theory]
    [InlineData("session-one", "replacement")]
    [InlineData("session-one", null)]
    [InlineData(null, null)]
    public async Task QueryReload_DoesNotTransferAnUnsavedRenameToAReplacementOrUnverifiedRow(string? originalId, string? currentId)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Page.Sessions.Single().SessionId = originalId;
            await harness.Controller.ReloadAsync();
            harness.Controller.SetLabelDraft(Harness.Key, "Original draft");
            harness.Gateway.Page = Page("Replacement");
            harness.Gateway.Page.Sessions.Single().SessionId = currentId;
            harness.Controller.SetFilters(harness.Controller.State.Filters with { Limit = "500" });

            Assert.Equal("Replacement", harness.Controller.State.Rows.Single().Label);
            Assert.Null(harness.Controller.State.LabelDraft.Key);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("name")]
    public async Task WireEquivalentSearchEdit_KeepsDetailsSelectionAndTranscriptResults(string search)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Controller.SetFilters(harness.Controller.State.Filters with { Search = search });
            harness.Controller.OpenDetails(Harness.Key);
            harness.Controller.SetLabelDraft(Harness.Key, "Unsaved name");
            harness.Controller.ToggleSelected(Harness.Key);
            harness.Controller.SetTranscriptQuery("transcript");
            await harness.Controller.RunTranscriptSearchAsync();
            var rows = harness.Controller.State.Rows;
            var result = harness.Controller.State.Transcript.Result;
            Assert.NotNull(result);

            harness.Controller.SetSearch($" {search} ");

            Assert.Equal($" {search} ", harness.Controller.State.Filters.Search);
            Assert.Same(rows, harness.Controller.State.Rows);
            Assert.Equal(Harness.Key, harness.Controller.State.DetailsRow?.Key);
            Assert.Equal("Unsaved name", harness.Controller.State.LabelDraft.Text);
            Assert.Contains(Harness.Key, harness.Controller.State.Selection.Keys);
            Assert.Same(result, harness.Controller.State.Transcript.Result);
        });
    }

    [Theory]
    [InlineData("reopened")]
    [InlineData("failed")]
    [InlineData("reconnected")]
    [InlineData("superseded")]
    public async Task OldBranchLookup_DoesNotReplaceTheCurrentDialogChoices(string change)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Worktrees = Task.FromResult<IReadOnlyList<WorktreeRecord>?>(
                [new WorktreeRecord("one", "one", "C:\\fixture", null, "main", null, "manual", null, 0, 0, null)]);
            await harness.Controller.LoadWorktreesAsync();
            var firstCreate = harness.Controller.CreateWorktreeAsync();
            harness.Controller.UpdateDialog(dialog => dialog with { BaseRef = "" });
            var oldResponse = new TaskCompletionSource<WorktreeBranchList>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Gateway.Branches = _ => oldResponse.Task;
            var oldLookup = harness.Controller.LoadDialogBranchesAsync();

            var freshResponse = new TaskCompletionSource<WorktreeBranchList>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Gateway.Branches = _ => freshResponse.Task;
            Task secondCreate = Task.CompletedTask;
            if (change != "superseded")
            {
                harness.Controller.CloseDialog(false);
                if (change == "reconnected")
                {
                    harness.Gateway.Epoch++;
                    harness.Controller.RefreshConnection();
                    await harness.Controller.LoadWorktreesAsync();
                }
                secondCreate = harness.Controller.CreateWorktreeAsync();
            }
            Assert.Equal("C:\\fixture", harness.Controller.State.Dialog?.Repo);
            var freshLookup = harness.Controller.LoadDialogBranchesAsync();
            if (change != "failed")
            {
                oldResponse.SetResult(Branches("stale-base"));
                await oldLookup;
            }
            freshResponse.SetResult(Branches("fresh-base"));
            await freshLookup;
            if (change == "failed")
            {
                oldResponse.SetException(new TimeoutException("Old lookup failed"));
                await oldLookup;
            }

            var dialog = Assert.IsType<SessionsHubDialog>(harness.Controller.State.Dialog);
            harness.Controller.CloseDialog(false);
            await Task.WhenAll(firstCreate, secondCreate);
            Assert.Equal(["fresh-base"], dialog.Branches);
            Assert.Equal("fresh-base", dialog.BaseRef);
            Assert.Null(dialog.Status);
        });
    }

    [Theory]
    [InlineData(null, "completed")]
    [InlineData(null, "pending")]
    [InlineData("original", "completed")]
    [InlineData(null, "none")]
    public async Task SingleDelete_KeepsTheConfirmedIdentityAcrossRosterChanges(string? sessionId, string reload)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Page.Sessions.Single().SessionId = sessionId;
            await harness.Controller.ReloadAsync();
            var deletion = harness.Controller.DeleteAsync(harness.Controller.State.Rows.Single(), "Original session");
            Assert.NotNull(harness.Controller.State.Dialog);
            var response = new TaskCompletionSource<SessionListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task reloading = Task.CompletedTask;
            if (reload != "none")
            {
                harness.Gateway.Page = Page("Replacement");
                harness.Gateway.Page.Sessions.Single().SessionId = "replacement";
                if (reload == "pending")
                {
                    harness.Gateway.List = _ => response.Task;
                    reloading = harness.Controller.ReloadAsync();
                    harness.Gateway.List = null;
                }
                else
                    await harness.Controller.ReloadAsync();
            }
            harness.Controller.CloseDialog(true);
            await deletion;
            response.TrySetResult(harness.Gateway.Page);
            await reloading;

            if (sessionId is null && reload != "none")
            {
                Assert.Empty(harness.Gateway.Deletions);
                Assert.Equal(InfoBarSeverity.Informational, harness.Controller.State.Notice?.Severity);
            }
            else
                Assert.Equal(sessionId, Assert.Single(harness.Gateway.Deletions).ExpectedSessionId);
        });
    }

    [Theory]
    [InlineData(true, "global sessions included", "sessions without routing information included")]
    [InlineData(false, "global sessions excluded", "sessions without routing information excluded")]
    public async Task TranscriptScope_ShowsGlobalAndUnknownMembership(bool included, string global, string unknown)
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(() =>
        {
            harness.Controller.SetFilters(harness.Controller.State.Filters with { IncludeGlobal = included, IncludeUnknown = included });
            harness.Controller.SetTranscriptOpen(true);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            var scope = Assert.Single(TestSupport.FindDescendants<TextBlock>(harness.Root!),
                text => text.Text.StartsWith("Searching:", StringComparison.Ordinal));
            Assert.Contains(global, scope.Text, StringComparison.Ordinal);
            Assert.Contains(unknown, scope.Text, StringComparison.Ordinal);
        });
    }

    private static WorktreeBranchList Branches(string name) => new()
    {
        Branches = [new WorktreeBranch(name)], DefaultBranch = name,
    };

    private static SessionListPage Page(string label = "Initial name", string? icon = null) => new()
    {
        Sessions =
        [
            new SessionInfo
            {
                Key = Harness.Key, SessionId = "session-one", Label = label, Icon = icon,
                IsMain = false, Status = "active", UpdatedAt = DateTime.UtcNow,
            },
        ],
    };

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private sealed class Harness(UIThreadFixture thread) : ISessionsHubHost, IAsyncDisposable
    {
        public const string Key = "agent:main:one";
        internal ReactorHostControl? Root { get; private set; }
        private ChatThemeProofScope? _theme;
        public SessionsHubController Controller { get; private set; } = null!;
        public GatewayProxy Gateway { get; private set; } = null!;
        public IOperatorGatewayClient Client { get; private set; } = null!;
        public XamlRoot? XamlRoot => thread.Container.XamlRoot;
        public IntPtr WindowHandle => IntPtr.Zero;
        public IReadOnlyList<SessionInfo> TrackedSessions => Gateway.Page.Sessions;
        public JsonElement? AgentsList => null;
        public bool ShowCompletedSessions { get; set; } = true;
        public void OpenChat(string sessionKey) { }
        public void OpenConnection() { }

        public static async Task<Harness> CreateAsync(UIThreadFixture thread, string? icon = null, bool mount = false)
        {
            var harness = new Harness(thread);
            await thread.RunOnUIAsync(() =>
            {
                harness.Client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
                harness.Gateway = (GatewayProxy)harness.Client;
                harness.Gateway.Page = Page(icon: icon);
                harness.Controller = new SessionsHubController(harness, action =>
                {
                    if (!thread.Dispatcher.TryEnqueue(() => action()))
                        throw new InvalidOperationException("The proof dispatcher rejected a controller update.");
                });
                harness.Controller.Activate(null);
                harness.Controller.OpenDetails(Key);
                if (mount)
                {
                    harness._theme = new ChatThemeProofScope("Light");
                    harness.Root = new ReactorHostControl { Width = 1200, Height = 800 };
                    thread.Container.Children.Add(harness.Root);
                    harness.Root.Mount(_ => Component<SessionsHubRoot, SessionsHubRootProps>(new(harness.Controller)));
                }
            });
            if (mount) await harness.RenderAsync();
            return harness;
        }

        public T Find<T>(string id) where T : FrameworkElement =>
            Assert.Single(TestSupport.FindDescendants<T>(Root!), element => AutomationProperties.GetAutomationId(element) == id);

        public async Task RenderAsync()
        {
            await thread.YieldToRenderAsync();
            await thread.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                () => Root is { IsLoaded: true } && TestSupport.FindDescendants<Button>(Root).Any(),
                "mounted Sessions controls"));
            await thread.YieldToRenderAsync();
        }

        public async ValueTask DisposeAsync() => await thread.RunOnUIAsync(() =>
        {
            Controller.Deactivate();
            Root?.Dispose();
            if (Root is not null) thread.Container.Children.Remove(Root);
            _theme?.Dispose();
        });
    }

    public class GatewayProxy : DispatchProxy
    {
        public SessionListPage Page = SessionsHubRegressionTests.Page();
        public Func<SessionListQuery, Task<SessionListPage>>? List;
        public Task<SessionSearchResult> Search = Task.FromResult(new SessionSearchResult());
        public Task PatchAcknowledgement = Task.CompletedTask;
        public Task<IReadOnlyList<WorktreeRecord>?> Worktrees = Task.FromResult<IReadOnlyList<WorktreeRecord>?>([]);
        public Func<string, Task<WorktreeBranchList>> Branches = _ => Task.FromResult(SessionsHubRegressionTests.Branches("initial"));
        public long Epoch = 1;
        public string[] Methods = ["sessions.list", "sessions.patch", "sessions.search", "sessions.groups.list", "sessions.groups.put", "sessions.groups.create", "worktrees.list"];
        public List<SessionPatch> Patches { get; } = [];
        public List<SessionDeleteRequest> Deletions { get; } = [];
        public List<string> CreatedGroups { get; } = [];
        public int CatalogReplacements;
        private SessionGroupCatalog _catalog = new() { Names = ["Release"] };

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name.StartsWith("add_", StringComparison.Ordinal)
                || method.Name.StartsWith("remove_", StringComparison.Ordinal))
                return null;
            switch (method.Name)
            {
                case "get_IsConnectedToGateway": return true;
                case "get_SessionMutationConnectionEpoch": return Epoch;
                case "get_MainSessionKey": return "agent:main:main";
                case "get_GrantedOperatorScopes": return new[] { "operator.admin", "operator.write", "operator.read" };
                case "get_AdvertisedServerMethods": return Methods;
                case nameof(IOperatorGatewayClient.ListSessionsPageAsync):
                    return List?.Invoke((SessionListQuery)args![0]!) ?? Task.FromResult(Page);
                case nameof(IOperatorGatewayClient.ListSessionGroupsAsync):
                    return Task.FromResult(_catalog);
                case nameof(IOperatorGatewayClient.SearchSessionTranscriptsAsync): return Search;
                case nameof(IOperatorGatewayClient.ListWorktreesAsync): return Worktrees;
                case nameof(IOperatorGatewayClient.ListWorktreeBranchesAsync):
                    return Branches((string)args![0]!);
                case nameof(IOperatorGatewayClient.DeleteSessionDetailedAsync):
                    var request = (SessionDeleteRequest)args![0]!;
                    Deletions.Add(request);
                    return Task.FromResult(new SessionDeleteOutcome(request.Key, true, null));
                case nameof(IOperatorGatewayClient.PatchSessionConfirmedAsync):
                    Patches.Add((SessionPatch)args![1]!);
                    return PatchAcknowledgement;
                case nameof(IOperatorGatewayClient.RequestSessionsAsync): return Task.CompletedTask;
                case nameof(IOperatorGatewayClient.PutSessionGroupsAsync):
                    CatalogReplacements++;
                    _catalog = new SessionGroupCatalog { Names = ["Release", "Mine"] };
                    return Task.FromResult(_catalog);
                case nameof(IOperatorGatewayClient.CreateSessionGroupAsync):
                    CreatedGroups.Add((string)args![0]!);
                    _catalog = new SessionGroupCatalog { Names = ["Release", "Other client's group", "Mine"] };
                    return Task.FromResult(_catalog);
                default: throw new NotSupportedException($"Unexpected Sessions proof call: {method.Name}");
            }
        }
    }
}
