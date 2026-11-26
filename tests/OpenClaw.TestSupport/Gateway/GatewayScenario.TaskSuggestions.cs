// Synthetic task producer for mounted production chat proof. No provider, filesystem or worker is invoked.
using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    public FixtureTaskSuggestions? TaskSuggestions { get; }

    /// <summary>Opt-in task mutations remain in memory; ordinary browse scenarios stay read-only.</summary>
    public static GatewayScenario CreateTaskSuggestions(bool allowSyntheticChatSend = false)
    {
        var browse = CreateBrowse();
        return new(browse._sessions, browse._reads, taskSuggestions: new FixtureTaskSuggestions(), allowChatSend: allowSyntheticChatSend);
    }
}

/// <summary>Records only synthetic launch choices and returns an authoritative fixture session.</summary>
public sealed class FixtureTaskSuggestions
{
    public const string TaskId = "fixture-task";
    public const string Title = "Check native suggested tasks";
    public const string Prompt = "Read the native card.\nKeep <instructions> and **literal text** unchanged.\nReport the synthetic proof result.";
    public const string Cwd = "/gateway/projects/native-proof";
    public static readonly string[] Methods = ["taskSuggestions.list", "taskSuggestions.accept", "taskSuggestions.dismiss", "environments.list", "question.list"];
    private readonly object _gate = new();
    private readonly HashSet<string> _additional = new(StringComparer.Ordinal);
    public void AddPendingTask(string id) { lock (_gate) _additional.Add(id); }
    public bool PrimaryAvailable { get; set; } = true;
    public bool RequireRepositoryCorrection { get; set; }
    public string TaskSessionKey { get; set; } = GatewayScenario.MainSessionKey;
    public object[] Questions { get; set; } = [];
    private bool _dismissed;
    private string? _acceptedKey;
    public ConcurrentQueue<JsonElement> Acceptances { get; } = new();
    public int Dismissals { get; private set; }

    internal object Respond(string method, JsonElement parameters, Func<object> createSession)
    {
        lock (_gate)
        {
            if (method == "question.list") return new { questions = Questions };
            if (method == "environments.list")
                return new { profiles = new[] { new { id = "fixture-cloud", name = "Synthetic cloud" } } };
            if (method == "taskSuggestions.list")
                return new { suggestions = (!PrimaryAvailable || _dismissed || _acceptedKey is not null ? Array.Empty<string>() : new[] { TaskId })
                    .Concat(_additional).Select(id => new
                    {
                        id, title = Title, prompt = Prompt, tldr = "Exercise a native task without changing the current conversation.",
                        cwd = Cwd, sessionKey = TaskSessionKey, agentId = "main", createdAt = 17L
                    }).ToArray() };
            var requestedId = parameters.GetProperty("taskId").GetString()!;
            if (requestedId != TaskId && !_additional.Contains(requestedId))
                throw new FixtureRequestException("INVALID_REQUEST", "Unknown synthetic task.");
            if (method == "taskSuggestions.dismiss")
            {
                if (requestedId == TaskId) _dismissed = true; else _additional.Remove(requestedId);
                Dismissals++;
                return new { taskId = requestedId, dismissed = true };
            }
            var mode = parameters.GetProperty("mode").GetString();
            if (mode is not ("local" or "session" or "worktree" or "cloud"))
                throw new FixtureRequestException("INVALID_REQUEST", "Unknown launch mode.");
            if (RequireRepositoryCorrection && mode == "worktree" && !parameters.TryGetProperty("cwd", out _))
                throw new FixtureRequestException("INVALID_REQUEST", "Synthetic repository correction required.", detailCode: OpenClaw.Shared.GatewayRequestErrors.TaskWorktreeSourceRequired);
            Acceptances.Enqueue(parameters.Clone());
            if (requestedId != TaskId)
            {
                _additional.Remove(requestedId);
                return new { taskId = requestedId, key = JsonSerializer.SerializeToElement(createSession()).GetProperty("key").GetString()! };
            }
            _acceptedKey ??= mode == "session" ? GatewayScenario.MainSessionKey
                : JsonSerializer.SerializeToElement(createSession()).GetProperty("key").GetString()!;
            return new { taskId = TaskId, key = _acceptedKey };
        }
    }
}
