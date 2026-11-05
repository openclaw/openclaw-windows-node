namespace OpenClaw.Shared;

/// <summary>
/// Bounded Gateway error codes attached to the <see cref="InvalidOperationException"/> that a failed
/// request raises. The exception type and message stay unchanged for existing callers; identity and
/// diagnostics read only codes from fixed protocol lists (anything else becomes UNKNOWN), never the
/// message. Exceptions raised locally (connection closed, handshake pending) carry no codes, so origin
/// is never inferred from text.
/// </summary>
public static class GatewayRequestErrors
{
    private static readonly HashSet<string> AllowedCodes = new(StringComparer.Ordinal)
    {
        "INVALID_REQUEST", "UNAVAILABLE", "FORBIDDEN", "UNAUTHORIZED", "NOT_LINKED", "NOT_PAIRED", "AGENT_TIMEOUT",
        Cron.CronProtocol.JobChangedCode, Cron.CronProtocol.JobNotFoundCode,
    };

    private static readonly HashSet<string> AllowedDetailCodes = new(StringComparer.Ordinal)
    {
        "AUTHENTICATED_PROFILE_UNAVAILABLE", "MISSING_SCOPE",
        Cron.CronProtocol.JobChangedCode, Cron.CronProtocol.JobNotFoundCode,
    };

    private const string CodeKey = "openclaw.gateway.error.code";
    private const string DetailCodeKey = "openclaw.gateway.error.detailCode";

    /// <summary>Creates the failure for an ok:false reply, attaching sanitized codes.</summary>
    public static InvalidOperationException Create(string message, string? code, string? detailCode)
    {
        var exception = new InvalidOperationException(message);
        exception.Data[CodeKey] = code is not null && AllowedCodes.Contains(code) ? code : "UNKNOWN";
        if (detailCode is not null && AllowedDetailCodes.Contains(detailCode))
            exception.Data[DetailCodeKey] = detailCode;
        return exception;
    }

    /// <summary>True when the exception came from a Gateway reply; yields its bounded codes (UNKNOWN if outside the allow-list).</summary>
    public static bool TryGetCodes(Exception exception, out string code, out string? detailCode)
    {
        if (exception.Data[CodeKey] is string c)
        {
            code = c;
            detailCode = exception.Data[DetailCodeKey] as string;
            return true;
        }
        code = string.Empty;
        detailCode = null;
        return false;
    }

    /// <summary>Allowlisted top-level <c>error.code</c>; unknown Gateway codes return <c>UNKNOWN</c>.</summary>
    public static string? Code(Exception exception) => exception.Data[CodeKey] as string;

    /// <summary>Allowlisted nested <c>error.details.code</c>.</summary>
    public static string? DetailCode(Exception exception) => exception.Data[DetailCodeKey] as string;
}
