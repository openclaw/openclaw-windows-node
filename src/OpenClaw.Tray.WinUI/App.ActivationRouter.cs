using Microsoft.UI.Xaml.Controls;
using Microsoft.Toolkit.Uwp.Notifications;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray;

/// <summary>
/// App's single <see cref="IActivationPlanSink"/> implementation: the one typed switch that
/// applies an <see cref="ActivationRoute"/> planned by <see cref="ActivationRouter"/> to the
/// existing A2 owners and services. Never builds or interprets a route itself; never touches the
/// current-user IPC listener or the launch/toast argument tables owned by ActivationRouter.
/// </summary>
public partial class App : IActivationPlanSink
{
    private ActivationRouter? _activationRouter;

    async Task IActivationPlanSink.DispatchAsync(ActivationRoute route, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => tcs.TrySetCanceled(cancellationToken));
        var queued = _dispatcherQueue?.TryEnqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                ApplyActivationRoute(route);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }) ?? false;

        if (!queued)
        {
            Logger.Warn("Activation route dropped: UI thread unavailable.");
            tcs.TrySetResult(false);
        }

        await tcs.Task.ConfigureAwait(false);
    }

    async Task<bool> IActivationPlanSink.ConfirmAsync(
        ActivationConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ContentDialog? dialog = null;
        using var registration = cancellationToken.Register(() =>
        {
            var queuedCancellation = _dispatcherQueue?.TryEnqueue(() =>
            {
                dialog?.Hide();
                tcs.TrySetResult(false);
            }) ?? false;
            if (!queuedCancellation)
                tcs.TrySetResult(false);
        });

        var queued = _dispatcherQueue?.TryEnqueue(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetResult(false);
                return;
            }

            try
            {
                var xamlRoot = _windowManager?.RuntimeAnchorXamlRoot;
                if (xamlRoot == null)
                {
                    Logger.Warn($"Cannot confirm deep link action without XAML root: {confirmation.RedactedInput}");
                    tcs.SetResult(false);
                    return;
                }

                dialog = new ContentDialog
                {
                    Title = "Confirm OpenClaw action",
                    Content = $"A deep link wants to {confirmation.ActionDisplayName}.",
                    PrimaryButtonText = "Allow",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot
                };
                var dialogResult = await dialog.ShowAsync();
                tcs.TrySetResult(
                    !cancellationToken.IsCancellationRequested &&
                    dialogResult == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    tcs.TrySetResult(false);
                else
                    tcs.TrySetException(ex);
            }
        }) ?? false;

        if (!queued)
            tcs.TrySetResult(false);

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// The single App-owned route switch. Every <see cref="ActivationRoute"/> case produced by
    /// <see cref="DeepLinkHandler.PlanRoute"/> or <see cref="ToastActivationRouter.PlanRoute"/>
    /// must be applied here and nowhere else.
    /// </summary>
    private void ApplyActivationRoute(ActivationRoute route)
    {
        switch (route)
        {
            case ActivationRoute.OpenHub r:
                ShowHub(r.Page);
                break;
            case ActivationRoute.OpenSetup:
                _ = ShowOnboardingAsync();
                break;
            case ActivationRoute.OpenDashboard r:
                OpenDashboard(r.Path);
                break;
            case ActivationRoute.OpenChat r:
                ShowWebChat(r.SessionKey);
                break;
            case ActivationRoute.OpenUrl r:
                try
                {
                    Process.Start(new ProcessStartInfo(r.Uri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Logger.Warn($"App: Toast activation failed to open URL '{SanitizeToastUrlForLog(r.Uri)}': {ex.Message}");
                }
                break;
            case ActivationRoute.OpenTrayMenu:
                _trayController?.ShowMenu();
                break;
            case ActivationRoute.OpenLogFile:
                OpenLogFile();
                break;
            case ActivationRoute.OpenLogFolder:
                OpenLogFolder();
                break;
            case ActivationRoute.OpenConfigFolder:
                OpenConfigFolder();
                break;
            case ActivationRoute.OpenDiagnosticsFolder:
                OpenDiagnosticsFolder();
                break;
            case ActivationRoute.CopyDiagnostics r:
                ApplyCopyDiagnostics(r.Kind);
                break;
            case ActivationRoute.CopyPairingCommand r:
                CopyTextToClipboard(r.Command);
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_PairingCommandCopied"))
                    .AddText(r.Command));
                break;
            case ActivationRoute.ReviewPairing:
                ShowPairingApprovalDialog();
                break;
            case ActivationRoute.RestartSshTunnel:
                RestartSshTunnel();
                break;
            case ActivationRoute.RunHealthCheck:
                _ = RunActivationActionAsync("health check", () => Task.Run(() => RunHealthCheckAsync(userInitiated: true)));
                break;
            case ActivationRoute.CheckForUpdates:
                _ = RunActivationActionAsync("update check", _updateCoordinator!.CheckForUpdatesUserInitiatedAsync);
                break;
            case ActivationRoute.OpenVoice:
                ShowHub("voice");
                break;
            case ActivationRoute.StopVoice:
                _ = StopVoiceAsync();
                break;
            case ActivationRoute.SendMessage r:
                var client = _connectionManager?.OperatorClient;
                if (client != null)
                {
                    _ = RunActivationActionAsync("agent message", async () =>
                    {
                        await client.SendChatMessageAsync(r.Message);
                        Logger.Info("ActivationRouter: Sent message via deep link");
                    });
                }
                else
                {
                    Logger.Warn("Deep link: agent message received but SendMessage handler is not registered");
                }
                break;
        }
    }

    private void ApplyCopyDiagnostics(DiagnosticsCopyKind kind)
    {
        switch (kind)
        {
            case DiagnosticsCopyKind.SupportContext:
                _diagnosticsClipboard!.CopySupportContext();
                break;
            case DiagnosticsCopyKind.DebugBundle:
                _diagnosticsClipboard!.CopyDebugBundle();
                break;
            case DiagnosticsCopyKind.BrowserSetupGuidance:
                _diagnosticsClipboard!.CopyBrowserSetupGuidance();
                break;
            case DiagnosticsCopyKind.PortDiagnostics:
                _diagnosticsClipboard!.CopyPortDiagnostics();
                break;
            case DiagnosticsCopyKind.CapabilityDiagnostics:
                _diagnosticsClipboard!.CopyCapabilityDiagnostics();
                break;
            case DiagnosticsCopyKind.NodeInventory:
                _diagnosticsClipboard!.CopyNodeInventory();
                break;
            case DiagnosticsCopyKind.ChannelSummary:
                _diagnosticsClipboard!.CopyChannelSummary();
                break;
            case DiagnosticsCopyKind.ActivitySummary:
                _diagnosticsClipboard!.CopyActivitySummary();
                break;
            case DiagnosticsCopyKind.ExtensibilitySummary:
                _diagnosticsClipboard!.CopyExtensibilitySummary();
                break;
        }
    }

    private static async Task RunActivationActionAsync(string actionName, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"ActivationRouter: activation {actionName} failed: {ex.Message}");
        }
    }
}
