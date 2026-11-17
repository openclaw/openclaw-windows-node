// Projects reported desktop availability without provisioning. Usage: DesktopPresentation.Resolve(row, connected).
namespace OpenClaw.Shared.Systems;

/// <summary>
/// What the desktop area of a machine's details shows. The native app reports availability only;
/// viewing, control and Gateway host desktop setup happen in the web interface.
/// </summary>
public enum DesktopSectionKind
{
    /// <summary>A desktop is advertised and the machine is online.</summary>
    Available,
    /// <summary>A desktop was advertised, but the machine or the Gateway is offline.</summary>
    Offline,
    /// <summary>A desktop is advertised, but the machine is starting, stopping or in error.</summary>
    NotReady,
    /// <summary>The online Gateway host has no desktop; it can be set up in the web interface.</summary>
    HostNotSetUp,
}

public static class DesktopPresentation
{
    /// <summary>Null when the machine has no desktop to report, so the section is not shown.</summary>
    public static DesktopSectionKind? Resolve(SystemRow row, bool connected)
    {
        ArgumentNullException.ThrowIfNull(row);
        var advertised = row.Environment?.Desktop == true || row.Environment?.Worker?.Desktop == true;
        if (!advertised)
        {
            return row.Environment?.Desktop == false && row.Id == SystemsProjection.GatewayId && connected && row.Status == SystemStatus.Online
                ? DesktopSectionKind.HostNotSetUp
                : null;
        }
        if (!connected || row.Status == SystemStatus.Offline)
            return DesktopSectionKind.Offline;
        return row.Status == SystemStatus.Online ? DesktopSectionKind.Available : DesktopSectionKind.NotReady;
    }
}
