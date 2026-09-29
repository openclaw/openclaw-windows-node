using System.Collections.Immutable;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;

namespace OpenClawTray.Services;

internal static class HubCommandCatalog
{
    public static ImmutableArray<HubCommand> Build(AppState? state, SettingsManager? settings, string agentId)
    {
        var toggles = settings is null
            ? null
            : new HubCommandToggleState(
                settings.EnableNodeMode,
                settings.NodeCameraEnabled,
                settings.NodeCanvasEnabled,
                settings.NodeScreenEnabled,
                settings.NodeBrowserProxyEnabled);
        var sessions = state?.Sessions?.Select(session => session.Key).ToImmutableArray()
            ?? ImmutableArray<string>.Empty;
        var resources = HubPageRegistry.CommandResourceKeys.ToImmutableDictionary(
            key => key,
            LocalizationHelper.GetString,
            StringComparer.Ordinal);
        return HubPageRegistry.BuildCommands(new HubCommandContext(
            agentId, DiagnosticsGate.IsVisible, toggles, sessions, resources));
    }
}
