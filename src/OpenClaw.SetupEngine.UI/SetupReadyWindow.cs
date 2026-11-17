using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.SetupEngine.UI.Pages;

namespace OpenClaw.SetupEngine.UI;

/// <summary>Presentation only after publication. No setup lock, staged owner or finalization entry.</summary>
public sealed class SetupReadyWindow : Window
{
    private readonly SetupShellLayout _shell = new();
    private readonly Frame _frame;
    private readonly SetupWindowSizingController _windowSizing;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<CancellationToken, Task<SetupVerifiedNativeRoute>> _verify;
    private readonly Func<SetupNativeCompletion, CancellationToken, Task> _navigate;
    private readonly Func<SetupNativeReadyBinding, IDisposable> _observe;
    private IDisposable? _observation;
    private readonly Action _requireLiveModel;
    private readonly Action _returnToConnection;
    private readonly Func<FrameworkElement, Task>? _collectLogs;
    private readonly Func<string, OpenClaw.Connection.GatewayRecord?>? _findGateway;
    private SetupReadyGateway _gateway = SetupReadyGateway.Unknown;
    private SetupReadyCoordinator? _choice;
    private Task _recovery = Task.CompletedTask;
    private Task<SetupVerifiedNativeRoute>? _verification;
    private Task _mounting = Task.CompletedTask;
    private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SetupReadyPresentation _presentation = new();
    private Task _choiceCleanup = Task.CompletedTask;
    private readonly SetupLoadingProgress _loading = new();
    private SetupLoadingProgress.Scope? _loadingScope;
    public IProgress<SetupLoadingStep>? Progress => _loadingScope;
    public bool IsClosed { get; private set; }
    public bool IsReady => !IsClosed && _presentation.IsReady;
    public Task CleanupCompleted => _cleanup.Task;

    public SetupReadyWindow(Func<CancellationToken, Task<SetupVerifiedNativeRoute>> verify,
        Func<SetupNativeCompletion, CancellationToken, Task> navigate,
        Func<SetupNativeReadyBinding, IDisposable> observe, Action requireLiveModel, Action returnToConnection,
        Func<string, OpenClaw.Connection.GatewayRecord?>? findGateway = null,
        Func<FrameworkElement, Task>? collectLogs = null)
    {
        (_verify, _navigate, _observe, _requireLiveModel) = (verify, navigate, observe, requireLiveModel);
        _returnToConnection = returnToConnection;
        _findGateway = findGateway;
        _collectLogs = collectLogs;
        _frame = _shell.Frame;
        _frame.Navigating += Frame_Navigating;
        Content = _shell;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        Title = SetupLocalization.GetString("Onboarding_Finishing_Heading.Text");
        _windowSizing = new(this, _frame);
        ShowPreparing(SetupLoadingStep.StartCompanion);
        Closed += async (_, _) =>
        {
            IsClosed = true;
            _loading.Dispose();
            _lifetime.Cancel();
            ReleaseChoice();
            try
            {
                await Task.WhenAll(_recovery, (Task?)_verification ?? Task.CompletedTask, _mounting, _choiceCleanup);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("Ready presentation cleanup finished ({0}).", error.GetType().Name);
            }
            finally { _lifetime.Dispose(); _cleanup.TrySetResult(); }
        };
    }

