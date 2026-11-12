using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class InboxGatewayItemsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static long NowMs => Now.ToUnixTimeMilliseconds();

    [Fact]
    public void BuildCron_FlagsActiveFailuresAndAutoDisabledJobsButNotPausedFailures()
    {
        var items = InboxGatewayItems.BuildCron(Json(new
        {
            jobs = new object[]
            {
                new { id = "nightly", name = "Nightly report", enabled = true, state = new { lastRunStatus = "error", lastRunAtMs = NowMs - 60_000, lastError = "Timed out" } },
                new { id = "escalated", enabled = false, state = new { autoDisabled = true, lastRunAtMs = NowMs - 120_000 } },
                new { id = "paused", enabled = false, state = new { lastStatus = "error" } },
                new { id = "healthy", enabled = true, state = new { lastRunStatus = "ok" } },
            },
        }), schedulerEnabled: true, NowMs);

        Assert.Equal(["gateway:cronFailed:nightly", "gateway:cronFailed:escalated"], items.Select(item => item.Id));
        Assert.All(items, item => Assert.Equal(InboxGatewayItemKind.AutomationFailed, item.Kind));
        Assert.Equal(("Nightly report", "Timed out"), (items[0].Label, items[0].Detail));
        Assert.Equal("escalated", items[1].Label);
    }

    [Fact]
    public void BuildCron_FlagsOverdueOnlyAfterGraceForIdleEnabledJobs()
    {
        var due = NowMs - InboxGatewayItems.CronOverdueGraceMs - 1;
        var items = InboxGatewayItems.BuildCron(Json(new
        {
            jobs = new object[]
            {
                new { id = "late", name = "Backup", enabled = true, state = new { nextRunAtMs = due } },
                new { id = "in-grace", enabled = true, state = new { nextRunAtMs = NowMs - 1_000 } },
                new { id = "running", enabled = true, state = new { nextRunAtMs = due, runningAtMs = NowMs } },
                new { id = "disabled", enabled = false, state = new { nextRunAtMs = due } },
            },
        }), schedulerEnabled: null, NowMs);

        var item = Assert.Single(items);
        Assert.Equal($"gateway:cronOverdue:late@{due}", item.Id);
        Assert.Equal(InboxGatewayItemKind.AutomationOverdue, item.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(due), item.DueAt);
    }

    [Fact]
    public void BuildCron_DoesNotFlagOverdueWhileTheSchedulerIsDisabled()
    {
        var items = InboxGatewayItems.BuildCron(Json(new
        {
            jobs = new[] { new { id = "late", enabled = true, state = new { nextRunAtMs = NowMs - 3_600_000 } } },
        }), schedulerEnabled: false, NowMs);

        Assert.Empty(items);
    }

    [Fact]
    public void BuildModelAuth_FlagsMonitoredExpiredOrMissingProvidersWithTheirRepair()
    {
        var items = InboxGatewayItems.BuildModelAuth(Json(new
        {
            ts = NowMs,
            providers = new object[]
            {
                new { provider = "openai", displayName = "OpenAI", status = "expired",
                    profiles = new[] { new { profileId = "work", type = "oauth", status = "expired" } },
                    expiry = new { at = NowMs - 60_000 } },
                new { provider = "anthropic", status = "missing", profiles = Array.Empty<object>() },
                new { provider = "keys-only", status = "expired",
                    profiles = new[] { new { profileId = "k", type = "api_key", status = "expired" } } },
                new { provider = "fine", status = "ok", profiles = new[] { new { type = "oauth", status = "ok" } } },
            },
        }), NowMs);

        Assert.Equal(["gateway:modelAuth:anthropic", "gateway:modelAuth:openai"], items.Select(item => item.Id));
        var openai = items[1].ModelAuth!;
        Assert.Equal((InboxModelAuthRepair.SignIn, "work", "expired"), (openai.Repair, openai.Scope, openai.Status));
        Assert.Equal(Now.AddMinutes(-1), items[1].ArrivedAt);
        Assert.Equal(InboxModelAuthRepair.Review, items[0].ModelAuth!.Repair);
    }

    [Fact]
    public void BuildModelAuth_KeepsTheWorstStatusPerProvider()
    {
        var items = InboxGatewayItems.BuildModelAuth(Json(new
        {
            providers = new object[]
            {
                new { provider = "openai", status = "ok", profiles = new[] { new { type = "token", status = "ok" } } },
                new { provider = "openai", status = "expired", profiles = new[] { new { type = "token", status = "expired" } } },
            },
        }), NowMs);

        Assert.Equal("expired", Assert.Single(items).ModelAuth!.Status);
    }

    [Fact]
    public void BuildModelAuth_GroupsProviderAliasesIntoOneRow()
    {
        var items = InboxGatewayItems.BuildModelAuth(Json(new
        {
            providers = new object[]
            {
                new { provider = "anthropic", status = "ok", profiles = new[] { new { type = "oauth", status = "ok" } } },
                new { provider = "claude-cli", status = "expired", profiles = new[] { new { type = "oauth", status = "expired" } } },
            },
        }), NowMs);

        var item = Assert.Single(items);
        Assert.Equal("gateway:modelAuth:anthropic", item.Id);
        Assert.Equal("expired", item.ModelAuth!.Status);
    }

    [Fact]
    public void NextModelAuthRefreshAt_MapsTheEarliestFutureExpiryOntoTheLocalClock()
    {
        var gatewayTs = 1_000_000L;
        var refreshAt = InboxGatewayItems.NextModelAuthRefreshAt(Json(new
        {
            ts = gatewayTs,
            providers = new object[]
            {
                new { provider = "a", status = "expiring", expiry = new { at = gatewayTs + 600_000 },
                    profiles = new[] { new { type = "oauth", expiry = new { at = gatewayTs + 120_000 } } } },
                new { provider = "b", status = "expired", expiry = new { at = gatewayTs - 1 }, profiles = Array.Empty<object>() },
            },
        }), Now);

        Assert.Equal(Now.AddMinutes(2), refreshAt);
        Assert.Null(InboxGatewayItems.NextModelAuthRefreshAt(Json(new { ts = gatewayTs, providers = Array.Empty<object>() }), Now));
    }

    [Theory]
    [InlineData("ls  -la\n  /tmp", "ls -la /tmp")]
    [InlineData("short", "short")]
    public void CompactCommand_CollapsesWhitespace(string command, string expected) =>
        Assert.Equal(expected, InboxGatewayItems.CompactCommand(command));

    [Fact]
    public void CompactCommand_CapsLongCommands()
    {
        var compact = InboxGatewayItems.CompactCommand(new string('a', 100));
        Assert.Equal(62, compact.Length);
        Assert.EndsWith("…", compact);
    }

    [Theory]
    [InlineData(292_000, "4:52")]
    [InlineData(1, "0:01")]
    [InlineData(3_600_000, "60:00")]
    public void FormatCountdown_ShowsMinutesAndSeconds(long remainingMs, string expected) =>
        Assert.Equal(expected, InboxGatewayItems.FormatCountdown(NowMs + remainingMs, NowMs));

    [Fact]
    public void FormatCountdown_IsNullOnceExpired() =>
        Assert.Null(InboxGatewayItems.FormatCountdown(NowMs, NowMs));

    [Fact]
    public void Builders_IgnoreTimestampsBeyondTheRepresentableRange()
    {
        const long tooLate = 9007199254740991;
        var cron = InboxGatewayItems.BuildCron(Json(new
        {
            jobs = new object[]
            {
                new { id = "late", enabled = true, state = new { nextRunAtMs = tooLate } },
                new { id = "failed", enabled = true, updatedAtMs = tooLate, state = new { lastRunStatus = "error", lastRunAtMs = tooLate } },
            },
        }), schedulerEnabled: true, NowMs);
        var auth = Json(new
        {
            ts = tooLate,
            providers = new object[]
            {
                new { provider = "openai", status = "missing", expiry = new { at = tooLate }, profiles = Array.Empty<object>() },
            },
        });

        var failed = Assert.Single(cron);
        Assert.Equal("gateway:cronFailed:failed", failed.Id);
        Assert.Equal(Now, failed.ArrivedAt);
        Assert.Equal(Now, Assert.Single(InboxGatewayItems.BuildModelAuth(auth, NowMs)).ArrivedAt);
        Assert.Null(InboxGatewayItems.NextModelAuthRefreshAt(auth, Now));
    }

    [Fact]
    public void NextModelAuthRefreshAt_ClampsAFarExpiryInsteadOfThrowing()
    {
        var refreshAt = InboxGatewayItems.NextModelAuthRefreshAt(Json(new
        {
            ts = 1,
            providers = new[] { new { provider = "a", status = "ok", expiry = new { at = GatewayUnixTime.MaxMs } } },
        }), Now);

        Assert.Equal(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), refreshAt!.Value.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData(-7_200_000L, 60_000L, 60_000L)]   // Gateway clock two hours behind
    [InlineData(7_200_000L, 60_000L, 60_000L)]    // Gateway clock two hours ahead
    [InlineData(0L, -5_000L, 0L)]                 // expiry before creation: already expired
    [InlineData(0L, 0L, 0L)]
    public void LocalDeadline_MeasuresTheGatewayLifetimeOnTheLocalClock(long gatewaySkewMs, long lifetimeMs, long expectedMs)
    {
        var created = NowMs + gatewaySkewMs;
        var approval = Approval("a", created, created + lifetimeMs);

        Assert.Equal(Now.AddMilliseconds(expectedMs), InboxGatewayItems.LocalDeadline(approval, Now));
    }

    [Fact]
    public void LocalDeadline_ClampsAHugeLifetime() =>
        Assert.Equal(GatewayUnixTime.MaxMs,
            InboxGatewayItems.LocalDeadline(Approval("a", 1, GatewayUnixTime.MaxMs), Now).ToUnixTimeMilliseconds());

    [Fact]
    public void DisplayCommand_KeepsTheWholeCommandAndRevealsHiddenCharacters()
    {
        var command = "echo safe\u202E txt.exe\r\nrm -rf x\u200B\rhidden\tend" + new string('y', 500);

        var display = InboxGatewayItems.DisplayCommand(command);

        Assert.Equal("echo safe\\u{202E} txt.exe\nrm -rf x\\u{200B}\\u{000D}hidden\tend" + new string('y', 500), display);
        Assert.DoesNotContain("…", display);
    }

    [Fact]
    public void Projection_PutsGatewayApprovalsFirstAndKeepsThemUndismissable()
    {
        var approval = InboxGatewayItems.FromApproval(Approval("a1", NowMs - 600_000), Now.AddMinutes(50));
        var failed = new InboxGatewayItem("gateway:cronFailed:j", InboxGatewayItemKind.AutomationFailed, "Job", Now);
        var auth = new InboxGatewayItem("gateway:modelAuth:p", InboxGatewayItemKind.ModelAuth, "P", Now.AddMinutes(-1));
        var notification = new AppNotification
        {
            Id = "update", Title = "Update", Message = "Failed", Severity = AppNotificationSeverity.Error,
            Source = InboxSources.Update, CreatedAt = Now,
        };

        var inbox = InboxProjection.Build([notification], [failed, auth, approval]);

        Assert.Equal(["gateway:approval:a1", "update", "gateway:cronFailed:j", "gateway:modelAuth:p"], inbox.Entries.Select(entry => entry.Id));
        Assert.Equal(InboxBadgeTone.Attention, inbox.Badge.Tone);
        Assert.Equal(1, inbox.Count(InboxTab.Approvals));
        Assert.Equal(1, inbox.Count(InboxTab.Automations));
        Assert.Equal(2, inbox.Count(InboxTab.System));
        Assert.Equal(["update", "gateway:cronFailed:j", "gateway:modelAuth:p"], InboxProjection.DismissibleIds(inbox, InboxTab.All));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(NowMs - 600_000), inbox.Entries[0].CreatedAt);
    }

    internal static GatewayPendingApproval Approval(string id, long createdAtMs, long? expiresAtMs = null, GatewayApprovalKind kind = GatewayApprovalKind.Exec) =>
        new(id, kind, "echo hi", createdAtMs, expiresAtMs ?? createdAtMs + 3_600_000) { SessionKey = "agent:main:main" };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}

