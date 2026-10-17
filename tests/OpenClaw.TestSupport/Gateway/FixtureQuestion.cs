using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static OpenClaw.TestSupport.Gateway.InteractiveGatewayRuntime;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>Validated synthetic question records. Only the runtime's gate may mutate them.</summary>
internal sealed class FixtureQuestion(JsonObject record)
{
    public JsonObject Record { get; } = record;
    public string Id => Record["id"]!.GetValue<string>();
    public string? RunId => Record["runId"]?.GetValue<string>();
    public string? SessionKey => Record["sessionKey"]?.GetValue<string>();
    public long ExpiresAt => Record["expiresAtMs"]!.GetValue<long>();
    public bool IsPending => Record["status"]!.GetValue<string>() == "pending";
    public TaskCompletionSource<string> Resolved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _resolutionId;

    public static FixtureQuestion CreateBuiltin(string id, string session, string run, string scenario, TimeSpan timeout)
    {
        var text = scenario == "question-text";
        return FromRequest(JsonSerializer.SerializeToElement(new
        {
            id, sessionKey = session, runId = run,
            questions = new[] { new
            {
                questionId = "fixture_choice", header = "Fixture", question = text ? "What should the synthetic agent call this task?" : "Which synthetic task should the agent continue?",
                options = text ? Array.Empty<object>() : new object[] { new { label = "Inspect" }, new { label = "Summarize" } },
                multiSelect = scenario == "question-multi", isOther = text
            } }
        }), id, timeout);
    }

    public static FixtureQuestion FromRequest(JsonElement p, string generatedId, TimeSpan timeout)
    {
        GatewayScenario.ValidateProperties(p, "id", "questions", "agentId", "sessionKey", "runId", "timeoutMs");
        var id = Optional(p, "id") ?? generatedId;
        if (!id.StartsWith("fixture-", StringComparison.Ordinal) || id.Length > 64)
            throw Invalid("Question IDs must be fixture-owned and at most 64 characters.");
        if (!p.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() is < 1 or > 3)
            throw Invalid("Provide one to three questions.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions.EnumerateArray())
        {
            GatewayScenario.ValidateProperties(question, "questionId", "header", "question", "options", "multiSelect", "isOther", "allowEmpty", "defaultAnswers", "answerFormat", "presentation");
            var questionId = Required(question, "questionId");
            if (!Regex.IsMatch(questionId, "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant) || !ids.Add(questionId))
                throw Invalid("Question IDs must be unique lowercase identifiers.");
            if (Required(question, "header").Length > 12 || Required(question, "question").Length > 4096)
                throw Invalid("Question header or text is too long.");
            foreach (var flag in new[] { "multiSelect", "isOther", "allowEmpty" }) Boolean(question, flag);
            if (Optional(question, "answerFormat") is { } format && format != "lines") throw Invalid("Unsupported answerFormat.");
            if (Optional(question, "presentation") is { } presentation && presentation != "form") throw Invalid("Unsupported presentation.");
            if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() > 64)
                throw Invalid("options must be an array with at most 64 choices.");
            var labels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options.EnumerateArray())
            {
                GatewayScenario.ValidateProperties(option, "label", "description", "value");
                if (!labels.Add(Required(option, "label"))) throw Invalid("Option labels must be unique.");
                if (Optional(option, "value")?.Length > 4096) throw Invalid("Option value is too long.");
                Optional(option, "description");
            }
            if (question.TryGetProperty("defaultAnswers", out var defaults)) ValidateAnswers(question, defaults);
        }
        var ms = Positive(p, "timeoutMs", (int)timeout.TotalMilliseconds);
        if (ms > 300_000) throw Invalid("Fixture question timeout must not exceed five minutes.");
        var row = Obj(new { id, questions, createdAtMs = Now, expiresAtMs = Now + ms, status = "pending" });
        foreach (var field in new[] { "agentId", "sessionKey", "runId" })
            if (Optional(p, field) is { } value) row[field] = value;
        return new FixtureQuestion(row);
    }

    public object Resolve(JsonElement p)
    {
        var cancel = Boolean(p, "cancel");
        var hasAnswers = p.TryGetProperty("answers", out var answers);
        if (cancel == false || (cancel == true) == hasAnswers) throw Invalid("Supply either answers or cancel:true.");
        var resolutionId = Optional(p, "resolutionId");
        if (resolutionId?.Length > 128) throw Invalid("resolutionId is too long.");
        var resolvedBy = Optional(p, "resolvedBy");
        if (cancel == true && resolutionId is not null) throw Invalid("Cancellation does not accept resolutionId.");
        if (hasAnswers)
        {
            GatewayScenario.ValidateProperties(answers, "answers");
            if (!answers.TryGetProperty("answers", out var values) || values.ValueKind != JsonValueKind.Object)
                throw Invalid("answers must contain an answers object keyed by questionId.");
            var questions = Record["questions"]!.AsArray();
            if (values.EnumerateObject().Count() != questions.Count) throw Invalid("Answer every question exactly once.");
            foreach (var question in questions)
            {
                var q = JsonSerializer.SerializeToElement(question);
                if (!values.TryGetProperty(Required(q, "questionId"), out var selections)) throw Invalid("Missing question answer.");
                ValidateAnswers(q, selections);
            }
        }
        if (!IsPending)
        {
            if (hasAnswers && Record["status"]!.GetValue<string>() == "answered" && resolutionId is not null && resolutionId == _resolutionId
                && JsonNode.DeepEquals(Record["answers"], JsonNode.Parse(answers.GetRawText())))
                return Result(includeId: false);
            throw Invalid("Question is no longer pending.");
        }
        if (hasAnswers) Record["answers"] = JsonNode.Parse(answers.GetRawText());
        if (resolvedBy is not null) Record["resolvedBy"] = resolvedBy;
        _resolutionId = resolutionId;
        End(hasAnswers ? "answered" : "cancelled");
        return Result(includeId: false);
    }

    private static void ValidateAnswers(JsonElement question, JsonElement selections)
    {
        if (selections.ValueKind != JsonValueKind.Array || selections.GetArrayLength() > 64) throw Invalid("Each answer must be a bounded array.");
        var options = question.GetProperty("options").EnumerateArray().Select(o => Optional(o, "value") ?? Required(o, "label")).ToHashSet(StringComparer.Ordinal);
        var allowOther = Boolean(question, "isOther") == true || options.Count == 0;
        if (selections.GetArrayLength() == 0 && Boolean(question, "allowEmpty") != true) throw Invalid("An answer is required.");
        if (selections.GetArrayLength() > 1 && Boolean(question, "multiSelect") != true && Optional(question, "answerFormat") != "lines")
            throw Invalid("This question permits a single answer.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections.EnumerateArray())
        {
            if (selection.ValueKind != JsonValueKind.String) throw Invalid("Answers must be strings.");
            var value = selection.GetString()!;
            if (value.Length > 4096 || string.IsNullOrWhiteSpace(value) || !seen.Add(value) || (!allowOther && !options.Contains(value)))
                throw Invalid("Invalid, duplicate, or unknown question answer.");
        }
    }

    public void End(string status)
    {
        if (!IsPending) return;
        Record["status"] = status;
        Resolved.TrySetResult(status);
    }

    public JsonObject Result(bool includeId = true, bool includeResolutionId = false)
    {
        var result = new JsonObject { ["status"] = Record["status"]!.DeepClone() };
        if (includeId) result["id"] = Id;
        if (Record["answers"] is { } answers) result["answers"] = answers.DeepClone();
        if (includeResolutionId && _resolutionId is not null) result["resolutionId"] = _resolutionId;
        return result;
    }
}