    // The ready window shows the last two steps of the route that reached the verified Gateway.
    private void Frame_Navigating(object sender, Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
    {
        if (SetupCompanionPresentation.For(e.SourcePageType) is { } presentation)
            _shell.Companion.Present(SetupLocalization.GetString(presentation.LineResourceKey), presentation.Mood);
        _shell.Companion.UpdateSteps(OnboardingFlowPolicy.GetStages(_gateway.Route, new SetupConfig()),
            e.SourcePageType == typeof(AiReadyPage) ? OnboardingStage.Ready : OnboardingStage.AiSetup);
    }

    // A missing or unreadable record falls back to a neutral description, never a wrong claim.
    private SetupReadyGateway DescribeGateway(string gatewayId)
    {
        try { return SetupReadyGateway.From(_findGateway?.Invoke(gatewayId)); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Ready Gateway description unavailable ({0}).", error.GetType().Name);
            return SetupReadyGateway.Unknown;
        }
    }

    public Task<SetupVerifiedNativeRoute> VerifyAsync(CancellationToken ct)
    {
        if (_verification is { IsCompleted: false })
            throw new InvalidOperationException("Readiness verification is already running.");
        return _verification = VerifyCoreAsync(ct);
    }

    private async Task<SetupVerifiedNativeRoute> VerifyCoreAsync(CancellationToken ct)
    {
        _loadingScope = _loading.Begin(SetupLoadingGroup.Finishing, SetupLoadingStep.ConnectGateway);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        return await _verify(linked.Token);
    }

    public void ShowPreparing(SetupLoadingStep step = SetupLoadingStep.ConnectGateway)
    {
        ReleaseChoice();
        _presentation.Fail();
        _loadingScope = _loading.Begin(SetupLoadingGroup.Finishing, step);
        _frame.Navigate(typeof(AiCompletionPage));
        if (_frame.Content is AiCompletionPage page) page.BindLoading(_loading);
    }

    public Task ShowReadyAsync(SetupVerifiedNativeRoute route, CancellationToken ct)
    {
        if (!_mounting.IsCompleted) throw new InvalidOperationException("Ready presentation is already mounting.");
        return _mounting = MountWithCleanupAsync(route, ct);
    }

    private async Task MountWithCleanupAsync(SetupVerifiedNativeRoute route, CancellationToken ct)
    {
        try { await ShowReadyCoreAsync(route, ct); }
        catch
        {
            _presentation.Fail();
            ReleaseChoice();
            ShowFailure();
            throw;
        }
    }

    private async Task ShowReadyCoreAsync(SetupVerifiedNativeRoute route, CancellationToken caller)
    {
        using var deadline = new CancellationTokenSource(SetupNativeCompletionTiming.Navigation);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token, deadline.Token);
        var ct = lifetime.Token;
        ct.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        var generation = _presentation.BeginMount();
        var binding = route.ReadyBinding ?? throw new InvalidOperationException("Current runtime readiness is unavailable.");
        var choice = new SetupReadyCoordinator(binding, async (completion, token) =>
        {
            await _navigate(completion, token);
            // Do not close/dispose the chooser while its own selection still owns the task.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!IsClosed && choiceCompleted()) Close();
            });
        }, _requireLiveModel);
        bool choiceCompleted() => _choice?.IsCompleted == true;
        _choice = choice;
        _observation = await binding.ObserveAndCheckAsync(_observe, ct, _loadingScope);
        ct.ThrowIfCancellationRequested();
        _requireLiveModel();
        _gateway = DescribeGateway(route.Verification.GatewayId);
        _frame.Navigate(typeof(AiReadyPage), new AiReadyPageArgs(choice, Recover,
            () => !IsClosed && ReferenceEquals(_choice, choice), Gateway: _gateway));
        if (_frame.Content is not FrameworkElement page)
            throw new InvalidOperationException("The Ready page is unavailable.");
        if (!page.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
            page.Loaded += OnLoaded;
            try { await loaded.Task.WaitAsync(ct); }
            finally { page.Loaded -= OnLoaded; }
        }
        ct.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_choice, choice)) throw new SetupNativeReadinessExpiredException();
        binding.RequireCurrent();
        _requireLiveModel();
        if (!_presentation.CompleteMount(generation)) throw new SetupNativeReadinessExpiredException();
        if (_presentation.IsReady && page is AiReadyPage ready) ready.AdmitChatAction();
        Activate();
    }

    public void Invalidate()
    {
        if (IsClosed || _choice is null) return;
        ShowFailure();
    }

    public void ShowFailure(Action? retryReceipt = null)
    {
        if (IsClosed) return;
        _loadingScope?.Dispose();
        _presentation.Fail();
        ReleaseChoice();
        if (_frame.Content is not AiCompletionPage)
            _frame.Navigate(typeof(AiCompletionPage));
        if (_frame.Content is AiCompletionPage page)
        {
            page.ShowFailure(_presentation.ReceiptConsumed ? Recover : retryReceipt, _returnToConnection);
            page.ConfigureRecovery(() => ShowFailure(retryReceipt), _collectLogs);
        }
    }

    public void CommitPresentation()
    {
        if (IsClosed) return;
        _presentation.ConsumeReceipt();
        try
        {
            _choice?.RequireCurrent();
            if (_presentation.IsReady && _choice is not null && _frame.Content is AiReadyPage page)
                page.AdmitChatAction();
            else ShowFailure();
        }
        catch (Exception) { ShowFailure(); }
    }

    private void Recover()
    {
        if (IsClosed || !_presentation.ReceiptConsumed || !_recovery.IsCompleted) return;
        _recovery = RecoverAsync();
    }

    private async Task RecoverAsync()
    {
        ShowPreparing();
        try { await ShowReadyAsync(await VerifyAsync(_lifetime.Token), _lifetime.Token); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Published setup readiness recovery failed ({0}).", error.GetType().Name);
            ShowFailure();
        }
    }

    private void ReleaseChoice()
    {
        _observation?.Dispose();
        _observation = null;
        if (_choice is not { } choice) return;
        _choice = null;
        choice.Dispose();
        _choiceCleanup = Task.WhenAll(_choiceCleanup, choice.CleanupCompleted);
    }
}