public sealed class InboxGatewaySourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = Start;

    [Fact]
    public void ApprovalEvents_AddAndResolvePendingApprovals()
    {
        var (source, gateway) = Attach();
        var changes = 0;
        source.Changed += (_, _) => changes++;

        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("a1", Ms(Start)));
        Assert.Equal(["gateway:approval:a1"], source.Items.Select(item => item.Id));

        gateway.Raise("PendingApprovalResolved", "a1");
        Assert.Empty(source.Items);
        Assert.Equal(2, changes);

        // A late duplicate of a resolved request does not resurrect it.
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("a1", Ms(Start)));
        Assert.Empty(source.Items);
    }

    [Fact]
    public void Tick_RemovesExpiredApprovals()
    {
        var (source, gateway) = Attach();
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("a1", Ms(Start), Ms(Start) + 5_000));

        _now = Start.AddSeconds(6);
        source.Tick();

        Assert.Empty(source.Items);
    }

    [Fact]
    public void ApprovalsFromASkewedGatewayClock_UseTheirLifetimeOnTheLocalClock()
    {
        var (source, gateway) = Attach();
        var created = Ms(Start) - 7_200_000; // Gateway two hours behind this machine
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("a1", created, created + 60_000));

        var item = Assert.Single(source.Items);
        Assert.Equal(Start.AddMinutes(1), item.ExpiresAt);

        _now = Start.AddSeconds(59);
        source.Tick();
        Assert.Single(source.Items);

        _now = Start.AddSeconds(61);
        source.Tick();
        Assert.Empty(source.Items);
    }

    [Fact]
    public void RepeatedApprovalRequests_KeepTheirOriginalDeadline()
    {
        var (source, gateway) = Attach();
        var approval = InboxGatewayItemsTests.Approval("a1", Ms(Start), Ms(Start) + 60_000);
        gateway.Raise("PendingApprovalRequested", approval);

        _now = Start.AddSeconds(50);
        gateway.Raise("PendingApprovalRequested", approval);

        Assert.Equal(Start.AddMinutes(1), Assert.Single(source.Items).ExpiresAt);
    }

    [Fact]
    public void Disconnect_ClearsGatewayFacts()
    {
        var (source, gateway) = Attach();
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("a1", Ms(Start)));
        gateway.SetCron(FailedCron("job"));
        Assert.Equal(2, source.Items.Count);

        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);

        Assert.Empty(source.Items);
    }

    [Fact]
    public void Connect_HydratesApprovalsAndDropsOnesTheGatewayNoLongerLists()
    {
        var (source, gateway) = Attach();
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("stale", Ms(Start)));
        gateway.Replies["exec.approval.list"] = JsonSerializer.SerializeToElement(new[]
        {
            new { id = "listed", request = new { command = "dir", sessionKey = "agent:main:main" }, createdAtMs = Ms(Start), expiresAtMs = Ms(Start) + 60_000 },
        });

        gateway.Raise("StatusChanged", ConnectionStatus.Connected);

        Assert.Equal(["gateway:approval:listed"], source.Items.Select(item => item.Id));
        Assert.Contains("exec.approval.list", gateway.Requests);
        Assert.Contains("models.authStatus", gateway.Requests);
        Assert.Contains("cron.list", gateway.Requests);
        Assert.DoesNotContain("RequestCronListAsync", gateway.Requests);
    }

    [Fact]
    public void Connect_SkipsApprovalListsWithoutApprovalScope()
    {
        var (_, gateway) = Attach(scopes: ["operator.read"]);

        gateway.Raise("StatusChanged", ConnectionStatus.Connected);

        Assert.DoesNotContain("exec.approval.list", gateway.Requests);
    }

    [Fact]
    public void Dismiss_HidesAFailureUntilItRecurs()
    {
        var (source, gateway) = Attach();
        gateway.SetCron(FailedCron("job"));

        source.Dismiss("gateway:cronFailed:job");
        Assert.Empty(source.Items);

        gateway.SetCron(JsonSerializer.SerializeToElement(new { jobs = Array.Empty<object>() }));
        gateway.SetCron(FailedCron("job"));

        Assert.Equal(["gateway:cronFailed:job"], source.Items.Select(item => item.Id));
    }

    [Fact]
    public void Dismiss_IgnoresApprovals()
    {
        var (source, gateway) = Attach();
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("a1", Ms(Start)));

        source.Dismiss("gateway:approval:a1");

        Assert.Single(source.Items);
    }

    [Fact]
    public void EventsFromAReplacedClient_AreIgnored()
    {
        var (source, oldGateway) = Attach();
        var replacement = FakeGateway.Create();
        source.AttachClient((IOperatorGatewayClient)replacement);

        oldGateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("a1", Ms(Start)));

        Assert.Empty(source.Items);
        Assert.False(oldGateway.HasHandlers("PendingApprovalRequested"));
        Assert.False(oldGateway.HasHandlers("CronEventReceived"));
    }

    [Fact]
    public void LateRepliesFromBeforeADisconnect_AreDropped()
    {
        var (source, gateway) = Attach();
        var approvals = new TaskCompletionSource<JsonElement>();
        var auth = new TaskCompletionSource<JsonElement>();
        gateway.Pending["exec.approval.list"] = approvals;
        gateway.Pending["models.authStatus"] = auth;
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);

        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        approvals.SetResult(JsonSerializer.SerializeToElement(new[]
        {
            new { id = "late", request = new { command = "dir" }, createdAtMs = Ms(Start), expiresAtMs = Ms(Start) + 60_000 },
        }));
        auth.SetResult(JsonSerializer.SerializeToElement(new
        {
            providers = new[] { new { provider = "openai", status = "missing", profiles = Array.Empty<object>() } },
        }));

        Assert.Empty(source.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateSchedulerStatusCannotHideOverdueJobsAfterDisconnectOrReplacement(bool replaceClient)
    {
        var (source, gateway) = Attach();
        gateway.Reads["cron.status"] = () =>
        {
            // Finish the new epoch inside the old read. Returning an already-completed
            // task then processes the stale response synchronously before the final assertion.
            gateway.Reads.Remove("cron.status");
            var current = replaceClient ? FakeGateway.Create() : gateway;
            if (replaceClient)
                source.AttachClient((IOperatorGatewayClient)current);
            else
                gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
            current.Replies["cron.status"] = JsonSerializer.SerializeToElement(new { enabled = true });
            current.Replies["cron.list"] = JsonSerializer.SerializeToElement(new
            {
                jobs = new[] { new { id = "current", enabled = true, state = new { nextRunAtMs = Ms(Start) - 3_600_000 } } },
            });
            current.Raise("StatusChanged", ConnectionStatus.Connected);
            Assert.Equal(InboxGatewayItemKind.AutomationOverdue, Assert.Single(source.Items).Kind);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { enabled = false }));
        };

        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        Assert.Equal(InboxGatewayItemKind.AutomationOverdue, Assert.Single(source.Items).Kind);
    }

    [Fact]
    public void ModelAuthLoad_IsNotBlockedByAReadFromAReplacedClient()
    {
        var (source, oldGateway) = Attach();
        oldGateway.Pending["models.authStatus"] = new TaskCompletionSource<JsonElement>();
        oldGateway.Raise("StatusChanged", ConnectionStatus.Connected);

        var replacement = FakeGateway.Create();
        replacement.Replies["models.authStatus"] = JsonSerializer.SerializeToElement(new
        {
            providers = new[] { new { provider = "openai", status = "missing", profiles = Array.Empty<object>() } },
        });
        source.AttachClient((IOperatorGatewayClient)replacement);
        replacement.Raise("StatusChanged", ConnectionStatus.Connected);

        Assert.Equal(["gateway:modelAuth:openai"], source.Items.Select(item => item.Id));
    }

    [Fact]
    public void Dismissals_DoNotCarryOverToAnotherGateway()
    {
        var (source, gateway) = Attach();
        gateway.SetCron(FailedCron("job"));
        source.Dismiss("gateway:cronFailed:job");

        var replacement = FakeGateway.Create();
        source.AttachClient((IOperatorGatewayClient)replacement);
        replacement.SetCron(FailedCron("job"));

        Assert.Equal(["gateway:cronFailed:job"], source.Items.Select(item => item.Id));
    }

    [Fact]
    public void Feed_RoutesDismissalsToTheOwningSource()
    {
        var (source, gateway) = Attach();
        gateway.SetCron(FailedCron("job"));
        var notifications = new AppNotificationService();
        notifications.Show(new AppNotification
        {
            Id = "n1", Title = "Error", Message = "Error", Source = "connection", Severity = AppNotificationSeverity.Error,
        });
        var feed = new InboxFeed(notifications, source);
        Assert.Equal(2, feed.Snapshot.Entries.Count);

        feed.Dismiss("gateway:cronFailed:job");
        feed.Dismiss("n1");

        Assert.Empty(feed.Snapshot.Entries);
    }

    [Fact]
    public void CronInventory_PublishesAllPagesOnlyAfterTheFinalPage()
    {
        var (source, gateway) = Attach();
        gateway.SetCron(FailedCron("previous"));
        var changes = 0;
        source.Changed += (_, _) => changes++;
        var offsets = new List<int>();
        gateway.CatalogPage = args =>
        {
            var offset = args["offset"]!.GetValue<int>();
            offsets.Add(offset);
            Assert.True(args["includeDisabled"]!.GetValue<bool>());
            Assert.Equal(["gateway:cronFailed:previous"], source.Items.Select(item => item.Id));
            Assert.Equal(0, changes);
            return Task.FromResult(offset == 0
                ? CronPage(["first"], "snapshot", true, 1, 2)
                : CronPage(["second"], "snapshot", false, 2, 2));
        };

        // The legacy event carries only a page, not the full inventory.
        gateway.Raise("CronListUpdated", CronPage(["first"], "snapshot", true, 1, 2));

        Assert.Equal([0, 1], offsets);
        Assert.Equal(["gateway:cronFailed:first", "gateway:cronFailed:second"], source.Items.Select(item => item.Id));
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CronInventory_PartialFailureOrRevisionChurnPreservesRowsAndDismissals(bool revisionChurn)
    {
        var (source, gateway) = Attach();
        var previous = CronPage(["visible", "dismissed"], "old", false, 2, 2);
        gateway.SetCron(previous);
        source.Dismiss("gateway:cronFailed:dismissed");
        var changes = 0;
        source.Changed += (_, _) => changes++;
        gateway.CatalogPage = args => args["offset"]!.GetValue<int>() == 0
            ? Task.FromResult(CronPage(["new-first"], "new", true, 1, 2))
            : revisionChurn
                ? Task.FromResult(CronPage(["new-second"], "changed", false, 2, 2))
                : Task.FromException<JsonElement>(new IOException("Synthetic page failure"));

        gateway.Raise("CronListUpdated", JsonSerializer.SerializeToElement(new { jobs = Array.Empty<object>() }));
        Assert.Equal(["gateway:cronFailed:visible"], source.Items.Select(item => item.Id));
        Assert.Equal(0, changes);

        gateway.CatalogPage = null;
        gateway.SetCron(previous);
        Assert.Equal(["gateway:cronFailed:visible"], source.Items.Select(item => item.Id));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void CronInventory_OlderOverlappingReadCannotReplaceNewerInventory()
    {
        var (source, gateway) = Attach();
        gateway.CatalogPage = _ =>
        {
            // A newer invalidation finishes before the older read returns.
            gateway.CatalogPage = null;
            gateway.SetCron(FailedCron("newer"));
            return Task.FromResult(FailedCron("older"));
        };

        gateway.Raise("CronListUpdated", FailedCron("older"));

        Assert.Equal(["gateway:cronFailed:newer"], source.Items.Select(item => item.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CronInventory_ReadFromBeforeDisconnectOrClientReplacementCannotPublish(bool replace)
    {
        var (source, gateway) = Attach();
        gateway.CatalogPage = _ =>
        {
            if (replace)
                source.AttachClient((IOperatorGatewayClient)FakeGateway.Create());
            else
                gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
            return Task.FromResult(FailedCron("old-connection"));
        };

        gateway.Raise("CronListUpdated", FailedCron("old-connection"));

        Assert.Empty(source.Items);
    }

    [Fact]
    public void CronInventory_SearchEntryLimitIsNotACompleteEmptyOrPartialInventory()
    {
        var (source, gateway) = Attach();
        gateway.SetCron(FailedCron("previous"));
        // Search intentionally bounds catalogs to 5000 entries; Inbox must reject its partial result.
        gateway.CatalogPage = _ => Task.FromResult(CronPage(
            Enumerable.Range(0, 5001).Select(i => $"job-{i}").ToArray(), "large", false, 5001, 5001));

        gateway.Raise("CronListUpdated", FailedCron("partial"));

        Assert.Equal(["gateway:cronFailed:previous"], source.Items.Select(item => item.Id));
    }

    [Fact]
    public void ExpiryDuringHydration_DoesNotResurrectFromLateSnapshotOrReplay()
    {
        var (source, gateway) = Attach();
        var approval = InboxGatewayItemsTests.Approval("expiring", Ms(Start), Ms(Start) + 5_000);
        gateway.Raise("PendingApprovalRequested", approval);
        gateway.Reads["exec.approval.list"] = () =>
        {
            _now = Start.AddSeconds(6);
            source.Tick();
            Assert.Empty(source.Items);
            return Task.FromResult(ApprovalList("expiring", 5_000));
        };

        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        Assert.Empty(source.Items);
        gateway.Raise("PendingApprovalRequested", approval);
        Assert.Empty(source.Items);
    }

    [Fact]
    public void ResolveDuringHydration_DoesNotResurrectFromLateSnapshotOrReplay()
    {
        var (source, gateway) = Attach();
        var approval = InboxGatewayItemsTests.Approval("resolved", Ms(Start));
        gateway.Reads["exec.approval.list"] = () =>
        {
            // A terminal can arrive before the first requested observation.
            gateway.Raise("PendingApprovalResolved", "resolved");
            return Task.FromResult(ApprovalList("resolved", 60_000));
        };

        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        gateway.Raise("PendingApprovalRequested", approval);
        Assert.Empty(source.Items);
    }

    [Fact]
    public void ApprovalMissingFromHydration_RemainsTerminalUntilReconnect()
    {
        var (source, gateway) = Attach();
        var approval = InboxGatewayItemsTests.Approval("gone", Ms(Start));
        gateway.Raise("PendingApprovalRequested", approval);
        gateway.Replies["exec.approval.list"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        gateway.Raise("PendingApprovalRequested", approval);
        Assert.Empty(source.Items);

        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Replies["exec.approval.list"] = ApprovalList("gone", 60_000);
        _now = Start.AddMinutes(1);
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        Assert.Equal(Start.AddMinutes(2), Assert.Single(source.Items).ExpiresAt);
    }

    [Fact]
    public void BoundedApprovalRetention_DoesNotEvictTerminalsToAdmitReplays()
    {
        var (source, gateway) = Attach();
        for (var i = 0; i < 5000; i++)
            gateway.Raise("PendingApprovalResolved", $"resolved-{i}");

        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("resolved-0", Ms(Start)));
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("resolved-4999", Ms(Start)));
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("unknown-after-capacity", Ms(Start)));
        Assert.Empty(source.Items);

        gateway.Raise("StatusChanged", ConnectionStatus.Disconnected);
        gateway.Raise("StatusChanged", ConnectionStatus.Connected);
        gateway.Raise("PendingApprovalRequested", InboxGatewayItemsTests.Approval("fresh-connection", Ms(Start)));
        Assert.Equal("gateway:approval:fresh-connection", Assert.Single(source.Items).Id);
    }

    private static JsonElement ApprovalList(string id, long lifetime) => JsonSerializer.SerializeToElement(new[]
    {
        new { id, request = new { command = "dir" }, createdAtMs = Ms(Start), expiresAtMs = Ms(Start) + lifetime },
    });

    private static JsonElement CronPage(string[] ids, string revision, bool hasMore, int nextOffset, int total) =>
        JsonSerializer.SerializeToElement(new
        {
            jobs = ids.Select(id => new { id, enabled = true, state = new { lastRunStatus = "error", lastRunAtMs = Ms(Start) } }),
            snapshotRevision = revision, hasMore, nextOffset, total,
        });

    private (InboxGatewaySource Source, FakeGateway Gateway) Attach(IReadOnlyList<string>? scopes = null)
    {
        var source = new InboxGatewaySource(new RecordingUiDispatcher(), () => _now, useTimer: false);
        var gateway = FakeGateway.Create();
        gateway.Scopes = scopes ?? ["operator.approvals"];
        source.AttachClient((IOperatorGatewayClient)gateway);
        return (source, gateway);
    }

    private JsonElement FailedCron(string id) => JsonSerializer.SerializeToElement(new
    {
        jobs = new[] { new { id, enabled = true, state = new { lastRunStatus = "error", lastRunAtMs = Ms(_now) } } },
    });

    private static long Ms(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    /// <summary>Operator client double: records requests and lets tests raise its events.</summary>
    public class FakeGateway : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _handlers = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Scopes = [];
        public Dictionary<string, JsonElement> Replies = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<JsonElement>> Pending = new(StringComparer.Ordinal);
        public List<string> Requests = [];
        public Dictionary<string, Func<Task<JsonElement>>> Reads = new(StringComparer.Ordinal);
        public Func<JsonObject, Task<JsonElement>>? CatalogPage;

        public void SetCron(JsonElement inventory)
        {
            Replies["cron.list"] = inventory;
            Raise("CronListUpdated", inventory);
        }

        public static FakeGateway Create() => (FakeGateway)(object)DispatchProxy.Create<IOperatorGatewayClient, FakeGateway>();

        public void Raise(string name, object argument) =>
            _handlers.GetValueOrDefault(name)?.DynamicInvoke(this, argument);

        public bool HasHandlers(string name) => _handlers.GetValueOrDefault(name) is not null;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = method?.Name ?? "";
            if (name.StartsWith("add_", StringComparison.Ordinal))
            {
                _handlers[name[4..]] = Delegate.Combine(_handlers.GetValueOrDefault(name[4..]), (Delegate)args![0]!);
                return null;
            }
            if (name.StartsWith("remove_", StringComparison.Ordinal))
            {
                _handlers[name[7..]] = Delegate.Remove(_handlers.GetValueOrDefault(name[7..]), (Delegate)args![0]!);
                return null;
            }
            switch (name)
            {
                case "get_IsConnectedToGateway":
                case "get_HasHandshakeSnapshot":
                    return false;
                case "get_AdvertisedServerMethods":
                    return Array.Empty<string>();
                case "get_GrantedOperatorScopes":
                    return Scopes;
                case "RequestCronListAsync":
                case "RequestCronStatusAsync":
                    lock (Requests) Requests.Add(name);
                    return Task.CompletedTask;
                case "ReadSearchCatalogAsync":
                    lock (Requests) Requests.Add((string)args![0]!);
                    // Supply external pages, not an already-assembled fake inventory: exercise
                    // the same pagination owner used by the real operator client.
                    return GatewaySearchProtocol.ReadAutomationPagesAsync(
                        (input, _) => CatalogPage is { } read ? read((JsonObject)input!)
                            : Replies.TryGetValue("cron.list", out var cron) ? Task.FromResult(cron)
                            : Task.FromException<JsonElement>(new InvalidOperationException("unknown method")),
                        args![1], (CancellationToken)args[2]!);
                case "SendWizardRequestAsync":
                    var requested = (string)args![0]!;
                    lock (Requests) Requests.Add(requested);
                    if (Reads.TryGetValue(requested, out var readReply))
                        return readReply();
                    if (Pending.TryGetValue(requested, out var pending))
                        return pending.Task;
                    return Replies.TryGetValue(requested, out var reply)
                        ? Task.FromResult(reply)
                        : Task.FromException<JsonElement>(new InvalidOperationException("unknown method"));
                default:
                    throw new NotSupportedException(name);
            }
        }
    }
}
