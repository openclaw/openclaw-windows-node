namespace OpenClaw.SetupEngine;

/// <summary>Selects the request timeout for a wizard step.</summary>
public static class WizardTimeouts
{
    /// <summary>Default per-step wizard request timeout.</summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>Extended timeout for steps that wait on external auth.</summary>
    public const int AuthTimeoutMs = 300_000;

    /// <summary>Polling delay for gateway progress/status wizard steps.</summary>
    public static readonly TimeSpan ProgressPollDelay = TimeSpan.FromSeconds(1);

    /// <summary>Total progress/status updates before setup fails as stalled.</summary>
    public const int MaxTotalProgressPolls = 1200;

    /// <summary>Per-step progress/status budget; allow a single long install to consume the total budget.</summary>
    public const int MaxProgressPollsPerStep = MaxTotalProgressPolls;

    private static readonly string[] s_authHints =
    {
        "device", "authorize", "login", "sign in", "oauth",
        "browser", "authenticate", "verification",
    };

    /// <summary>Auth-style steps get <see cref="AuthTimeoutMs"/>; others get <see cref="DefaultTimeoutMs"/>.</summary>
    public static int ForStep(string? title, string? message)
    {
        var text = $"{title} {message}";
        foreach (var hint in s_authHints)
        {
            if (text.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return AuthTimeoutMs;
        }

        return DefaultTimeoutMs;
    }
}
