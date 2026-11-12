using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal sealed class ChatQuestionDraft
{
    internal HashSet<string> Selected { get; } = new(StringComparer.Ordinal);
    internal string FreeText { get; set; } = "";

    internal string[] Answers(GatewayQuestion question)
    {
        var selected = question.Options.Where(option => Selected.Contains(option.Value))
            .Select(option => option.Value).Distinct(StringComparer.Ordinal);
        var text = question.IsSecret || question.IsForm ? FreeText : FreeText.Trim();
        var free = text.Length == 0 ? [] : question.AnswerAsLines
            ? text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
            : new[] { text };
        return selected.Concat(free).ToArray();
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
                || (!question.AllowEmpty && values.Length == 0)
                || values.Any(value => question.IsSecret || question.IsForm
                    ? value.Length == 0 : string.IsNullOrWhiteSpace(value))
                || (!question.MultiSelect && values.Length > 1)
                || (!question.AllowOther && question.Options.Count > 0
                    && values.Any(value => !question.Options.Any(option => option.Value == value))))
                throw new ArgumentException("Choose a valid answer for each question.");
        }
    }
}
