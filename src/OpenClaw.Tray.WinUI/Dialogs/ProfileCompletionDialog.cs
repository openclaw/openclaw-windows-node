using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using Windows.Storage.Streams;

namespace OpenClawTray.Dialogs;

internal enum ProfileCompletionOutcome { Pending, Saved, Skipped }

/// <summary>
/// Optional "Your profile" step. Shows the Windows account name and photo as an editable preview;
/// nothing is sent to the Gateway until Save, and Skip sends nothing.
/// </summary>
internal sealed class ProfileCompletionDialog : ContentDialog
{
    private readonly ProfileCompletionService _service;
    private readonly ProfilePreparation _preparation;
    private readonly TextBox _name = new() { MaxLength = ProfileNamePolicy.MaxLength };
    private readonly CheckBox _usePhoto = new();
    private readonly Image _preview = new() { Width = 64, Height = 64, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
    private readonly InfoBar _status = new() { IsClosable = false };
    private readonly CancellationTokenSource _cancellation = new();
    private InMemoryRandomAccessStream? _previewStream;
    private bool _saving;
    private bool _nameDone;
    private bool _photoDone;

    public ProfileCompletionOutcome Outcome { get; private set; }

    private static string L(string key) => LocalizationHelper.GetString($"ProfileCompletion_{key}");

    public ProfileCompletionDialog(XamlRoot root, ProfileCompletionService service, ProfilePreparation preparation)
    {
        _service = service;
        _preparation = preparation;
        XamlRoot = root;
        if (root.Content is FrameworkElement content)
            RequestedTheme = content.ActualTheme;
        Title = L("Title");
        PrimaryButtonText = L("Save");
        CloseButtonText = L("Skip");
        DefaultButton = ContentDialogButton.Primary;
        AutomationProperties.SetAutomationId(this, "ProfileCompletionDialog");

        _name.Header = L("Name");
        _name.Text = preparation.Account?.DisplayName ?? "";
        AutomationProperties.SetAutomationId(_name, "ProfileCompletionName");

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = L("Description"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_name);

        var photo = preparation.Account?.Photo;
        if (photo is not null)
        {
            _usePhoto.Content = L("UsePhoto");
            _usePhoto.IsChecked = false;
            AutomationProperties.SetAutomationId(_usePhoto, "ProfileCompletionUsePhoto");
            AutomationProperties.SetName(_preview, L("PhotoPreview"));
            AutomationProperties.SetAutomationId(_preview, "ProfileCompletionPhotoPreview");
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { _preview, _usePhoto },
            });
            _ = ShowPreviewAsync(photo);
        }

        panel.Children.Add(_status);
        Content = panel;
        ReportAvailability();

