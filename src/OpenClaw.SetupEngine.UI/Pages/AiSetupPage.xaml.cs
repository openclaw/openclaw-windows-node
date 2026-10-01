using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.Shared;
using OpenClaw.Connection;
using OpenClawTray.Helpers;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed record AiSetupPageArgs(
    SetupConfig Config, string DataDir, string LocalDataDir,
    Func<bool> NavigateToLegacyWizard, Func<Task> CompleteSetup,
    string? ExpectedConfiguredModelRef = null,
    Func<IGatewayAiSetupTransport>? TransportFactory = null,
    ISetupLocalAiHost? LocalAiHost = null,
    Func<LocalAiOnboardingSnapshot, Task>? ReviewLocalAi = null,
    string? ExpectedGatewayId = null,
    SetupCompletionIntent ConfiguredCompletionIntent = SetupCompletionIntent.Dashboard,
    Func<GatewayAiSetupCompletion, Task>? CompleteVerifiedSetup = null,
    NativeGatewaySetupSession? NativeSession = null, Func<Task>? CancelNativeSetup = null,
    GatewayConnectionManager? ConnectionManager = null, string? ExpectedEndpointBinding = null);

public sealed partial class AiSetupPage : Page, IAsyncDisposable
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private AiSetupPageArgs? _args;
    private SetupGatewaySession? _session;
    private NativeGatewaySetupConnection? _nativeConnection;
    private WizardConsoleTail? _nativeConsole;
    private GatewayLogTailIssue? _nativeConsoleIssue;
    private readonly Queue<string> _nativeOutput = new();
    private bool _nativeRecoveryBusy;
    private bool _managedNative;
    private GatewayAiSetupController? _controller;
    private CancellationTokenSource _request = new();
    private Task _activeRequest = Task.CompletedTask;
    private Task? _dialogTask;
    private Task? _closeTask;
    private bool _closed;
    private int _generation;
    private bool _busy;
    private bool _submittingAnswer;
    private bool _rendering;
    private readonly ProviderSetupDialog _providerDialog = new();
    private AiSetupPresentationModel _presentation = new();
    private bool _apiKeyFormRequested;
    private bool _cancelling;
    private bool _providerOperationActive;
    private string? _operationTitle;
    private sealed record ProviderBackdrop(Control? FocusTarget, double VerticalOffset, int Version);
    private ProviderBackdrop? _providerBackdrop;
    private Control? _providerRestoreFocus;
    private bool _providerRestoreBringOnFocus;
    private double _providerRestoreOffset;
    private GatewayAiSetupDetection? _deferredDetection;
    private string? _providerError;
    private int _backdropVersion;
    private bool ProviderPending => _providerOperationActive ||
        Client?.Phase is GatewayAiSetupPhase.Running or GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.VerificationRequired;
    private bool BackdropLocked => ProviderPending || _providerBackdrop is not null;
    private LocalAiOnboardingObservation? _localObservation;
    private bool _localActionBusy;
    private LocalAiOnboardingUse? _localUse;
    private string? _localExpectedModel => _localUse?.Expected?.ModelRef;
    private string? ExpectedGatewayId => _localUse?.Expected?.GatewayId ?? _args?.ExpectedGatewayId;
    private readonly ProviderArtworkSession _artworkSession = new();
    private GatewayAiSetupClient? Client => _controller?.Client;

    public AiSetupPage()
    {
        InitializeComponent();
        TitleText.Text = S("Title.Text");
        CandidatesHeading.Text = SetupLocalization.GetString("Onboarding_V4_DetectedAi.Text");
        RecommendedHeading.Text = S("Recommended");
        RecommendedDetail.Text = S("RecommendedDetail");
        UnavailableHeading.Text = S("Unavailable");
        PrepareHeading.Text = S("OtherLocal");
        PrepareDetail.Text = S("OtherLocalDetail");
        SignInHeading.Text = S("ConnectProvider");
        LocalAiTitle.Text = S("LocalTitle");
        LocalAiCard.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Devices, 20);
        AutomationProperties.SetName(LocalAiCard, S("LocalTitle"));
        UtilityNotice.Message = S("UtilityNotice");
        ApiKeysTitle.Text = S("ApiKeys");
        ApiKeysDescription.Text = ApiKeyFormHeading.Text = S("ManualHeading");
        ApiKeysActionText.Text = S("ManualConnect");
        ApiKeyConnectButton.Content = S("ManualConnect");
        ApiKeyHelp.Text = S("ManualHelp");
        ApiKeysButton.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Key, 20);
        AutomationProperties.SetName(ApiKeysButton, S("ApiKeys"));
        ApiProviderPicker.Header = S("ApiProvider");
        CheckAgainButton.Content = S("CheckAgain");
        NoChoicesText.Text = S("NoChoices");
        MoreExpander.Header = S("More.Header");
        ApiKeyInput.Header = S("ApiKey.Header");
        CatalogPreferenceText.Text = S("CatalogOptIn");
        AutomationProperties.SetName(CatalogPreference, S("CatalogOptIn"));
        RefreshButton.Content = S("Refresh.Content");
        ProviderCancelButton.Content = S("Cancel.Content");
        LegacyButton.Content = S("Legacy.Content");
        AutomationProperties.SetName(CandidateChoices, S("Candidates"));
        AutomationProperties.SetName(PrepareChoices, S("Prepare"));
        AutomationProperties.SetName(FeaturedSignInChoices, S("SignIn"));
        AutomationProperties.SetName(MoreSignInChoices, S("More.Header"));
        AutomationProperties.SetName(ApiProviderPicker, S("ApiProvider"));
        AutomationProperties.SetName(ApiKeyInput, S("ApiKey.Header"));
        _providerDialog.ContinueRequested += ProviderContinueRequested;
        _providerDialog.RefreshRequested += ProviderRefreshRequested;
        _providerDialog.CancelRequested += ProviderCancelRequested;
        _providerDialog.ExternalLinkRequested += ProviderExternalLinkRequested;
        ChoicesScroller.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => CancelProviderViewportRestore()), true);
        ChoicesScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => CancelProviderViewportRestore()), true);
        ChoicesScroller.AddHandler(UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler((_, _) => CancelProviderViewportRestore()), true);
        ChoicesScroller.LosingFocus += (_, _) => CancelProviderViewportRestore();
        ChoicesScroller.ViewChanged += (_, e) =>
        {
            // A bring request admitted before focus restoration may settle afterwards.
            if (!e.IsIntermediate && _providerRestoreFocus is not null &&
                Math.Abs(ChoicesScroller.VerticalOffset - _providerRestoreOffset) > 0.5)
                ChoicesScroller.ChangeView(null, _providerRestoreOffset, null, disableAnimation: true);
        };
        ChoicePanel.BringIntoViewRequested += (_, e) =>
        {
            // Intercept the restored control's deferred caret request before the outer
            // ScrollViewer handles it. New input/focus/operations end this ownership scope.
            if (_providerRestoreFocus is not null)
                e.Handled = true;
        };
        Unloaded += Page_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = e.Parameter as AiSetupPageArgs
            ?? throw new ArgumentException("AI setup requires window-scoped navigation arguments.");
        _providerDialog.NativeRecoveryRequested += NativeRecoveryRequested;
        if (_args.NativeSession is { IsIsolated: false } native)
        {
            var tail = new WizardConsoleTail(logger: NullLogger.Instance, nativeLogPath: native.ConsoleLogPath);
            _nativeConsole = tail;
            tail.Start(message => DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || !ReferenceEquals(_nativeConsole, tail)) return;
                _nativeOutput.Enqueue(message);
                while (_nativeOutput.Count > 40) _nativeOutput.Dequeue();
                UpdateNativeRecovery();
            }));
        }
        if (_args.LocalAiHost is { } host && _args.ExpectedConfiguredModelRef is null)
        {
            _localUse = new(host);
            _localObservation = new(host);
            _localObservation.Changed += LocalObservationChanged;
            AsyncEventHandlerGuard.Run(_localObservation.RefreshAsync, onError: ReportFailure);
        }
        AsyncEventHandlerGuard.Run(() => RunAsync(InitializeAsync), onError: ReportFailure);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
        => BeginClose();

    private void Page_Unloaded(object sender, RoutedEventArgs e) => BeginClose();

    private void BeginClose() =>
        AsyncEventHandlerGuard.Run(CloseAsync, onError: ex =>
            Trace.TraceWarning("AI setup connection cleanup failed ({0}).", ex.GetType().Name));

    /// <summary>Call on the UI thread and await before releasing the owning setup lock.</summary>
    public Task CloseAsync()
    {
        if (_closeTask is not null)
            return _closeTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeTask = completion.Task;
        _closed = true;
        _nativeConsole?.Dispose();
        _nativeConsole = null;
        CancelProviderViewportRestore();
        _providerBackdrop = null;
        _deferredDetection = null;
        ++_generation;
        _artworkSession.Dispose();
        _ = CompleteCloseAsync(completion);
        return _closeTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try
        {
            try { _request.Cancel(); }
            catch (AggregateException ex)
            {
                Trace.TraceWarning("AI setup request cancellation callback failed ({0}).", ex.GetType().Name);
            }
            _providerDialog.ClearInputs();
            ApiKeyInput.Password = "";
            if (_localObservation is not null)
            {
                _localObservation.Changed -= LocalObservationChanged;
                await _localObservation.DisposeAsync();
            }
            await CloseCoreAsync();
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private async Task CloseCoreAsync()
    {
        // Runtime cancellation may still be withdrawing a Gateway route. It owns
        // the setup lock until rollback has really ended, unlike read-only RPCs.
        if (_localUse is not null)
            await _localUse.DrainAsync();
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            var dialogClose = _providerDialog.CloseAsync();
            await ReleaseAsync(timeout.Token);
            await _activeRequest.WaitAsync(timeout.Token);
            await dialogClose.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Trace.TraceWarning("AI setup request shutdown reached its bounded deadline.");
        }
        finally
        {
            _controller = null;
            _providerDialog.ContinueRequested -= ProviderContinueRequested;
            _providerDialog.RefreshRequested -= ProviderRefreshRequested;
            _providerDialog.CancelRequested -= ProviderCancelRequested;
            _providerDialog.ExternalLinkRequested -= ProviderExternalLinkRequested;
            _providerDialog.NativeRecoveryRequested -= NativeRecoveryRequested;
            _request.Dispose();
            Unloaded -= Page_Unloaded;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        IGatewayAiSetupTransport transport;
        if (_args!.TransportFactory is { } factory)
            transport = factory();
        else if (_args.NativeSession is { } native)
        {
            var connection = await NativeGatewaySetupConnection.ConnectAsync(native, ct);
            if (_closed || ct.IsCancellationRequested)
            {
                await connection.DisposeAsync();
                throw new OperationCanceledException(ct);
            }
            _nativeConnection = connection;
            transport = connection;
        }
        else
        {
            var registry = new GatewayRegistry(_args.DataDir);
            registry.Load();
            SetupGatewaySession.RequireExpectedGateway(registry.GetActive(), ExpectedGatewayId, _args.ExpectedEndpointBinding);
            if (registry.GetActive() is { NativePackageFamilyName: not null } active)
            {
                _managedNative = true;
                if (_args.ConnectionManager is not { } manager)
                    throw new InvalidOperationException("The native Gateway connection owner is unavailable.");
                transport = await GatewayAiSetupTransport.BorrowNativeAsync(_args.DataDir, manager, active.Id, ct,
                    _args.ExpectedEndpointBinding);
            }
            else
            {
                var session = await SetupGatewaySession.ConnectAsync(_args.DataDir,
                    () => Client?.RequiresReconciliation == true, ct, ExpectedGatewayId,
                    expectedEndpointBinding: _args.ExpectedEndpointBinding);
                if (_closed || ct.IsCancellationRequested)
                {
                    await session.DisposeAsync();
                    throw new OperationCanceledException(ct);
                }
                _session = session;
                transport = new GatewayAiSetupTransport(session.Client, session.GetRoute,
                    requireRestartAuthority: session.RequireRestartAuthority);
            }
        }
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_closed, this);
        LocalAiOnboardingUse.RequireGateway(ExpectedGatewayId, transport.Route.GatewayId);
        if (_args.NativeSession is { IsIsolated: true } isolated)
            await StartIsolatedConsoleAsync(transport, isolated.LifetimeToken);
        _controller = new(new GatewayAiSetupClient(transport, _args.ExpectedConfiguredModelRef,
            _localUse?.Expected?.CompletionIntent ?? _args.ConfiguredCompletionIntent));
        if ((_localExpectedModel ?? _args.ExpectedConfiguredModelRef) is { Length: > 0 } modelRef)
        {
            var result = await Client!.VerifyConfiguredAsync(modelRef, ct);
            if (!result.Ok)
                ShowError("VerificationFailed");
        }
        else
            await DetectAsync(ct);
    }

    private async Task StartIsolatedConsoleAsync(IGatewayAiSetupTransport transport, CancellationToken ct)
    {
        _nativeConsole?.Dispose();
        _nativeOutput.Clear();
        _nativeConsoleIssue = null;
        var tail = new WizardConsoleTail(logger: NullLogger.Instance,
            gatewayLogTail: WizardConsoleTail.CreateGatewayLogReader(transport.RequestAsync));
        _nativeConsole = tail;
        void AppendMessage(string message) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || !ReferenceEquals(_nativeConsole, tail)) return;
            _nativeOutput.Enqueue(message);
            while (_nativeOutput.Count > 40) _nativeOutput.Dequeue();
            UpdateNativeRecovery();
        });
        void ReportIssue(GatewayLogTailIssue issue) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || !ReferenceEquals(_nativeConsole, tail) ||
                _nativeConsoleIssue == GatewayLogTailIssue.Skipped) return;
            _nativeConsoleIssue = issue;
            var message = SetupLocalization.GetString(issue == GatewayLogTailIssue.Skipped
                ? "Onboarding_Wizard_GatewayConsoleGap" : "Onboarding_Wizard_GatewayConsoleUnavailable");
            if (BackdropLocked) _providerError = message;
            else
            {
                ErrorBar.Message = message;
                ErrorBar.IsOpen = true;
            }
            Render();
        });
        try { await tail.StartGatewayAsync(AppendMessage, ReportIssue, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _closed) { throw; }
        catch (Exception error)
        {
            Trace.TraceWarning("Isolated setup console could not start ({0}).", error.GetType().Name);
            if (ReferenceEquals(_nativeConsole, tail))
            {
                tail.Stop();
                ReportIssue(GatewayLogTailIssue.Unavailable);
            }
        }
    }

    private async Task DetectAsync(CancellationToken ct)
    {
        GatewayAiSetupDetection? detection;
        try { detection = await Client!.DetectAsync(ct); }
        catch
        {
            _presentation = AiSetupPresentationModel.Create(null, new HashSet<GatewayAiSetupChoiceKind>(), discoveryFailed: true);
            throw;
        }
        if (_closed || ct.IsCancellationRequested)
            return;
        if (detection is null)
            return;
        ApplyDetection(detection);
    }

    private void ApplyDetection(GatewayAiSetupDetection detection)
    {
        if (_providerBackdrop is not null)
        {
            _deferredDetection = detection;
            return;
        }
        var catalogConsent = CatalogPreference.IsChecked;
        var local = _localObservation?.Snapshot;
        _presentation = AiSetupPresentationModel.Create(detection,
            Enum.GetValues<GatewayAiSetupChoiceKind>().Where(Client!.SupportsChoice).ToHashSet(),
            apiKeyFormRequested: _apiKeyFormRequested, gatewayId: Client!.Route.GatewayId,
            localGatewayId: local?.CanUse == true ? local.Target?.GatewayId : null,
            localModelRef: local?.CanUse == true ? local.ModelRef : null,
            hasLocalChoice: local?.ShowLocalChoice == true);
        _rendering = true;
        try
        {
            CandidateChoices.ItemsSource = _presentation.Candidates.Select(candidate =>
                new ChoiceRow(GatewayAiSetupChoiceKind.Candidate, candidate.Kind, candidate.Label, candidate.Detail,
                    _artworkSession, candidate.ModelRef, candidate.BrandId, candidate.Icon, candidate.Kind)).ToArray();
            PrepareChoices.ItemsSource = ProviderRows(_presentation.PrepareOptions, GatewayAiSetupChoiceKind.Prepare);
            FeaturedSignInChoices.ItemsSource = ProviderRows(_presentation.FeaturedSignIn, GatewayAiSetupChoiceKind.Auth);
            MoreSignInChoices.ItemsSource = ProviderRows(_presentation.MoreSignIn, GatewayAiSetupChoiceKind.Auth);
            ApiProviderPicker.ItemsSource = ProviderRows(_presentation.ManualProviders, GatewayAiSetupChoiceKind.ManualProvider);
            ApiProviderPicker.SelectedIndex = -1;
            RecommendedInstalls.ItemsSource = ProviderRows(_presentation.RecommendedInstalls, GatewayAiSetupChoiceKind.Prepare);
            UnavailableCandidates.ItemsSource = _presentation.UnavailableCandidates.Select(candidate =>
                new ChoiceRow(GatewayAiSetupChoiceKind.Candidate, candidate.Id, candidate.Label, candidate.Detail,
                    _artworkSession, BrandId: candidate.BrandId, Icon: candidate.Icon)).ToArray();
            CandidatesSection.Visibility = Visible(_presentation.Candidates.Count > 0);
            RecommendedSection.Visibility = Visible(_presentation.RecommendedInstalls.Count > 0);
            UnavailableSection.Visibility = Visible(_presentation.UnavailableCandidates.Count > 0);
            PrepareSection.Visibility = Visible(_presentation.PrepareOptions.Count > 0);
            SignInSection.Visibility = Visible(_presentation.FeaturedSignIn.Count +
                _presentation.MoreSignIn.Count > 0);
            FeaturedSignInChoices.Visibility = Visible(_presentation.FeaturedSignIn.Count > 0);
            ApiKeySection.Visibility = Visible(_presentation.ManualProviders.Count > 0);
            ApiKeyForm.Visibility = Visible(_presentation.ShowApiKeyForm);
            MoreExpander.Visibility = Visible(_presentation.MoreSignIn.Count > 0);
            MoreExpander.IsExpanded = false;
            NoChoicesText.Visibility = Visible(!_presentation.HasChoices);
            ApiKeyInput.Password = "";
            CatalogPreference.Visibility = Visible(_presentation.NativeSessionCatalogPreferenceRequired);
            CatalogPreference.IsChecked = catalogConsent;
            UtilityNotice.Visibility = Visible(_presentation.HasUtilityChoices);
        }
        finally { _rendering = false; }
    }

    private ChoiceRow[] ProviderRows(IReadOnlyList<GatewayAiSetupProvider> providers, GatewayAiSetupChoiceKind kind) =>
        providers.Select(option => new ChoiceRow(kind, option.Id, option.Label,
            option.Hint ?? "",
            _artworkSession, BrandId: option.BrandId, Icon: option.Icon, ProviderKind: option.Kind,
            MetadataActionLabel: option.ActionLabel, Website: option.Website)).ToArray();

    private void ApiKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _busy || _localActionBusy || BackdropLocked)
            return;
        CancelProviderViewportRestore();
        _apiKeyFormRequested = true;
        ApiKeyForm.Visibility = Visibility.Visible;
        ApiKeyForm.StartBringIntoView();
        ApiProviderPicker.Focus(FocusState.Programmatic);
        Render();
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || _rendering || _busy || _localActionBusy || BackdropLocked)
            return;
        if (e.AddedItems.FirstOrDefault() is not ChoiceRow choice)
        {
            Render();
            return;
        }

        _rendering = true;
        try
        {
            SelectChoice(choice);
            ApiKeyInput.Password = "";
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex) { ReportFailure(ex); }
        finally { _rendering = false; }
        Render();
    }

    private void ChoiceAction_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _busy || _localActionBusy || BackdropLocked || sender is not FrameworkElement { Tag: ChoiceRow row })
            return;
        AsyncEventHandlerGuard.Run(() => StartChoiceAsync(row, returnFocus: sender as Control), onError: ReportFailure);
    }

    private void SelectChoice(ChoiceRow row)
    {
        switch (row.Kind)
        {
            case GatewayAiSetupChoiceKind.Candidate: Client!.SelectCandidate(row.Id, row.ModelRef!); break;
            case GatewayAiSetupChoiceKind.ManualProvider: Client!.SelectManualProvider(row.Id); break;
            case GatewayAiSetupChoiceKind.Auth: Client!.SelectAuthOption(row.Id); break;
            case GatewayAiSetupChoiceKind.Prepare: Client!.SelectPrepareOption(row.Id); break;
        }
        if (Client!.Selection is not { } selected || selected.Kind != row.Kind ||
            selected.Id != row.Id || selected.ModelRef != row.ModelRef)
            throw new InvalidOperationException("The selected provider does not match the invoked action.");
    }

    private Task StartChoiceAsync(ChoiceRow row, string? key = null, Control? returnFocus = null)
    {
        if (_closed || _busy || _localActionBusy || BackdropLocked)
            return Task.CompletedTask;
        CaptureProviderBackdrop(returnFocus);
        _providerOperationActive = true;
        ApiKeyInput.Password = "";
        _operationTitle = row.Label;
        var catalogPreference = _presentation.NativeSessionCatalogPreferenceRequired
            ? CatalogPreference.IsChecked == true : (bool?)null;
        return RunAsync(async ct =>
        {
            SelectChoice(row);
            await _controller!.StartSelectedAsync(key, catalogPreference, Render, ct);
        });
    }

    private void ApiKeyFields_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var textScale = new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
        VisualStateManager.GoToState(this,
            e.NewSize.Width >= 560 * Math.Min(textScale, 1.5) ? "ManualHorizontal" : "ManualStacked", false);
    }

    private void LocalObservationChanged()
    {
        if (_closed) return;
        // Preserve a deliberate provider selection and conversation consent while local facts arrive.
        if (!BackdropLocked && Client?.Detection is { } detection && Client.Selection is null && !_busy)
            ApplyDetection(detection);
        Render();
    }

    private void LocalAi_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(LocalAiActionAsync, onError: ReportFailure);

    private async Task LocalAiActionAsync()
    {
        if (_closed || _localActionBusy || BackdropLocked || _localObservation is null ||
            Client is { CanLeaveForLocalAi: false } && Client.Phase != GatewayAiSetupPhase.Idle)
            return;
        var selected = _localObservation.Snapshot;
        if (selected.CanRefresh)
        {
            await _localObservation.RefreshAsync();
            return;
        }
        if (!selected.CanReview && !selected.CanUse)
            return;
        _localActionBusy = true;
        Render();
        try
        {
            // Discovery can be cancelled independently. Provider writes cannot.
            if (_busy)
            {
                _request.Cancel();
                await _activeRequest;
            }
            if (_closed || Client is { CanLeaveForLocalAi: false })
                return;
            ApiKeyInput.Password = "";
            _providerDialog.ClearInputs();
            if (selected.CanReview && _args?.ReviewLocalAi is { } review)
                await review(selected);
            else if (selected.CanUse)
            {
                await RunAsync(async ct =>
                {
                    if (Client is null)
                        await InitializeAsync(ct);
                    Client!.EnsureLocalAiCanStart(selected.Target!.GatewayId);
                    try { await _localUse!.UseAsync(selected, ct); }
                    catch (LocalAiSelectionRejectedException)
                    {
                        if (_closed) return;
                        ShowError("LocalChanged");
                        await _localObservation.RefreshAsync();
                        return;
                    }
                    catch (LocalAiStartFailedException ex)
                    {
                        if (_closed) return;
                        await _localObservation.RefreshAsync();
                        if (!_closed)
                        {
                            ErrorBar.Message = S("LocalStartFailed") + " " + ex.Message;
                            ErrorBar.IsOpen = true;
                        }
                        return;
                    }
                    ct.ThrowIfCancellationRequested();
                    // Route publication may restart the Gateway. Acquire a fresh setup-owned handshake.
                    await ReleaseAsync(ct);
                    _controller = null;
                    await InitializeAsync(ct);
                });
            }
        }
        finally { _localActionBusy = false; Render(); }
    }

    private void ManualConnect_Click(object sender, RoutedEventArgs e)
    {
        if (ApiProviderPicker.SelectedItem is ChoiceRow row && CanConnectManual())
        {
            var key = ApiKeyInput.Password;
            ApiKeyInput.Password = "";
            AsyncEventHandlerGuard.Run(() => StartChoiceAsync(row, key, ApiKeyInput), onError: ReportFailure);
        }
    }

    private Task ContinueAsync()
    {
        if (_closed || _busy || _localActionBusy)
            return Task.CompletedTask;
        if (Client?.Phase == GatewayAiSetupPhase.Prepared)
            return RunAsync(ct => _controller!.ActivatePreparedExplicitlyAsync(Render, ct));
        var step = Client?.Phase == GatewayAiSetupPhase.Running ? Client.Wizard?.Step : null;
        if (step is null || !_providerDialog.CanSubmit)
            return Task.CompletedTask;
        var answer = _providerDialog.TakeAnswer();
        return RunAsync(ct => _controller!.SubmitAsync(step.Id, answer, Render, ct), submittingAnswer: true);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!BackdropLocked)
            AsyncEventHandlerGuard.Run(RefreshAsync, onError: ReportFailure);
    }

    private Task RefreshAsync() => RunAsync(async ct =>
    {
        _controller?.StopAutomaticContinuation();
        if (Client?.Phase == GatewayAiSetupPhase.Idle && _session is not null &&
            !_session.Client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal))
        {
            await ReleaseAsync();
            _controller = null;
        }
        if (Client is null)
            await InitializeAsync(ct);
        else if ((_localExpectedModel ?? _args!.ExpectedConfiguredModelRef) is { Length: > 0 } expectedModel)
        {
            LocalAiOnboardingUse.RequireGateway(ExpectedGatewayId, Client.Route.GatewayId);
            var verification = await Client.VerifyConfiguredAsync(expectedModel, ct);
            if (!verification.Ok)
                ShowError("VerificationFailed");
        }
        else if (Client.Phase == GatewayAiSetupPhase.Verified)
            return;
        else if (Client.Phase == GatewayAiSetupPhase.Prepared)
            ShowError("PreparedChanged");
        else if (Client.RequiresReconciliation)
        {
            if (Client.SessionId is not null)
                await Client.RefreshAsync(ct);
            else
            {
                var verification = await Client.VerifyAsync(ct);
                if (!verification.Ok)
                    ShowError("VerificationFailed");
            }
            await _controller!.WaitForInputAsync(Render, ct);
        }
        else if (Client.Phase == GatewayAiSetupPhase.Running)
            await _controller!.WaitForInputAsync(Render, ct);
        else
            await DetectAsync(ct);
    });

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(CancelAsync, onError: ReportFailure);

    private async Task CancelAsync()
    {
        if (_closed || _cancelling)
            return;
        if (Client?.SessionId is null)
        {
            if (Client?.Phase is GatewayAiSetupPhase.Prepared or GatewayAiSetupPhase.Choosing)
            {
                _providerOperationActive = false;
                _providerDialog.Dismiss();
                await RunAsync(DetectAsync);
            }
            return;
        }
        _cancelling = true;
        _controller!.StopAutomaticContinuation();
        try
        {
            ++_generation;
            try { _request.Cancel(); }
            catch (AggregateException ex)
            {
                Trace.TraceWarning("AI setup request cancellation callback failed ({0}).", ex.GetType().Name);
            }
            _busy = false;
            _providerDialog.ClearInputs();
            ApiKeyInput.Password = "";
            await RunAsync(ct => Client!.CancelAsync(ct));
        }
        finally
        {
            _cancelling = false;
            Render();
        }
    }

    private void Legacy_Click(object sender, RoutedEventArgs e)
    {
        if (!_closed && !BackdropLocked && Client?.Phase == GatewayAiSetupPhase.ClassicWizardRequired && !_args!.NavigateToLegacyWizard())
            ShowError("NavigationFailed");
    }

    private void NativeRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<NativeSetupRecoveryAction>(tag, out var action))
            NativeRecoveryRequested(action);
    }

    private void NativeRecoveryRequested(NativeSetupRecoveryAction action) =>
        AsyncEventHandlerGuard.Run(() => RecoverNativeAsync(action), onError: ReportFailure);

    private async Task RecoverNativeAsync(NativeSetupRecoveryAction action)
    {
        if (_closed || _nativeRecoveryBusy || _args?.NativeSession is not { } native) return;
        _nativeRecoveryBusy = true;
        try
        {
            if (action == NativeSetupRecoveryAction.OpenTerminal)
            {
                native.OpenRecoveryTerminal();
                return;
            }
            if (action == NativeSetupRecoveryAction.CancelSetup)
            {
                if (_args.CancelNativeSetup is not { } cancel)
                    throw new InvalidOperationException("The native setup cancellation owner is unavailable.");
                await cancel();
                return;
            }
            _controller?.StopAutomaticContinuation();
            if (Client?.SessionId is not null)
                await CancelAsync();
            if (Client is { RequiresReconciliation: true } || Client?.SessionId is not null)
                throw new InvalidOperationException("The provider operation must settle before restarting setup.");
            var pending = _activeRequest;
            _request.Cancel();
            await pending;
            _providerDialog.Dismiss();
            await ReleaseAsync();
            _controller = null;
            if (action == NativeSetupRecoveryAction.RestartGateway)
                await native.RestartAsync(native.LifetimeToken);
            await RunAsync(InitializeAsync);
        }
        finally { _nativeRecoveryBusy = false; }
    }

    private void ProviderContinueRequested() => AsyncEventHandlerGuard.Run(ContinueAsync, onError: ReportFailure);
    private void ProviderRefreshRequested() => AsyncEventHandlerGuard.Run(RefreshAsync, onError: ReportFailure);
    private void ProviderCancelRequested() => AsyncEventHandlerGuard.Run(CancelAsync, onError: ReportFailure);
    private void ProviderExternalLinkRequested(string? url) =>
        AsyncEventHandlerGuard.Run(() => OpenExternalLinkAsync(url), onError: ReportFailure);

    private void RecommendedInstall_Click(object sender, RoutedEventArgs e)
    {
        if (!_closed && !_busy && !BackdropLocked && sender is FrameworkElement { Tag: string website })
            ProviderExternalLinkRequested(website);
    }

    private async Task OpenExternalLinkAsync(string? url)
    {
        if (_closed)
            return;
        if (!GatewayAiSetupPresentation.TryGetExternalUri(url, out var uri))
        {
            ShowError("InvalidLink");
            return;
        }
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                ShowError("InvalidLink");
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private Task RunAsync(Func<CancellationToken, Task> action, bool submittingAnswer = false)
    {
        if (_closed || _busy)
            return Task.CompletedTask;
        CancelProviderViewportRestore();
        return _activeRequest = RunCoreAsync(action, submittingAnswer);
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> action, bool submittingAnswer)
    {
        var generation = ++_generation;
        _request.Dispose();
        _request = new();
        var token = _request.Token;
        _busy = true;
        // Uncertain is also used for recovery. Retain a prompt only for its own answer request.
        _submittingAnswer = submittingAnswer;
        if (BackdropLocked) _providerError = null;
        else ErrorBar.IsOpen = false;
        Render();
        try
        {
            await action(token);
            token.ThrowIfCancellationRequested();
            if (!_closed && generation == _generation && Client?.Phase == GatewayAiSetupPhase.VerificationRequired)
            {
                if (Client.WaitingForRestart && _nativeConnection is { } native)
                    await native.RestartAsync(token);
                await _controller!.WaitForExpectedRestartAsync(Render, token);
                var verification = await Client.VerifyAsync(token);
                if (!verification.Ok)
                    ShowError("VerificationFailed");
            }
            if (!_closed && generation == _generation && Client?.Phase == GatewayAiSetupPhase.Verified)
            {
                LocalAiOnboardingUse.RequireGateway(ExpectedGatewayId, Client.Route.GatewayId);
                _providerOperationActive = false;
                _providerDialog.Dismiss();
                if (_args!.CompleteVerifiedSetup is { } complete)
                    await complete(Client.GetVerifiedCompletion());
                else
                    await _args.CompleteSetup();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                if (Client?.Phase is not (GatewayAiSetupPhase.Running or GatewayAiSetupPhase.Uncertain or
                    GatewayAiSetupPhase.VerificationRequired or GatewayAiSetupPhase.Prepared))
                    _providerOperationActive = false;
                ReportFailure(ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _busy = false;
                _submittingAnswer = false;
                if (Client?.Phase is GatewayAiSetupPhase.Cancelled or GatewayAiSetupPhase.Rejected)
                {
                    _providerOperationActive = false;
                    if (Client.Phase == GatewayAiSetupPhase.Rejected)
                        ShowError("Rejected");
                }
                Render();
            }
        }
    }

    private void Render()
    {
        if (_closed || _rendering)
            return;
        if (ProviderPending)
            CaptureProviderBackdrop();
        else if (!_busy && _providerBackdrop is { } backdrop)
            ReleaseProviderBackdrop(backdrop);
        _rendering = true;
        try
        {
            var phase = Client?.Phase ?? GatewayAiSetupPhase.Idle;
            var freezeBackdrop = _providerBackdrop is not null;
            if (!freezeBackdrop) RenderLocalAi(phase);
            MascotHero.Mood = phase == GatewayAiSetupPhase.Verified ? OnboardingMascotMood.Celebrating
                : _busy || phase is GatewayAiSetupPhase.Running or GatewayAiSetupPhase.VerificationRequired
                    ? OnboardingMascotMood.Thinking
                : ErrorBar.IsOpen || phase is GatewayAiSetupPhase.Uncertain or GatewayAiSetupPhase.Rejected
                    ? OnboardingMascotMood.Sad : OnboardingMascotMood.Curious;
            if (phase == GatewayAiSetupPhase.Running && !string.IsNullOrWhiteSpace(Client?.Wizard?.Error))
                ShowError("InvalidInput");
            var status = S(phase switch
            {
                GatewayAiSetupPhase.ClassicWizardRequired => "Unsupported",
                GatewayAiSetupPhase.Uncertain => _busy ? "WorkingProvider" : "Uncertain",
                GatewayAiSetupPhase.VerificationRequired => Client!.WaitingForRestart ? "Reconnecting" : "Verifying",
                GatewayAiSetupPhase.Prepared => "Prepared",
                GatewayAiSetupPhase.Verified => "Verified",
                GatewayAiSetupPhase.Rejected => _args?.ExpectedConfiguredModelRef is { Length: > 0 } ? "VerificationFailed" : "Rejected",
                GatewayAiSetupPhase.Cancelled => "Cancelled",
                GatewayAiSetupPhase.Running => "Running",
                _ => _busy ? (_providerOperationActive ? "Starting" : "Connecting") : _presentation.DiscoveryFailed ? "DiscoveryFailed" :
                    _presentation.NoUsableCandidates ? "NoUsableCandidates" : "Choose"
            });
            if (!freezeBackdrop)
            {
                StatusText.Text = status;
                ChoicePanel.Visibility = Visible(_localExpectedModel is null && _args?.ExpectedConfiguredModelRef is null &&
                    (phase is GatewayAiSetupPhase.Choosing or GatewayAiSetupPhase.Cancelled or GatewayAiSetupPhase.Rejected));
                CandidatesHeading.Visibility = Visible(LocalAiSection.Visibility == Visibility.Visible ||
                    ChoicePanel.Visibility == Visibility.Visible && CandidatesSection.Visibility == Visibility.Visible);
                LegacyButton.Visibility = phase == GatewayAiSetupPhase.ClassicWizardRequired ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                LocalAiCard.IsClickEnabled = false;
                LocalAiCard.IsEnabled = false;
            }
            var backgroundEnabled = !_busy && !_localActionBusy && !BackdropLocked;
            CandidateChoices.IsEnabled = PrepareChoices.IsEnabled = FeaturedSignInChoices.IsEnabled =
                MoreSignInChoices.IsEnabled = ApiProviderPicker.IsEnabled = ApiKeyInput.IsEnabled =
                ApiKeysButton.IsEnabled = CatalogPreference.IsEnabled = RecommendedInstalls.IsEnabled =
                MoreExpander.IsEnabled = RecommendedSection.IsEnabled = CheckAgainButton.IsEnabled =
                RefreshButton.IsEnabled = LegacyButton.IsEnabled = backgroundEnabled;
            var step = Client?.Wizard?.Step;
            var showProvider = ProviderPending && !_cancelling &&
                GatewayAiSetupPresentation.ShowProviderDialog(step, phase, _busy,
                    !string.IsNullOrWhiteSpace(_providerError), _submittingAnswer);
            var inlineProvider = ProviderPending && !showProvider;
            var canCancelProvider = Client?.SessionId is not null ||
                (!_busy && phase is GatewayAiSetupPhase.Prepared or GatewayAiSetupPhase.Choosing);
            ProviderActivity.Visibility = Visible(inlineProvider);
            ProviderActivityStatus.Text = _cancelling ? S("Cancelling") :
                phase == GatewayAiSetupPhase.Running && !string.IsNullOrWhiteSpace(step?.Message)
                    ? step.Message : status;
            ProviderActivityProgress.Visibility = Visible(_busy || _cancelling);
            ProviderActivityError.Message = _providerError ?? "";
            ProviderActivityError.IsOpen = inlineProvider && !string.IsNullOrWhiteSpace(_providerError);
            StatusText.Visibility = Visible(!inlineProvider);
            ProviderCancelButton.Visibility = Visible(inlineProvider && (canCancelProvider || _cancelling));
            ProviderCancelButton.IsEnabled = canCancelProvider && !_cancelling;
            BusyProgress.Visibility = Visible(_busy && !ProviderPending);
            if (showProvider)
            {
                _providerDialog.Update(step, phase, _busy, canCancelProvider, _cancelling,
                    status, _providerError, _operationTitle, _submittingAnswer);
                if (XamlRoot is not null)
                {
                    var showing = _providerDialog.ShowOwnedAsync(XamlRoot, ActualTheme);
                    if (!ReferenceEquals(_dialogTask, showing))
                    {
                        _dialogTask = showing;
                        AsyncEventHandlerGuard.Run(() => showing, onError: ReportFailure);
                    }
                    try
                    {
                        if (_controller?.TakeAutomaticAuthUri() is { } uri)
                            AsyncEventHandlerGuard.Run(() => OpenExternalLinkAsync(uri.AbsoluteUri), onError: ReportFailure);
                    }
                    catch (InvalidOperationException error)
                    {
                        // A live route can change between a reply and its render. Surface the
                        // rejected link without throwing out of an unguarded XAML event.
                        _controller?.StopAutomaticContinuation();
                        ReportFailure(error);
                    }

                }
            }
            else
                _providerDialog.Dismiss();
            ApiKeyConnectButton.IsEnabled = backgroundEnabled && CanConnectManual();
            if (_providerBackdrop is { } visibleBackdrop)
                ChoicesScroller.ChangeView(null, visibleBackdrop.VerticalOffset, null, disableAnimation: true);
            UpdateNativeRecovery();
        }
        finally { _rendering = false; }
    }

    private void UpdateNativeRecovery()
    {
        var hasError = _providerBackdrop is not null
            ? !string.IsNullOrWhiteSpace(_providerError)
            : ErrorBar.IsOpen;
        var visible = !_closed && _args?.NativeSession is not null &&
            GatewayAiSetupPresentation.ShowNativeRecovery(Client?.Phase ?? GatewayAiSetupPhase.Idle, _busy,
                hasError || _nativeConsoleIssue is not null);
        NativeRecovery.Visibility = Visible(visible);
        if (!visible) NativeRecovery.IsExpanded = false;
        NativeOutput.Text = visible ? string.Join(Environment.NewLine, _nativeOutput) : "";
        _providerDialog.UpdateNativeRecovery(visible, NativeOutput.Text);
    }

    private void CaptureProviderBackdrop(Control? returnFocus = null)
    {
        CancelProviderViewportRestore();
        _providerBackdrop ??= new(returnFocus ??
            (XamlRoot is { } root ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as Control : null),
            ChoicesScroller.VerticalOffset, ++_backdropVersion);
    }

    private void ReleaseProviderBackdrop(ProviderBackdrop backdrop)
    {
        _providerBackdrop = null;
        if (_deferredDetection is { } detection)
        {
            _deferredDetection = null;
            ApplyDetection(detection);
        }
        ErrorBar.Message = _providerError ?? "";
        ErrorBar.IsOpen = _providerError is not null;
        _providerError = null;
        var showing = _dialogTask ?? Task.CompletedTask;
        var generation = _generation;
        AsyncEventHandlerGuard.Run(async () =>
        {
            await showing;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || BackdropLocked || _busy || generation != _generation || backdrop.Version != _backdropVersion ||
                    Client?.Phase == GatewayAiSetupPhase.Verified) return;
                ChoicesScroller.UpdateLayout();
                var focus = Client?.Phase == GatewayAiSetupPhase.Rejected &&
                    Client.Selection?.Kind == GatewayAiSetupChoiceKind.ManualProvider ? ApiKeyInput : backdrop.FocusTarget;
                CancelProviderViewportRestore();
                var bringOnFocus = ChoicesScroller.BringIntoViewOnFocusChange;
                ChoicesScroller.BringIntoViewOnFocusChange = false;
                try
                {
                    if (focus is not { IsLoaded: true, IsEnabled: true } || !focus.Focus(FocusState.Programmatic))
                    {
                        focus = RefreshButton;
                        RefreshButton.Focus(FocusState.Programmatic);
                    }
                    _providerRestoreFocus = focus;
                    _providerRestoreBringOnFocus = bringOnFocus;
                    _providerRestoreOffset = backdrop.VerticalOffset;
                    ChoicesScroller.ChangeView(null, backdrop.VerticalOffset, null, disableAnimation: true);
                }
                finally
                {
                    if (_providerRestoreFocus is null)
                        ChoicesScroller.BringIntoViewOnFocusChange = bringOnFocus;
                }
            }))
                Trace.TraceWarning("AI setup could not restore the provider chooser after dialog close.");
        }, onError: ReportFailure);
    }

    private void CancelProviderViewportRestore()
    {
        if (_providerRestoreFocus is null) return;
        _providerRestoreFocus = null;
        ChoicesScroller.BringIntoViewOnFocusChange = _providerRestoreBringOnFocus;
    }

    private void RenderLocalAi(GatewayAiSetupPhase phase)
    {
        if (_args?.NativeSession is not null || _managedNative)
        {
            LocalAiSection.Visibility = Visibility.Collapsed;
            return;
        }
        var snapshot = _localObservation?.Snapshot ?? new(LocalAiOnboardingState.UnsupportedGateway);
        LocalAiSection.Visibility = Visible(_args?.ExpectedConfiguredModelRef is null && _localExpectedModel is null &&
            snapshot.ShowLocalChoice);
        var state = snapshot.State;
        LocalAiDescription.Text = string.Join(" · ", new[]
        {
            S("LocalState_" + state),
            string.Join(" · ", new[] { snapshot.GpuName, snapshot.ModelName }.Where(value => !string.IsNullOrWhiteSpace(value))),
            state == LocalAiOnboardingState.Unsupported && snapshot.Eligibility is { } eligibility
                ? LocalAiSetupControl.DescribeLocalAiUnavailable(eligibility) : null,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        LocalAiActionText.Text = state switch
        {
            LocalAiOnboardingState.SetUp => S("LocalSetUp"),
            LocalAiOnboardingState.StartAndUse => S("LocalStartAndUse"),
            LocalAiOnboardingState.Use => S("LocalUse"),
            LocalAiOnboardingState.Repair => S("LocalRepair"),
            _ => snapshot.CanRefresh ? S("CheckAgain") : "",
        };
        var providerPending = _providerOperationActive || phase is GatewayAiSetupPhase.Running or GatewayAiSetupPhase.Uncertain or
            GatewayAiSetupPhase.VerificationRequired;
        LocalAiCard.IsClickEnabled = _localObservation is not null && !_localActionBusy && !providerPending &&
            (snapshot.CanReview && _args?.ReviewLocalAi is not null || snapshot.CanUse || snapshot.CanRefresh);
        LocalAiCard.IsEnabled = !_localActionBusy && !providerPending;
        AutomationProperties.SetHelpText(LocalAiCard, LocalAiDescription.Text + " " + LocalAiActionText.Text + " " +
            S("LocalExplanation"));
    }

    private bool CanConnectManual() =>
        !BackdropLocked &&
        _args?.ExpectedConfiguredModelRef is null && _localExpectedModel is null &&
        Client?.Phase is GatewayAiSetupPhase.Choosing or GatewayAiSetupPhase.Rejected &&
        ApiProviderPicker.SelectedItem is ChoiceRow { Kind: GatewayAiSetupChoiceKind.ManualProvider } row &&
        Client.Selection is { Kind: GatewayAiSetupChoiceKind.ManualProvider } selected &&
        selected.Id == row.Id && !string.IsNullOrWhiteSpace(ApiKeyInput.Password);

    private void Input_Changed(object sender, RoutedEventArgs e) => Render();
    private void Preference_Changed(object sender, RoutedEventArgs e) => Render();
    private static string S(string key) => SetupLocalization.GetString("Onboarding_AiSetup_" + key);
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void ShowError(string key)
    {
        if (BackdropLocked) _providerError = S(key);
        else
        {
            ErrorBar.Message = S(key);
            ErrorBar.IsOpen = true;
        }
        Render();
    }
    private void ReportFailure(Exception error)
    {
        Trace.TraceWarning("AI setup request failed ({0}).", error.GetType().Name);
        if (!_closed)
        {
            ShowError(error is LocalAiSelectionRejectedException ? "LocalChanged" :
                error is UnauthorizedAccessException ? "AdminRequired" :
                Client?.Phase == GatewayAiSetupPhase.Prepared ? "PreparedChanged" :
                _localExpectedModel is not null || Client?.RequiresReconciliation == true ? "Uncertain" : "Failed");
            Render();
        }
    }

    private async Task ReleaseAsync(CancellationToken ct = default)
    {
        if (_args?.NativeSession?.IsIsolated == true)
        {
            _nativeConsole?.Dispose();
            _nativeConsole = null;
        }
        var session = _session;
        var nativeConnection = _nativeConnection;
        var client = Client;
        _session = null;
        _nativeConnection = null;
        try
        {
            if (client?.SessionId is not null)
                await client.CancelAsync(ct).WaitAsync(CloseTimeout, ct);
        }
        catch (Exception ex) { Trace.TraceWarning("AI setup cancellation could not be confirmed ({0}).", ex.GetType().Name); }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
            if (nativeConnection is not null)
                await nativeConnection.DisposeAsync();
        }
    }

    private sealed record ChoiceRow(
        GatewayAiSetupChoiceKind Kind, string Id, string Label, string Detail, ProviderArtworkSession ArtworkSession,
        string? ModelRef = null, string? BrandId = null, string? Icon = null, string? ProviderKind = null,
        string? MetadataActionLabel = null, string? Website = null)
    {
        public Visibility DetailVisibility => Visible(!string.IsNullOrWhiteSpace(Detail));
        public ProviderArtworkDescriptor Artwork => GatewayAiSetupPresentation.GetProviderArtwork(
            BrandId, Id, Icon, GatewayAiSetupPresentation.GetProviderFallback(Kind, ProviderKind ?? Id));
        public string ActionLabel
        {
            get
            {
                if (Kind == GatewayAiSetupChoiceKind.Candidate)
                    return S("LocalUse");
                if (Kind == GatewayAiSetupChoiceKind.ManualProvider)
                    return "";
                if (!string.IsNullOrWhiteSpace(MetadataActionLabel))
                    return MetadataActionLabel;
                return S(GatewayAiSetupPresentation.GetProviderActionLabel(null, ProviderKind, Kind) switch
                {
                    "Pair" => "ArtworkAction_Pair",
                    "Set up…" => "ArtworkAction_Setup",
                    "Configure…" => "ArtworkAction_Configure",
                    "Connect / Set up" => "ArtworkAction_Prepare",
                    _ => "ArtworkAction_SignIn"
                });
            }
        }
    }
}
