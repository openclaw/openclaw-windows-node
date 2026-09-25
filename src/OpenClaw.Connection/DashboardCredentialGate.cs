namespace OpenClaw.Connection;

/// <summary>
/// Decides whether a dashboard URL may carry a token for one pinned gateway record.
/// Device and bootstrap credentials are not written into the URL. A shared token is
/// appended only when that is the credential resolved for the same pin.
/// </summary>
public static class DashboardCredentialGate
{
    public const string PinMismatchMessage = "Dashboard blocked because of a pin mismatch.";

    public static DashboardCredentialDecision Decide(
        bool pinMatches,
        bool samePinnedRecord,
        bool tunnelAllowsSharedToken,
        string? credentialSource,
        bool isBootstrapToken,
        string? resolvedToken,
        string? pinnedSharedToken)
    {
        if (!pinMatches || !samePinnedRecord)
            return DashboardCredentialDecision.Mismatch;

        var source = string.IsNullOrWhiteSpace(credentialSource) ? "none" : credentialSource;
        var sharedChosen = !isBootstrapToken
            && string.Equals(source, CredentialResolver.SourceSharedGatewayToken, StringComparison.Ordinal);

        if (!sharedChosen)
        {
            return new DashboardCredentialDecision(
                AppendToken: false,
                CredentialSource: source,
                Token: null,
                PinMismatch: false);
        }

        if (!tunnelAllowsSharedToken)
        {
            return new DashboardCredentialDecision(
                AppendToken: false,
                CredentialSource: source,
                Token: null,
                PinMismatch: false);
        }

        if (string.IsNullOrEmpty(resolvedToken)
            || !string.Equals(resolvedToken, pinnedSharedToken, StringComparison.Ordinal))
        {
            return DashboardCredentialDecision.Mismatch;
        }

        return new DashboardCredentialDecision(
            AppendToken: true,
            CredentialSource: source,
            Token: resolvedToken,
            PinMismatch: false);
    }
}

public readonly record struct DashboardCredentialDecision(
    bool AppendToken,
    string CredentialSource,
    string? Token,
    bool PinMismatch)
{
    public static DashboardCredentialDecision Mismatch { get; } = new(
        AppendToken: false,
        CredentialSource: "none",
        Token: null,
        PinMismatch: true);
}
