using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

public sealed class ExecApprovalPromptService : IExecApprovalPromptHandler
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<FrameworkElement?> _rootProvider;
    private readonly IOpenClawLogger _logger;

    public ExecApprovalPromptService(
        DispatcherQueue dispatcherQueue,
        Func<FrameworkElement?> rootProvider,
        IOpenClawLogger logger)
    {
        _dispatcherQueue = dispatcherQueue;
        _rootProvider = rootProvider;
        _logger = logger;
    }

    public Task<ExecApprovalPromptDecision> RequestAsync(
        ExecApprovalPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ExecApprovalPromptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(ExecApprovalPromptDecision.Deny("Approval prompt was cancelled"));

        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var root = _rootProvider();
                if (root?.XamlRoot == null)
                {
                    _logger.Warn("[ExecApproval] Cannot show prompt because no XamlRoot is available");
                    tcs.TrySetResult(ExecApprovalPromptDecision.Deny("No desktop prompt surface is available"));
                    return;
                }

                var content = new StackPanel { Spacing = 8 };
                content.Children.Add(new TextBlock
                {
                    Text = "A remote agent wants to run a local command on this Windows machine.",
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new TextBlock
                {
                    Text = request.Command,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.WrapWholeWords
                });
                content.Children.Add(new TextBlock
                {
                    Text = $"Shell: {request.Shell ?? "auto"}\nReason: {request.Reason}",
                    TextWrapping = TextWrapping.Wrap
                });

                var dialog = new ContentDialog
                {
                    Title = "Approve local command?",
                    Content = content,
                    PrimaryButtonText = "Allow once",
                    SecondaryButtonText = "Always allow",
                    CloseButtonText = "Deny",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = root.XamlRoot
                };

                var result = await dialog.ShowAsync();
                var decision = result switch
                {
                    ContentDialogResult.Primary => ExecApprovalPromptDecision.AllowOnce(),
                    ContentDialogResult.Secondary => ExecApprovalPromptDecision.AlwaysAllow(),
                    _ => ExecApprovalPromptDecision.Deny()
                };

                _logger.Info($"[ExecApproval] Prompt decision: {decision.Kind}");
                tcs.TrySetResult(decision);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[ExecApproval] Prompt failed: {ex.Message}");
                tcs.TrySetResult(ExecApprovalPromptDecision.Deny("Approval prompt failed"));
            }
        }))
        {
            _logger.Warn("[ExecApproval] Failed to enqueue prompt on UI thread");
            tcs.TrySetResult(ExecApprovalPromptDecision.Deny("Unable to show approval prompt"));
        }

        return tcs.Task;
    }
}
