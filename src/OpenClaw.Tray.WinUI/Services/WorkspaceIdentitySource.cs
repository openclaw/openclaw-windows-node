using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// UI-thread-owned display identity for one operator connection. Never an authorization source.
/// </summary>
internal sealed class WorkspaceIdentitySource(Action changed, Action<string> reportUnavailable)
{
    private IOperatorGatewayClient? _client;
    private int _generation;
    private Task? _pending;
    private bool _refreshAgain;
    private bool _unavailable;

    public string? DisplayName { get; private set; }

    public bool SetConnection(IOperatorGatewayClient? client, ConnectionStatus status)
    {
        var connected = status == ConnectionStatus.Connected && client?.IsConnectedToGateway == true;
        var next = connected ? client : null;
        if (ReferenceEquals(next, _client))
            return false;

        _generation++;
        _client = next;
        _pending = null;
        _refreshAgain = false;
        _unavailable = false;
        DisplayName = null;
        changed();
        return true;
    }

    public Task RefreshAsync(bool invalidate = false)
    {
        if (invalidate) _unavailable = false;
        if (_unavailable || _client is not { IsConnectedToGateway: true } client ||
            !client.GrantedOperatorScopes.Any(scope => scope is
                "operator.admin" or "operator.read" or "operator.write" or
                "operator.sessions.read" or "operator.sessions.write"))
            return Task.CompletedTask;

        if (_pending is { IsCompleted: false })
        {
            _refreshAgain = true;
            return _pending;
        }

        return _pending = LoadAsync(client, _generation);
    }

    private async Task LoadAsync(IOperatorGatewayClient client, int generation)
    {
        do
        {
            _refreshAgain = false;
            try
            {
                var response = await client.SendWizardRequestAsync("users.self", new { }, timeoutMs: 12000);
                if (!IsCurrent()) return;
                if (_refreshAgain) continue;
                DisplayName = ReadDisplayName(response);
                changed();
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
                OperationCanceledException or JsonException)
            {
                if (!IsCurrent()) return;
                if (_refreshAgain) continue;
                DisplayName = null;
                _unavailable = ex is InvalidOperationException or JsonException;
                // Do not log profile payloads, names, emails, or gateway error messages.
                reportUnavailable(ex.GetType().Name);
                changed();
                return;
            }
        } while (IsCurrent() && _refreshAgain);

        bool IsCurrent() => generation == _generation &&
            ReferenceEquals(client, _client) && client.IsConnectedToGateway;
    }

    internal static string? ReadDisplayName(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("profile", out var profile) ||
            profile.ValueKind != JsonValueKind.Object ||
            !profile.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            throw new JsonException("users.self did not return a profile.");

        if (profile.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String)
            return name.GetString();
        if (profile.TryGetProperty("emails", out var emails) && emails.ValueKind == JsonValueKind.Array &&
            emails.GetArrayLength() > 0 && emails[0].ValueKind == JsonValueKind.String)
            return emails[0].GetString();
        return null;
    }
}
