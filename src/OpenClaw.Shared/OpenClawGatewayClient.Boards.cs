using System.Collections.Immutable;
using System.Text.Json;
using OpenClaw.Shared.Dashboards;

namespace OpenClaw.Shared;

/// <summary>
/// Board (session dashboard) surface. Wire shapes follow upstream
/// <c>packages/gateway-protocol/src/schema/board.ts</c> and
/// <c>src/gateway/server-methods/board.ts</c>; see docs/DASHBOARDS.md.
/// </summary>
public partial class OpenClawGatewayClient : IBoardGatewayClient
{
    private ImmutableArray<PluginWidgetKind> _pluginWidgetKinds = [];

    /// <summary>Raised for <c>board.changed</c>: a board revision changed; refetch it.</summary>
    public event EventHandler<BoardChangedEvent>? BoardChanged;

    /// <summary>Raised for <c>board.command</c> (<c>focus_tab</c>, <c>set_chat_dock</c>).</summary>
    public event EventHandler<BoardCommandEvent>? BoardCommandReceived;

    /// <summary>Trusted core plugin widget kinds from the current hello (<c>controlUiWidgetKinds</c>).</summary>
    public ImmutableArray<PluginWidgetKind> PluginWidgetKinds => _pluginWidgetKinds;

    /// <summary>HTTP(S) origin of the connected Gateway, used for ticketed widget documents.</summary>
    public Uri? GatewayHttpOrigin => BoardWidgetUrlPolicy.TryGetGatewayHttpOrigin(_currentGatewayUrl);

    private void CaptureBoardHelloFacts(JsonElement hello) =>
        ImmutableInterlocked.InterlockedExchange(ref _pluginWidgetKinds, DashboardProtocol.ParsePluginWidgetKinds(hello));

    private void ResetBoardHelloFacts() =>
        ImmutableInterlocked.InterlockedExchange(ref _pluginWidgetKinds, []);

    // ── sessions.list (dashboard gallery) ──

    /// <summary>
    /// Reads one gallery page: sessions with a board, scoped to
    /// <paramref name="agentId"/>. Returns null when the Gateway does not
    /// implement the method or the handshake is pending.
    /// </summary>
    public async Task<DashboardSessionPage?> ListDashboardSessionsAsync(string? agentId, int offset = 0, int timeoutMs = 15000)
    {
        var payload = await TryRequestPayloadAsync(
            "sessions.list",
            DashboardProtocol.BuildSessionListParams(agentId, offset),
            timeoutMs).ConfigureAwait(false);
        if (payload is null)
            return null;
        return DashboardProtocol.ParseSessionPage(payload.Value, ParseDetachedSessionRow);
    }

    private SessionInfo? ParseDetachedSessionRow(JsonElement item)
    {
        var key = GetString(item, "key");
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var session = new SessionInfo { Key = key };
        UpdateSessionMainStatus(session, key, item);
        PopulateSessionFromObject(session, item, authoritativeSessionList: true);
        return session;
    }

    // ── board.* ──

    public async Task<BoardSnapshot> GetBoardAsync(BoardSessionTarget target, int timeoutMs = 15000) =>
        DashboardProtocol.ParseBoardSnapshot(await SendResponseRequestAsync(
            "board.get", DashboardProtocol.BuildBoardParams(target), timeoutMs).ConfigureAwait(false));

    private void HandleBoardEvent(string eventType, JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
            return;
        switch (eventType)
        {
            case "board.changed":
                if (DashboardProtocol.ParseBoardChanged(payload) is { } changed)
                    BoardChanged?.Invoke(this, changed);
                break;
            case "board.command":
                if (DashboardProtocol.ParseBoardCommand(payload) is { } command)
                    BoardCommandReceived?.Invoke(this, command);
                break;
        }
    }
}
