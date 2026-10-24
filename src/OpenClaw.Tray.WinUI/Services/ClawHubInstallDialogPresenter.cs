using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using System.Text;

namespace OpenClawTray.Services;

internal sealed class ClawHubInstallDialogPresenter(Func<XamlRoot?> getXamlRoot)
    : IClawHubInstallInteraction
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private ContentDialog? _progressDialog;
    private Task<ContentDialogResult>? _progressTask;

    public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken) =>
        ShowMessageAsync(title, message, "Close", cancellationToken);

    public async Task<bool> ConfirmAsync(
        PluginInspectionInfo inspection,
        CancellationToken cancellationToken)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = inspection.Name,
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(inspection.Description))
        {
            content.Children.Add(new TextBlock
            {
                Text = inspection.Description,
                TextWrapping = TextWrapping.Wrap
            });
        }

        content.Children.Add(CreateSection(
            "Declared capabilities",
            FormatCapabilities(inspection.DeclaredCapabilities, "No declared capabilities.")));
        content.Children.Add(CreateSection(
            "Operator grants",
            inspection.Grants.Count > 0
                ? string.Join(Environment.NewLine, inspection.Grants.Select(static grant => $"- {grant}"))
                : "No operator grants."));

        if (!string.IsNullOrWhiteSpace(inspection.TrustDisposition))
        {
            var trust = new StringBuilder(inspection.TrustDisposition);
            foreach (var reason in inspection.TrustReasons)
                trust.AppendLine().Append("- ").Append(reason);
            content.Children.Add(CreateSection("Trust review", trust.ToString()));
        }

        var dialog = CreateDialog(
            "Review ClawHub plugin",
            new ScrollViewer
            {
                Content = content,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
        dialog.PrimaryButtonText = "Install";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowDialogAsync(dialog, cancellationToken) == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmSkillAsync(
        string skillId,
        CancellationToken cancellationToken)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = skillId,
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text =
                "The connected Gateway will download this skill from ClawHub and " +
                "apply its install security policy before writing it to the agent workspace.",
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = CreateDialog("Install ClawHub skill?", content);
        dialog.PrimaryButtonText = "Install";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowDialogAsync(dialog, cancellationToken) == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmPackageAsync(
        string listingId,
        string packageName,
        CancellationToken cancellationToken)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = listingId,
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(CreateSection("ClawHub package", packageName));
        content.Children.Add(new TextBlock
        {
            Text =
                "This plugin is not in the Gateway's managed catalog. The Gateway will fetch it " +
                "from ClawHub, verify its identity and declared capabilities, and apply the " +
                "plugin install security policy before saving it.",
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = CreateDialog("Review ClawHub plugin", content);
        dialog.PrimaryButtonText = "Install";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowDialogAsync(dialog, cancellationToken) == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmInstallPolicyWarningAsync(
        PluginInstallPolicyWarning warning,
        CancellationToken cancellationToken)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = warning.TargetName,
            Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(CreateSection("Gateway security warning", warning.Reason));
        if (warning.Findings.Count > 0)
        {
            content.Children.Add(CreateSection(
                "Findings",
                string.Join(
                    Environment.NewLine,
                    warning.Findings.Select(static finding =>
                        $"- [{finding.Severity}] {finding.Message}"))));
        }

        var dialog = CreateDialog(
            "Install plugin despite security warning?",
            new ScrollViewer
            {
                Content = content,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
        dialog.PrimaryButtonText = "Install anyway";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowDialogAsync(dialog, cancellationToken) == ContentDialogResult.Primary;
    }

    public Task ShowInstallProgressAsync(
        PluginInspectionInfo inspection,
        CancellationToken cancellationToken) =>
        ShowInstallProgressCoreAsync(
            "Installing ClawHub plugin",
            inspection.Name,
            cancellationToken);

    public Task ShowSkillInstallProgressAsync(
        string skillId,
        CancellationToken cancellationToken) =>
        ShowInstallProgressCoreAsync(
            "Installing ClawHub skill",
            skillId,
            cancellationToken);

    public Task ShowPackageInstallProgressAsync(
        string packageName,
        CancellationToken cancellationToken) =>
        ShowInstallProgressCoreAsync(
            "Installing ClawHub plugin",
            packageName,
            cancellationToken);

    private async Task ShowInstallProgressCoreAsync(
        string title,
        string displayName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _dialogGate.WaitAsync(cancellationToken);

        try
        {
            if (_progressDialog is not null)
                throw new InvalidOperationException("A ClawHub install progress dialog is already open.");

            var panel = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(new ProgressRing { IsActive = true, Width = 36, Height = 36 });
            panel.Children.Add(new TextBlock
            {
                Text = $"Installing {displayName}...",
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            var progressDialog = CreateDialog(title, panel);
            progressDialog.Closing += (_, args) =>
            {
                if (ReferenceEquals(_progressDialog, progressDialog))
                    args.Cancel = true;
            };
            _progressDialog = progressDialog;
            _progressTask = progressDialog.ShowAsync().AsTask();
        }
        catch
        {
            _progressDialog = null;
            _progressTask = null;
            _dialogGate.Release();
            throw;
        }
    }

    public async Task HideInstallProgressAsync()
    {
        var dialog = _progressDialog;
        var progressTask = _progressTask;
        _progressDialog = null;
        _progressTask = null;
        if (dialog is null || progressTask is null)
            return;

        try
        {
            dialog.Hide();
            await progressTask;
            await WaitForDialogReleaseAsync(dialog);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    public Task ShowSuccessAsync(
        PluginInspectionInfo inspection,
        PluginInstallResult result,
        CancellationToken cancellationToken)
    {
        var message = new StringBuilder($"{inspection.Name} was installed successfully.");
        if (result.RestartRequired)
            message.Append(" Restart the Gateway to activate all plugin changes.");
        foreach (var warning in result.Warnings)
            message.AppendLine().Append("Warning: ").Append(warning);
        return ShowMessageAsync("Plugin installed", message.ToString(), "Done", cancellationToken);
    }

    public Task ShowSkillSuccessAsync(
        string skillId,
        ClawHubSkillInstallResult result,
        CancellationToken cancellationToken)
    {
        var message = new StringBuilder(
            $"{skillId} version {result.Version} was installed successfully.");
        if (!string.IsNullOrWhiteSpace(result.Warning))
            message.AppendLine().Append("Warning: ").Append(result.Warning);
        return ShowMessageAsync("Skill installed", message.ToString(), "Done", cancellationToken);
    }

    public Task ShowPackageSuccessAsync(
        string packageName,
        PluginInstallResult result,
        CancellationToken cancellationToken)
    {
        var message = new StringBuilder($"{packageName} was installed successfully.");
        if (result.RestartRequired)
            message.Append(" Restart the Gateway to activate all plugin changes.");
        foreach (var warning in result.Warnings)
            message.AppendLine().Append("Warning: ").Append(warning);
        return ShowMessageAsync("Plugin installed", message.ToString(), "Done", cancellationToken);
    }

    private async Task ShowMessageAsync(
        string title,
        string message,
        string closeButtonText,
        CancellationToken cancellationToken)
    {
        var dialog = CreateDialog(title, new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });
        dialog.CloseButtonText = closeButtonText;
        await ShowDialogAsync(dialog, cancellationToken);
    }

    private ContentDialog CreateDialog(string title, object content)
    {
        var xamlRoot = getXamlRoot();
        if (xamlRoot is null)
            throw new InvalidOperationException("Windows Hub is not ready to show the ClawHub install dialog.");

        return new ContentDialog
        {
            Title = title,
            Content = content,
            XamlRoot = xamlRoot
        };
    }

    private async Task<ContentDialogResult> ShowDialogAsync(
        ContentDialog dialog,
        CancellationToken cancellationToken)
    {
        await _dialogGate.WaitAsync(cancellationToken);
        try
        {
            using var registration = cancellationToken.Register(dialog.Hide);
            var result = await dialog.ShowAsync();
            await WaitForDialogReleaseAsync(dialog);
            return result;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static Task WaitForDialogReleaseAsync(ContentDialog dialog)
    {
        var released = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dialog.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                released.SetResult))
        {
            throw new InvalidOperationException(
                "Windows Hub could not finish closing the ClawHub install dialog.");
        }
        return released.Task;
    }

    private static FrameworkElement CreateSection(string heading, string body)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = heading,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private static string FormatCapabilities(
        IReadOnlyList<PluginCapabilityGroup> capabilities,
        string emptyText)
    {
        if (capabilities.Count == 0)
            return emptyText;

        return string.Join(
            Environment.NewLine,
            capabilities.Select(group => $"{group.Name}: {string.Join(", ", group.Values)}"));
    }
}
