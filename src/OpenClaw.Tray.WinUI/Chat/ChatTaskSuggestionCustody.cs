// Coordinator-lifetime volatile receipts. Bind to a registry Gateway and verified operator before restoration.
namespace OpenClawTray.Chat;

/// <summary>
/// Carries only bounded uncertain receipts and local hides across provider replacement.
/// A null transport does not erase custody. A different registry target or verified operator does.
/// Never a cross-Gateway cache, a disk store, or permission to replay an old client's mutation.
/// </summary>
internal sealed class ChatTaskSuggestionCustody
{
    private readonly object _gate = new();
    private string? _gatewayId;
    private string? _operatorIdentity;
    private ChatTaskCard[] _receipts = [];
    private string[] _hidden = [];

    internal void SelectGateway(string? gatewayId)
    {
        lock (_gate)
        {
            if (gatewayId is null || gatewayId == _gatewayId) return;
            _gatewayId = gatewayId;
            _operatorIdentity = null;
            _receipts = [];
            _hidden = [];
        }
    }

    internal (ChatTaskCard[] Cards, string[] Hidden) Restore(string gatewayId, string identity)
    {
        lock (_gate)
        {
            if (_gatewayId != gatewayId) return ([], []);
            if (_operatorIdentity != identity)
            {
                _operatorIdentity = identity;
                _receipts = [];
                _hidden = [];
            }
            return (_receipts.ToArray(), _hidden.ToArray());
        }
    }

    internal void Retain(string gatewayId, string identity, IEnumerable<ChatTaskCard> cards, IEnumerable<string> hidden)
    {
        lock (_gate)
        {
            if (_gatewayId != gatewayId || _operatorIdentity != identity) return;
            _receipts = cards.Where(card => card.Attempt is not null && card.StartedKey is null)
                .OrderByDescending(card => card.Suggestion.CreatedAt).Take(100)
                .Select(card => card with { Busy = false, Unconfirmed = true, CanRetry = false }).ToArray();
            _hidden = hidden.Take(100).ToArray();
        }
    }

    internal void Clear()
    {
        lock (_gate) { _gatewayId = null; _operatorIdentity = null; _receipts = []; _hidden = []; }
    }
}
