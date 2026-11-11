using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Pages;
using OpenClawTray.Sessions;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SessionsHubRegressionTests(UIThreadFixture ui)
{
    [Fact]
    public async Task MissingGatewayFacts_RenderUnavailableInsteadOfReadyLocalOrZero()
    {
        await ui.RunOnUIAsync(() =>
        {
            var unknown = new SessionInfo { Key = "agent:main:unknown", Status = "unknown" };
            Assert.Equal(
                (SessionsHubPresentation.T("NotAvailable"), SessionsHubPresentation.StatusTone.Unknown),
                SessionsHubPresentation.Status(unknown));
            Assert.Equal(SessionsHubPresentation.T("NotAvailable"), SessionsHubPresentation.Placement(null));
            Assert.Equal(SessionsHubPresentation.T("NotAvailable"), SessionsHubPresentation.RelativeTime(null, DateTime.UtcNow));
            Assert.Equal(SessionsHubPresentation.T("NotAvailable"),
                SessionsHubPresentation.WorktreeOwner(
                    new WorktreeRecord("w", "w", "", null, "", null, "", null, 0, 0, null),
                    _ => null));

            var worktreesLoading = SessionsHubPresentation.MastCopy(new SessionsHubState
            {
                IsConnected = true,
                Tab = SessionsHubTab.Worktrees,
                Worktrees = new WorktreesState(Load: SessionsHubLoad.Loading),
            });
            Assert.Equal(SessionsHubPresentation.T("Loading"), worktreesLoading.Subtitle);
        });
    }

    [Fact]
    public async Task PendingDelete_WhenGatewayEpochChanges_CancelsWithoutMutation()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        Task deletion = Task.CompletedTask;
        await ui.RunOnUIAsync(async () =>
        {
            await harness.Controller.ReloadAsync();
            deletion = harness.Controller.DeleteAsync(harness.Controller.State.Rows.Single(), "Initial name");
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.NotNull(harness.Controller.State.Dialog);
            harness.Gateway.Epoch++;
            harness.Controller.RefreshConnection();
            Assert.Null(harness.Controller.State.Dialog);
        });
        await deletion.WaitAsync(TimeSpan.FromSeconds(10));
        await ui.RunOnUIAsync(() => Assert.Empty(harness.Gateway.Deletions));
    }

    [Theory]
    [InlineData("reloading")]
    [InlineData("failed")]
    public async Task PendingDelete_AfterRosterStopsBeingCurrentDoesNotMutate(string refreshState)
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        var response = new TaskCompletionSource<SessionListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task deletion = Task.CompletedTask;
        Task reload = Task.CompletedTask;
        await ui.RunOnUIAsync(async () =>
        {
            await harness.Controller.ReloadAsync();
            deletion = harness.Controller.DeleteAsync(harness.Controller.State.Rows.Single(), "Initial name");
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            if (refreshState == "reloading")
            {
                harness.Gateway.List = _ => response.Task;
                reload = harness.Controller.ReloadAsync();
            }
            else
            {
                harness.Gateway.List = _ => Task.FromException<SessionListPage>(new TimeoutException("List unavailable"));
                await harness.Controller.ReloadAsync();
                Assert.Equal(SessionsHubLoad.Failed, harness.Controller.State.Load);
            }
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.NotNull(harness.Controller.State.Dialog);
            harness.Controller.CloseDialog(true);
        });
        await deletion.WaitAsync(TimeSpan.FromSeconds(10));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Empty(harness.Gateway.Deletions);
            Assert.Single(harness.Controller.State.Rows);
        });
        if (refreshState == "reloading")
        {
            response.SetResult(harness.Gateway.Page);
            await reload.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SessionMast_ReflectsLoadedAndEmptyStatesWithoutInventingCounts()
    {
        await ui.RunOnUIAsync(() =>
        {
            var current = new SessionsHubState
            {
                IsConnected = true,
                Load = SessionsHubLoad.Loaded,
                Rows = [new SessionInfo { Key = "agent:main:failed", Status = "failed" }],
            };
            Assert.Equal(
                (SessionsHubPresentation.F("HeroSessionsLoaded", "Zero", 1), SessionsHubPresentation.T("Subtitle")),
                SessionsHubPresentation.MastCopy(current));
            Assert.Equal(
                (SessionsHubPresentation.T("HeroSessionsNoMatches"), SessionsHubPresentation.T("HeroSessionsNoMatchesDetail")),
                SessionsHubPresentation.MastCopy(current with
                {
                    Filters = new SessionsHubFilters(Status: SessionArchiveFilter.Archived),
                    Rows = [],
                }));
            Assert.Equal(
                (SessionsHubPresentation.T("Loading"), SessionsHubPresentation.T("HeroSessionsLoadingDetail")),
                SessionsHubPresentation.MastCopy(current with { Load = SessionsHubLoad.Loading, Rows = [] }));
            Assert.Equal(
                (SessionsHubPresentation.T("LoadFailedTitle"), SessionsHubPresentation.T("NotAvailable")),
                SessionsHubPresentation.MastCopy(current with { Load = SessionsHubLoad.Failed, Rows = [] }));
            Assert.Equal(
                (SessionsHubPresentation.T("HeroSessionsDisconnected"), SessionsHubPresentation.T("HeroSessionsDisconnectedDetail")),
                SessionsHubPresentation.MastCopy(new SessionsHubState()));
            var lastSeen = new DateTime(2026, 6, 1, 13, 30, 0, DateTimeKind.Utc);
            Assert.Equal(
                (SessionsHubPresentation.F("HeroSessionsLastSeenAt", SessionsHubPresentation.LastSeenTime(lastSeen)),
                    SessionsHubPresentation.T("HeroSessionsDisconnectedDetail")),
                SessionsHubPresentation.MastCopy(new SessionsHubState { LastLoadedAtUtc = lastSeen }));
            Assert.Equal(
                (SessionsHubPresentation.F("WorktreeMastTitle", "home-gateway"),
                    SessionsHubPresentation.F("WorktreeMastSubtitle", 2, 1)),
                SessionsHubPresentation.MastCopy(new SessionsHubState
                {
                    IsConnected = true,
                    Tab = SessionsHubTab.Worktrees,
                    GatewayName = "home-gateway",
                    Worktrees = new WorktreesState(
                        Load: SessionsHubLoad.Loaded,
                        Rows:
                        [
                            new WorktreeRecord("a", "a", "", null, "", null, "", null, 0, 0, null),
                            new WorktreeRecord("b", "b", "", null, "", null, "", null, 0, 0, 1),
                            new WorktreeRecord("c", "c", "", null, "", null, "", null, 0, 0, null),
                        ]),
                }));
        });
    }

    [Theory]
    [InlineData(SessionArchiveFilter.Active, "", "Current sessions", "2 shown of 5 loaded",
        "Loaded matches: 3. Background hidden, completed hidden, unrouted hidden. Change in Options.")]
    [InlineData(SessionArchiveFilter.Archived, "", "Archived sessions", "1 shown of 1 loaded",
        "Loaded matches: 1. Background hidden, completed shown, unrouted hidden. Change in Options.")]
    [InlineData(SessionArchiveFilter.All, "", "All sessions", "2 shown of 6 loaded",
        "Loaded matches: 5. Background hidden, completed shown, unrouted hidden. Change in Options.")]
    [InlineData(SessionArchiveFilter.Active, "needle", "Matching sessions", "1 shown of 1 loaded",
        "Loaded matches: 1. Background hidden, completed hidden, unrouted hidden. Change in Options.")]
    [InlineData(SessionArchiveFilter.Active, "absent", "Matching sessions", "0 shown of 0 loaded",
        "Loaded matches: 0. Background hidden, completed hidden, unrouted hidden. Change in Options.")]
    public async Task InventoryHeadingCountAndFilterNote_DescribeTheLoadedView(
        SessionArchiveFilter status, string search, string title, string count, string note)
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(async () =>
        {
            var seed = harness.Gateway.Page.Sessions.Single();
            var rows = Enumerable.Range(0, 6).Select(index =>
            {
                var row = seed.Clone();
                row.Key = "agent:main:inventory-" + index;
                row.SessionId = "inventory-" + index;
                row.Label = index == 0 ? "needle" : "Session " + index;
                row.Archived = index == 5;
                row.Status = index == 3 ? "done" : "running";
                row.HasActiveRun = index != 3;
                row.IsBackground = index == 4;
                return row;
            }).ToArray();
            harness.Gateway.List = query => Task.FromResult(new SessionListPage
            {
                Sessions = rows.Where(row =>
                        (query.Archive == SessionArchiveFilter.All || row.Archived == (query.Archive == SessionArchiveFilter.Archived))
                        && (string.IsNullOrEmpty(query.Search) || row.Label == query.Search))
                    .ToArray(),
                // A Gateway total is not the number loaded or shown.
                TotalCount = 800,
            });
            harness.Controller.CloseDetails();
            harness.Controller.SetFilters(new SessionsHubFilters(Status: status, Search: search), reload: false);
            harness.Controller.SetPageSize(2);
            await harness.Controller.ReloadAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            var heading = harness.Find<FrameworkElement>("SessionsHubAllSessions");
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(heading), text => text.Text == title);
            Assert.Equal(count, harness.Find<TextBlock>("SessionsHubInventoryCount").Text);
            Assert.Equal(note, harness.Find<TextBlock>("SessionsHubFilterNote").Text);
            Assert.Equal("Sessions", harness.Find<TextBlock>("SessionsPageMarker").Text);
            Assert.Equal(SessionsHubPresentation.MastCopy(harness.Controller.State).Headline,
                harness.Find<TextBlock>("SessionsHubMastHeadline").Text);
        });
    }

    [Fact]
    public async Task ReloadingRoster_DisablesMutationsUntilCurrentRowsReturn()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        var response = new TaskCompletionSource<SessionListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reload = Task.CompletedTask;
        await ui.RunOnUIAsync(() =>
        {
            harness.Gateway.List = _ => response.Task;
            reload = harness.Controller.ReloadAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            Assert.True(harness.Controller.State.IsReloading);
            Assert.False(harness.Controller.State.CanMutateRows);
            Assert.Single(harness.Controller.State.Rows);
            Assert.False(harness.Find<TextBox>("SessionsHubLabel").IsEnabled);
            await harness.Controller.PatchAsync(harness.Controller.State.Rows.Single(), new SessionPatch { Pinned = true });
            Assert.Empty(harness.Gateway.Patches);
        });
        response.SetResult(harness.Gateway.Page);
        await reload.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.True(harness.Controller.State.CanMutateRows);
            Assert.True(harness.Find<TextBox>("SessionsHubLabel").IsEnabled);
        });
    }

    [Fact]
    public async Task StaleRosterDisabledRowActions_ExplainWhyTheyAreUnavailable()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        const string rosterNotCurrent = "The session list is not current. Wait for refresh to finish before changing sessions.";
        var response = new TaskCompletionSource<SessionListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reload = Task.CompletedTask;
        await ui.RunOnUIAsync(async () =>
        {
            await harness.Controller.ReloadAsync();
            harness.Controller.OpenDetails(Harness.Key);
            harness.Gateway.List = _ => response.Task;
            reload = harness.Controller.ReloadAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.False(harness.Controller.State.CanMutateRows);
            var rowAction = harness.Find<Button>("SessionsHubCommandPin");
            Assert.False(rowAction.IsEnabled);
            Assert.Equal(rosterNotCurrent, ToolTipService.GetToolTip(rowAction));
            Assert.Equal(rosterNotCurrent, AutomationProperties.GetHelpText(rowAction));

        });
        response.SetResult(harness.Gateway.Page);
        await reload.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ClientFilterNoteAndPageCount_UpdateWithoutInventingUnloadedMatches()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(async () =>
        {
            var seed = harness.Gateway.Page.Sessions.Single();
            harness.Gateway.Page = new SessionListPage
            {
                Sessions = Enumerable.Range(0, 3).Select(index =>
                {
                    var row = seed.Clone();
                    row.Key = "agent:main:page-" + index;
                    row.SessionId = "page-" + index;
                    row.IsBackground = index == 2;
                    row.Status = "running";
                    row.HasActiveRun = true;
                    return row;
                }).ToArray(),
            };
            harness.Controller.CloseDetails();
            harness.Controller.SetFilters(new SessionsHubFilters(ShowBackground: true, ShowCompleted: true, IncludeUnknown: true),
                reload: false);
            await harness.Controller.ReloadAsync();
            harness.Controller.SetPageSize(2);
            harness.Controller.SetPage(1);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal("1 shown of 3 loaded", harness.Find<TextBlock>("SessionsHubInventoryCount").Text);
            Assert.Equal("Loaded matches: 3. Background shown, completed shown, unrouted shown. Change in Options.",
                harness.Find<TextBlock>("SessionsHubFilterNote").Text);
            harness.Controller.SetFilters(harness.Controller.State.Filters with { ShowBackground = false });
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal("2 shown of 3 loaded", harness.Find<TextBlock>("SessionsHubInventoryCount").Text);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArchiveUndo_RevealsItsPageAndGroupAndReturnsFocusToTheRestoredRow(bool wasPinned)
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        SessionInfo? target = null;
        SessionListPage? restoredPage = null;
        await ui.RunOnUIAsync(async () =>
        {
            target = harness.Gateway.Page.Sessions.Single().Clone();
            target.Label = "Morning brief";
            target.Channel = "telegram";
            target.Category = "Release";
            target.Pinned = wasPinned;
            target.Status = "running";
            target.HasActiveRun = true;
            var other = target.Clone();
            other.Pinned = wasPinned;
            other.Key = "agent:main:other";
            other.SessionId = "other";
            other.Channel = "slack";
            other.UpdatedAt = DateTime.UtcNow;
            target.UpdatedAt = other.UpdatedAt.Value.AddMinutes(-1);
            restoredPage = new SessionListPage { Sessions = [other, target] };
            harness.Gateway.Page = restoredPage;
            await harness.Controller.ReloadAsync();
            harness.Controller.CloseDetails();
            harness.Controller.SetPageSize(1);
            harness.Controller.SetGroupBy(SessionsGroupBy.Category);
            harness.Gateway.List = _ => Task.FromResult(new SessionListPage { Sessions = [other] });
            await harness.Controller.ArchiveAsync(target, "Morning brief");
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => WaitForElementAsync(harness, "SessionsHubGroup:Release"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Contains("telegram", harness.Controller.State.Notice!.Message);
            Assert.Equal("Undo", harness.Find<Button>("SessionsHubNoticeAction").Content);
            Assert.True(harness.Find<Button>("SessionsHubNoticeDismiss").IsEnabled);
            Invoke(harness.Find<Button>("SessionsHubGroup:Release"));
            harness.Gateway.List = _ => Task.FromResult(restoredPage!);
            Invoke(harness.Find<Button>("SessionsHubNoticeAction"));
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(() =>
        {
            var row = TestSupport.FindDescendants<Expander>(harness.Root!).FirstOrDefault(element =>
                AutomationProperties.GetAutomationId(element) == "SessionsHubRow:" + Harness.Key && IsShown(element, harness.Root!));
            var focus = FocusManager.GetFocusedElement(harness.Root!.XamlRoot) as DependencyObject;
            while (focus is not null && focus != row)
                focus = VisualTreeHelper.GetParent(focus);
            return row is { IsExpanded: true } && focus == row && harness.Controller.State.RowReveal is null;
        }, "archive Undo restored row focus", () =>
        {
            var rows = TestSupport.FindDescendants<Expander>(harness.Root!)
                .Where(element => AutomationProperties.GetAutomationId(element) == "SessionsHubRow:" + Harness.Key).ToList();
            var row = rows.FirstOrDefault(element => IsShown(element, harness.Root!)) ?? rows.FirstOrDefault();
            var focused = FocusManager.GetFocusedElement(harness.Root!.XamlRoot) as FrameworkElement;
            return $"Row copies: {rows.Count}; shown: {(row is null ? null : IsShown(row, harness.Root!))}; expanded: {row?.IsExpanded}; focused: {focused?.GetType().Name} " +
                $"'{(focused is null ? null : AutomationProperties.GetAutomationId(focused))}'; " +
                $"reveal pending: {harness.Controller.State.RowReveal is not null}; page: {harness.Controller.State.PageIndex}.";
        }));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal(1, harness.Controller.State.PageIndex);
            Assert.True(harness.FindShown<Expander>("SessionsHubRow:" + Harness.Key).IsExpanded);
            Assert.Null(harness.Controller.State.Notice);
            Assert.Equal(new[] { Harness.Key, Harness.Key }, harness.Gateway.PatchKeys);
            Assert.True(harness.Gateway.Patches[0].Archived.Value);
            Assert.False(harness.Gateway.Patches[1].Archived.Value);
            Assert.All(harness.Gateway.Patches, patch => Assert.Equal("session-one", patch.ExpectedSessionId));
            Assert.Equal(wasPinned, harness.Gateway.Patches[1].Pinned.IsSpecified);
            if (wasPinned)
                Assert.True(harness.Gateway.Patches[1].Pinned.Value);
        });
    }

    [Fact]
    public async Task ArchiveAndUnarchive_CarryTheObservedSessionIdentity()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            var row = harness.Gateway.Page.Sessions.Single();
            await harness.Controller.ArchiveAsync(row, "Review");
            row.Archived = true;
            await harness.Controller.ReloadAsync();
            await harness.Controller.UnarchiveAsync(row, "Review");
            Assert.Equal(new[] { Harness.Key, Harness.Key }, harness.Gateway.PatchKeys);
            Assert.Equal(new[] { true, false }, harness.Gateway.Patches.Select(patch => patch.Archived.Value));
            Assert.All(harness.Gateway.Patches, patch => Assert.Equal("session-one", patch.ExpectedSessionId));
        });
    }

    [Fact]
    public async Task PendingPrompt_SurvivesDisconnectWithoutConfirmingAndClosesOnReconnect()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            var row = harness.Gateway.Page.Sessions.Single();
            var disconnected = harness.Controller.RunSharedActionAsync(WorkspaceSessionMenuAction.Reset, row, "Review");
            Assert.NotNull(harness.Controller.State.Dialog);
            harness.Gateway.Connected = false;
            harness.Controller.RefreshConnection();
            // The Settings shell defers navigation pruning until this real prompt closes.
            Assert.NotNull(harness.Controller.State.Dialog);
            harness.Controller.CloseDialog(true);
            await disconnected;
            Assert.Empty(harness.Gateway.Resets);

            harness.Gateway.Connected = true;
            harness.Controller.RefreshConnection();
            await harness.Controller.ReloadAsync();
            row = harness.Gateway.Page.Sessions.Single();
            var reconnected = harness.Controller.RunSharedActionAsync(WorkspaceSessionMenuAction.Reset, row, "Review");
            Assert.NotNull(harness.Controller.State.Dialog);
            harness.Gateway.Epoch++;
            harness.Controller.RefreshConnection();
            Assert.Null(harness.Controller.State.Dialog);
            await reconnected;
            Assert.Empty(harness.Gateway.Resets);
        });
    }

    [Fact]
    public async Task ArchiveUndo_RejectsAnOldConnectionAndDoesNotDismissItsError()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            var row = harness.Gateway.Page.Sessions.Single();
            await harness.Controller.ArchiveAsync(row, "Review");
            var undo = Assert.IsType<SessionsHubNotice>(harness.Controller.State.Notice);
            harness.Gateway.Epoch++;
            harness.Controller.RefreshConnection();
            undo.Action!();
            harness.Controller.DismissNotice(undo);
            Assert.Single(harness.Gateway.Patches);
            Assert.Equal(InfoBarSeverity.Error, harness.Controller.State.Notice?.Severity);
            Assert.Null(harness.Controller.State.RowReveal);
        });
    }

    [Fact]
    public async Task ArchiveUndo_DoesNotChangeTheViewAfterNavigationWhileAcceptanceIsPending()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        var acceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var showCompleted = false;
        await ui.RunOnUIAsync(async () =>
        {
            var row = harness.Gateway.Page.Sessions.Single();
            await harness.Controller.ArchiveAsync(row, "Review");
            harness.Gateway.PatchAcknowledgement = acceptance.Task;
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Invoke(harness.Find<Button>("SessionsHubNoticeAction"));
            Assert.True(harness.Controller.State.IsBusy);
            showCompleted = !harness.Controller.State.Filters.ShowCompleted;
            harness.Controller.SetFilters(harness.Controller.State.Filters with { ShowCompleted = showCompleted });
            acceptance.SetResult();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => !harness.Controller.State.IsBusy, "archive Undo acceptance"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal(showCompleted, harness.Controller.State.Filters.ShowCompleted);
            Assert.Empty(harness.Controller.State.ExpandedKeys);
            Assert.Null(harness.Controller.State.RowReveal);
            Assert.Equal(2, harness.Gateway.Patches.Count);
        });
    }

    [Fact]
    public async Task GatewayFieldLabels_RenderPlacementAndBaseBranch()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Page.Sessions.Single().PlacementState = "active";
            await harness.Controller.ReloadAsync();
            harness.Controller.OpenDetails(Harness.Key);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
            Assert.Contains(TestSupport.FindDescendants<TextBlock>(harness.Root!), text => text.Text == "Placement"));

        // ContentDialog content renders in the XAML popup layer, outside the mounted root.
        IEnumerable<FrameworkElement> OpenDialogContent() =>
            VisualTreeHelper.GetOpenPopupsForXamlRoot(harness.Root!.XamlRoot)
                .Where(popup => popup.Child is not null)
                .SelectMany(popup => TestSupport.FindDescendants<FrameworkElement>(popup.Child));
        Task? pendingDialog = null;
        try
        {
            await ui.RunOnUIAsync(() => { pendingDialog = harness.Controller.CreateWorktreeAsync(); });
            await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                () => OpenDialogContent().Any(element => AutomationProperties.GetAutomationId(element) == "SessionsHubNewWorktreeBase"
                    && TestSupport.FindDescendants<TextBlock>(element).Any(text => text.Text == "Base branch")),
                "New worktree base branch field"));
        }
        finally
        {
            await ui.RunOnUIAsync(() => harness.Controller.CloseDialog(false));
            await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                () => !VisualTreeHelper.GetOpenPopupsForXamlRoot(harness.Root!.XamlRoot).Any(popup =>
                    popup.Child is ContentDialog || (popup.Child is not null && TestSupport.FindDescendants<ContentDialog>(popup.Child).Any())),
                "New worktree dialog closed"));
        }
        await pendingDialog!;
    }

    [Fact]
    public async Task SessionsPageHostMarker_IsAccessibleAndTracksPageWidth()
    {
        SessionsPage? page = null;
        await ui.RunOnUIAsync(() =>
        {
            page = new SessionsPage { Width = 760, Height = 700 };
            ui.Container.Children.Add(page);
        });
        try
        {
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
                () => page!.FindName("ResponsiveHostMarker") is ProgressBar { ActualWidth: > 0 },
                "Sessions page responsive host marker"));
            await ui.RunOnUIAsync(() =>
            {
                var marker = Assert.IsType<ProgressBar>(page!.FindName("ResponsiveHostMarker"));
                Assert.InRange(marker.ActualWidth, 759, 761);
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(marker);
                Assert.NotNull(peer);
                Assert.Equal("SessionsHubHost", peer.GetAutomationId());
                Assert.True(peer.IsControlElement());
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() => ui.Container.Children.Remove(page));
        }
    }

    [Fact]
    public async Task MastWebButton_KeepsItsFreshStyleWhileLoadingIndicatorAppears()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        var response = new TaskCompletionSource<SessionListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reload = Task.CompletedTask;
        Style? webStyle = null;
        Brush? webBackground = null;
        Thickness webBorder = default;
        await ui.RunOnUIAsync(() =>
        {
            var web = harness.Find<Button>("SessionsHubOpenGatewayWeb");
            var refresh = harness.Find<Button>("SessionsPageRefresh");
            webStyle = web.Style;
            webBackground = web.Background;
            webBorder = web.BorderThickness;
            Assert.NotSame(web.Style, refresh.Style);
            harness.Gateway.List = _ => response.Task;
            reload = harness.Controller.ReloadAsync(preserveCount: true);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.True(harness.Controller.State.IsReloading);
            var web = harness.Find<Button>("SessionsHubOpenGatewayWeb");
            var refresh = harness.Find<Button>("SessionsPageRefresh");
            Assert.Same(webStyle, web.Style);
            Assert.Same(webBackground, web.Background);
            Assert.Equal(webBorder, web.BorderThickness);
            Assert.NotSame(web.Style, refresh.Style);
        });
        response.SetResult(harness.Gateway.Page);
        await reload;
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            var web = harness.Find<Button>("SessionsHubOpenGatewayWeb");
            Assert.Same(webStyle, web.Style);
            Assert.Equal(webBorder, web.BorderThickness);
        });
    }

    [Theory]
    [InlineData(1400)]
    [InlineData(1000)]
    [InlineData(760)]
    [InlineData(520)]
    public async Task ResponsiveLayout_UsesInlineErrorBadgesAndKeepsMastActionsAligned(double width)
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true, width: width, height: 700);
        await ui.RunOnUIAsync(async () =>
        {
            var seed = harness.Gateway.Page.Sessions.Single();
            harness.Gateway.Page = new SessionListPage
            {
                Sessions = Enumerable.Range(0, 4).Select(index =>
                {
                    var row = seed.Clone();
                    row.Key = $"agent:main:responsive-{index}";
                    row.SessionId = $"responsive-{index}";
                    row.Label = $"Responsive session {index}";
                    row.UpdatedAt = DateTime.UtcNow.AddMinutes(-index);
                    row.Status = index < 2 ? "failed" : "done";
                    row.HasActiveRun = false;
                    row.LastRunError = index < 2 ? $"Responsive run error {index}" : null;
                    return row;
                }).ToArray(),
            };
            await harness.Controller.ReloadAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<FrameworkElement>(harness.Root!)
                .Count(element => AutomationProperties.GetAutomationId(element).StartsWith("SessionsHubStoppedWithError:", StringComparison.Ordinal)) == 2,
            $"responsive stopped-error badges at {width}"));
        await ui.RunOnUIAsync(() =>
        {
            var badges = TestSupport.FindDescendants<FrameworkElement>(harness.Root!)
                .Where(element => AutomationProperties.GetAutomationId(element).StartsWith("SessionsHubStoppedWithError:", StringComparison.Ordinal))
                .ToArray();
            Assert.DoesNotContain(TestSupport.FindDescendants<FrameworkElement>(harness.Root!),
                element => AutomationProperties.GetAutomationId(element) == "SessionsHubJumpBackIn");
            Assert.DoesNotContain(TestSupport.FindDescendants<FrameworkElement>(harness.Root!),
                element => AutomationProperties.GetAutomationId(element) == "SessionsHubNeedsYou");
            Assert.DoesNotContain(TestSupport.FindDescendants<FrameworkElement>(harness.Root!),
                element => AutomationProperties.GetAutomationId(element) == "SessionsHubMastBackdrop");
            var hero = harness.Find<FrameworkElement>("SessionsHubMast");
            var heroLeft = hero.TransformToVisual(harness.Root).TransformPoint(default).X;
            Assert.InRange(hero.ActualWidth,
                PageResponsiveLayout.ContentWidth(width) - 1,
                PageResponsiveLayout.ContentWidth(width) + 1);
            Assert.InRange(Math.Abs(heroLeft - ((width - hero.ActualWidth) / 2)), 0, 1);
            var title = harness.Find<TextBlock>("SessionsPageMarker");
            Assert.Equal("Sessions", title.Text);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));
            Assert.Contains(title, TestSupport.FindDescendants<TextBlock>(hero));
            Assert.NotNull(harness.Find<FrameworkElement>("SessionsHubMast:Glyph"));
            var mastHeadline = harness.Find<TextBlock>("SessionsHubMastHeadline");
            Assert.Contains(mastHeadline, TestSupport.FindDescendants<TextBlock>(hero));
            var openInWeb = harness.Find<Button>("SessionsHubOpenGatewayWeb");
            var openInWebGlyph = Assert.Single(TestSupport.FindDescendants<TextBlock>(openInWeb),
                text => text.Text == "\uE8A7");
            Assert.Equal("Open in web", AutomationProperties.GetName(openInWeb));
            Assert.Equal("\uE8A7", openInWebGlyph.Text);
            Assert.Contains(openInWeb, TestSupport.FindDescendants<Button>(hero));
            var tabs = harness.Find<FrameworkElement>("SessionsHubTabs");
            var tabsLeft = tabs.TransformToVisual(harness.Root).TransformPoint(default).X;
            Assert.InRange(Math.Abs(heroLeft - tabsLeft), 0, 1.25);
            Assert.True(tabs.TransformToVisual(harness.Root).TransformPoint(default).Y
                >= hero.TransformToVisual(harness.Root).TransformPoint(default).Y + hero.ActualHeight);
            Assert.Equal(2, badges.Length);
            var firstRow = harness.Find<Expander>("SessionsHubRow:agent:main:responsive-0");
            Assert.Contains(SessionsHubPresentation.T("StoppedWithError"), AutomationProperties.GetName(firstRow));
            Assert.Equal("Responsive run error 0",
                Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(badges[0]) as string);
            Assert.All(badges, badge => Assert.True(badge.ActualWidth > 0));

            var actions = harness.Find<StackPanel>("SessionsHubMastActions");
            var actionButtons = TestSupport.FindDescendants<Button>(actions).Where(button => IsShown(button, actions)).ToArray();
            Assert.NotEmpty(actionButtons);
            var actionCenters = actionButtons.Select(button =>
            {
                var top = button.TransformToVisual(actions).TransformPoint(default).Y;
                return top + (button.ActualHeight / 2);
            }).ToArray();
            Assert.InRange(actionCenters.Max() - actionCenters.Min(), 0, 4);

            var search = harness.Find<TextBox>("SessionsHubSearch");
            Assert.False(search.AcceptsReturn);
            Assert.Equal(TextWrapping.NoWrap, search.TextWrapping);
            Assert.InRange(search.ActualHeight, 31.5, 32.5);

            var agentFilter = harness.Find<FrameworkElement>("SessionsHubAgentFilter");
            var options = harness.Find<FrameworkElement>("SessionsHubOptions");
            FrameworkElement[] toolbarRow = width >= PageResponsiveLayout.WideMin
                ? [agentFilter, options, search]
                : [agentFilter, options];
            var toolbarCenters = toolbarRow.Select(control =>
                control.TransformToVisual(harness.Root).TransformPoint(default).Y + (control.ActualHeight / 2)).ToArray();
            Assert.InRange(toolbarCenters.Max() - toolbarCenters.Min(), 0, 1.5);

            foreach (var badge in badges)
            {
                var row = harness.Find<FrameworkElement>(
                    "SessionsHubRow:" + AutomationProperties.GetAutomationId(badge)["SessionsHubStoppedWithError:".Length..]);
                var bounds = badge.TransformToVisual(row).TransformBounds(
                    new global::Windows.Foundation.Rect(0, 0, badge.ActualWidth, badge.ActualHeight));
                Assert.True(bounds.Left >= -1 && bounds.Right <= row.ActualWidth + 1,
                    $"Stopped-error badge extends past its row at width {width}: {bounds}.");
            }
        });
    }

    [Fact]
    public async Task PageScroll_MovesHeroWithContentWithoutAFixedBackdrop()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true, width: 1000, height: 520);
        var scrolled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        double markerTop = 0;
        await ui.RunOnUIAsync(() =>
        {
            var root = Assert.IsAssignableFrom<FrameworkElement>(harness.Root);
            var scroll = harness.Find<ScrollView>("SessionsHubPageScroll");
            var marker = harness.Find<FrameworkElement>("SessionsPageMarker");
            Assert.True(scroll.ScrollableHeight > 200);
            Assert.DoesNotContain(TestSupport.FindDescendants<FrameworkElement>(root),
                element => AutomationProperties.GetAutomationId(element) == "SessionsHubMastBackdrop");

            markerTop = marker.TransformToVisual(root).TransformPoint(default).Y;
            scroll.ScrollCompleted += (_, _) => scrolled.TrySetResult();
            scroll.ScrollTo(0, 200);
        });
        await scrolled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ui.RunOnUIAsync(() =>
        {
            var root = Assert.IsAssignableFrom<FrameworkElement>(harness.Root);
            var scroll = harness.Find<ScrollView>("SessionsHubPageScroll");
            var marker = harness.Find<FrameworkElement>("SessionsPageMarker");
            Assert.InRange(scroll.VerticalOffset, 199, 201);
            Assert.True(marker.TransformToVisual(root).TransformPoint(default).Y < markerTop - 150);
        });
    }

    [Fact]
    public async Task PageInset_UpdatesScrollViewportWithoutRemounting()
    {
        await using var harness = await Harness.CreateAsync(
            ui, mount: true, width: 1000, height: 520, topInset: 56);
        await harness.RenderAsync();
        ScrollView? originalScroll = null;
        FrameworkElement? root = null;
        await ui.RunOnUIAsync(() =>
        {
            root = Assert.IsAssignableFrom<FrameworkElement>(harness.Root);
            var scroll = harness.Find<ScrollView>("SessionsHubPageScroll");
            originalScroll = scroll;
            Assert.Equal(56d, scroll.Margin.Top);
            Assert.InRange(scroll.TransformToVisual(root).TransformPoint(default).Y, 55.5, 56.5);
        });

        await ui.RunOnUIAsync(() => harness.Presentation.ApplyTopInset(0));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => harness.Find<ScrollView>("SessionsHubPageScroll").TransformToVisual(root!).TransformPoint(default).Y <= 0.5,
            "page viewport without inset"));
        await ui.RunOnUIAsync(() =>
        {
            var scroll = harness.Find<ScrollView>("SessionsHubPageScroll");
            Assert.Same(originalScroll, scroll);
            Assert.Equal(0d, scroll.Margin.Top);
            Assert.InRange(scroll.TransformToVisual(root!).TransformPoint(default).Y, 0, 0.5);
        });

        await ui.RunOnUIAsync(() => harness.Presentation.ApplyTopInset(56));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => harness.Find<ScrollView>("SessionsHubPageScroll").TransformToVisual(root!).TransformPoint(default).Y >= 55.5,
            "page viewport with restored inset"));
        await ui.RunOnUIAsync(() =>
        {
            var scroll = harness.Find<ScrollView>("SessionsHubPageScroll");
            Assert.Same(originalScroll, scroll);
            Assert.Equal(56d, scroll.Margin.Top);
            Assert.InRange(scroll.TransformToVisual(root!).TransformPoint(default).Y, 55.5, 56.5);
        });
    }

    [Fact]
    public async Task SessionAndWorktreeRows_UseSharedCardGap()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true, width: 1000, height: 700);
        var first = Page("First session").Sessions[0];
        var second = first.Clone();
        second.Key = "agent:main:two";
        second.SessionId = "session-two";
        second.Label = "Second session";
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Page = new SessionListPage { Sessions = [first, second] };
            await harness.Controller.ReloadAsync();
            harness.Controller.OpenDetails(first.Key);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => AssertCardGapAsync(harness,
            "SessionsHubRow:" + first.Key, "SessionsHubRow:" + second.Key));

        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Worktrees = Task.FromResult<IReadOnlyList<WorktreeRecord>?>(
            [
                new WorktreeRecord("wt-one", "one", "C:\\src", "C:\\src.one", "one", "main",
                    "manual", null, 1, 2, null),
                new WorktreeRecord("wt-two", "two", "C:\\src", "C:\\src.two", "two", "main",
                    "manual", null, 1, 2, null),
            ]);
            harness.Controller.SetTab(SessionsHubTab.Worktrees);
            await harness.Controller.LoadWorktreesAsync();
            harness.Controller.SetWorktreeDetails("wt-one");
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => AssertCardGapAsync(harness, "SessionsHubWorktree:wt-one", "SessionsHubWorktree:wt-two"));
    }

    /// <summary>Waits for the two shown cards to settle (tab switches and loads re-lay them out), then checks their gap.</summary>
    private static async Task AssertCardGapAsync(Harness harness, string firstId, string secondId)
    {
        double? Gap()
        {
            var first = TestSupport.FindDescendants<Expander>(harness.Root!).FirstOrDefault(element =>
                AutomationProperties.GetAutomationId(element) == firstId && IsShown(element, harness.Root!));
            var second = TestSupport.FindDescendants<Expander>(harness.Root!).FirstOrDefault(element =>
                AutomationProperties.GetAutomationId(element) == secondId && IsShown(element, harness.Root!));
            if (first is not { ActualHeight: > 0 } || second is not { ActualHeight: > 0 })
                return null;
            var firstBottom = first.TransformToVisual(harness.Root!).TransformPoint(default).Y + first.ActualHeight;
            return second.TransformToVisual(harness.Root!).TransformPoint(default).Y - firstBottom;
        }
        await TestSupport.WaitForRenderedConditionAsync(
            () => Gap() is { } gap && Math.Abs(gap - PageResponsiveLayout.GapInner) <= 0.5,
            $"{PageResponsiveLayout.GapInner} DIP gap between {firstId} and {secondId}",
            () => $"Measured gap: {Gap()?.ToString() ?? "rows not laid out"}.");
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(720)]
    public async Task SessionDetails_ExpandInsideTheirRowAtEveryWidth(double width)
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true, width: width);
        await ui.RunOnUIAsync(async () =>
        {
            var list = harness.Find<ItemsView>("SessionsHubList");
            var row = harness.Find<Expander>("SessionsHubRow:" + Harness.Key);
            Assert.True(row.IsExpanded);
            await WaitForExpansionAsync(row, expanded: true);
            var details = harness.Find<FrameworkElement>("SessionsHubDetails");
            Assert.Contains(details, TestSupport.FindDescendants<FrameworkElement>(row));
            Assert.Contains(row, TestSupport.FindDescendants<Expander>(list));
            Assert.Empty(TestSupport.FindDescendants<ScrollView>(details));
            Assert.True(details.ActualWidth >= row.ActualWidth - 80,
                $"Inline details width {details.ActualWidth} did not fill row width {row.ActualWidth}.");
            var header = Assert.IsAssignableFrom<FrameworkElement>(row.Header);
            var headerBounds = header.TransformToVisual(row).TransformBounds(
                new global::Windows.Foundation.Rect(0, 0, header.ActualWidth, header.ActualHeight));
            var detailsBounds = details.TransformToVisual(row).TransformBounds(
                new global::Windows.Foundation.Rect(0, 0, details.ActualWidth, details.ActualHeight));
            Assert.True(detailsBounds.Top >= headerBounds.Bottom, "Details must be below their session header.");
            // The row header already identifies the session; the expanded body must not repeat it.
            Assert.Single(TestSupport.FindDescendants<TextBlock>(row), text => text.Text == "Initial name");
            // Expansion uses a neutral card surface, not an accent tint.
            var surface = Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(row.Background).Color;
            Assert.True(surface.R == surface.G && surface.G == surface.B, $"Expanded surface is tinted: {surface}.");
            Assert.NotEqual(Assert.IsType<Microsoft.UI.Xaml.Media.SolidColorBrush>(row.BorderBrush).Color,
                ((Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]).Color);
            var settings = harness.Find<Expander>("SessionsHubSettings");
            var technical = harness.Find<Expander>("SessionsHubTechnicalDetails");
            Assert.Equal(HorizontalAlignment.Stretch, settings.HorizontalContentAlignment);
            Assert.Equal(HorizontalAlignment.Stretch, technical.HorizontalContentAlignment);
            var settingsContent = Assert.IsAssignableFrom<FrameworkElement>(settings.Content);
            Assert.True(settingsContent.ActualWidth >= settings.ActualWidth - 80,
                $"Settings content width {settingsContent.ActualWidth} did not fill expander width {settings.ActualWidth} at {width}.");
            ((IExpandCollapseProvider)new ExpanderAutomationPeer(technical).GetPattern(PatternInterface.ExpandCollapse)).Expand();
            await WaitForExpansionAsync(technical, expanded: true);
            var technicalContent = Assert.IsAssignableFrom<FrameworkElement>(technical.Content);
            await TestSupport.WaitForRenderedConditionAsync(
                () => technicalContent.ActualWidth > 0,
                $"technical details content at {width}");
            Assert.True(technicalContent.ActualWidth >= technical.ActualWidth - 80,
                $"Technical content width {technicalContent.ActualWidth} did not fill expander width {technical.ActualWidth} at {width}.");
            Assert.InRange(harness.Find<DropDownButton>("SessionsHubMoveGroup").ActualWidth, 149, 151);
            if (width == 720)
            {
                var settingsBounds = settings.TransformToVisual(details).TransformBounds(
                    new global::Windows.Foundation.Rect(0, 0, settings.ActualWidth, settings.ActualHeight));
                var technicalBounds = technical.TransformToVisual(details).TransformBounds(
                    new global::Windows.Foundation.Rect(0, 0, technical.ActualWidth, technical.ActualHeight));
                Assert.InRange(Math.Abs(settingsBounds.Left - technicalBounds.Left), 0, 1);
                Assert.InRange(Math.Abs(settingsBounds.Right - technicalBounds.Right), 0, 1);
            }
            await VisualTestCapture.CaptureAsync(harness.Root!, $"SessionsInline-{width}-expanded");
            ((IExpandCollapseProvider)new ExpanderAutomationPeer(row).GetPattern(PatternInterface.ExpandCollapse)).Collapse();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            Assert.Null(harness.Controller.State.DetailsKey);
            var row = harness.Find<Expander>("SessionsHubRow:" + Harness.Key);
            Assert.False(row.IsExpanded);
            await WaitForExpansionAsync(row, expanded: false);
            // Collapsed details must not remain visible or reachable through UI Automation.
            var retained = TestSupport.FindDescendants<FrameworkElement>(row)
                .Where(element => AutomationProperties.GetAutomationId(element) == "SessionsHubDetails")
                .ToArray();
            Assert.All(retained, element => Assert.False(IsShown(element, row)));
            Invoke(harness.Find<Button>("SessionsHubOpen:" + Harness.Key));
            Assert.Equal(Harness.Key, Assert.Single(harness.OpenedSessions));
            Assert.False(row.IsExpanded);
            await VisualTestCapture.CaptureAsync(harness.Root!, $"SessionsInline-{width}-collapsed");
            ((IExpandCollapseProvider)new ExpanderAutomationPeer(row).GetPattern(PatternInterface.ExpandCollapse)).Expand();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<TextBox>(harness.Root!).Any(box =>
                AutomationProperties.GetAutomationId(box) == "SessionsHubLabel" && IsShown(box, harness.Root!)),
            "re-expanded session details"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal(Harness.Key, harness.Controller.State.DetailsKey);
            Assert.Equal("Initial name", harness.FindShown<TextBox>("SessionsHubLabel").Text);
        });
    }

    [Fact]
    public async Task MoveGroupAction_Keeps150DipWidthAcrossSameHostResize()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true, width: 1200);
        foreach (var width in new[] { 1200d, 720d, 520d })
        {
            await ui.RunOnUIAsync(() => harness.Root!.Width = width);
            await harness.RenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var details = harness.Find<FrameworkElement>("SessionsHubDetails");
                var move = harness.Find<DropDownButton>("SessionsHubMoveGroup");
                var moveBounds = move.TransformToVisual(details).TransformBounds(
                    new global::Windows.Foundation.Rect(0, 0, move.ActualWidth, move.ActualHeight));
                Assert.InRange(move.ActualWidth, 149, 151);
                Assert.True(moveBounds.Right <= details.ActualWidth + 1);
            });
        }
    }

    [Fact]
    public async Task TranscriptDetails_RevealTheMatchingPageAndCollapsedGroup()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        const string targetKey = "agent:main:two";
        await ui.RunOnUIAsync(async () =>
        {
            var first = harness.Gateway.Page.Sessions.Single();
            first.Category = "Release";
            var now = DateTime.UtcNow;
            first.UpdatedAt = now;
            var target = first.Clone();
            target.Key = targetKey;
            target.SessionId = "session-two";
            target.Label = "Second session";
            target.UpdatedAt = now.AddMinutes(-1);
            harness.Gateway.Page = new SessionListPage { Sessions = [first, target] };
            await harness.Controller.ReloadAsync();
            harness.Controller.CloseDetails();
            harness.Controller.SetPageSize(1);
            harness.Controller.SetGroupBy(SessionsGroupBy.Category);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => WaitForElementAsync(harness, "SessionsHubGroup:Release"));
        await ui.RunOnUIAsync(() =>
        {
            Invoke(harness.Find<Button>("SessionsHubGroup:Release"));
            harness.Gateway.Search = Task.FromResult(new SessionSearchResult
            {
                Hits = [new SessionSearchHit(targetKey, "match", "assistant", 0, "Transcript match")],
            });
            harness.Controller.SetTranscriptQuery("match");
            harness.Controller.SetTranscriptOpen(true);
            return harness.Controller.RunTranscriptSearchAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<Button>(harness.Root!)
                .Any(button => AutomationProperties.GetAutomationId(button) == "SessionsHubTranscriptDetails:0"),
            "completed transcript result button"));
        await ui.RunOnUIAsync(() => Invoke(harness.Find<Button>("SessionsHubTranscriptDetails:0")));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<Expander>(harness.Root!)
                .Any(row => AutomationProperties.GetAutomationId(row) == "SessionsHubRow:" + targetKey
                    && IsShown(row, harness.Root!)),
            "revealed transcript session row"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.False(harness.Controller.State.Transcript.IsOpen);
            Assert.Equal(1, harness.Controller.State.PageIndex);
            Assert.Equal(targetKey, harness.Controller.State.DetailsKey);
            var row = harness.FindShown<Expander>("SessionsHubRow:" + targetKey);
            Assert.True(row.IsExpanded);
            Assert.Contains(TestSupport.FindDescendants<FrameworkElement>(row),
                element => AutomationProperties.GetAutomationId(element) == "SessionsHubDetails");
        });
    }

    [Fact]
    public async Task NameSearch_OffersSeparateScopedTranscriptRequest()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(() => harness.Controller.SetSearch("needle"));
        await Task.Delay(350);
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.False(harness.Controller.State.Transcript.IsOpen);
            harness.Gateway.Search = Task.FromResult(new SessionSearchResult
            {
                Hits = [new SessionSearchHit(Harness.Key, "message", "assistant", 0, "needle in transcript")],
            });
            var prompt = harness.Find<Border>("SessionsHubTranscriptPrompt");
            Invoke(Assert.Single(TestSupport.FindDescendants<Button>(prompt)));
        });
        await Task.Delay(100);
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal("needle", harness.Controller.State.Transcript.Query);
            Assert.NotNull(harness.Find<Border>("SessionsHubTranscript"));
            Assert.NotNull(harness.Find<Border>("SessionsHubTranscriptScope"));
        });
    }

    [Fact]
    public async Task ConfirmedDelete_ShowsDeletionNoticeWithoutUndoAction()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        Task? deletion = null;
        await ui.RunOnUIAsync(() =>
        {
            var row = harness.Gateway.Page.Sessions.Single();
            deletion = harness.Controller.DeleteAsync(row, "Initial name");
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.NotNull(harness.Controller.State.Dialog);
            harness.Gateway.List = _ => Task.FromResult(new SessionListPage());
            harness.Controller.CloseDialog(true);
        });
        await deletion!;
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Single(harness.Gateway.Deletions);
            var notice = Assert.IsType<SessionsHubNotice>(harness.Controller.State.Notice);
            Assert.Null(notice.Action);
            Assert.Null(notice.ActionLabel);
            Assert.DoesNotContain(TestSupport.FindDescendants<Button>(harness.Root!),
                button => AutomationProperties.GetAutomationId(button) == "SessionsHubNoticeAction");
            Assert.True(IsShown(harness.Find<FrameworkElement>("SessionsHubNotice"), harness.Root!));
        });
    }

    [Theory]
    [InlineData("operator.admin", true, false, false)]
    [InlineData("operator.read", false, true, false)]
    [InlineData("operator.write", false, false, false)]
    [InlineData("operator.write", false, true, true)]
    [InlineData("operator.admin", false, false, true)]
    public async Task SingleDelete_PreservesMainProtectionPermissionsAndIdentity(
        string scope, bool isMain, bool archived, bool allowed)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Scopes = [scope];
            var row = harness.Gateway.Page.Sessions.Single();
            row.Key = isMain ? "agent:main:main" : Harness.Key;
            row.Archived = archived;
            harness.Controller.RefreshConnection();
            await harness.Controller.ReloadAsync();
            var deletion = harness.Controller.DeleteAsync(row, "Review");
            Assert.Equal(allowed, harness.Controller.State.Dialog is not null);
            Assert.Empty(harness.Gateway.Deletions);
            if (allowed)
                harness.Controller.CloseDialog(true);
            await deletion;
            if (!allowed)
            {
                Assert.Empty(harness.Gateway.Deletions);
                Assert.NotNull(harness.Controller.State.Notice);
                return;
            }
            var request = Assert.Single(harness.Gateway.Deletions);
            Assert.Equal(Harness.Key, request.Key);
            Assert.Equal("main", request.AgentId);
            Assert.Equal("session-one", request.ExpectedSessionId);
            Assert.Equal(archived, request.ArchivedOnly);
        });
    }

    [Theory]
    [InlineData("refused", InfoBarSeverity.Warning, true)]
    [InlineData("preserved", InfoBarSeverity.Informational, false)]
    [InlineData("timeout", InfoBarSeverity.Error, true)]
    [InlineData("rejected", InfoBarSeverity.Error, true)]
    public async Task SingleDelete_ReportsTheGatewayOutcomeWithoutFalseSuccess(
        string result, InfoBarSeverity severity, bool detailsRemainOpen)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Delete = request => result switch
            {
                "timeout" => Task.FromException<SessionDeleteOutcome>(new TimeoutException()),
                "rejected" => Task.FromException<SessionDeleteOutcome>(new InvalidOperationException("Delete rejected")),
                _ => Task.FromResult(new SessionDeleteOutcome(request.Key, result != "refused",
                    result == "preserved" ? new PreservedWorktreeInfo("worktree-one", "feature", @"C:\fixture", "busy") : null)),
            };
            var deletion = harness.Controller.DeleteAsync(harness.Gateway.Page.Sessions.Single(), "Review");
            harness.Controller.CloseDialog(true);
            await deletion;
            Assert.Single(harness.Gateway.Deletions);
            var notice = Assert.IsType<SessionsHubNotice>(harness.Controller.State.Notice);
            Assert.Equal(severity, notice.Severity);
            Assert.Null(notice.Action);
            Assert.Equal(detailsRemainOpen, harness.Controller.State.ExpandedKeys.Contains(Harness.Key));
            if (result == "preserved")
                Assert.Contains("feature", notice.Message);
            else
                Assert.DoesNotContain("Deleted 1 session", notice.Message);
        });
    }

    [Fact]
    public async Task LoadAttentionRow_FindsItsSessionOutsideTheInventoryFilters()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        var attention = new SessionInfo
        {
            Key = "agent:main:filtered-attention", SessionId = "filtered-attention", Label = "Hidden failure",
            Status = "failed", HasActiveRun = false, IsBackground = true, LastRunError = "Failure details",
        };
        var filters = new SessionsHubFilters(
            AgentId: "other", Search: "unrelated", Status: SessionArchiveFilter.Archived,
            ActiveMinutes: "1", Limit: "1", IncludeGlobal: false);
        var detailsQueries = new List<SessionListQuery>();
        var preferenceWrites = 0;
        await ui.RunOnUIAsync(() =>
        {
            harness.Gateway.Page = new SessionListPage { Sessions = [attention] };
            harness.Gateway.List = _ => Task.FromResult(new SessionListPage());
            harness.Controller.CloseDetails();
            harness.Controller.SetFilters(filters, persist: false);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            Assert.Empty(harness.Controller.State.Rows);
            harness.Gateway.List = query =>
            {
                detailsQueries.Add(query);
                var prefixMatch = attention.Clone();
                prefixMatch.Key += "-newer";
                prefixMatch.SessionId = "prefix-match";
                prefixMatch.UpdatedAt = DateTime.UtcNow;
                return Task.FromResult(query.Offset == 0
                    ? new SessionListPage { Sessions = [prefixMatch], HasMore = true, NextOffset = 1 }
                    : new SessionListPage { Sessions = [attention] });
            };
            preferenceWrites = harness.ShowCompletedWrites;
            Assert.True(await harness.Controller.LoadAttentionRowAsync(attention.Key));
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Contains(harness.Controller.State.Rows, row => row.Key == attention.Key);
            Assert.Equal([0, 1], detailsQueries.Select(query => query.Offset));
            Assert.All(detailsQueries, query =>
            {
                Assert.Null(query.AgentId);
                Assert.Equal(attention.Key, query.Search);
                Assert.Equal(SessionArchiveFilter.All, query.Archive);
                Assert.Null(query.ActiveMinutes);
                Assert.True(query.IncludeGlobal);
                Assert.True(query.IncludeUnknown);
                Assert.Equal(1, query.Limit);
            });
            Assert.Equal(preferenceWrites, harness.ShowCompletedWrites);
        });
    }

    [Theory]
    [InlineData("filters")]
    [InlineData("reconnect")]
    [InlineData("deactivate")]
    public async Task AttentionDetails_DropsAResponseAfterNavigationOrConnectionChanges(string change)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            var target = harness.Gateway.Page;
            harness.Gateway.List = _ => Task.FromResult(new SessionListPage());
            harness.Controller.CloseDetails();
            harness.Controller.SetFilters(new SessionsHubFilters(AgentId: "other"));
            var response = new TaskCompletionSource<SessionListPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Gateway.List = _ => response.Task;
            var reveal = harness.Controller.LoadAttentionRowAsync(Harness.Key);

            harness.Gateway.List = _ => Task.FromResult(new SessionListPage());
            if (change == "filters")
                harness.Controller.SetFilters(new SessionsHubFilters(Search: "new choice"));
            else if (change == "reconnect")
            {
                harness.Gateway.Epoch++;
                harness.Gateway.Page = new SessionListPage();
                harness.Controller.RefreshConnection();
            }
            else
                harness.Controller.Deactivate();
            response.SetResult(target);

            Assert.False(await reveal);
            Assert.Empty(harness.Controller.State.Rows);
            Assert.Null(harness.Controller.State.DetailsKey);
        });
    }

    [Fact]
    public async Task AttentionDetails_ReportsWhenTheSessionNoLongerExists()
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.List = _ => Task.FromResult(new SessionListPage());
            harness.Controller.CloseDetails();
            harness.Controller.SetFilters(new SessionsHubFilters(AgentId: "other"));

            Assert.False(await harness.Controller.LoadAttentionRowAsync(Harness.Key));
            Assert.Null(harness.Controller.State.DetailsKey);
            Assert.Equal("Couldn't show this session. Refresh the list and try again.",
                harness.Controller.State.Notice?.Message);
        });
    }

    [Fact]
    public async Task WorktreeLink_UsesExplicitIdAndExpandsThatRecord()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        const string worktreeId = "wt-explicit";
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Worktrees = Task.FromResult<IReadOnlyList<WorktreeRecord>?>(
            [
                new WorktreeRecord("wt-old", "old", "C:\\src\\old", "C:\\src\\old.wt",
                    "old", "main", "manual", null, 1, 1, null),
            ]);
            await harness.Controller.LoadWorktreesAsync();
            var row = harness.Gateway.Page.Sessions.Single();
            row.Worktree = new SessionWorktreeInfo
            {
                Id = worktreeId,
                Branch = "feature/session-design",
                RepoRoot = "C:\\src\\openclaw",
            };
            harness.Gateway.Worktrees = Task.FromResult<IReadOnlyList<WorktreeRecord>?>(
            [
                new WorktreeRecord(worktreeId, "session-design", "C:\\src\\openclaw",
                    "C:\\src\\openclaw.wt", "feature/session-design", "main", "session", Harness.Key,
                    1, 2, null),
            ]);
            await harness.Controller.ReloadAsync();
            harness.Controller.SetTab(SessionsHubTab.Sessions);
            harness.Controller.OpenDetails(Harness.Key);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => Invoke(harness.Find<Button>("SessionsHubShowWorktree")));
        await Task.Delay(100);
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Equal(worktreeId, harness.Controller.State.Worktrees.DetailsId);
            Assert.True(harness.Find<Expander>("SessionsHubWorktree:" + worktreeId).IsExpanded);
        });
    }

    [Fact]
    public async Task ExpandingAnotherSession_KeepsBothRowsOpenWithoutScrollingAndEditorsBoundToTheirRows()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        const string otherKey = "agent:main:two";
        await ui.RunOnUIAsync(async () =>
        {
            harness.Controller.CloseDetails();
            var first = harness.Gateway.Page.Sessions.Single();
            var other = first.Clone();
            other.Key = otherKey;
            other.SessionId = "session-two";
            other.Label = "Other session";
            harness.Gateway.Page = new SessionListPage { Sessions = [first, other] };
            await harness.Controller.ReloadAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
            ((IExpandCollapseProvider)new ExpanderAutomationPeer(harness.Find<Expander>("SessionsHubRow:" + Harness.Key))
                .GetPattern(PatternInterface.ExpandCollapse)).Expand());
        await harness.RenderAsync();
        double scrollOffset = 0;
        await ui.RunOnUIAsync(() =>
        {
            harness.Controller.SetLabelDraft(Harness.Key, "First row draft");
            scrollOffset = harness.Find<ScrollView>("SessionsHubPageScroll").VerticalOffset;
            ((IExpandCollapseProvider)new ExpanderAutomationPeer(harness.Find<Expander>("SessionsHubRow:" + otherKey))
                .GetPattern(PatternInterface.ExpandCollapse)).Expand();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            await WaitForExpansionAsync(harness.Find<Expander>("SessionsHubRow:" + otherKey), expanded: true);
            Assert.Equal(otherKey, harness.Controller.State.DetailsKey);
            var other = harness.Find<Expander>("SessionsHubRow:" + otherKey);
            var first = harness.Find<Expander>("SessionsHubRow:" + Harness.Key);
            Assert.True(other.IsExpanded);
            Assert.True(first.IsExpanded);
            Assert.Equal("First row draft", Assert.Single(TestSupport.FindDescendants<TextBox>(first),
                box => AutomationProperties.GetAutomationId(box) == "SessionsHubLabel").Text);
            Assert.Equal("Other session", Assert.Single(TestSupport.FindDescendants<TextBox>(other),
                box => AutomationProperties.GetAutomationId(box) == "SessionsHubLabel").Text);
            Assert.InRange(Math.Abs(harness.Find<ScrollView>("SessionsHubPageScroll").VerticalOffset - scrollOffset), 0, 1);
            Assert.Null(harness.Controller.State.RowReveal);
            Assert.Single(TestSupport.FindDescendants<TextBox>(other),
                box => AutomationProperties.GetAutomationId(box) == "SessionsHubLabel").Text = "Edited other";
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => Invoke(Assert.Single(
            TestSupport.FindDescendants<Button>(harness.Find<Expander>("SessionsHubRow:" + otherKey)),
            button => AutomationProperties.GetAutomationId(button) == "SessionsHubLabelSave")));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => Assert.Equal(otherKey, Assert.Single(harness.Gateway.PatchKeys)));
    }

    [Fact]
    public async Task ArchivedRowInAllFilter_DimsOnlyItsHeaderWhenExpanded()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(() =>
        {
            harness.Gateway.Page.Sessions.Single().Archived = true;
            harness.Controller.SetFilters(harness.Controller.State.Filters with { Status = SessionArchiveFilter.All });
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => harness.Controller.OpenDetails(Harness.Key));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var row = harness.Find<Expander>("SessionsHubRow:" + Harness.Key);
            await WaitForExpansionAsync(row, expanded: true);
            var header = Assert.IsAssignableFrom<UIElement>(row.Header);
            Assert.Equal(0.75, header.Opacity, 3);
            // Opacity multiplies down the tree: every ancestor of the editable body must be opaque.
            var details = harness.FindShown<FrameworkElement>("SessionsHubDetails");
            for (DependencyObject? node = details; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            {
                if (node is UIElement element)
                    Assert.True(element.Opacity == 1, $"{element.GetType().Name} dims the archived row's details.");
                if (ReferenceEquals(node, row)) break;
            }
        });
    }

    [Fact]
    public async Task TranscriptReveal_ShowsACompletedSessionWithoutSavingTheShowCompletedPreference()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Page.Sessions.Single().Status = "done";
            harness.Controller.CloseDetails();
            harness.ShowCompletedSessions = false;
            harness.Controller.Activate(null);
            await harness.Controller.ReloadAsync();
            harness.ShowCompletedWrites = 0;
            Assert.False(harness.Controller.State.Filters.ShowCompleted);
            harness.Gateway.Search = Task.FromResult(new SessionSearchResult
            {
                Hits = [new SessionSearchHit(Harness.Key, "match", "assistant", 0, "Transcript match")],
            });
            harness.Controller.SetTranscriptQuery("match");
            harness.Controller.SetTranscriptOpen(true);
            await harness.Controller.RunTranscriptSearchAsync();
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<Button>(harness.Root!)
                .Any(button => AutomationProperties.GetAutomationId(button) == "SessionsHubTranscriptDetails:0"),
            "completed transcript result button"));
        await ui.RunOnUIAsync(() => Invoke(harness.Find<Button>("SessionsHubTranscriptDetails:0")));
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<Expander>(harness.Root!)
                .Any(row => AutomationProperties.GetAutomationId(row) == "SessionsHubRow:" + Harness.Key
                    && IsShown(row, harness.Root!)),
            "revealed completed session row"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.True(harness.FindShown<Expander>("SessionsHubRow:" + Harness.Key).IsExpanded);
            Assert.Equal(0, harness.ShowCompletedWrites);
            Assert.False(harness.ShowCompletedSessions);
        });
    }

    [Fact]
    public void HiddenCompletedCount_IsExactlyWhatShowCompletedWouldReveal()
    {
        SessionInfo[] rows =
        [
            new() { Key = "agent:main:done", Status = "done" },
            new() { Key = "agent:main:completed", Status = "completed" },
            new() { Key = "agent:main:working", Status = "done", HasActiveRun = true },
            new() { Key = "agent:main:stopped", Status = "done", AbortedLastRun = true },
            new() { Key = "agent:main:failed", Status = "failed" },
            new() { Key = "agent:main:unknown", Status = "unknown" },
            new() { Key = "agent:main:quiet", Status = "done", IsBackground = true },
        ];
        var current = new SessionsHubFilters();

        Assert.Equal(2, current.CountHiddenCompleted(rows));
        Assert.Equal(4, current.ApplyClientFilters(rows).Count());
        Assert.Equal(3, (current with { ShowBackground = true }).CountHiddenCompleted(rows));
        Assert.Equal(0, (current with { ShowCompleted = true }).CountHiddenCompleted(rows));
        Assert.Equal(0, (current with { Status = SessionArchiveFilter.All }).CountHiddenCompleted(rows));
        Assert.Equal(0, (current with { Status = SessionArchiveFilter.Archived }).CountHiddenCompleted(rows));
        foreach (var filters in new[] { current, current with { ShowBackground = true } })
        {
            var revealed = (filters with { ShowCompleted = true }).ApplyClientFilters(rows).Count()
                - filters.ApplyClientFilters(rows).Count();
            Assert.Equal(revealed, filters.CountHiddenCompleted(rows));
        }
    }

    [Fact]
    public async Task AllCompletedSessions_ExplainTheEmptyCurrentListAndShowCompletedRevealsThem()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(() => SeedCompletedPageAsync(harness, 3));
        await ui.RunOnUIAsync(() => WaitForElementAsync(harness, "SessionsPageEmpty"));
        await ui.RunOnUIAsync(() =>
        {
            var empty = EmptyStateText(harness);
            Assert.Contains("No sessions in progress", empty);
            Assert.Contains("3 completed sessions are hidden.", empty);
            Assert.DoesNotContain("No active sessions", empty);
            Assert.False(harness.Controller.State.Filters.ShowCompleted);
        });

        await ui.RunOnUIAsync(() => Invoke(harness.Find<Button>("SessionsHubShowCompleted")));
        await ui.RunOnUIAsync(() => WaitForElementAsync(harness, "SessionsPageEmpty", present: false));
        await ui.RunOnUIAsync(() => WaitForElementAsync(harness, "SessionsHubRow:agent:main:finished-3"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.True(harness.Controller.State.Filters.ShowCompleted);
            Assert.True(harness.ShowCompletedSessions);
            Assert.Equal(1, harness.ShowCompletedWrites);
            foreach (var index in new[] { 1, 2, 3 })
                harness.Find<Expander>($"SessionsHubRow:agent:main:finished-{index}");
        });
    }

    [Fact]
    public async Task SearchMatchingOnlyCompletedSessions_OffersShowCompletedInsteadOfClearingTheSearch()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(async () =>
        {
            await SeedCompletedPageAsync(harness, 1);
            harness.Controller.SetFilters(harness.Controller.State.Filters with { Search = "Finished" });
            await harness.Controller.ReloadAsync();
        });
        await ui.RunOnUIAsync(() => WaitForElementAsync(harness, "SessionsHubShowCompleted"));
        await ui.RunOnUIAsync(() =>
        {
            Assert.Contains("1 completed session is hidden.", EmptyStateText(harness));
            Assert.DoesNotContain(TestSupport.FindDescendants<Button>(harness.Root!),
                button => AutomationProperties.GetAutomationId(button) == "SessionsHubShowAll");

            Invoke(harness.Find<Button>("SessionsHubShowCompleted"));
            Assert.Equal("Finished", harness.Controller.State.Filters.Search);
            Assert.True(harness.Controller.State.Filters.ShowCompleted);
        });
        await ui.RunOnUIAsync(() => WaitForElementAsync(harness, "SessionsHubRow:agent:main:finished-1"));
    }

    /// <summary>Loads finished sessions with the saved Show completed preference off, as on a default profile.</summary>
    private static async Task SeedCompletedPageAsync(Harness harness, int count)
    {
        var template = harness.Gateway.Page.Sessions.Single();
        var rows = Enumerable.Range(1, count).Select(index =>
        {
            var row = template.Clone();
            row.Key = $"agent:main:finished-{index}";
            row.SessionId = $"session-finished-{index}";
            row.Label = $"Finished {index}";
            row.Status = "done";
            row.UpdatedAt = DateTime.UtcNow.AddMinutes(-index);
            return row;
        }).ToArray();
        harness.Gateway.Page = new SessionListPage { Sessions = rows };
        harness.Controller.CloseDetails();
        harness.ShowCompletedSessions = false;
        harness.Controller.Activate(null);
        await harness.Controller.ReloadAsync();
        harness.ShowCompletedWrites = 0;
    }

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
            Assert.False(harness.Controller.State.CanMutateRows);
            await harness.Controller.PatchAsync(harness.Controller.State.Rows.Single(), new SessionPatch { Pinned = true });
            Assert.Empty(harness.Gateway.Patches);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() =>
        {
            Assert.Single(harness.Controller.State.Rows);
            var error = harness.Find<InfoBar>("SessionsHubReloadFailed");
            Assert.True(error.IsOpen);
            Assert.Equal(InfoBarSeverity.Error, error.Severity);
            Assert.Equal("List unavailable", error.Message);
            Assert.False(harness.Find<TextBox>("SessionsHubLabel").IsEnabled);
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
    public async Task WireEquivalentSearchEdit_KeepsDetailsAndTranscriptResults(string search)
    {
        await using var harness = await Harness.CreateAsync(ui);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Controller.SetFilters(harness.Controller.State.Filters with { Search = search });
            harness.Controller.OpenDetails(Harness.Key);
            harness.Controller.SetLabelDraft(Harness.Key, "Unsaved name");
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
        Task firstCreate = Task.CompletedTask;
        Task secondCreate = Task.CompletedTask;
        Task oldLookup = Task.CompletedTask;
        var oldResponse = new TaskCompletionSource<WorktreeBranchList>(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshResponse = new TaskCompletionSource<WorktreeBranchList>(TaskCreationOptions.RunContinuationsAsynchronously);
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Worktrees = Task.FromResult<IReadOnlyList<WorktreeRecord>?>(
                [new WorktreeRecord("one", "one", "C:\\fixture", null, "main", null, "manual", null, 0, 0, null)]);
            await harness.Controller.LoadWorktreesAsync();
            firstCreate = harness.Controller.CreateWorktreeAsync();
        });
        // CreateWorktreeAsync queues its first branch load; let it settle before the race under test.
        await ui.YieldToRenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            harness.Controller.UpdateDialog(dialog => dialog with { BaseRef = "" });
            harness.Gateway.Branches = _ => oldResponse.Task;
            oldLookup = harness.Controller.LoadDialogBranchesAsync();
            harness.Gateway.Branches = _ => freshResponse.Task;
            if (change != "superseded")
            {
                harness.Controller.CloseDialog(false);
                if (change == "reconnected")
                {
                    harness.Gateway.Epoch++;
                    harness.Controller.RefreshConnection();
                    await harness.Controller.LoadWorktreesAsync();
                }
                harness.Gateway.Branches = _ => Task.FromResult(Branches("initial"));
                secondCreate = harness.Controller.CreateWorktreeAsync();
            }
        });
        await ui.YieldToRenderAsync();
        await ui.RunOnUIAsync(async () =>
        {
            harness.Gateway.Branches = _ => freshResponse.Task;
            harness.Controller.UpdateDialog(dialog => dialog with { BaseRef = "" });
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

    [Fact]
    public async Task TranscriptScopeTag_IsConciseWhileRequestKeepsMembershipFilters()
    {
        await using var harness = await Harness.CreateAsync(ui, mount: true);
        await ui.RunOnUIAsync(() =>
        {
            harness.Controller.SetFilters(harness.Controller.State.Filters with { IncludeGlobal = true, IncludeUnknown = true });
            harness.Controller.SetTranscriptOpen(true);
        });
        await harness.RenderAsync();
        await ui.RunOnUIAsync(() => TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<Border>(harness.Root!)
                .Any(border => AutomationProperties.GetAutomationId(border) == "SessionsHubTranscriptScope"),
            "transcript scope pill"));
        await ui.RunOnUIAsync(() =>
        {
            var scope = harness.Find<Border>("SessionsHubTranscriptScope");
            var text = Assert.Single(TestSupport.FindDescendants<TextBlock>(scope));
            Assert.Equal("All agents · Current", text.Text);
            Assert.True(harness.Controller.State.Filters.IncludeGlobal);
            Assert.True(harness.Controller.State.Filters.IncludeUnknown);
        });
    }

    private static WorktreeBranchList Branches(string name) => new()
    {
        Branches = [new WorktreeBranch(name)], DefaultBranch = name,
    };

    private static SessionListPage Page(string label = "Initial name") => new()
    {
        Sessions =
        [
            new SessionInfo
            {
                Key = Harness.Key, SessionId = "session-one", Label = label,
                IsMain = false, Status = "active", UpdatedAt = DateTime.UtcNow,
            },
        ],
    };

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static bool IsShown(DependencyObject element, DependencyObject root)
    {
        for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
                return false;
            if (ReferenceEquals(node, root))
                return true;
        }
        return true;
    }

    private static Task WaitForElementAsync(Harness harness, string automationId, bool present = true) =>
        TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<FrameworkElement>(harness.Root!)
                .Any(element => AutomationProperties.GetAutomationId(element) == automationId) == present,
            $"{automationId} to be {(present ? "shown" : "removed")}");

    private static string[] EmptyStateText(Harness harness) =>
        TestSupport.FindDescendants<TextBlock>(harness.Find<FrameworkElement>("SessionsPageEmpty"))
            .Select(text => text.Text).ToArray();

    private static Task WaitForExpansionAsync(Expander row, bool expanded) =>
        TestSupport.WaitForRenderedConditionAsync(
            () => TestSupport.FindDescendants<FrameworkElement>(row)
                .SelectMany(VisualStateManager.GetVisualStateGroups)
                .Any(group => group.Name == "ExpandStates"
                    && group.CurrentState?.Name == (expanded ? "ExpandDown" : "CollapseUp")
                    && group.CurrentState.Storyboard is { } storyboard
                    && storyboard.GetCurrentState() != Microsoft.UI.Xaml.Media.Animation.ClockState.Active),
            "native session row expansion animation");

    private sealed class Harness(UIThreadFixture thread) : ISessionsHubHost, IAsyncDisposable
    {
        public const string Key = "agent:main:one";
        internal ReactorHostControl? Root { get; private set; }
        internal SessionsHubPresentationState Presentation { get; } = new();
        private ChatThemeProofScope? _theme;
        public SessionsHubController Controller { get; private set; } = null!;
        public GatewayProxy Gateway { get; private set; } = null!;
        public IOperatorGatewayClient Client { get; private set; } = null!;
        public XamlRoot? XamlRoot => thread.Container.XamlRoot;
        public IntPtr WindowHandle => IntPtr.Zero;
        public IReadOnlyList<SessionInfo> TrackedSessions => Gateway.Page.Sessions;
        public string? GatewayName { get; set; } = "Fixture Gateway";
        public JsonElement? AgentsList => null;
        public ElementTheme ActualTheme => _theme?.ElementTheme ?? ElementTheme.Default;
        public bool ShowCompletedSessions
        {
            get => _showCompleted;
            set { _showCompleted = value; ShowCompletedWrites++; }
        }
        private bool _showCompleted = true;
        public int ShowCompletedWrites;
        public List<string> OpenedSessions { get; } = [];
        public void OpenChat(string sessionKey) => OpenedSessions.Add(sessionKey);
        public void OpenSessionDashboard(string sessionKey) => OpenedDashboards.Add(sessionKey);
        public List<string> OpenedDashboards { get; } = [];
        public void OpenConnection() { }
        public void OpenDashboard() { }

        public static async Task<Harness> CreateAsync(
            UIThreadFixture thread, bool mount = false, double width = 1200,
            string theme = "Light", double height = 800, double topInset = 0)
        {
            var harness = new Harness(thread);
            await thread.RunOnUIAsync(() =>
            {
                harness.Presentation.ApplyTopInset(topInset);
                harness.Client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
                harness.Gateway = (GatewayProxy)harness.Client;
                harness.Gateway.Page = Page();
                harness.Controller = new SessionsHubController(harness, action =>
                {
                    if (!thread.Dispatcher.TryEnqueue(() => action()))
                        throw new InvalidOperationException("The proof dispatcher rejected a controller update.");
                });
                harness.Controller.Activate(null);
                harness.Controller.OpenDetails(Key);
                if (mount)
                {
                    harness._theme = new ChatThemeProofScope(theme);
                    harness.Root = new ReactorHostControl
                    {
                        Width = width, Height = height, RequestedTheme = harness._theme.ElementTheme,
                        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            theme == "Dark"
                                ? global::Windows.UI.Color.FromArgb(255, 32, 32, 32)
                                : global::Windows.UI.Color.FromArgb(255, 243, 243, 243)),
                    };
                    thread.Container.Children.Add(harness.Root);
                    harness.Root.Mount(_ => Component<SessionsHubRoot, SessionsHubRootProps>(
                        new(harness.Controller, harness.Presentation)));
                }
            });
            if (mount) await harness.RenderAsync();
            return harness;
        }

        public T Find<T>(string id) where T : FrameworkElement =>
            Assert.Single(TestSupport.FindDescendants<T>(Root!), element => AutomationProperties.GetAutomationId(element) == id);

        /// <summary>The one matching element currently laid out (a collapsing row may retain a hidden copy).</summary>
        public T FindShown<T>(string id) where T : FrameworkElement =>
            Assert.Single(TestSupport.FindDescendants<T>(Root!),
                element => AutomationProperties.GetAutomationId(element) == id && IsShown(element, Root!));

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
        public bool Connected = true;
        public string[] Scopes = ["operator.admin", "operator.write", "operator.read"];
        public Func<SessionDeleteRequest, Task<SessionDeleteOutcome>>? Delete;
        public List<string> Resets { get; } = [];
        public long Epoch = 1;
        public string[] Methods = ["sessions.list", "sessions.patch", "sessions.search", "worktrees.list"];
        public List<SessionPatch> Patches { get; } = [];
        public List<string> PatchKeys { get; } = [];
        public List<SessionDeleteRequest> Deletions { get; } = [];
        private EventHandler<SessionInfo[]>? _sessionsUpdated;

        public void PublishSessions(params SessionInfo[] sessions)
        {
            Page = new SessionListPage { Sessions = sessions };
            _sessionsUpdated?.Invoke(this, sessions);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "add_SessionsUpdated")
            {
                _sessionsUpdated += (EventHandler<SessionInfo[]>)args![0]!;
                return null;
            }
            if (method.Name == "remove_SessionsUpdated")
            {
                _sessionsUpdated -= (EventHandler<SessionInfo[]>)args![0]!;
                return null;
            }
            if (method!.Name.StartsWith("add_", StringComparison.Ordinal)
                || method.Name.StartsWith("remove_", StringComparison.Ordinal))
                return null;
            switch (method.Name)
            {
                case "get_IsConnectedToGateway": return Connected;
                case "get_SessionMutationConnectionEpoch": return Epoch;
                case "get_MainSessionKey": return "agent:main:main";
                case "get_GrantedOperatorScopes": return Scopes;
                case "get_AdvertisedServerMethods": return Methods;
                case nameof(IOperatorGatewayClient.ListSessionsPageAsync):
                    return List?.Invoke((SessionListQuery)args![0]!) ?? Task.FromResult(Page);
                case nameof(IOperatorGatewayClient.SearchSessionTranscriptsAsync): return Search;
                case nameof(IOperatorGatewayClient.ListWorktreesAsync): return Worktrees;
                case nameof(IOperatorGatewayClient.ListWorktreeBranchesAsync):
                    return Branches((string)args![0]!);
                case nameof(IOperatorGatewayClient.DeleteSessionDetailedAsync):
                    var request = (SessionDeleteRequest)args![0]!;
                    Deletions.Add(request);
                    return Delete?.Invoke(request) ?? Task.FromResult(new SessionDeleteOutcome(request.Key, true, null));
                case nameof(IOperatorGatewayClient.PatchSessionConfirmedAsync):
                    PatchKeys.Add((string)args![0]!);
                    Patches.Add((SessionPatch)args![1]!);
                    return PatchAcknowledgement;
                case nameof(IOperatorGatewayClient.RequestSessionsAsync): return Task.CompletedTask;
                case nameof(IOperatorGatewayClient.ResetSessionAsync):
                    Resets.Add((string)args![0]!);
                    return Task.FromResult(true);
                default: throw new NotSupportedException($"Unexpected Sessions proof call: {method.Name}");
            }
        }
    }
}
