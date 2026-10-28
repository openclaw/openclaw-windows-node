using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    public event EventHandler<GatewayQuestionEvent>? QuestionReceived;
    public event EventHandler? QuestionProtocolError;

    private static Exception? QuestionRequestError(string method, JsonElement root)
    {
        if (method is not ("question.get" or "question.resolve")
            || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;
        return TryGetErrorDetailString(error, "reason") switch
        {
            "QUESTION_NOT_FOUND" => new GatewayQuestionUnavailableException(),
            "QUESTION_ALREADY_TERMINAL" when method == "question.resolve" => new GatewayQuestionAlreadyTerminalException(),
            "QUESTION_INVALID_ANSWER" when method == "question.resolve" => new GatewayQuestionInvalidAnswerException(),
            _ => null,
        };
    }

    public async Task<GatewayQuestionList> ListQuestionsAsync()
    {
        JsonElement result;
        try
        {
            result = await SendWizardRequestAsync("question.list", new { }, 15000).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsUnknownMethodError(ex.Message))
        {
            _logger.Warn("This gateway does not support question.list.");
            return new(false, Array.Empty<GatewayQuestionRequest>());
        }
        if (!result.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid question.list response.");
        return new(true, questions.EnumerateArray().Select(GatewayQuestions.ParseRequest).ToArray());
    }

    public async Task<GatewayQuestionRequest> GetQuestionAsync(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var result = await SendWizardRequestAsync("question.get", new { id }, 15000).ConfigureAwait(false);
        return GatewayQuestions.ParseRequest(result.GetProperty("question"));
    }

    public async Task<string> ResolveQuestionAsync(
        string id,
        IReadOnlyDictionary<string, string[]>? answers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        object parameters = answers is null
            ? new { id, cancel = true }
            : new { id, answers = new { answers } };
        var result = await SendWizardRequestAsync("question.resolve", parameters, 15000).ConfigureAwait(false);
        var status = GatewayQuestions.Status(result);
        if (status != (answers is null ? "cancelled" : "answered"))
            throw new JsonException("Unexpected question.resolve outcome.");
        return status;
    }

    private void HandleQuestionEvent(JsonElement root, string eventName)
    {
        GatewayQuestionEvent questionEvent;
        try
        {
            questionEvent = GatewayQuestions.ParseEvent(eventName, root.GetProperty("payload"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.Warn("Invalid question event. Refresh questions or use the web chat.");
            QuestionProtocolError?.Invoke(this, EventArgs.Empty);
            return;
        }
        QuestionReceived?.Invoke(this, questionEvent);
    }
}
