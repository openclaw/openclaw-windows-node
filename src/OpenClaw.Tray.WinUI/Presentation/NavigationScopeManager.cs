using Microsoft.Extensions.DependencyInjection;

namespace OpenClawTray.Presentation;

/// <summary>
/// Owns the lifetime of the current page's view model. Each navigation runs inside
/// its own DI scope: navigating away deactivates the previous view model and disposes
/// its scope (which disposes the transient view model), then the new page's view model
/// is resolved from a fresh scope and activated.
/// </summary>
/// <remarks>
/// This is the canonical owner of page view-model activation/deactivation and disposal.
/// It is deliberately WinUI-free so the lifetime rules are unit-tested without a live
/// frame. The WinUI <c>FramePageActivator</c> adapter maps page types to view-model
/// types and drives this manager, then assigns the returned view model as the page's
/// data context. Nothing is started in the constructor.
/// </remarks>
public sealed class NavigationScopeManager : IDisposable
{
    private readonly IServiceProvider _rootProvider;
    private IServiceScope? _currentScope;
    private object? _currentViewModel;
    private bool _navigating;
    private bool _disposed;

    public NavigationScopeManager(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
    }

    /// <summary>The view model active for the current page, or null when none is active.</summary>
    public object? CurrentViewModel => _currentViewModel;

    /// <summary>True after <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Advance navigation to the page whose view model is <paramref name="viewModelType"/>.
    /// The previous view model (if any) is deactivated and its scope disposed first. When
    /// <paramref name="viewModelType"/> is null (page has no registered view model), no new
    /// scope is created and null is returned - this is the no-op path used for pages that
    /// have no registered view model.
    /// </summary>
    /// <remarks>
    /// Navigation is not re-entrant: calling <see cref="Navigate"/> from within a view
    /// model's <see cref="INavigationAware.Activate"/> or <see cref="INavigationAware.Deactivate"/>
    /// throws <see cref="InvalidOperationException"/>, so a nested navigation can never
    /// overwrite or leak the scope the outer call is managing.
    /// </remarks>
    /// <returns>The resolved, activated view model, or null.</returns>
    public object? Navigate(Type? viewModelType, object? parameter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_navigating)
        {
            throw new InvalidOperationException(
                "NavigationScopeManager.Navigate cannot be called re-entrantly " +
                "(for example from a view model's Activate or Deactivate).");
        }

        _navigating = true;
        try
        {
            DeactivateAndDisposeCurrent();

            if (viewModelType is null)
            {
                return null;
            }

            var scope = _rootProvider.CreateScope();
            object? viewModel;
            try
            {
                viewModel = scope.ServiceProvider.GetService(viewModelType);
            }
            catch
            {
                scope.Dispose();
                throw;
            }

            if (viewModel is null)
            {
                // Registered nowhere: don't leak an empty scope.
                scope.Dispose();
                return null;
            }

            _currentScope = scope;
            _currentViewModel = viewModel;
            try
            {
                (viewModel as INavigationAware)?.Activate(parameter);
            }
            catch
            {
                // Activation failed: don't leave a half-activated current or leak the scope.
                _currentViewModel = null;
                _currentScope = null;
                scope.Dispose();
                throw;
            }

            return viewModel;
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// Deactivates and disposes the current view model and its scope, returning the
    /// manager to an empty state without disposing the manager itself. Used when the
    /// owning window closes so a page view model does not outlive its window. Safe to
    /// call when nothing is active. Not re-entrant with <see cref="Navigate"/>.
    /// </summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_navigating)
        {
            throw new InvalidOperationException(
                "NavigationScopeManager.Reset cannot be called re-entrantly from Navigate " +
                "or a view model's Activate/Deactivate.");
        }

        _navigating = true;
        try
        {
            DeactivateAndDisposeCurrent();
        }
        finally
        {
            _navigating = false;
        }
    }

    private void DeactivateAndDisposeCurrent()
    {
        var viewModel = _currentViewModel;
        var scope = _currentScope;
        _currentViewModel = null;
        _currentScope = null;

        try
        {
            (viewModel as INavigationAware)?.Deactivate();
        }
        finally
        {
            // Always dispose the scope (and its transient view model) even if a
            // misbehaving view model throws from Deactivate, so it cannot leak a scope.
            scope?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            DeactivateAndDisposeCurrent();
        }
        // slopwatch-ignore: SW003 Disposal teardown must not throw; the scope is already
        // disposed in DeactivateAndDisposeCurrent's finally, so a misbehaving view model's
        // Deactivate cannot leak a scope or surface from Dispose.
        catch (Exception)
        {
        }
    }
}
