using System.Collections.Immutable;

namespace OpenClaw.Shared.Dashboards;

/// <summary>
/// Board and dashboard surface of the operator connection. Kept separate from
/// <see cref="IOperatorGatewayClient"/> so dashboard owners depend only on the
/// contract they use and tests can supply a focused fake.
/// </summary>
public interface IBoardGatewayClient
{
    bool IsConnectedToGateway { get; }
    /// <summary>Session routing defaults from the current hello, used to canonicalize progress-card keys.</summary>
    BoardSessionDefaults? SessionDefaults => null;
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    ImmutableArray<PluginWidgetKind> PluginWidgetKinds { get; }
    Uri? GatewayHttpOrigin { get; }
    IReadOnlyList<string> AdvertisedServerMethods { get; }

    event EventHandler<BoardChangedEvent>? BoardChanged;
    event EventHandler<BoardCommandEvent>? BoardCommandReceived;
    event EventHandler<BoardProgressChangedEvent>? ProgressCardChanged { add { } remove { } }
    /// <summary>Raised for <c>sessions.changed</c>; a <c>reset</c> or <c>delete</c> ends that session's progress card.</summary>
    event EventHandler<SessionListChange>? SessionListChanged { add { } remove { } }
    /// <summary>The operator owner's authoritative session publication, not detached gallery responses.</summary>
    event EventHandler<SessionInfo[]>? SessionsUpdated;

    Task<DashboardSessionPage?> ListDashboardSessionsAsync(string? agentId, int offset = 0, int timeoutMs = 15000);
    Task<BoardSnapshot> GetBoardAsync(BoardSessionTarget target, int timeoutMs = 15000);
    Task<BoardProgressCard?> GetProgressCardAsync(ProgressCardTarget target, int timeoutMs = 15000) =>
        Task.FromException<BoardProgressCard?>(new NotSupportedException("Progress cards are unavailable."));
}

/// <summary>Access facts for the dashboards surface, from the hello scopes and methods.</summary>
public static class BoardAccessPolicy
{
    public const string ReadScope = "operator.read";
    public const string WriteScope = "operator.write";
    public const string AdminScope = "operator.admin";

    /// <summary>
    /// Dashboards are readable when the connection holds read access (admin
    /// implies it) and the Gateway did not omit <c>board.get</c> from a
    /// non-empty method advertisement.
    /// </summary>
    public static bool CanRead(IBoardGatewayClient client) =>
        HasScope(client, ReadScope) && Advertised(client, "board.get");

    /// <summary>
    /// Progress cards are newer than boards, so reads require an explicit
    /// <c>progressCard.get</c> advertisement and an explicit read-capable scope.
    /// Older Gateways that omit either show the widget as unavailable, not failed.
    /// </summary>
    public static bool CanReadProgress(IBoardGatewayClient client) =>
        client.GrantedOperatorScopes.Count > 0 && HasScope(client, ReadScope) &&
        client.AdvertisedServerMethods.Contains("progressCard.get", StringComparer.Ordinal);

    private static bool HasScope(IBoardGatewayClient client, string scope)
    {
        var scopes = client.GrantedOperatorScopes;
        if (scopes.Count == 0)
            return true;
        return scopes.Contains(scope, StringComparer.Ordinal) ||
            scopes.Contains(AdminScope, StringComparer.Ordinal) ||
            (scope == ReadScope && scopes.Contains(WriteScope, StringComparer.Ordinal));
    }

    private static bool Advertised(IBoardGatewayClient client, string method) =>
        client.AdvertisedServerMethods.Count == 0 || client.AdvertisedServerMethods.Contains(method, StringComparer.Ordinal);
}
