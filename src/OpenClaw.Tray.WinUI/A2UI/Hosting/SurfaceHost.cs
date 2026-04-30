using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.DataModel;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.A2UI.Theming;

namespace OpenClawTray.A2UI.Hosting;

/// <summary>
/// One per active surface. Owns the component definition table, rebuilds the
/// XAML tree from the declared root, and exposes a single root element that
/// the canvas window slots into a content host.
///
/// Lifecycle in v0.8:
///   surfaceUpdate (defs come in)  → ApplyComponents
///   beginRendering (root + style) → BeginRendering, triggers Build
///   dataModelUpdate               → store applies; subscribed renderers refresh
/// A re-issued surfaceUpdate with the same surfaceId patches in place.
/// </summary>
public sealed class SurfaceHost : IDisposable
{
    // Hard caps that keep an adversarial / buggy agent from collapsing the UI thread.
    // Cycle and depth guards above; component count guards a million-node fan-out.
    internal const int MaxRenderDepth = 64;
    internal const int MaxComponentsPerSurface = 2000;

    private readonly DataModelObservable _dataModel;
    private readonly ComponentRendererRegistry _registry;
    private readonly IActionSink _actions;
    private readonly IOpenClawLogger _logger;
    private readonly Dictionary<string, IDisposable> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, A2UIComponentDef> _defs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _renderingIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _secretPaths = new(StringComparer.Ordinal);
    private readonly Grid _root;
    private A2UITheme _theme;
    private string? _rootId;
    private int _renderDepth;
    private int _renderCount;
    private MediaLoadBudget _mediaBudget = new();

    public string SurfaceId { get; }
    public string? Title { get; private set; }
    public FrameworkElement RootElement => _root;

    public SurfaceHost(
        string surfaceId,
        DataModelObservable dataModel,
        ComponentRendererRegistry registry,
        IActionSink actions,
        IOpenClawLogger? logger = null)
    {
        SurfaceId = surfaceId;
        _dataModel = dataModel;
        _registry = registry;
        _actions = actions;
        _logger = logger ?? NullLogger.Instance;
        _theme = A2UITheme.Empty;
        _root = new Grid { Padding = new Thickness(16) };
    }

    /// <summary>
    /// Add or replace components in the definition table. If a root has
    /// already been declared, rebuild the visual tree — but only if the
    /// incoming defs actually change something. A surfaceUpdate that re-sends
    /// already-known components verbatim no-ops; this preserves caret / scroll
    /// / tab selection for agents that re-emit the full surface as their
    /// "update" mechanism. Spec §3.3 calls for full structural diffing
    /// (M1 in unified review); this is a partial down payment that catches
    /// the most common case without per-component XAML element tracking.
    /// </summary>
    public void ApplyComponents(IReadOnlyList<A2UIComponentDef> components)
    {
        bool anyChanged = false;
        foreach (var def in components)
        {
            if (_defs.Count >= MaxComponentsPerSurface && !_defs.ContainsKey(def.Id))
            {
                // Cap the dictionary at the same bound the renderer enforces, so a
                // malicious surfaceUpdate can't fill memory with definitions that
                // never render anyway.
                _logger.Warn($"[A2UI] component cap ({MaxComponentsPerSurface}) on surface '{SurfaceId}'; dropping '{LogSafe(def.Id)}'");
                continue;
            }
            if (!_defs.TryGetValue(def.Id, out var existing) || !ComponentsEqual(existing, def))
            {
                _defs[def.Id] = def;
                anyChanged = true;
            }
        }
        if (anyChanged && _rootId != null) Rebuild();
    }

