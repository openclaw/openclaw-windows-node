using System;
using OpenClaw.Shared.ExecApprovals;

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
        var command = Sanitize(request.Command, 4_000);
        var preview = ExecApprovalContextDisplaySanitizer.Sanitize(request.CommandPreview);
        var reason = Sanitize(request.Reason, 400);
        var shell = Sanitize(request.Shell, 80);

        if (german)
        {
            var summary = string.IsNullOrWhiteSpace(preview)
                ? $"{displayName} hat keine verständliche Beschreibung mitgesendet. Wenn du unsicher bist, lehne ab und lass die Anfrage neu formulieren."
                : preview;
            return
                $"{displayName} möchte etwas auf diesem Windows-PC ausführen.\r\n\r\n" +
                "Technische Details:\r\n" +
                (string.IsNullOrWhiteSpace(command) ? "(kein Befehl angegeben)" : command) +
                "\r\n" +
                $"Shell: {(string.IsNullOrWhiteSpace(shell) ? "automatisch" : shell)}" +
                "\r\n" +
                $"Policy: {(string.IsNullOrWhiteSpace(reason) ? "Freigabe erforderlich" : reason)}" +
                "\r\n\r\n" +
                $"Worum es geht (von {displayName} beschrieben):\r\n" +
                summary +
                "\r\n\r\n" +
                "Sicherheitsgrenze: Policy und konfigurierte Sandbox-Regeln bleiben maßgeblich. Diese Beschreibung ersetzt nicht die technische Prüfung durch den Hub.";
        }

        var englishSummary = string.IsNullOrWhiteSpace(preview)
            ? "The agent did not include a plain-language description. Deny if unsure and ask it to retry with a clearer summary."
            : preview;
        return
            $"{displayName} needs approval before a remote agent can run something on this Windows machine.\r\n\r\n" +
            "Technical details:\r\n" +
            (string.IsNullOrWhiteSpace(command) ? "(no command supplied)" : command) +
            "\r\n" +
            $"Shell: {(string.IsNullOrWhiteSpace(shell) ? "auto" : shell)}" +
            "\r\n" +
            $"Policy: {(string.IsNullOrWhiteSpace(reason) ? "Approval required" : reason)}" +
            "\r\n\r\n" +
            "What this is for (described by the agent):\r\n" +
            englishSummary +
            "\r\n\r\n" +
            "Security boundary: policy and configured sandbox controls remain authoritative. This description does not replace the Hub's technical checks.";
    }

    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var safe = ExecApprovalCommandDisplaySanitizer.Sanitize(value).Trim();
        return safe.Length <= maxLength ? safe : safe[..(maxLength - 1)] + "…";
    }
}
