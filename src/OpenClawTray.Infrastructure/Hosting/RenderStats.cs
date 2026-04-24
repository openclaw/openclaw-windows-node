namespace OpenClawTray.Infrastructure.Hosting;

/// <summary>
/// Snapshot of render loop performance, updated every ~1 second by <see cref="ReactorHost"/>.
/// Always available: FPS, frame timing, render count.
/// DEBUG builds add per-reconcile element counters from the last render pass.
///
/// Threading contract: this struct is written field-by-field on the UI thread and exposed
/// via <c>ref readonly</c>. Readers on other threads may observe torn values. Always read
/// on the UI thread or copy the entire struct atomically via assignment.
/// </summary>
public readonly struct RenderStats
{
    /// <summary>Renders per second over the last measurement window.</summary>
    public double Fps { get; init; }

    /// <summary>Number of renders in the last measurement window (~1 second).</summary>
    public int RendersInWindow { get; init; }

    /// <summary>Total renders since the ReactorHost was created.</summary>
    public long TotalRenders { get; init; }

    /// <summary>Average tree-build time (ms) over the last window.</summary>
    public double AvgTreeBuildMs { get; init; }

    /// <summary>Average reconcile time (ms) over the last window.</summary>
    public double AvgReconcileMs { get; init; }

    /// <summary>Average effects flush time (ms) over the last window.</summary>
    public double AvgEffectsMs { get; init; }

    /// <summary>Average total frame time (ms) over the last window (tree + reconcile + effects).</summary>
    public double AvgTotalMs { get; init; }

    /// <summary>Elements diffed in the last reconcile pass.</summary>
    public int LastDiffed { get; init; }

    /// <summary>Elements skipped (memo/ShallowEquals) in the last reconcile pass.</summary>
    public int LastSkipped { get; init; }

    /// <summary>New UIElements created (mounted) in the last reconcile pass.</summary>
    public int LastCreated { get; init; }

    /// <summary>UIElements modified (property updates) in the last reconcile pass.</summary>
    public int LastModified { get; init; }
}
