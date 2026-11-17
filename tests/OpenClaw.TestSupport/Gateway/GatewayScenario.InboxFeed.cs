using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    private readonly InboxFeedData? _inboxFeed;

    /// <summary>Canonical read-only Inbox inputs. Optional chat sends accept only the
    /// two synthetic explain prompts below; no scheduler, provider or approval is executed.</summary>
    public static GatewayScenario CreateInboxFeed(bool seedApproval = false, bool allowExplainPrompts = false)
    {
        var browse = CreateBrowse();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var job = Job("fixture-inbox-cron", "Fixture nightly archive", "main", true,
            new { kind = "every", everyMs = 86_400_000 },
            new { kind = "agentTurn", message = "Synthetic fixture only. Never executed." },
            new { mode = "none" },
            new { lastRunStatus = "error", lastRunAtMs = now - 60_000,
                lastError = "Synthetic archive destination unavailable.", nextRunAtMs = now + 86_400_000 });
        job["createdAtMs"] = now - 120_000;
        job["updatedAtMs"] = now - 60_000;
        return new(browse._sessions, browse._reads, allowChatSend: allowExplainPrompts,
            cronJobs: [job], inboxFeed: new InboxFeedData(seedApproval));
    }

    /// <summary>Changes the fixture's authoritative inventory without sending an event.
    /// Used while its owned sockets are disconnected to prove fresh list hydration.</summary>
    public void ReplaceInboxApprovalForReconnect() => InboxFeed.ReplaceForReconnect();

    /// <summary>Admitted wire reads carrying explicit offset=0 and limit=200, not
    /// other page sizes used by the native Automations page. No request text is retained.</summary>
    public int InboxFeedPagedReadCount => InboxFeed.PagedReadCount;

    /// <summary>Changes synthetic scheduler input only; it never runs or edits a real job.</summary>
    public void SetInboxAutomationState(bool failed, bool overdue = false, bool removed = false)
    {
        _ = InboxFeed;
        var jobs = _cronJobs!;
        lock (jobs)
        {
            if (removed)
            {
                jobs.Remove("fixture-inbox-cron");
                return;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            jobs["fixture-inbox-cron"]["state"] = JsonSerializer.SerializeToNode(new
            {
                lastRunStatus = failed ? "error" : "ok", lastRunAtMs = now - 60_000,
                lastError = failed ? "Synthetic archive destination unavailable." : null,
                nextRunAtMs = overdue ? now - 3_600_000 : now + 86_400_000,
            });
        }
    }

    private InboxFeedData InboxFeed => _inboxFeed ??
        throw new InvalidOperationException("Inbox feed inputs require the inbox-feed scenario.");

    internal JsonElement CreateInboxLiveApproval() => InboxFeed.PublishLive();

    private static void ValidateInboxQuestion(JsonElement parameters)
    {
        ValidateProperties(parameters, "sessionKey", "message", "idempotencyKey", "attachments");
        var message = RequiredString(parameters, "message");
        if (RequiredString(parameters, "sessionKey") != MainSessionKey ||
            message is not ("This automation failed: Fixture nightly archive. Explain why it failed and how to fix it." or
                "These model-provider credentials need attention: Fixture Cloud: expired. Explain what expired and how to re-authenticate them.") ||
            (parameters.TryGetProperty("attachments", out var attachments) &&
                (attachments.ValueKind != JsonValueKind.Array || attachments.GetArrayLength() != 0)))
            throw new FixtureRequestException("FIXTURE_READ_ONLY", "Only the fixture Inbox explain prompts are accepted.");
        _ = RequiredString(parameters, "idempotencyKey");
    }

    // This supplies wire records only. Parsing, redaction, countdowns, routing and
    // reconnect ownership remain in the real client and mounted app. All values are synthetic.
    private sealed class InboxFeedData
    {
        private static readonly TimeSpan HydratedLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan LiveLifetime = TimeSpan.FromSeconds(45);
        private readonly object _sync = new();
        private readonly long _snapshotAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private JsonElement? _approval;
        private DateTimeOffset _expires;
        private bool _livePublished;
        private int _pagedReadCount;
        public bool HasInitialApproval { get; }
        public int PagedReadCount { get { lock (_sync) return _pagedReadCount; } }

        public InboxFeedData(bool seedApproval)
        {
            HasInitialApproval = seedApproval;
            if (seedApproval) SetApproval("fixture-inbox-initial", "echo fixture-initial", HydratedLifetime);
        }

        public void ReplaceForReconnect()
        {
            lock (_sync)
                SetApproval("fixture-inbox-reconnected", "echo fixture-reconnected", HydratedLifetime);
        }

        public JsonElement PublishLive()
        {
            lock (_sync)
            {
                if (_livePublished) throw new InvalidOperationException("The fixture live approval was already published.");
                _livePublished = true;
                // The current exec producer escapes control characters. Keep the positive
                // wire sample printable, with a shell separator and an independent final marker.
                return SetApproval("fixture-inbox-live",
                    "curl https://example.test/run?token=fixture-secret ; echo fixture-end", LiveLifetime);
            }
        }

        private JsonElement SetApproval(string id, string command, TimeSpan lifetime)
        {
            var now = DateTimeOffset.UtcNow;
            _expires = now + lifetime;
            // Deliberately skew the producer clock. The client must use the stated
            // lifetime on its own clock, not wait another three hours for wall expiry.
            var created = now.AddHours(3).ToUnixTimeMilliseconds();
            var approval = JsonSerializer.SerializeToElement(new
            {
                id, createdAtMs = created, expiresAtMs = created + (long)lifetime.TotalMilliseconds,
                request = new
                {
                    command, cwd = @"C:\fixture", host = "gateway",
                    sessionKey = OtherSessionKey, agentId = "research",
                    allowedDecisions = new[] { "allow-once", "deny" }
                }
            });
            _approval = approval;
            return approval;
        }

        public void RecordCronPage(JsonElement parameters)
        {
            if (OptionalInt(parameters, "offset") == 0 && OptionalInt(parameters, "limit") == 200)
                lock (_sync) _pagedReadCount++;
        }

        public object Read(string method, JsonElement parameters)
        {
            ValidateProperties(parameters);
            lock (_sync)
            {
                if (method == "exec.approval.list")
                    return _approval is { } approval && DateTimeOffset.UtcNow < _expires
                        ? new[] { approval } : Array.Empty<JsonElement>();
                // Repeated account reads describe the same credential expiry episode.
                var now = _snapshotAtMs;
                if (method == "models.authStatus")
                    return new
                    {
                        ts = now, providers = new[]
                        {
                            new { provider = "fixture-cloud", displayName = "Fixture Cloud", status = "expired",
                                expiry = new { at = now - 60_000 },
                                profiles = new[] { new { profileId = "fixture-token", type = "token", status = "expired" } } }
                        }
                    };
                throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown Inbox read.");
            }
        }
    }
}
