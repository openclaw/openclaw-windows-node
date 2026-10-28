using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

public sealed record GatewayQuestionOption(string Label, string Value, string? Description);

public sealed record GatewayQuestion(
    string Id,
    string Header,
    string Text,
    IReadOnlyList<GatewayQuestionOption> Options,
    bool MultiSelect,
    bool AllowOther,
    bool AllowEmpty,
    bool AnswerAsLines,
    bool IsSecret,
    IReadOnlyList<string> DefaultAnswers,
    string? Url,
    bool RequiresWebControl,
    bool HasUnavailableLink = false,
    bool IsForm = false);

public sealed record GatewayQuestionRequest(
    string Id,
    string? SessionKey,
    string? AgentId,
    string? RunId,
    long CreatedAtMs,
    long ExpiresAtMs,
    string Status,
    IReadOnlyList<GatewayQuestion> Questions);

public sealed record GatewayQuestionEvent(
    string Id,
    string Status,
    GatewayQuestionRequest? Request = null);

public sealed record GatewayQuestionList(bool IsSupported, IReadOnlyList<GatewayQuestionRequest> Questions);

public sealed class GatewayQuestionUnavailableException()
    : InvalidOperationException("The question is no longer available.");

public sealed class GatewayQuestionAlreadyTerminalException()
    : InvalidOperationException("The question is already resolved or being resolved.");

public sealed class GatewayQuestionInvalidAnswerException()
    : InvalidOperationException("Choose a valid answer for each question.");

/// <summary>Transient question protocol. Answers never travel through chat.send or transcript storage.</summary>
public static class GatewayQuestions
{
    public static GatewayQuestionRequest ParseRequest(JsonElement value)
    {
        var id = RequiredString(value, "id");
        var status = Status(value);
        var created = Timestamp(value, "createdAtMs");
        var expires = Timestamp(value, "expiresAtMs");
        if (expires < created || !value.TryGetProperty("questions", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is < 1 or > 3)
            throw new JsonException("Invalid question request.");

        var questions = items.EnumerateArray().Select(ParseQuestion).ToArray();
        if (questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != questions.Length)
            throw new JsonException("Duplicate question identity.");
        return new(id, OptionalString(value, "sessionKey"), OptionalString(value, "agentId"),
            OptionalString(value, "runId"), created, expires, status, Array.AsReadOnly(questions));
    }

    public static GatewayQuestionEvent ParseEvent(string eventName, JsonElement value)
    {
        if (eventName == "question.requested")
        {
            var request = ParseRequest(value);
            if (request.Status != "pending")
                throw new JsonException("Requested question is not pending.");
            return new(request.Id, request.Status, request);
        }
        if (eventName != "question.resolved")
            throw new JsonException("Unknown question event.");
        var status = Status(value);
        if (status == "pending")
            throw new JsonException("Resolved question is still pending.");
        return new(RequiredString(value, "id"), status);
    }

    public static string Status(JsonElement value)
    {
        var status = RequiredString(value, "status");
        return status is "pending" or "answered" or "cancelled" or "expired"
            ? status : throw new JsonException("Invalid question status.");
    }

    private static GatewayQuestion ParseQuestion(JsonElement value)
    {
        var id = RequiredString(value, "questionId");
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new JsonException("Invalid question identity.");
        var form = OptionalString(value, "presentation");
        if (form is not (null or "form"))
            throw new JsonException("Unsupported question presentation.");
        if (!value.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array
            || options.GetArrayLength() > 64)
            throw new JsonException("Invalid question options.");
        var choices = options.EnumerateArray().Select(option =>
        {
            var label = RequiredString(option, "label");
            var wireValue = OptionalString(option, "value") ?? label;
            if (wireValue.Length > 4096)
                throw new JsonException("Question option is too long.");
            return new GatewayQuestionOption(label, wireValue, OptionalString(option, "description"));
        }).ToArray();
        var defaults = Array.Empty<string>();
        if (value.TryGetProperty("defaultAnswers", out var answers))
        {
            if (answers.ValueKind != JsonValueKind.Array || answers.GetArrayLength() > 64)
                throw new JsonException("Invalid default answers.");
            defaults = answers.EnumerateArray().Select(answer =>
                answer.ValueKind == JsonValueKind.String && answer.GetString() is { Length: <= 4096 } text
                    ? text : throw new JsonException("Invalid default answer.")).ToArray();
        }
        var url = OptionalString(value, "url");
        var unavailableLink = url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo));
        var answerFormat = OptionalString(value, "answerFormat");
        if (answerFormat is not (null or "lines"))
            throw new JsonException("Unsupported answer format.");
        var secret = Boolean(value, "isSecret");
        return new(id, OptionalString(value, "header") ?? "", RequiredString(value, "question"),
            Array.AsReadOnly(choices), Boolean(value, "multiSelect"), Boolean(value, "isOther"),
            Boolean(value, "allowEmpty"), answerFormat == "lines", secret,
            Array.AsReadOnly(secret ? Array.Empty<string>() : defaults), unavailableLink ? null : url,
            value.TryGetProperty("secretStore", out _) || value.TryGetProperty("resource", out _),
            unavailableLink, IsForm: form == "form");
    }

    private static bool Boolean(JsonElement value, string name, bool fallback = false)
    {
        if (!value.TryGetProperty(name, out var property))
            return fallback;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException("Invalid question flag."),
        };
    }

    private static long Timestamp(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var number) && number is >= 0 and <= 253402300799999
            ? number : throw new JsonException("Invalid question timestamp.");

    private static string RequiredString(JsonElement value, string name) =>
        OptionalString(value, name) is { Length: > 0 } text
            ? text : throw new JsonException("Missing question field.");

    private static string? OptionalString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected question object.");
        if (!value.TryGetProperty(name, out var property))
            return null;
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() : throw new JsonException("Invalid question string.");
    }
}
