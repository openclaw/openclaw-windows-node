namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayAiSetupLifecycleContractTests
{
    [Fact]
    public void VerifiedCompletion_ShowsOwnedChooserWithoutFinalizingOrIssuingHandoff()
    {
        var window = ReadSource("OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs");
        Assert.Contains("_startAtLocalAiRecoveryReview && _pinLocalAiRecoveryModel", window);
        Assert.Contains("? SetupCompletionIntent.Dashboard : SetupCompletionIntent.CustodianOnboarding", window);
        Assert.Contains("SetupGatewaySession.RequireCompletionGateway(_dataDir, completion)", window);
        Assert.Contains("GatewayDashboardBinding.Capture(active) != completion.EndpointBinding",
            ReadSource("OpenClaw.SetupEngine", "SetupGatewaySession.cs"));
        var ready = window[window.IndexOf("private Task CompleteVerifiedAiSetupAsync", StringComparison.Ordinal)..
            window.IndexOf("internal bool OwnsReadyChoice", StringComparison.Ordinal)];
        Assert.Contains("NavigateTo(typeof(AiReadyPage)", ready);
        Assert.Contains("new AiReadyPageArgs(_readyChoice, this)", ready);
        Assert.DoesNotContain("return CompleteSetupAsync()", ready);
        Assert.DoesNotContain(".Issue(", ready);
        Assert.DoesNotContain("await _aiPageCleanupTask", ready);
        var complete = window[window.IndexOf("private async Task CompleteSetupCoreAsync()", StringComparison.Ordinal)..
            window.IndexOf("internal void RefreshFlowProgress()", StringComparison.Ordinal)];
        Assert.DoesNotContain("_verifiedAiCompletion", window);
        Assert.Contains("RequestSetupCompleted(", complete);
        Assert.DoesNotContain("NavigateToComplete(true", complete);
    }

    [Fact]
    public void WindowAndNavigationCleanup_ShareIdempotentAwaitableCloseContract()
    {
        var page = ReadSource("OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs");
        Assert.Contains("Page, IAsyncDisposable", page);
        Assert.Contains("Unloaded += Page_Unloaded;", page);
        Assert.Contains("protected override void OnNavigatedFrom(NavigationEventArgs e)\n        => BeginClose();", page);
        Assert.Contains("AsyncEventHandlerGuard.Run(CloseAsync", page);
        Assert.Contains("public Task CloseAsync()", page);
        Assert.Contains("if (_closeTask is not null)\n            return _closeTask;", page);
        Assert.Contains("_closeTask = completion.Task;", page);
        Assert.Contains("public ValueTask DisposeAsync() => new(CloseAsync());", page);
        Assert.True(page.IndexOf("_closed = true;", StringComparison.Ordinal) <
            page.IndexOf("_request.Cancel();", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedPage_RejectsNewWorkLateInitializationAndCompletion()
    {
        var page = ReadSource("OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs");
        Assert.Contains("if (_closed || _busy)\n            return Task.CompletedTask;", page);
        Assert.Contains("if (_closed || ct.IsCancellationRequested)", page);
        Assert.Contains("ObjectDisposedException.ThrowIf(_closed, this);", page);
        Assert.Contains("if (!_closed && generation == _generation && Client?.Phase == GatewayAiSetupPhase.Verified)", page);
        Assert.Contains("await _activeRequest.WaitAsync(timeout.Token);", page);
        Assert.Contains("await client.CancelAsync(ct).WaitAsync(CloseTimeout, ct);", page);
        var session = ReadSource("OpenClaw.SetupEngine", "SetupGatewaySession.cs");
        Assert.Contains("ct.Register(static state => ((OpenClawGatewayClient)state!).Dispose(), client)", session);
        Assert.Contains("await client.ConnectAsync().WaitAsync(ct);", session);
        Assert.Contains("Client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5))", session);
        Assert.Contains("finally { Client.Dispose(); }", session);
    }

    [Fact]
    public void ConfiguredModelRetry_PrecedesGeneralPhaseRoutingAndPinsClientMode()
    {
        var page = ReadSource("OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs");
        Assert.Contains("new GatewayAiSetupClient(transport, _args.ExpectedConfiguredModelRef,", page);
        Assert.Contains("_args.ConfiguredCompletionIntent", page);
        var refresh = page[page.IndexOf("private Task RefreshAsync()", StringComparison.Ordinal)..
            page.IndexOf("private void Cancel_Click", StringComparison.Ordinal)];
        Assert.Contains("await Client.VerifyConfiguredAsync(expectedModel, ct);", refresh);
        Assert.True(refresh.IndexOf("_args!.ExpectedConfiguredModelRef", StringComparison.Ordinal) <
            refresh.IndexOf("Client.Phase == GatewayAiSetupPhase.Verified", StringComparison.Ordinal));
    }

    [Fact]
    public void ProviderSurface_HasOnePageOwnerAndNoProtocolOrPersistenceOwner()
    {
        var page = ReadSource("OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs");
        var xaml = ReadSource("OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml");
        var dialog = ReadSource("OpenClaw.SetupEngine.UI", "Controls", "ProviderSetupDialog.xaml.cs");
        Assert.Contains("private readonly ProviderSetupDialog _providerDialog = new();", page);
        Assert.DoesNotContain("WizardPanel", xaml);
        Assert.DoesNotContain("FeaturedChoices", xaml);
        Assert.DoesNotContain("MoreChoices", xaml);
        Assert.DoesNotContain("GatewayAiSetupClient", dialog);
        Assert.DoesNotContain("SetupGatewaySession", dialog);
        Assert.Contains("args.Cancel = true;", dialog);
        Assert.Contains("if (!_showTask.IsCompleted)", dialog);
        Assert.Contains("while (_requested && !_closed);", dialog);
        Assert.Contains("_closed = true;", dialog);
        Assert.Contains("SecretInput.Password = \"\";", dialog);
        Assert.Contains("TextInput.Text = \"\";", dialog);
        Assert.Contains("await dialogClose.WaitAsync(timeout.Token);", page);
        Assert.Contains("_providerDialog.CancelRequested -= ProviderCancelRequested;", page);
        Assert.Contains("if (_closed || _cancelling)", page);
        Assert.Contains("if (Client?.SessionId is null)", page);
        Assert.Contains("private bool ProviderPending => _providerOperationActive ||", page);
        Assert.Contains("var showProvider = ProviderPending", page);
        Assert.Contains("_controller!.StopAutomaticContinuation();", page);
        var completion = page[page.IndexOf("Client?.Phase == GatewayAiSetupPhase.Verified", StringComparison.Ordinal)..];
        Assert.True(completion.IndexOf("_providerDialog.Dismiss();", StringComparison.Ordinal) <
            completion.IndexOf("await complete(Client.GetVerifiedCompletion());", StringComparison.Ordinal));
        Assert.Contains("CompleteVerifiedSetup: CompleteVerifiedAiSetupAsync",
            ReadSource("OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
    }

    [Fact]
    public void ProviderBackdrop_IsRetainedAndLateFocusRestoreCannotCrossAnOperation()
    {
        var page = ReadSource("OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs");
        Assert.Contains("if (!freezeBackdrop)", page);
        Assert.Contains("if (!freezeBackdrop) RenderLocalAi(phase)", page);
        Assert.Contains("!_busy && !_localActionBusy && !BackdropLocked", page);
        Assert.Contains("_deferredDetection = detection", page);
        Assert.Contains("CaptureProviderBackdrop(returnFocus)", page);
        Assert.Contains("ChoicesScroller.VerticalOffset, ++_backdropVersion", page);
        Assert.Contains("await showing;", page);
        Assert.Contains("backdrop.Version != _backdropVersion", page);
        Assert.Contains("ChoicesScroller.ChangeView(null, backdrop.VerticalOffset, null, disableAnimation: true)", page);
        Assert.Contains("status, _providerError, _operationTitle", page);
        Assert.DoesNotContain("Frame.Navigate", page);
    }

    [Fact]
    public void ReceiptReconciliation_RetriesVerificationWhenNoWizardRemains()
    {
        var page = ReadSource("OpenClaw.SetupEngine.UI", "Pages", "AiSetupPage.xaml.cs");
        var refresh = page[page.IndexOf("else if (Client.RequiresReconciliation)", StringComparison.Ordinal)..
            page.IndexOf("private void Cancel_Click", StringComparison.Ordinal)];
        Assert.Contains("if (Client.SessionId is not null)\n                await Client.RefreshAsync(ct);", refresh);
        Assert.Contains("else\n            {\n                var verification = await Client.VerifyAsync(ct);", refresh);
        Assert.DoesNotContain("else if (Client.Phase == GatewayAiSetupPhase.Uncertain)", refresh);
    }

    private static string ReadSource(params string[] components)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        for (string? directory = AppContext.BaseDirectory; root is null && directory is not null;
             directory = Directory.GetParent(directory)?.FullName)
        {
            if (File.Exists(Path.Combine(directory, "openclaw-windows-node.slnx")))
                root = directory;
        }
        return File.ReadAllText(Path.Combine([root ?? throw new DirectoryNotFoundException("Repository root not found."),
            "src", .. components])).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