        _name.TextChanged += (_, _) => UpdateAvailability();
        _usePhoto.Click += (_, _) => UpdateAvailability();
        PrimaryButtonClick += OnSave;
        CloseButtonClick += (_, _) => { if (Outcome == ProfileCompletionOutcome.Pending) Outcome = ProfileCompletionOutcome.Skipped; };
        Closing += (_, args) =>
        {
            if (_saving) { args.Cancel = true; return; }
            _cancellation.Cancel();
            ReleasePreview();
        };
        UpdateAvailability();
    }

    private async Task ShowPreviewAsync(AccountPhotoDraft photo)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(photo.Bytes.AsBuffer());
            stream.Seek(0);
            if (_cancellation.IsCancellationRequested) { stream.Dispose(); return; }
            _previewStream = stream;
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            _preview.Source = image;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or ObjectDisposedException)
        {
            _usePhoto.IsEnabled = false;
        }
    }

    private void ReleasePreview()
    {
        _preview.Source = null;
        _previewStream?.Dispose();
        _previewStream = null;
    }

    private void ReportAvailability()
    {
        var account = _preparation.Account;
        var message = !_preparation.CanWrite ? L("PermissionRequired")
            : account is null ? null
            : account.NameStatus != AccountFieldStatus.Available && account.PhotoStatus != AccountFieldStatus.Available ? L("AccountUnavailable")
            : account.PhotoStatus != AccountFieldStatus.Available ? L("PhotoUnavailable")
            : account.NameStatus != AccountFieldStatus.Available ? L("NameUnavailable")
            : null;
        if (message is null) return;
        _status.Message = message;
        _status.Severity = _preparation.CanWrite ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        _status.IsOpen = true;
    }

    private bool WantsPhoto => _usePhoto.IsChecked == true && _preparation.Account?.Photo is not null;

    private void UpdateAvailability()
    {
        var hasName = ProfileNamePolicy.TryNormalize(_name.Text, out _);
        var nameWanted = hasName && !_nameDone;
        var photoWanted = WantsPhoto && !_photoDone;
        IsPrimaryButtonEnabled = !_saving && _preparation.CanWrite && (nameWanted || photoWanted) &&
            (string.IsNullOrWhiteSpace(_name.Text) || hasName);
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        AsyncEventHandlerGuard.Run(() => SaveAsync(args, deferral), new AppLogger(), nameof(OnSave));
    }

    private async Task SaveAsync(ContentDialogButtonClickEventArgs args, ContentDialogButtonClickDeferral deferral)
    {
        _saving = true;
        _name.IsEnabled = _usePhoto.IsEnabled = false;
        _status.Message = L("Saving");
        _status.Severity = InfoBarSeverity.Informational;
        _status.IsOpen = true;
        UpdateAvailability();
        try
        {
            var nameWanted = ProfileNamePolicy.TryNormalize(_name.Text, out var name) && !_nameDone;
            var photoWanted = WantsPhoto && !_photoDone;
            var result = await _service.SaveAsync(
                _preparation, nameWanted ? name : null, photoWanted ? _preparation.Account!.Photo : null,
                retryNameOnly: _photoDone, retryPhotoOnly: _nameDone, _cancellation.Token);

            _nameDone |= result.Name is ProfileStep.Saved or ProfileStep.ExistingValue;
            _photoDone |= result.Photo is ProfileStep.Saved or ProfileStep.ExistingValue;
            if (result.IsComplete)
            {
                Outcome = ProfileCompletionOutcome.Saved;
                args.Cancel = false;
                return;
            }

            // Name and photo are separate Gateway writes: report each truthfully and offer a retry of only what is missing.
            Outcome = _nameDone || _photoDone ? ProfileCompletionOutcome.Saved : Outcome;
            _status.Message = Describe(result);
            _status.Severity = InfoBarSeverity.Error;
        }
        catch (OperationCanceledException)
        {
            args.Cancel = false;
        }
        finally
        {
            _saving = false;
            _name.IsEnabled = !_nameDone;
            _usePhoto.IsEnabled = !_photoDone && _preparation.Account?.Photo is not null;
            UpdateAvailability();
            deferral.Complete();
        }
    }

    private static string Describe(ProfileSaveResult result)
    {
        if (result.Name == ProfileStep.ContextChanged || result.Photo == ProfileStep.ContextChanged)
            return L("ContextChanged");
        var nameOk = result.Name is ProfileStep.Saved or ProfileStep.ExistingValue;
        var photoOk = result.Photo is ProfileStep.Saved or ProfileStep.ExistingValue;
        if (result.Name == ProfileStep.ConnectionLost || result.Photo == ProfileStep.ConnectionLost)
            return nameOk || photoOk ? L("ConnectionLostPartial") : L("ConnectionLost");
        if (nameOk && !photoOk) return L("NameSavedPhotoFailed");
        if (photoOk && !nameOk) return L("PhotoSavedNameFailed");
        return result.Name == ProfileStep.Denied || result.Photo == ProfileStep.Denied ? L("PermissionRequired") : L("Failed");
    }
}
