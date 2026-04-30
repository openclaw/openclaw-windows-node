using System;

namespace OpenClawTray.A2UI.Telemetry;

/// <summary>
/// Per-spec §11 structured telemetry seam. Renderer code calls these from the
/// hot paths so a downstream telemetry adapter can emit per-event records
/// without scraping flat log lines. Default implementation is a no-op so the
/// renderer ships gateway-less without an extra dependency.
/// </summary>
public interface IA2UITelemetry
{
    /// <summary>An A2UI JSONL push was applied. <paramref name="kind"/> is the envelope discriminator (e.g. "surfaceUpdate", "beginRendering").</summary>
    void Push(string surfaceId, string kind, int messageCount);

    /// <summary>A user-originated action was raised on a surface. Does not carry the action context (which can include PII).</summary>
    void Action(string surfaceId, string actionName, string? sourceComponentId);

    /// <summary>A componentName fell through to <c>UnknownRenderer</c> (catalog drift signal).</summary>
    void UnknownComponent(string surfaceId, string componentName, string componentId);

    /// <summary>Media URL blocked by the resolver allowlist or scheme/size policy. <paramref name="reason"/> is one of "scheme", "host", "size", "decode".</summary>
    void MediaBlocked(string surfaceId, string url, string reason);
}

/// <summary>No-op default. Renderer code calls into this safely when no adapter is wired.</summary>
public sealed class NullA2UITelemetry : IA2UITelemetry
{
    public static readonly NullA2UITelemetry Instance = new();
    private NullA2UITelemetry() { }
    public void Push(string surfaceId, string kind, int messageCount) { }
    public void Action(string surfaceId, string actionName, string? sourceComponentId) { }
    public void UnknownComponent(string surfaceId, string componentName, string componentId) { }
    public void MediaBlocked(string surfaceId, string url, string reason) { }
}
