using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

/// <summary>Passive extension of the fixture UIA receipt, never a positioning controller.</summary>
internal sealed class GatewayFixturePositioningObservation : IDisposable
{
    private readonly ItemsView _view;
    private readonly Func<bool> _ownerPending;
    private readonly GatewayFixturePositioningState _state = new();
    private ScrollView? _scrollView;
    private bool _disposed;

    private GatewayFixturePositioningObservation(ItemsView view, Func<bool> ownerPending)
    {
        _view = view;
        _ownerPending = ownerPending;
        CompositionTarget.Rendering += OnRendering;
    }

    internal static GatewayFixturePositioningObservation? Create(ItemsView view, Func<bool> ownerPending) =>
        GatewayFixtureIsolation.IsEnabled ? new(view, ownerPending) : null;

    internal void Begin(string requestKey, long historyRevision)
    {
        _state.Begin(requestKey, historyRevision);
        Publish();
    }

    internal void Attach(ScrollView? scrollView)
    {
        if (ReferenceEquals(scrollView, _scrollView)) return;
        if (_scrollView is not null)
        {
            _scrollView.BringingIntoView -= OnBringingIntoView;
            _scrollView.ScrollCompleted -= OnScrollCompleted;
        }
        _scrollView = scrollView;
        _state.ResetNativeScrolls();
        if (_scrollView is not null)
        {
            _scrollView.BringingIntoView += OnBringingIntoView;
            _scrollView.ScrollCompleted += OnScrollCompleted;
        }
        Publish();
    }

    private void OnBringingIntoView(ScrollView sender, ScrollingBringingIntoViewEventArgs args)
    {
        _state.ScrollStarted(args.CorrelationId);
        Publish();
    }

    private void OnScrollCompleted(ScrollView sender, ScrollingScrollCompletedEventArgs args)
    {
        _state.ScrollCompleted(args.CorrelationId);
        Publish();
    }

    private void OnRendering(object? sender, object args)
    {
        if (_disposed) return;
        _state.RenderedFrame(_ownerPending(), _view.IsLoaded &&
            _scrollView is { IsLoaded: true, State: ScrollingInteractionState.Idle });
        Publish();
    }

    private void Publish() => AutomationProperties.SetItemStatus(_view, _state.Receipt());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= OnRendering;
        Attach(null);
        AutomationProperties.SetItemStatus(_view, string.Empty);
    }
}
