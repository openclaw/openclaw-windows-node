using System.Text;
using OpenClaw.Shared;

namespace OpenClawTray.Services.VoiceAssistant;

public static class VoiceWakeGate
{
    public static bool TryExtractRequest(string? transcript, string? wakePhrase, out string request)
    {
        request = string.Empty;
        if (!VoiceAssistantSettingsPolicy.TryNormalizeWakePhrase(wakePhrase, out var normalizedWakePhrase) ||
            string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var wakeTokens = Tokenize(normalizedWakePhrase);
        var transcriptTokens = Tokenize(transcript);
        if (transcriptTokens.Count <= wakeTokens.Count)
            return false;

        for (var index = 0; index < wakeTokens.Count; index++)
        {
            if (!string.Equals(
                    transcriptTokens[index].Value,
                    wakeTokens[index].Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var requestStart = transcriptTokens[wakeTokens.Count - 1].End;
        while (requestStart < transcript.Length && !char.IsLetterOrDigit(transcript[requestStart]))
            requestStart++;

        if (requestStart >= transcript.Length)
            return false;

        request = CapitalizeFirstCharacter(transcript[requestStart..].Trim());
        return request.Any(char.IsLetterOrDigit);
    }

    private static string CapitalizeFirstCharacter(string value)
    {
        if (value.Length == 0)
            return value;

        var uppercase = char.ToUpperInvariant(value[0]);
        return uppercase == value[0]
            ? value
            : uppercase + value[1..];
    }

    private static List<Token> Tokenize(string value)
    {
        var tokens = new List<Token>();
        var index = 0;

        while (index < value.Length)
        {
            while (index < value.Length && !IsTokenCharacter(value[index]))
                index++;

            if (index >= value.Length)
                break;

            var start = index;
            while (index < value.Length && IsTokenCharacter(value[index]))
                index++;

            var normalized = value[start..index].Normalize(NormalizationForm.FormKC);
            tokens.Add(new Token(normalized, index));
        }

        return tokens;
    }

    private static bool IsTokenCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '\'' or '\u2019' or '-';

    private readonly record struct Token(string Value, int End);
}
