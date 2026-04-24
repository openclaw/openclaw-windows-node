using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// State for the search operation: loading, results, error, or empty.
/// </summary>
public enum SearchState
{
    Idle,
    Loading,
    Results,
    Empty,
    Error
}

/// <summary>
/// A type-safe auto-suggest element with async search, debounce, and custom templates.
/// </summary>
public sealed record AutoSuggestElement<T>(
    T? Selected,
    Action<T?>? OnSelected = null,
    Func<string, CancellationToken, Task<IReadOnlyList<T>>>? Search = null,
    Func<T, string>? DisplayText = null,
    string? Placeholder = null) : Element
{
    /// <summary>Debounce delay in milliseconds before triggering search. Default: 300ms.</summary>
    public int DebounceMs { get; init; } = 300;

    /// <summary>Custom template for rendering suggestion items.</summary>
    public Func<T, Element>? Template { get; init; }

    /// <summary>Error message to display when search fails.</summary>
    public string ErrorMessage { get; init; } = "Search failed. Please try again.";

    /// <summary>Message to display when search returns no results.</summary>
    public string EmptyMessage { get; init; } = "No results found.";
}

/// <summary>
/// DSL factory for AutoSuggest.
/// </summary>
public static class AutoSuggestDsl
{
    /// <summary>
    /// Creates an auto-suggest element with async search.
    /// </summary>
    public static AutoSuggestElement<T> AutoSuggest<T>(
        T? selected,
        Action<T?>? onSelected = null,
        Func<string, CancellationToken, Task<IReadOnlyList<T>>>? search = null,
        Func<T, string>? displayText = null,
        string? placeholder = null,
        int debounceMs = 300,
        Func<T, Element>? template = null) =>
        new(selected, onSelected, search, displayText, placeholder)
        {
            DebounceMs = debounceMs,
            Template = template
        };
}

/// <summary>
/// Manages async search with debounce and cancellation for AutoSuggest.
/// </summary>
public sealed class SearchManager<T> : IDisposable
{
    private CancellationTokenSource? _cts;
    private Timer? _debounceTimer;
    private readonly int _debounceMs;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<T>>> _search;

    public SearchState State { get; private set; } = SearchState.Idle;
    public IReadOnlyList<T> Results { get; private set; } = Array.Empty<T>();
    public string? ErrorText { get; private set; }

    /// <summary>Fired when state changes (for triggering re-renders).</summary>
    public event Action? StateChanged;

    public SearchManager(Func<string, CancellationToken, Task<IReadOnlyList<T>>> search, int debounceMs = 300)
    {
        _search = search;
        _debounceMs = debounceMs;
    }

    /// <summary>
    /// Triggers a debounced search. Cancels any in-flight search.
    /// </summary>
    public void Search(string query)
    {
        // Cancel previous
        _cts?.Cancel();
        _cts?.Dispose();
        _debounceTimer?.Dispose();

        if (string.IsNullOrEmpty(query))
        {
            State = SearchState.Idle;
            Results = Array.Empty<T>();
            StateChanged?.Invoke();
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _debounceTimer = new Timer(async _ =>
        {
            State = SearchState.Loading;
            StateChanged?.Invoke();

            try
            {
                var results = await _search(query, token);
                token.ThrowIfCancellationRequested();

                Results = results;
                State = results.Count > 0 ? SearchState.Results : SearchState.Empty;
            }
            catch (OperationCanceledException)
            {
                // Cancelled — don't update state
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    State = SearchState.Error;
                    ErrorText = ex.Message;
                }
            }

            StateChanged?.Invoke();
        }, null, _debounceMs, Timeout.Infinite);
    }

    /// <summary>
    /// Cancels any in-flight search and resets state.
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _debounceTimer?.Dispose();
        State = SearchState.Idle;
        Results = Array.Empty<T>();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _debounceTimer?.Dispose();
    }
}
