using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Per-server, memory-only chat state. The gate serializes mutation and publication;
/// delays and waits never hold it, so Stop and answers remain responsive.
/// </summary>
public sealed class InteractiveGatewayRuntime : IAsyncDisposable
{
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly(new[]
    {
        "chat.send", "chat.abort", "chat.inject", "chat.history", "sessions.list", "sessions.preview",
        "sessions.create", "sessions.patch", "sessions.delete", "sessions.reset",
        "question.request", "question.list", "question.get", "question.resolve", "question.waitAnswer",
        "exec.approval.resolve"
    });

    private readonly GatewayScenario _seed;
    private readonly Func<string, object, Task> _publish;
    private readonly Func<CancellationToken, Task> _disconnect;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime;
    private readonly ConcurrentDictionary<string, JsonObject> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonObject>> _histories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Session, string Nonce), Run> _sends = [];
    private readonly Dictionary<string, FixtureQuestion> _questions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Approval> _approvals = new(StringComparer.Ordinal);
    private readonly List<Task> _work = [];
    private int _nextSession;
    private int _nextMessage;
    private int _nextInteraction;
    private bool _disposed;

    public FixturePlayback Playback { get; } = new();
    public TimeSpan EventDelay { get; }
    public TimeSpan InteractionTimeout { get; }
    public bool ContainsSession(string key) => _sessions.ContainsKey(key);
    public void ThrowIfFaulted()
    {
        lock (_work)
            foreach (var task in _work.Where(task => task.IsFaulted))
                throw new InvalidOperationException("Interactive fixture playback failed.", task.Exception);
    }

    private sealed class Run(string id, string session, string sessionId, string scenario, string input)
    {
        public string Id { get; } = id;
        public string Session { get; } = session;
        public string SessionId { get; } = sessionId;
        public string Scenario { get; } = scenario;
        public string Input { get; } = input;
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StringBuilder Text { get; } = new();
        public bool Terminal { get; set; }
        public int Sequence { get; set; }
    }

    private sealed record Approval(string Id, Run Run, object Request, long CreatedAt, long ExpiresAt)
    {
        public TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal InteractiveGatewayRuntime(GatewayScenario seed, Func<string, object, Task> publish,
        Func<CancellationToken, Task> disconnect, CancellationToken ct, TimeSpan eventDelay, TimeSpan interactionTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(eventDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interactionTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interactionTimeout, TimeSpan.FromMinutes(5));
        _seed = seed;
        _publish = publish;
        _disconnect = disconnect;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        EventDelay = eventDelay;
        InteractionTimeout = interactionTimeout;
        Seed();
    }

    private void Seed()
    {
        var list = JsonSerializer.SerializeToElement(_seed.Respond("sessions.list", JsonSerializer.SerializeToElement(new { })));
        foreach (var row in list.GetProperty("sessions").EnumerateArray())
        {
            var key = row.GetProperty("key").GetString()!;
            _sessions[key] = Obj(row);
            var history = JsonSerializer.SerializeToElement(_seed.Respond("chat.history", JsonSerializer.SerializeToElement(new { sessionKey = key })));
            _histories[key] = history.GetProperty("messages").EnumerateArray().Select(message => Obj(message)).ToList();
            _nextMessage = Math.Max(_nextMessage, _histories[key].Count);
        }
    }

    internal async Task<object> RespondAsync(string method, JsonElement p, CancellationToken ct)
    {
        if (method == "question.waitAnswer")
            return await WaitAnswerAsync(p, ct);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            return method switch
            {
                "chat.send" => Send(p),
                "chat.abort" => await AbortAsync(p),
                "chat.inject" => await InjectAsync(p),
                "chat.history" => History(p),
                "sessions.list" => List(p),
                "sessions.preview" => Preview(p),
                "sessions.create" => await CreateAsync(p),
                "sessions.patch" or "sessions.reset" or "sessions.delete" => await MutateAsync(method, p),
                "question.list" => ListQuestions(p),
                "question.get" => GetQuestion(p),
                "question.request" => await RequestQuestionAsync(p),
                "question.resolve" => await ResolveQuestionAsync(p),
                "exec.approval.resolve" => await ResolveApprovalAsync(p),
                _ => throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown interactive fixture method.", unexpected: true)
            };
        }
        finally { _gate.Release(); }
    }

    // Release playback after attempting the acknowledgement, even if its socket
    // disconnected. Accepted work and idempotent retries survive connection loss.
    internal async Task AcknowledgeAsync(string method, object payload)
    {
        if (method != "chat.send") return;
        var id = JsonSerializer.SerializeToElement(payload).GetProperty("runId").GetString()!;
        await _gate.WaitAsync(_lifetime.Token);
        try { _runs[id].Accepted.TrySetResult(); }
        finally { _gate.Release(); }
    }

    private object Send(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "sessionKey", "message", "idempotencyKey", "sessionId", "agentId");
        var key = Required(p, "sessionKey");
        var row = Session(key);
        var message = Required(p, "message");
        var nonce = Required(p, "idempotencyKey");
        if (message.Length > 32_768 || nonce.Length > 128)
            throw Invalid("Fixture message or idempotency key exceeds its limit.");
        if (Optional(p, "agentId") is { } agent && agent != row["agentId"]!.GetValue<string>())
            throw Invalid("agentId does not match the session.");
        if (Optional(p, "sessionId") is { } sessionId && sessionId != row["sessionId"]!.GetValue<string>())
            throw Invalid("sessionId does not match the session.");
        if (_sends.TryGetValue((key, nonce), out var prior))
        {
            if (prior.SessionId != row["sessionId"]!.GetValue<string>())
                throw Invalid("Idempotency key belongs to a reset or replaced session.");
            if (prior.Input != message) throw Invalid("Idempotency key was already used with different input.");
            return new { runId = prior.Id, status = "started" };
        }
        if (row["archived"]?.GetValue<bool>() == true) throw Invalid("The fixture session is archived.");
        if (_runs.Values.Any(run => run.Session == key && !run.Terminal))
            throw new FixtureRequestException("UNAVAILABLE", "Fixture session already has an active run. Queue in the client or stop it first.");
        if (_runs.Count >= 256) throw Invalid("Fixture run limit reached. Restart this disposable fixture.");
        var scenario = Playback.TakeNext();
        if (scenario == "send-error")
            throw new FixtureRequestException("UNAVAILABLE", "Synthetic send rejection. The next send can be retried.");
        var run = new Run($"fixture-run-{_runs.Count + 1}", key, row["sessionId"]!.GetValue<string>(), scenario, message);
        _runs.Add(run.Id, run);
        _sends.Add((key, nonce), run);
        AddMessage(key, "user", message);
        row["hasActiveRun"] = true;
        row["status"] = "running";
        Track(PlayAsync(run));
        return new { runId = run.Id, status = "started" };
    }

    private async Task PlayAsync(Run run)
    {
        try
        {
            await run.Accepted.Task.WaitAsync(_lifetime.Token);
            if (!await StepAsync(run, async () =>
            {
                await AgentAsync(run, "lifecycle", new { phase = "start" });
                await ChangedAsync(run.Session);
            })) return;
            if (!await StepAsync(run, () => AgentAsync(run, "reasoning", new { delta = "Inspecting synthetic data. No provider or command is executed." }))) return;
            if (run.Scenario == "slow")
                for (var i = 0; i < 12; i++)
                    if (!await StepAsync(run, () => ChatAsync(run, "status", new { phase = "starting_model" }))) return;
            if (run.Scenario is "tools" or "tool-error")
            {
                var id = $"{run.Id}-tool";
                if (!await StepAsync(run, async () =>
                {
                    var message = AddMessage(run.Session, "assistant", "");
                    message["content"] = JsonSerializer.SerializeToNode(new[] { new { type = "toolCall", id, name = "read", arguments = new { path = "fixture.txt" } } });
                    message["stopReason"] = "toolUse";
                    await AgentAsync(run, "tool", new { phase = "start", name = "read", toolCallId = id, args = new { path = "fixture.txt" } });
                })) return;
                if (!await StepAsync(run, async () =>
                {
                    var text = run.Scenario == "tool-error" ? "Synthetic tool failure." : "Synthetic file contents.";
                    var result = AddMessage(run.Session, "toolResult", text);
                    result["toolCallId"] = id;
                    result["toolName"] = "read";
                    result["isError"] = run.Scenario == "tool-error";
                    await AgentAsync(run, "tool", new
                    {
                        phase = "result", name = "read", toolCallId = id, isError = run.Scenario == "tool-error",
                        result = new { content = new[] { new { type = "text", text } } }
                    });
                })) return;
            }
            if (run.Scenario == "agent-activity")
            {
                var id = $"{run.Id}-command";
                if (!await StepAsync(run, () => AgentAsync(run, "job", new { state = "running", title = "Synthetic activity" }))) return;
                if (!await StepAsync(run, () => AgentAsync(run, "item", new { kind = "command", phase = "start", toolCallId = id, name = "exec", command = "echo fixture-only" }))) return;
                if (!await StepAsync(run, () => AgentAsync(run, "command_output", new { phase = "end", toolCallId = id, status = "completed", output = "Synthetic output. Nothing executed." }))) return;
                if (!await StepAsync(run, () => AgentAsync(run, "item", new { kind = "patch", phase = "start", toolCallId = $"{run.Id}-patch", name = "apply_patch", title = "Synthetic patch" }))) return;
                if (!await StepAsync(run, () => AgentAsync(run, "patch", new { phase = "end", toolCallId = $"{run.Id}-patch", summary = "Synthetic patch summary. No files changed." }))) return;
            }
            if (run.Scenario.StartsWith("question-", StringComparison.Ordinal))
            {
                FixtureQuestion? question = null;
                if (!await StepAsync(run, async () =>
                {
                    question = FixtureQuestion.CreateBuiltin(NextQuestionId(), run.Session, run.Id, run.Scenario, InteractionTimeout);
                    _questions.Add(question.Id, question);
                    Track(ExpireQuestionAsync(question));
                    await _publish("question.requested", question.Record);
                })) return;
                var answer = await question!.Resolved.Task.WaitAsync(_lifetime.Token);
                if (!await DeltaAsync(run, answer == "answered" ? "Your answer was accepted. " : $"The question was {answer}. ")) return;
            }
            if (run.Scenario is "approval" or "approval-deny-only")
            {
                Approval? approval = null;
                if (!await StepAsync(run, async () =>
                {
                    var now = Now;
                    approval = new Approval($"fixture-approval-{++_nextInteraction}", run, new
                    {
                        command = run.Scenario == "approval-deny-only" ? "" : "echo fixture-only",
                        host = "gateway", sessionKey = run.Session, agentId = Session(run.Session)["agentId"]!.GetValue<string>()
                    }, now, now + (long)InteractionTimeout.TotalMilliseconds);
                    _approvals.Add(approval.Id, approval);
                    await _publish("exec.approval.requested", new { id = approval.Id, request = approval.Request, createdAtMs = now, expiresAtMs = approval.ExpiresAt });
                })) return;
                string decision;
                try { decision = await approval!.Decision.Task.WaitAsync(InteractionTimeout, _lifetime.Token); }
                catch (TimeoutException)
                {
                    await _gate.WaitAsync(_lifetime.Token);
                    try
                    {
                        if (approval!.Decision.TrySetResult("deny"))
                            await PublishApprovalAsync(approval, "deny");
                        decision = await approval.Decision.Task;
                    }
                    finally { _gate.Release(); }
                }
                if (!await DeltaAsync(run, $"Approval decision: {decision}. No command was executed. ")) return;
            }
            if (!await DeltaAsync(run, "This is a **synthetic streaming answer**.\n\n")) return;
            if (run.Scenario == "disconnect")
            {
                if (!await StepAsync(run, () => _disconnect(_lifetime.Token))) return;
            }
            if (run.Scenario == "stream-error")
            {
                await StepAsync(run, () => FinishAsync(run, "error", "Synthetic mid-stream failure."));
                return;
            }
            if (!await DeltaAsync(run, "```csharp\nvar ready = true;\n```\n\n")) return;
            if (!await DeltaAsync(run, "| Check | Result |\n|---|---|\n| Provider | Not contacted |\n\nFIXTURE INTERACTIVE COMPLETE")) return;
            await StepAsync(run, () => FinishAsync(run, "final"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task<bool> StepAsync(Run run, Func<Task> action)
    {
        await Playback.WaitAsync(EventDelay, _lifetime.Token);
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (run.Terminal) return false;
            await action();
            return true;
        }
        finally { _gate.Release(); }
    }

    private Task<bool> DeltaAsync(Run run, string delta) => StepAsync(run, async () =>
    {
        run.Text.Append(delta);
        if (run.Scenario == "chat-deltas")
            await ChatAsync(run, "delta", new { deltaText = delta, message = new { role = "assistant", content = run.Text.ToString() } });
        else
            await AgentAsync(run, "assistant", new { delta });
    });

    private Task AgentAsync(Run run, string stream, object data) => _publish("agent", new
    {
        runId = run.Id, sessionKey = run.Session, seq = ++run.Sequence, stream, ts = Now, data
    });

    private Task ChatAsync(Run run, string state, object? extra = null)
    {
        var payload = Obj(extra ?? new { });
        payload["runId"] = run.Id;
        payload["sessionKey"] = run.Session;
        payload["seq"] = ++run.Sequence;
        payload["state"] = state;
        return _publish("chat", payload);
    }

    private async Task FinishAsync(Run run, string state, string? error = null)
    {
        if (run.Terminal) return;
        run.Terminal = true;
        foreach (var question in _questions.Values.Where(q => q.RunId == run.Id && q.IsPending))
            await EndQuestionAsync(question, "cancelled");
        foreach (var approval in _approvals.Values.Where(a => a.Run == run && !a.Decision.Task.IsCompleted))
        {
            approval.Decision.TrySetResult("deny");
            await PublishApprovalAsync(approval, "deny");
        }
        var message = run.Text.Length == 0 ? null : AddMessage(run.Session, "assistant", run.Text.ToString());
        var row = Session(run.Session);
        row["hasActiveRun"] = false;
        row["status"] = "idle";
        var final = new JsonObject();
        if (message is not null) final["message"] = message.DeepClone();
        if (error is not null) final["errorMessage"] = error;
        await ChatAsync(run, state, final);
        await AgentAsync(run, "lifecycle", error is null ? new { phase = "end", message = state } : new { phase = "error", message = error });
        await ChangedAsync(run.Session);
    }

    private async Task<object> AbortAsync(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "sessionKey", "runId");
        var key = Required(p, "sessionKey");
        _ = Session(key);
        var id = Optional(p, "runId");
        var targets = _runs.Values.Where(run => run.Session == key && !run.Terminal && (id is null || run.Id == id)).ToArray();
        foreach (var run in targets) await FinishAsync(run, "aborted");
        return new { ok = true, aborted = targets.Length > 0, runIds = targets.Select(r => r.Id).ToArray() };
    }

    private async Task<object> InjectAsync(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "sessionKey", "message", "label");
        var key = Required(p, "sessionKey");
        _ = Session(key);
        if (_runs.Values.Any(r => r.Session == key && !r.Terminal)) throw Invalid("Stop the active run before injecting.");
        var message = Required(p, "message");
        var label = Optional(p, "label");
        if (message.Length > 32_768 || label?.Length > 100) throw Invalid("Injected message exceeds fixture limits.");
        var row = AddMessage(key, "assistant", label is null ? message : $"{label}: {message}");
        await ChangedAsync(key);
        return new { ok = true, messageId = row["id"]!.GetValue<string>() };
    }

    private object History(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "sessionKey", "limit");
        var key = Required(p, "sessionKey");
        var row = Session(key);
        return new { sessionKey = key, sessionId = row["sessionId"]!.GetValue<string>(),
            messages = _histories[key].TakeLast(Positive(p, "limit", 1000)).Select(m => m.DeepClone()).ToArray(), thinkingLevel = "off" };
    }

    private object List(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "agentId", "limit", "activeMinutes", "includeGlobal", "includeUnknown", "includeDerivedTitles", "includeLastMessage", "archived");
        var agent = Optional(p, "agentId");
        if (agent is not null) ValidateAgent(agent);
        foreach (var flag in new[] { "includeGlobal", "includeUnknown", "includeDerivedTitles", "includeLastMessage" }) Boolean(p, flag);
        var archived = Boolean(p, "archived") ?? false;
        var minutes = Positive(p, "activeMinutes", int.MaxValue);
        var rows = _sessions.Values.Where(row => (agent is null || row["agentId"]!.GetValue<string>() == agent)
                && (row["archived"]?.GetValue<bool>() ?? false) == archived
                && Now - row["updatedAt"]!.GetValue<long>() <= (long)minutes * 60_000)
            .OrderByDescending(row => row["updatedAt"]!.GetValue<long>()).Take(Positive(p, "limit", 100))
            .Select(row => row.DeepClone()).ToArray();
        return new { ts = Now, count = rows.Length, defaults = new { modelProvider = "fixture", model = "browse", contextTokens = 128_000 }, sessions = rows };
    }

    private object Preview(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "keys", "limit", "maxChars");
        if (!p.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is < 1 or > 100)
            throw Invalid("keys must be a nonempty array with at most 100 entries.");
        var limit = Positive(p, "limit", 12);
        var max = Positive(p, "maxChars", 240);
        var previews = keys.EnumerateArray().Select(value =>
        {
            if (value.ValueKind != JsonValueKind.String) throw Invalid("keys must contain strings.");
            var key = value.GetString()!;
            _ = Session(key);
            return new { key, status = _histories[key].Count == 0 ? "empty" : "ok", items = _histories[key].TakeLast(limit).Select(m =>
            {
                var text = m["content"] is JsonValue v && v.TryGetValue<string>(out var plain) ? plain : m["content"]?.ToJsonString() ?? "";
                return new { role = m["role"]!.GetValue<string>(), text = text[..Math.Min(text.Length, max)] };
            }).ToArray() };
        }).ToArray();
        return new { ts = Now, previews };
    }

    private async Task<object> CreateAsync(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "key", "agentId", "parentSessionKey", "emitCommandHooks", "succeedsParent", "fork", "forkFrom");
        var agent = Optional(p, "agentId") ?? "main";
        ValidateAgent(agent);
        var key = Optional(p, "key") ?? $"agent:{agent}:fixture-created-{++_nextSession}";
        if (!key.StartsWith($"agent:{agent}:", StringComparison.Ordinal) || key.Length > 200 || key.Any(char.IsWhiteSpace))
            throw Invalid("key must be a bounded canonical fixture agent key.");
        if (_sessions.ContainsKey(key)) throw Invalid("Session key already exists.");
        if (_sessions.Count >= 100) throw Invalid("Fixture session limit reached.");
        var parent = Optional(p, "parentSessionKey");
        if (parent is not null) _ = Session(parent);
        Boolean(p, "emitCommandHooks");
        if (Boolean(p, "succeedsParent") == true)
            throw new FixtureRequestException("FIXTURE_UNSUPPORTED", "Session succession is not simulated.");
        var fork = Boolean(p, "fork") ?? false;
        var forkFrom = Optional(p, "forkFrom");
        if (forkFrom is not null && (!fork || forkFrom != "last-completed"))
            throw Invalid("forkFrom requires fork:true and the last-completed selector.");
        if (fork && parent is null) throw Invalid("A fork needs parentSessionKey.");
        var history = fork ? _histories[parent!] : [];
        // Tool calls are mid-turn checkpoints, not completed assistant replies.
        var count = forkFrom == "last-completed"
            ? history.FindLastIndex(message => message["role"]!.GetValue<string>() == "assistant"
                && message["stopReason"]?.GetValue<string>() != "toolUse") + 1
            : history.Count;
        var id = Guid.NewGuid().ToString();
        _sessions[key] = Obj(new { key, sessionId = id, agentId = agent, label = "Fixture: new conversation", displayName = "Fixture: new conversation",
            kind = "direct", channel = "webchat", status = "idle", hasActiveRun = false, isMain = false, model = "browse", modelProvider = "fixture", updatedAt = Now });
        _histories[key] = history.Take(count).Select(m => m.DeepClone().AsObject()).ToList();
        await ChangedAsync(key);
        return new { ok = true, key, sessionId = id };
    }

    private async Task<object> MutateAsync(string method, JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, method switch
        {
            "sessions.patch" => ["key", "label", "pinned", "unread", "archived", "expectedMarkedUnreadAt", "model", "thinkingLevel"],
            "sessions.reset" => ["key", "reason"],
            _ => ["key", "deleteTranscript"]
        });
        var key = Required(p, "key");
        var row = Session(key);
        if (_runs.Values.Any(run => run.Session == key && !run.Terminal)
            && (method != "sessions.patch" || Boolean(p, "archived") == true))
            throw Invalid("Stop the active run before removing or resetting this session.");
        if (method == "sessions.patch")
        {
            foreach (var field in new[] { "pinned", "unread", "archived" }) Boolean(p, field);
            foreach (var field in new[] { "label", "model", "thinkingLevel" })
                if (p.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null) _ = Required(p, field);
            if (key == GatewayScenario.MainSessionKey && Boolean(p, "archived") == true) throw Invalid("The main session cannot be archived.");
            if (p.TryGetProperty("expectedMarkedUnreadAt", out var expected))
            {
                if (Boolean(p, "unread") != false || p.EnumerateObject().Count() != 3
                    || expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt64(out var stamp) || stamp < 0)
                    throw Invalid("expectedMarkedUnreadAt is only valid for a read acknowledgement.");
                if (row["markedUnreadAt"]?.GetValue<long>() != stamp)
                    throw new FixtureRequestException("UNAVAILABLE", "The unread marker changed. Refresh before acknowledging.");
            }
            foreach (var field in new[] { "label", "pinned", "unread", "archived", "model", "thinkingLevel" })
                if (p.TryGetProperty(field, out var value)) row[field] = JsonNode.Parse(value.GetRawText());
            if (Boolean(p, "unread") == true)
                row["markedUnreadAt"] = Math.Max(Now, (row["markedUnreadAt"]?.GetValue<long>() ?? 0) + 1);
            else if (Boolean(p, "unread") == false)
                row.Remove("markedUnreadAt");
            if (row["label"] is not null) row["displayName"] = row["label"]!.DeepClone();
        }
        else if (method == "sessions.reset")
        {
            _ = Optional(p, "reason");
            _histories[key].Clear();
            row["sessionId"] = Guid.NewGuid().ToString();
        }
        else
        {
            Boolean(p, "deleteTranscript");
            if (key == GatewayScenario.MainSessionKey) throw Invalid("The main session cannot be deleted.");
            _sessions.TryRemove(key, out _);
            _histories.Remove(key);
        }
        await ChangedAsync(key);
        return new { ok = true, key, deleted = method == "sessions.delete" };
    }

    private JsonObject AddMessage(string key, string role, string text)
    {
        var history = _histories[key];
        var id = $"fixture-message-{++_nextMessage}";
        var message = Obj(new { id, role, content = text, timestamp = Now, __openclaw = new { id, seq = _nextMessage, kind = "message" } });
        history.Add(message);
        if (history.Count > 1000) history.RemoveAt(0);
        _sessions[key]["updatedAt"] = Now;
        return message;
    }

    private Task ChangedAsync(string key) => _publish("sessions.changed", new { sessionKey = key, reason = "fixture" });

    private object ListQuestions(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p);
        return new { questions = _questions.Values.Select(q => q.Record.DeepClone()).ToArray() };
    }

    private object GetQuestion(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "id");
        return new { question = Question(Required(p, "id")).Record.DeepClone() };
    }

    private async Task<object> RequestQuestionAsync(JsonElement p)
    {
        if (_questions.Count >= 256) throw Invalid("Fixture question limit reached.");
        var question = FixtureQuestion.FromRequest(p, NextQuestionId(), InteractionTimeout);
        if (_questions.ContainsKey(question.Id)) throw Invalid("Question ID already exists.");
        if (question.SessionKey is { } key) _ = Session(key);
        if (question.RunId is { } runId && (!_runs.TryGetValue(runId, out var run) || run.Terminal || run.Session != question.SessionKey))
            throw Invalid("Question run is not active in this session.");
        _questions.Add(question.Id, question);
        Track(ExpireQuestionAsync(question));
        await _publish("question.requested", question.Record);
        return new { id = question.Id, expiresAtMs = question.ExpiresAt };
    }

    private async Task<object> ResolveQuestionAsync(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "id", "answers", "cancel", "resolvedBy", "resolutionId");
        var question = Question(Required(p, "id"));
        if (question.IsPending && question.ExpiresAt <= Now) await EndQuestionAsync(question, "expired");
        var result = question.Resolve(p);
        await _publish("question.resolved", question.Result());
        return result;
    }

    private async Task<object> WaitAnswerAsync(JsonElement p, CancellationToken ct)
    {
        GatewayScenario.ValidateProperties(p, "id", "timeoutMs", "includeResolutionId");
        FixtureQuestion question;
        var timeout = Math.Min(Positive(p, "timeoutMs", 1000), 60_000);
        var includeResolution = Boolean(p, "includeResolutionId") == true;
        await _gate.WaitAsync(ct);
        try { question = Question(Required(p, "id")); }
        finally { _gate.Release(); }
        try { await question.Resolved.Task.WaitAsync(TimeSpan.FromMilliseconds(timeout), ct); }
        catch (TimeoutException) { return new { status = "pending" }; }
        await _gate.WaitAsync(ct);
        try { return question.Result(includeId: false, includeResolutionId: includeResolution); }
        finally { _gate.Release(); }
    }

    private async Task ExpireQuestionAsync(FixtureQuestion question)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, question.ExpiresAt - Now)), _lifetime.Token);
            await _gate.WaitAsync(_lifetime.Token);
            try { if (question.IsPending) await EndQuestionAsync(question, "expired"); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task EndQuestionAsync(FixtureQuestion question, string status)
    {
        question.End(status);
        await _publish("question.resolved", question.Result());
    }

    private async Task<object> ResolveApprovalAsync(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "id", "decision");
        var id = Required(p, "id");
        var decision = Required(p, "decision");
        if (decision is not ("allow-once" or "allow-always" or "deny")) throw Invalid("Unsupported approval decision.");
        if (!_approvals.TryGetValue(id, out var approval) || approval.Run.Terminal || approval.Decision.Task.IsCompleted || approval.ExpiresAt <= Now)
            throw Invalid("Approval is not pending.");
        if (approval.Run.Scenario == "approval-deny-only" && decision != "deny") throw Invalid("This approval only permits Deny.");
        approval.Decision.TrySetResult(decision);
        await PublishApprovalAsync(approval, decision);
        return new { ok = true };
    }

    private Task PublishApprovalAsync(Approval approval, string decision) => _publish("exec.approval.resolved",
        new { id = approval.Id, request = approval.Request, decision, resolvedBy = "fixture", ts = Now });

    private FixtureQuestion Question(string id) => _questions.TryGetValue(id, out var question) ? question : throw Invalid("Unknown fixture question ID.");
    private string NextQuestionId()
    {
        string id;
        do { id = $"fixture-question-{++_nextInteraction}"; } while (_questions.ContainsKey(id));
        return id;
    }
    private JsonObject Session(string key) => _sessions.TryGetValue(key, out var row) ? row : throw Invalid("Unknown fixture session key.");
    private static void ValidateAgent(string agent)
    {
        if (agent is not ("main" or "research")) throw Invalid("Unknown fixture agentId.");
    }

    internal static JsonObject Obj(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    internal static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static string Required(JsonElement p, string name) => GatewayScenario.RequiredString(p, name);
    internal static string? Optional(JsonElement p, string name) => p.TryGetProperty(name, out _) ? Required(p, name) : null;
    internal static FixtureRequestException Invalid(string message) => new("INVALID_PARAMS", message);
    internal static int Positive(JsonElement p, string name, int fallback)
    {
        if (!p.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result <= 0) throw Invalid($"{name} must be a positive integer.");
        return result;
    }
    internal static bool? Boolean(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid($"{name} must be boolean.");
        return value.GetBoolean();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        Task[] tasks;
        try
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            lock (_work) tasks = _work.ToArray();
        }
        finally { _gate.Release(); }
        try { await Task.WhenAll(tasks); }
        finally { _lifetime.Dispose(); }
    }

    private void Track(Task task)
    {
        lock (_work) _work.Add(task);
    }
}
