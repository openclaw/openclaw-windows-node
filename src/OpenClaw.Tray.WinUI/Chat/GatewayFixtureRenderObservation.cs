using System.Text.Json;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>
/// Passive UIA acknowledgement of the snapshot consumed by a fixture render.
/// Provider/MCP readiness can precede UI delivery; no message content is exposed here.
/// </summary>
internal static class GatewayFixtureRenderObservation
{
    public static string Create(ChatDataSnapshot snapshot, string? selectedThreadId, bool fixtureEnabled)
    {
        if (!fixtureEnabled)
            return string.Empty;
        return JsonSerializer.Serialize(new
        {
            selectedThreadId,
            loadedThreadIds = snapshot.Timelines
                .Where(pair => pair.Value.HistoryLoaded)
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            historyRevisions = snapshot.Timelines
                .Where(pair => pair.Value.HistoryLoaded)
                .ToDictionary(pair => pair.Key, pair =>
                    snapshot.HistoryRevisions?.TryGetValue(pair.Key, out var revision) == true
                        ? (long?)revision : null, StringComparer.Ordinal)
        });
    }
}

internal sealed class GatewayFixturePositioningState
{
    private readonly HashSet<int> _nativeScrolls = [];
    private string? _requestKey;
    private long _historyRevision;
    private string? _completedRequestKey;
    private bool _renderedIdleFrame;

    internal void Begin(string requestKey, long historyRevision)
    {
        _requestKey = requestKey;
        _historyRevision = historyRevision;
        Invalidate();
    }
    internal void ScrollStarted(int correlationId)
    {
        if (correlationId >= 0) _nativeScrolls.Add(correlationId);
        Invalidate();
    }
    internal void ScrollCompleted(int correlationId)
    {
        _nativeScrolls.Remove(correlationId);
        Invalidate();
    }
    internal void ResetNativeScrolls() { _nativeScrolls.Clear(); Invalidate(); }
    private void Invalidate() { _renderedIdleFrame = false; _completedRequestKey = null; }

    internal void RenderedFrame(bool ownerPending, bool nativeIdle)
    {
        if (_requestKey is null || ownerPending || !nativeIdle || _nativeScrolls.Count != 0)
        {
            Invalidate();
            return;
        }
        if (_renderedIdleFrame) _completedRequestKey = _requestKey;
        _renderedIdleFrame = true;
    }
    internal string Receipt() => JsonSerializer.Serialize(new
    {
        requestKey = _requestKey,
        historyRevision = _historyRevision,
        completedRequestKey = _completedRequestKey,
        settled = _requestKey is not null && _requestKey == _completedRequestKey
    });
}
