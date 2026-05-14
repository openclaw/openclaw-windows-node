namespace OpenClawTray.Onboarding.V2;

/// <summary>
/// Logical pages in the V2 onboarding flow. Index order is the canonical
/// visit order for the local-setup happy path; the Advanced setup link on
/// <see cref="Welcome"/> is the only branch out of this enum (handled by
/// the host).
/// </summary>
public enum V2Route
{
    Welcome = 0,
    LocalSetupProgress = 1,
    GatewayWelcome = 2,
    Permissions = 3,
    AllSet = 4,
}