    private static bool ComponentsEqual(A2UIComponentDef a, A2UIComponentDef b)
    {
        if (!string.Equals(a.ComponentName, b.ComponentName, StringComparison.Ordinal)) return false;
        if (a.Weight != b.Weight) return false;
        // JsonObject equality: serialize and compare. Properties are small
        // (per-component < a few KiB after the M5 size caps) so the cost is
        // negligible compared to the XAML rebuild it might avoid.
        var sa = a.Properties.ToJsonString();
        var sb = b.Properties.ToJsonString();
        return string.Equals(sa, sb, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declare which component is the root and apply surface-level styles.
    /// Triggers an immediate rebuild.
    /// </summary>
    public void BeginRendering(string rootId, System.Text.Json.Nodes.JsonObject? styles)
    {
        _rootId = rootId;
        _theme = A2UITheme.Parse(styles);
        // Surface title is optional and lives in the styles bag in v0.8.
        // Falling back to null lets the window title default to "Canvas".
        if (styles is not null && styles["title"] is System.Text.Json.Nodes.JsonValue tv
            && tv.TryGetValue<string>(out var titleStr) && !string.IsNullOrWhiteSpace(titleStr))
        {
            Title = titleStr;
        }
        else
        {
            Title = null;
        }
        ApplyThemeToScope(_root, _theme);
        Rebuild();
    }

    public void Dispose()
    {
        DisposeSubscriptions();
        _defs.Clear();
        _root.Children.Clear();
    }

    /// <summary>
    /// JSON snapshot of this surface's logical state — components (id +
    /// componentName + properties), declared root, and the current data
    /// model tree. Used by <c>canvas.a2ui.dump</c> for headless verification.
    /// Sensitive paths (obscured fields + denylist matches) are redacted.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject GetSnapshot()
    {
        var components = new System.Text.Json.Nodes.JsonArray();
        foreach (var def in _defs.Values)
        {
            var entry = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = def.Id,
                ["componentName"] = def.ComponentName,
                ["properties"] = def.Properties.DeepClone(),
            };
            if (def.Weight is { } w) entry["weight"] = w;
            components.Add(entry);
        }
        return new System.Text.Json.Nodes.JsonObject
        {
            ["surfaceId"] = SurfaceId,
            ["root"] = _rootId,
            ["components"] = components,
            // CloneRoot snapshots under the model lock so a concurrent SetByPointer
            // can't produce a half-mutated tree mid-clone. RedactInPlace avoids the
            // second DeepClone the public Redact does.
            ["dataModel"] = SecretRedactor.RedactInPlace(_dataModel.CloneRoot(), _secretPaths),
        };
    }

    /// <summary>True if the path was registered as secret (e.g., obscured TextField bound there).</summary>
    internal bool IsSecretPath(string? path) => SecretRedactor.IsSecret(path, _secretPaths);

    private void Rebuild()
    {
        DisposeSubscriptions();
        _root.Children.Clear();
        _renderingIds.Clear();
        _secretPaths.Clear();
        _renderDepth = 0;
        _renderCount = 0;
        _mediaBudget = new MediaLoadBudget();
        if (_rootId == null) return;

        var built = BuildElement(_rootId);
        if (built != null) _root.Children.Add(built);
    }

    private FrameworkElement? BuildElement(string id)
    {
        if (!_defs.TryGetValue(id, out var def))
            return null;

        if (_renderingIds.Contains(id))
        {
            _logger.Warn($"[A2UI] cycle detected on surface '{SurfaceId}' component '{LogSafe(id)}'; rendering placeholder");
            return BuildErrorPlaceholder(def.ComponentName, "cycle detected");
        }
        if (_renderDepth >= MaxRenderDepth)
        {
            _logger.Warn($"[A2UI] depth cap ({MaxRenderDepth}) on surface '{SurfaceId}' at component '{LogSafe(id)}'");
            return BuildErrorPlaceholder(def.ComponentName, $"depth cap ({MaxRenderDepth})");
        }
        if (_renderCount >= MaxComponentsPerSurface)
        {
            _logger.Warn($"[A2UI] component cap ({MaxComponentsPerSurface}) on surface '{SurfaceId}' at component '{LogSafe(id)}'");
            return BuildErrorPlaceholder(def.ComponentName, $"component cap ({MaxComponentsPerSurface})");
        }

        _renderingIds.Add(id);
        _renderDepth++;
        _renderCount++;
        try
        {
            var renderer = _registry.GetOrUnknown(def.ComponentName);
            var ctx = new RenderContext
            {
                SurfaceId = SurfaceId,
                DataModel = _dataModel,
                Actions = _actions,
                Theme = _theme,
                BuildChild = BuildElement,
                Subscriptions = _subscriptions,
                SecretPaths = _secretPaths,
                Logger = _logger,
                MediaBudget = _mediaBudget,
            };

            try { return renderer.Render(def, ctx); }
            catch (Exception ex)
            {
                // Renderer failure should never crash the surface. Don't reroute through
                // the registry — that's how we lose the real component name when fallback
                // also fails. Render an inline placeholder showing actual name + message.
                _logger.Warn($"[A2UI] renderer for '{def.ComponentName}' threw: {ex.Message}");
                return BuildErrorPlaceholder(def.ComponentName, ex.Message);
            }
        }
        finally
        {
            _renderingIds.Remove(id);
            _renderDepth--;
        }
    }

    private static FrameworkElement BuildErrorPlaceholder(string componentName, string message)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            CornerRadius = new CornerRadius(4),
        };
        stack.Children.Add(new FontIcon
        {
            Glyph = "",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{componentName}: {message}",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return stack;
    }

    private static string LogSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var trimmed = s.Length > 64 ? s.Substring(0, 64) : s;
        return trimmed.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    private void DisposeSubscriptions()
    {
        foreach (var s in _subscriptions.Values)
        {
            try { s.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }

    private static void ApplyThemeToScope(FrameworkElement element, A2UITheme theme)
    {
        if (theme == A2UITheme.Empty) return;

        var resources = element.Resources;
        if (theme.Accent is { } accent)
        {
            resources["A2UIAccentBrush"] = new SolidColorBrush(accent);
            resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accent);
        }
        if (theme.Foreground is { } fg)
            resources["A2UIForegroundBrush"] = new SolidColorBrush(fg);
        if (theme.FontFamily is { } font && !string.IsNullOrWhiteSpace(font))
            resources["A2UIFontFamily"] = new FontFamily(font);
    }
}
