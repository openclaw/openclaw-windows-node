using OpenClaw.Shared.Sessions;

namespace OpenClawTray.Helpers;

internal static class SessionActionPromptLocalizer
{
    public static SessionActionPrompt Localize(SessionActionPrompt prompt)
    {
        var prefix = prompt.Kind switch
        {
            SessionActionKind.Reset => "SessionActionPrompt_Reset",
            SessionActionKind.Compact => "SessionActionPrompt_Compact",
            SessionActionKind.Delete => "SessionActionPrompt_Delete",
            SessionActionKind.Restore => "SessionActionPrompt_Restore",
            _ => null,
        };

        if (prefix is null)
            return prompt;

        return prompt with
        {
            Title = LocalizationHelper.GetString($"{prefix}_Title"),
            Body = LocalizationHelper.Format($"{prefix}_BodyFormat", prompt.SessionName),
            ConfirmLabel = LocalizationHelper.GetString($"{prefix}_ConfirmLabel"),
        };
    }
}
