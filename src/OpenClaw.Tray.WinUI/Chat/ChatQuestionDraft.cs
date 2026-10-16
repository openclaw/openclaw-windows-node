using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed class ChatQuestionDraft
{
    internal HashSet<string> Selected { get; } = new(StringComparer.Ordinal);
    internal string FreeText { get; set; } = "";

    internal string[] Answers(GatewayQuestion question)
    {
        var selected = question.Options.Where(option => Selected.Contains(option.Value))
            .Select(option => option.Value);
        var free = question.AnswerAsLines
            ? FreeText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : string.IsNullOrWhiteSpace(FreeText) ? [] : new[] { question.IsSecret ? FreeText : FreeText.Trim() };
        return selected.Concat(free).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static void Validate(GatewayQuestionRequest request, IReadOnlyDictionary<string, string[]> answers)
    {
        if (answers.Count != request.Questions.Count)
            throw new ArgumentException("Answer every question.");
        foreach (var question in request.Questions)
        {
            if (question.RequiresWebControl)
                throw new ArgumentException("This question requires the web chat's resource or secret-store controls.");
            if (!answers.TryGetValue(question.Id, out var values) || values is null
                || values.Length > 64 || values.Any(value => value is null || value.Length > 4096)
                || (!question.AllowEmpty && (values.Length == 0 || values.All(string.IsNullOrWhiteSpace)))
                || (!question.MultiSelect && !question.AnswerAsLines && values.Length > 1)
                || (!question.AllowOther && question.Options.Count > 0
                    && values.Any(value => !question.Options.Any(option => option.Value == value))))
                throw new ArgumentException("Choose a valid answer for each question.");
        }
    }
}
