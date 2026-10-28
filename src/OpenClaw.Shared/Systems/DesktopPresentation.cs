namespace OpenClaw.Shared.Systems;

/// <summary>What the desktop area of a machine's details shows.</summary>
public enum DesktopSectionKind
{
    /// <summary>Gateway host desktop setup guidance and the enable action.</summary>
    HostSetup,
    /// <summary>A desktop is advertised. Viewing/control are deferred in the native app.</summary>
    Available,
    Offline,
    /// <summary>Starting, stopping, error or unknown runtime state.</summary>
    NotReady,
    NoDesktop,
    NoHostDesktop,
}

public enum DesktopTitle
{
    Enabled,
    ManagedConfigured,
    ScreenSharingDetected,
    DesktopDetected,
    ScreenSharingNeeded,
    DesktopServerNeeded,
    Attention,
    Available,
    Offline,
    NotReady,
    NoDesktop,
}

public enum DesktopHint
{
    Connecting,
    EnableAccess,
    ScreenSharingSetup,
    DesktopServerSetup,
    ViewingDeferred,
    Offline,
    NoDesktop,
    NoHostDesktop,
}

/// <summary>
/// Desktop area content. <see cref="ShowEnable"/> offers "Enable desktop access"
/// (always behind a confirmation); <see cref="CanEnable"/> is false for operators
/// without administrator access, who are pointed to an administrator instead.
/// </summary>
public sealed record DesktopSection(
    DesktopSectionKind Kind,
    DesktopTitle Title,
    DesktopHint Hint,
    bool ShowEnable,
    bool CanEnable,
    bool ShowCheckAgain,
    string? Detail);

public static class DesktopPresentation
{
    public static DesktopSection Resolve(SystemRow row, bool connected, bool hostDesktopEnabled, bool canAdmin)
    {
        ArgumentNullException.ThrowIfNull(row);
        var environment = row.Environment;
        var isHost = row.Id == SystemsProjection.GatewayId;
        var setup = environment?.DesktopSetup;

        if (!connected || row.Status == SystemStatus.Offline)
            return Simple(DesktopSectionKind.Offline, DesktopTitle.Offline, DesktopHint.Offline);
        if (row.Status != SystemStatus.Online)
            return Simple(DesktopSectionKind.NotReady, DesktopTitle.NotReady, DesktopHint.Offline);
        if (environment?.Desktop == true || environment?.Worker?.Desktop == true)
            return Simple(DesktopSectionKind.Available, DesktopTitle.Available, DesktopHint.ViewingDeferred);

        if (isHost && (setup is not null || hostDesktopEnabled))
        {
            var ready = setup?.State is DesktopSetupState.Ready or DesktopSetupState.Managed;
            var mac = PlatformLabels.IsMac(row.Platform);
            var title = hostDesktopEnabled ? DesktopTitle.Enabled : setup?.State switch
            {
                DesktopSetupState.Managed => DesktopTitle.ManagedConfigured,
                DesktopSetupState.Ready => mac ? DesktopTitle.ScreenSharingDetected : DesktopTitle.DesktopDetected,
                DesktopSetupState.NeedsServer => mac ? DesktopTitle.ScreenSharingNeeded : DesktopTitle.DesktopServerNeeded,
                _ => DesktopTitle.Attention,
            };
            var hint = hostDesktopEnabled ? DesktopHint.Connecting
                : ready ? DesktopHint.EnableAccess
                : mac && setup?.State == DesktopSetupState.NeedsServer ? DesktopHint.ScreenSharingSetup
                : DesktopHint.DesktopServerSetup;
            return new DesktopSection(
                DesktopSectionKind.HostSetup, title, hint,
                ShowEnable: !hostDesktopEnabled && ready,
                CanEnable: canAdmin,
                ShowCheckAgain: !hostDesktopEnabled && !ready,
                Detail: setup?.State == DesktopSetupState.Unsupported ? setup.Detail : null);
        }

        return isHost
            ? Simple(DesktopSectionKind.NoHostDesktop, DesktopTitle.NoDesktop, DesktopHint.NoHostDesktop)
            : Simple(DesktopSectionKind.NoDesktop, DesktopTitle.NoDesktop, DesktopHint.NoDesktop);
    }

    private static DesktopSection Simple(DesktopSectionKind kind, DesktopTitle title, DesktopHint hint) =>
        new(kind, title, hint, ShowEnable: false, CanEnable: false, ShowCheckAgain: false, Detail: null);

    /// <summary>The <c>config.patch</c> body that turns on the Gateway host desktop.</summary>
    public const string EnableHostDesktopPatch = """{"desktop":{"host":{"enabled":true}}}""";
}
