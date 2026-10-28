namespace OpenClaw.Shared;

/// <summary>
/// Structured codes for Gateway RPCs that answered <c>ok:false</c>. The client keeps
/// throwing plain <see cref="InvalidOperationException"/> (existing callers rely on the
/// exact type) and records the codes in <see cref="Exception.Data"/>.
/// </summary>
public static class GatewayRequestErrors
{
    private const string CodeKey = "openclaw.gateway.code";
    private const string DetailCodeKey = "openclaw.gateway.detailCode";

    public static InvalidOperationException Create(string message, string? code, string? detailCode)
    {
        var exception = new InvalidOperationException(message);
        if (code is not null) exception.Data[CodeKey] = code;
        if (detailCode is not null) exception.Data[DetailCodeKey] = detailCode;
        return exception;
    }

    /// <summary>Top-level <c>error.code</c>, for example <c>INVALID_REQUEST</c> or <c>FORBIDDEN</c>.</summary>
    public static string? Code(Exception exception) => exception.Data[CodeKey] as string;

    /// <summary>Nested <c>error.details.code</c>, for example <c>CRON_JOB_CHANGED</c>.</summary>
    public static string? DetailCode(Exception exception) => exception.Data[DetailCodeKey] as string;
}
