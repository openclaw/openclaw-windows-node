using OpenClaw.Connection;

namespace OpenClawTray.Helpers;

internal static class PersonalDashboardWindowReusePolicy
{
    internal static bool CanReuse(
        PersonalOperatorLease? currentLease,
        PersonalOperatorLease windowLease,
        PersonalOperatorLease requestedLease,
        bool isClosed,
        bool isRetired) =>
        !isClosed && !isRetired &&
        ReferenceEquals(currentLease, requestedLease) &&
        ReferenceEquals(windowLease, requestedLease);
}
