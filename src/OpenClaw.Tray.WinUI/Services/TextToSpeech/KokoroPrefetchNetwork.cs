using Windows.Networking.Connectivity;

namespace OpenClawTray.Services;

/// <summary>
/// Windows connection-cost check for <see cref="KokoroVoicePackPrefetcher"/>. Kept apart
/// from the prefetcher so the prefetcher has no WinRT dependency.
/// </summary>
internal static class KokoroPrefetchNetwork
{
    /// <summary>
    /// Only an unrestricted connection allows the ~380 MB download. Metered, roaming,
    /// over-limit, offline, and unknown-cost connections skip it: Windows reports
    /// <see cref="NetworkCostType.Unknown"/> when it cannot classify the connection,
    /// which may still be a metered one.
    /// </summary>
    public static bool IsMeteredConnection()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null) return true;
        var cost = profile.GetConnectionCost();
        return cost.NetworkCostType != NetworkCostType.Unrestricted
            || cost.Roaming
            || cost.OverDataLimit;
    }
}
