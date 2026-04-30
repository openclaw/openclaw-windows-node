using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.A2UI.Telemetry;
using OpenClawTray.A2UI.Theming;

namespace OpenClawTray.A2UI.Hosting;

/// <summary>
/// Stateful per-window router. Parses inbound JSONL, dispatches to surface
/// hosts on the UI thread, and exposes events for the window to react to
/// (surface lifecycle).
///
/// Designed for single-window/multiple-surfaces — the spec leaves room for a
/// future multi-window mode but the v1 host stacks surfaces in TabView slots.
/// </summary>
public sealed class A2UIRouter
{
    /// <summary>
    /// Cap on concurrent surfaces. v0.8 deployments stack a small number of
    /// related surfaces per window; this bound keeps an adversarial agent from
    /// driving the host to OOM by creating unique surface IDs in a loop.
    /// </summary>
    internal const int MaxSurfaces = 64;

    private readonly DispatcherQueue _dispatcher;
    private readonly DataModelStore _dataModel;
    private readonly ComponentRendererRegistry _registry;
    private readonly IActionSink _actions;
    private readonly IOpenClawLogger _logger;
    private readonly IA2UITelemetry _telemetry;
    private readonly Dictionary<string, SurfaceHost> _surfaces = new(StringComparer.Ordinal);

    public event EventHandler<SurfaceHost>? SurfaceCreated;
    public event EventHandler<SurfaceHost>? SurfaceRendered;
    public event EventHandler<string>? SurfaceDeleted;

    public A2UIRouter(
        DispatcherQueue dispatcher,
        DataModelStore dataModel,
        ComponentRendererRegistry registry,
        IActionSink actions,
        IOpenClawLogger logger,
        IA2UITelemetry? telemetry = null)
    {
        _dispatcher = dispatcher;
        _dataModel = dataModel;
        _registry = registry;
        _actions = actions;
        _logger = logger;
        _telemetry = telemetry ?? NullA2UITelemetry.Instance;
    }

    /// <summary>
    /// Live view of the surfaces dictionary. Callers should NOT mutate or
    /// enumerate concurrently with router activity; for stable iteration use
    /// <see cref="SnapshotSurfaces"/>.
    /// </summary>
    public IReadOnlyDictionary<string, SurfaceHost> Surfaces => _surfaces;

    /// <summary>Stable snapshot of currently-known surfaces. Safe to enumerate.</summary>
    public IReadOnlyList<KeyValuePair<string, SurfaceHost>> SnapshotSurfaces()
    {
        var copy = new List<KeyValuePair<string, SurfaceHost>>(_surfaces.Count);
        foreach (var kv in _surfaces) copy.Add(kv);
        return copy;
    }

    /// <summary>Push a JSONL blob. Each line is parsed independently.</summary>
    public void Push(string jsonl)
    {
        foreach (var msg in A2UIMessageParser.Parse(jsonl, _logger))
        {
            DispatchOnUI(msg);
        }
    }

    public void ResetAll()
    {
        DispatchToUI(() =>
        {
            foreach (var s in _surfaces.Values)
            {
                try { s.Dispose(); } catch { }
                SurfaceDeleted?.Invoke(this, s.SurfaceId);
            }
            _surfaces.Clear();
            _dataModel.RemoveAll();
            _logger.Info("[A2UI] reset all surfaces");
        });
    }

    private void DispatchOnUI(A2UIMessage msg)
    {
        DispatchToUI(() =>
        {
            try { Apply(msg); }
            catch (Exception ex) { _logger.Error("[A2UI] Router apply failed", ex); }
        });
    }

    private void DispatchToUI(Action action)
    {
        if (_dispatcher.HasThreadAccess) { action(); return; }
        // TryEnqueue returns false when the dispatcher is shutting down (or
        // its queue is at capacity). Silently dropping a router push there
        // would hide the failure from upstream callers that already returned
        // success on the wire — log a warning so we can correlate the dropped
        // surface update with whatever shutdown sequence is underway.
        if (!_dispatcher.TryEnqueue(() => action()))
            _logger.Warn("[A2UI] Router dispatch dropped: dispatcher unavailable (likely shutting down)");
    }

    private void Apply(A2UIMessage msg)
    {
        switch (msg)
        {
            case SurfaceUpdateMessage su:
            {
                var host = GetOrCreateSurface(su.SurfaceId);
                if (host == null) break; // cap reached; logged inside GetOrCreateSurface
                host.ApplyComponents(su.Components);
                _logger.Info($"[A2UI] surfaceUpdate '{LogSafe(su.SurfaceId)}' ({su.Components.Count} component(s))");
                _telemetry.Push(su.SurfaceId, "surfaceUpdate", su.Components.Count);
                break;
            }

            case BeginRenderingMessage br:
            {
                var host = GetOrCreateSurface(br.SurfaceId);
                if (host == null) break;
                host.BeginRendering(br.Root, br.Styles);
                SurfaceRendered?.Invoke(this, host);
                _logger.Info($"[A2UI] beginRendering '{LogSafe(br.SurfaceId)}' root='{LogSafe(br.Root)}' (catalog={LogSafe(br.CatalogId) ?? "default"})");
                _telemetry.Push(br.SurfaceId, "beginRendering", 1);
                break;
            }

            case DataModelUpdateMessage dmu:
            {
                _dataModel.ApplyDataModelUpdate(dmu.SurfaceId, dmu.Path, dmu.Contents);
                _logger.Debug($"[A2UI] dataModelUpdate '{LogSafe(dmu.SurfaceId)}' path='{LogSafe(dmu.Path) ?? "/"}' ({dmu.Contents.Count} entry(ies))");
                _telemetry.Push(dmu.SurfaceId, "dataModelUpdate", dmu.Contents.Count);
                break;
            }

            case DeleteSurfaceMessage ds:
            {
                if (_surfaces.TryGetValue(ds.SurfaceId, out var existing))
                {
                    existing.Dispose();
                    _surfaces.Remove(ds.SurfaceId);
                    _dataModel.Remove(ds.SurfaceId);
                    SurfaceDeleted?.Invoke(this, ds.SurfaceId);
                    _logger.Info($"[A2UI] deleteSurface '{LogSafe(ds.SurfaceId)}'");
                    _telemetry.Push(ds.SurfaceId, "deleteSurface", 1);
                }
                break;
            }

            case UnknownEnvelopeMessage ue:
                _logger.Warn($"[A2UI] Unknown envelope kind '{LogSafe(ue.Kind)}'; skipping");
                break;
        }
    }

    private SurfaceHost? GetOrCreateSurface(string surfaceId)
    {
        if (_surfaces.TryGetValue(surfaceId, out var existing)) return existing;
        if (_surfaces.Count >= MaxSurfaces)
        {
            // Cap reached. The previous "degraded fallback" returned the first
            // existing surface, which corrupted unrelated surface state — a
            // surfaceUpdate aimed at the new id would clobber the components
            // of an entirely different surface. Skip the message instead and
            // let the cap log + telemetry counter signal the misbehavior.
            _logger.Warn($"[A2UI] surface cap ({MaxSurfaces}) reached; dropping push for new surface '{LogSafe(surfaceId)}'");
            return null;
        }

        var observable = _dataModel.GetOrCreate(surfaceId);
        var host = new SurfaceHost(surfaceId, observable, _registry, _actions, _logger);
        _surfaces[surfaceId] = host;
        SurfaceCreated?.Invoke(this, host);
        return host;
    }

    private static string LogSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var trimmed = s.Length > 64 ? s.Substring(0, 64) : s;
        return trimmed.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
