namespace OpenClawTray.Presentation;

/// <summary>
/// UI-thread marshaling abstraction. Presentation-layer view models and services
/// depend on this instead of the concrete <c>Microsoft.UI.Dispatching.DispatcherQueue</c>
/// so they stay unit-testable without a live WinUI dispatcher.
/// </summary>
/// <remarks>
/// This is the canonical seam for "post work back to the UI thread". The WinUI
/// adapter (<c>Presentation/Adapters/WinUIDispatcher</c>) wraps the real dispatcher
/// queue; App and legacy WinUI code may still call the dispatcher directly until
/// the view-model migration retires those call sites.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already running on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>
    /// Queues <paramref name="action"/> to run on the UI thread. Returns false when
    /// the underlying queue could not accept the work (for example during shutdown).
    /// </summary>
    bool TryEnqueue(Action action);
}
