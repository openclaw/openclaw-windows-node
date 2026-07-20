using System;
using System.Linq;

namespace OpenClaw.Shared;

/// <summary>
/// Builds the native exec-approval explanation. The human-readable preview is
/// explicitly labelled as agent-supplied context; the exact command remains
/// visible and host policy remains authoritative.
/// </summary>
internal static class ExecApprovalPromptText
{
    internal static string Build(
        ExecApprovalPromptRequest request,
        bool german,
        string displayName)
    {
        var command = Sanitize(request.TechnicalCommand ?? request.Command, 4_000);
        var preview = Sanitize(request.CommandPreview, 1_200);
        var reason = Sanitize(request.Reason, 400);
        var shell = Sanitize(request.Shell, 80);

        if (german)
        {
            var summary = string.IsNullOrWhiteSpace(preview)
                ? "Otti hat keine verständliche Beschreibung mitgesendet. Wenn du unsicher bist, lehne ab und lass die Anfrage neu formulieren."
                : preview;
            return
                "Otti möchte etwas auf diesem Windows-PC ausführen.\r\n\r\n" +
                "Worum es geht (von Otti beschrieben):\r\n" +
                summary +
                "\r\n\r\n" +
                "Sicherheitsgrenze: Policy und Sandbox bleiben aktiv. Diese Beschreibung ersetzt nicht die technische Prüfung durch den Hub.\r\n\r\n" +
                "Technische Details:\r\n" +
                (string.IsNullOrWhiteSpace(command) ? "(kein Befehl angegeben)" : command) +
                "\r\n" +
                $"Shell: {(string.IsNullOrWhiteSpace(shell) ? "automatisch" : shell)}" +
                "\r\n" +
                $"Policy: {(string.IsNullOrWhiteSpace(reason) ? "Freigabe erforderlich" : reason)}";
        }

        var englishSummary = string.IsNullOrWhiteSpace(preview)
            ? "The agent did not include a plain-language description. Deny if unsure and ask it to retry with a clearer summary."
            : preview;
        return
            $"{displayName} needs approval before a remote agent can run something on this Windows machine.\r\n\r\n" +
            "What this is for (described by the agent):\r\n" +
            englishSummary +
            "\r\n\r\n" +
            "Security boundary: policy and sandbox enforcement remain active. This description does not replace the Hub's technical checks.\r\n\r\n" +
            "Technical details:\r\n" +
            (string.IsNullOrWhiteSpace(command) ? "(no command supplied)" : command) +
            "\r\n" +
            $"Shell: {(string.IsNullOrWhiteSpace(shell) ? "auto" : shell)}" +
            "\r\n" +
            $"Policy: {(string.IsNullOrWhiteSpace(reason) ? "Approval required" : reason)}";
    }

    /// <summary>
    /// Compatibility bridge for installed presenters that only render the
    /// legacy Command property. New presenters use CommandPreview and
    /// TechnicalCommand separately.
    /// </summary>
    internal static string BuildLegacyDisplayCommand(string command, string? preview)
    {
        var safeCommand = Sanitize(command, 4_000);
        var safePreview = Sanitize(preview, 1_200);
        if (string.IsNullOrWhiteSpace(safePreview))
            return safeCommand;

        return safePreview + "\r\n\r\nTechnische Details:\r\n" + safeCommand;
    }

    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var safe = new string(value
            .Where(IsSafeDisplayCharacter)
            .ToArray())
            .Trim();
        return safe.Length <= maxLength ? safe : safe[..(maxLength - 1)] + "…";
    }

    private static bool IsSafeDisplayCharacter(char ch)
    {
        if (ch == '\r' || ch == '\n' || ch == '\t')
            return true;
        return !char.IsControl(ch) && !IsBidirectionalControl(ch);
    }

    private static bool IsBidirectionalControl(char ch) =>
        ch == '\u061C' || ch == '\u200E' || ch == '\u200F' ||
        (ch >= '\u202A' && ch <= '\u202E') ||
        (ch >= '\u2066' && ch <= '\u2069');
}
