namespace OpenClawTray.Services;

internal static class PairingApprovalCommand
{
    public static string Build(string? requestId) =>
        string.IsNullOrWhiteSpace(requestId)
            ? "openclaw devices list"
            : $"openclaw devices approve {requestId.Trim()}";
}
