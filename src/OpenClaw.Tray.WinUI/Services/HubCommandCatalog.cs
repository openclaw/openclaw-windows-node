using System.Collections.Immutable;
using System.Text.Json;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class HubCommandCatalog
{
    internal static IReadOnlyList<HubSearchItem> BuildSearch(AppState? state, string agentId, bool canAdmin = false)
    {
        var resources = HubPageRegistry.CommandResourceKeys.ToImmutableDictionary(
            key => key, LocalizationHelper.GetString, StringComparer.Ordinal);
        var connected = state?.Status == ConnectionStatus.Connected;
        var supported = HubSearchCatalog.SupportsDashboard(state?.GatewaySelf?.ServerVersion);
        var commands = HubPageRegistry.BuildCommands(new(agentId, DiagnosticsGate.IsVisible, null, [], resources));
        var items = HubSearchCatalog.Navigation(commands, connected, supported, LocalizationHelper.GetString).ToList();
        foreach (var (id, label) in new[]
        {
            ("NodeMode", "PermissionsPage_NodeMode.Text"),
            ("Camera", "PermissionsPage_Cap_Camera_Label"),
            ("Canvas", "PermissionsPage_Cap_Canvas_Label"),
            ("Screen", "PermissionsPage_Cap_Screen_Label"),
            ("BrowserProxy", "PermissionsPage_Cap_Browser_Label")
        })
            items.Add(new($"setting:{id}", HubSearchGroup.Settings, LocalizationHelper.GetString(label),
                LocalizationHelper.GetString("PermissionsPage_Permissions.Text"), "\uE713",
                new(HubSearchTargetKind.Page, "permissions", id),
                $"{id} {LocalizationHelper.GetString(id == "NodeMode" ? "PermissionsPage_WhenOnThisPC.Text" : label.Replace("_Label", "_Description"))}"));
        if (connected)
        {
            items.AddRange(HubSearch.Sessions(state!.Sessions.Select(session => session.Clone()), dashboardSupported: supported));
            if (state.AgentsList is { ValueKind: JsonValueKind.Object } agents &&
                agents.TryGetProperty("agents", out var agentRows) && agentRows.ValueKind == JsonValueKind.Array)
                items.AddRange(HubSearchCatalog.ParseCatalog("agents.list", agents, agentId, supported));
        }
        if (supported)
            foreach (var route in HubSearchCatalog.DashboardRoutes)
            {
                if (route.Admin && !canAdmin) continue;
                items.Add(new($"route:{route.Id}", HubSearchGroup.Dashboard,
                    LocalizationHelper.GetString($"Search_Dashboard_{route.Id}"),
                    LocalizationHelper.GetString("Search_Dashboard"), "\uE774",
                    new(HubSearchTargetKind.Dashboard, route.Path), route.Id, route.Path));
            }
        return items;
    }

}
