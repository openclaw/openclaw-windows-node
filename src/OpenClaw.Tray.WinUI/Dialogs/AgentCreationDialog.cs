using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;

namespace OpenClawTray.Dialogs;

internal sealed class AgentCreationDialog : ContentDialog
{
    private readonly AgentCreationService _service;
    private readonly TextBox _name = new();
    private readonly TextBox _workspace = new();
    private readonly InfoBar _status = new() { IsClosable = false };
    private bool _creating;
    private bool _uncertain;

    private static string L(string key) => LocalizationHelper.GetString($"AgentCreation_{key}");

    public AgentCreationDialog(XamlRoot root, AgentCreationService service)
    {
        _service = service;
        XamlRoot = root;
        Title = L("Title");
        PrimaryButtonText = L("Create");
        CloseButtonText = LocalizationHelper.GetString("ChatPage_Cancel");
        DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(this, "AgentCreationDialog");
        _name.Header = L("Name");
        _workspace.Header = L("Workspace");
        AutomationProperties.SetAutomationId(_name, "AgentCreationName");
        AutomationProperties.SetAutomationId(_workspace, "AgentCreationWorkspace");
        Content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = L("Description"), TextWrapping = TextWrapping.Wrap },
                _name,
                _workspace,
                _status
            }
        };
        _name.TextChanged += (_, _) => UpdateAvailability();
        _workspace.TextChanged += (_, _) => UpdateAvailability();
        PrimaryButtonClick += OnCreate;
        Closing += (_, args) => args.Cancel = _creating;
        if (!_service.CanCreate)
        {
            _status.Message = L("PermissionRequired");
            _status.Severity = InfoBarSeverity.Warning;
            _status.IsOpen = true;
        }
        UpdateAvailability();
    }

    private void UpdateAvailability() => IsPrimaryButtonEnabled =
        !_creating && !_uncertain && _service.CanCreate &&
        !string.IsNullOrWhiteSpace(_name.Text) && !string.IsNullOrWhiteSpace(_workspace.Text);

    private void OnCreate(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        AsyncEventHandlerGuard.Run(() => CreateAsync(args, deferral), new AppLogger(), nameof(OnCreate));
    }

    private async Task CreateAsync(ContentDialogButtonClickEventArgs args, ContentDialogButtonClickDeferral deferral)
    {
        _creating = true;
        _name.IsEnabled = _workspace.IsEnabled = false;
        _status.Message = L("Creating");
        _status.Severity = InfoBarSeverity.Informational;
        _status.IsOpen = true;
        UpdateAvailability();
        try
        {
            await _service.CreateAsync(_name.Text, _workspace.Text);
            args.Cancel = false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Workspace] Agent creation failed: {ex.Message}");
            // A response may be lost after the gateway has committed. Do not offer a blind retry.
            _uncertain = true;
            _status.Message = $"{L("Failed")} {ex.Message}";
            _status.Severity = InfoBarSeverity.Error;
            _status.IsOpen = true;
        }
        finally
        {
            _creating = false;
            UpdateAvailability();
            deferral.Complete();
        }
    }
}
