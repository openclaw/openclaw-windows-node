using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OpenClawTray.Pages;

public sealed partial class SandboxPage : Page
{
    private static App CurrentApp => (App)Application.Current;
    private static string L(string key) => LocalizationHelper.GetString(key);
    private bool _suppress;
    private bool _dialogOpen;
    private bool _probeInFlight;
    private MxcAvailability? _availability;
    private MxcRequestContext? _folderContext;
    public ObservableCollection<CustomFolderRow> CustomFolders { get; } = new();

    public SandboxPage()
    {
        InitializeComponent();
        CustomFoldersList.ItemsSource = CustomFolders;
    }

    public void Initialize()
    {
        LoadState();
        AsyncEventHandlerGuard.Run(RefreshAvailabilityAsync, new AppLogger(), nameof(RefreshAvailabilityAsync));
    }

    private async Task RefreshAvailabilityAsync()
    {
        if (_probeInFlight || _availability is { ProbeErrored: false }) return;
        _probeInFlight = true;
        try
        {
            var result = await Task.Run(() => (Availability: MxcAvailability.Probe(),
                Folders: MxcRequestContext.Capture(SettingsManager.SettingsDirectoryPath)));
            _availability = result.Availability;
            _folderContext = result.Folders;
        }
        catch (Exception ex)
        {
            new AppLogger().Warn($"[mxc] operation=ui-probe-failed error={ex.GetType().Name}");
            _availability = new(false, false, false, [L("SandboxPage_ProbeErrorReason")], probeErrored: true);
        }
        finally { _probeInFlight = false; UpdateStatus(); }
    }

    private void LoadState()
    {
        if (CurrentApp.Settings is not { } settings) return;
        _suppress = true;
        try
        {
            ScopeCombo.SelectedItem = ScopeCombo.Items.OfType<ComboBoxItem>().First(i =>
                (string)i.Tag == (settings.SystemRunFilesystemScope ?? SystemRunFilesystemScope.SelectedFolders).ToString());
            NetInternetToggle.IsOn = settings.SystemRunAllowOutbound;
            WindowsUiToggle.IsOn = settings.SystemRunAllowWindowsUi;
            SelectAccess(DocsAccessCombo, settings.SandboxDocumentsAccess);
            SelectAccess(DownloadsAccessCombo, settings.SandboxDownloadsAccess);
            SelectAccess(DesktopAccessCombo, settings.SandboxDesktopAccess);
            CustomFolders.Clear();
            foreach (var folder in settings.SandboxCustomFolders)
                CustomFolders.Add(new(folder.Path, folder.Access));
            RefreshCustomFoldersUi();
            (settings.SandboxClipboard switch
            {
                SandboxClipboardMode.Read => ClipboardReadRadio,
                SandboxClipboardMode.Write => ClipboardWriteRadio,
                SandboxClipboardMode.Both => ClipboardBothRadio,
                _ => ClipboardNoneRadio,
            }).IsChecked = true;
            var seconds = Math.Clamp(settings.SandboxTimeoutMs / 1000, 5, 300);
            TimeoutSlider.Value = seconds;
            TimeoutLabel.Text = LocalizationHelper.Format("SandboxPage_TimeoutFormat", seconds);
            MaxOutputCombo.SelectedItem = MaxOutputCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(
                i => long.TryParse((string?)i.Tag, out var value) && value == settings.SandboxMaxOutputBytes) ?? MaxOutputCombo.Items[1];
        }
        finally { _suppress = false; }
        WindowsUiWarningBar.IsOpen = WindowsUiToggle.IsOn;
        UpdatePresetHighlight();
        UpdateStatus();
        PresetCustomFoldersWarning.IsOpen = settings.SandboxCustomFolders.Any(f => f.Access == SandboxFolderAccess.Blocked) ||
            settings.SandboxDocumentsAccess == SandboxFolderAccess.Blocked ||
            settings.SandboxDownloadsAccess == SandboxFolderAccess.Blocked ||
            settings.SandboxDesktopAccess == SandboxFolderAccess.Blocked;
        if (PresetCustomFoldersWarning.IsOpen)
        {
            PresetCustomFoldersWarning.Title = L("SandboxPage_LegacyBlockedTitle");
            PresetCustomFoldersWarning.Message = L("SandboxPage_LegacyBlockedText");
        }
    }

    private void UpdateStatus()
    {
        var userFiles = CurrentApp.Settings?.SystemRunFilesystemScope is SystemRunFilesystemScope.UserFilesReadOnly or SystemRunFilesystemScope.UserFilesReadWrite;
        var unsupported = _availability?.IsWindowsUnsupported == true;
        var resolutionFailed = userFiles && _folderContext?.UserFolders.Any(f => f.Error is not null) == true;
        if (unsupported)
        {
            SandboxStatusTitle.Text = L("SandboxPage_WindowsCompatibilityTitle");
            SandboxStatusSubtext.Text = L("SandboxPage_WindowsCompatibilityText");
        }
        else if (resolutionFailed)
        {
            SandboxStatusTitle.Text = L("SandboxPage_ScopeResolutionBlockedTitle");
            SandboxStatusSubtext.Text = L("SandboxPage_ScopeResolutionBlockedText");
        }
        else
        {
            SandboxStatusTitle.Text = L(_availability is null ? "SandboxPage_CheckingTitle" :
                _availability.CanRunSystemRunSandbox ? "SandboxPage_StatusOnTitle" : "SandboxPage_StatusUnavailableBlockedTitle");
            SandboxStatusSubtext.Text = L(_availability is null ? "SandboxPage_CheckingText" :
                _availability.CanRunSystemRunSandbox ? "SandboxPage_StatusOnSubtext" : "SandboxPage_StatusUnavailableBlockedSubtext");
        }
        ScopeDetails.Visibility = userFiles ? Visibility.Visible : Visibility.Collapsed;
        ScopeDetails.Text = _folderContext is null ? L("SandboxPage_CheckingUserFolders") :
            L("SandboxPage_IncludedUserFolders") + "\n" +
            string.Join("\n", _folderContext.UserFolders.Where(f => f.Path is not null).Select(f => f.Path)) +
            (_folderContext.UserFolders.Any(f => f.Name == "OneDrive" && f.Path is null && f.Error is null)
                ? "\n" + L("SandboxPage_OneDriveNotIncluded") : "") +
            string.Join("", _folderContext.UserFolders.Where(f => f.Error is not null)
                .Select(f => "\n" + f.Name + ": " + f.Error));
        UnavailableActionBar.IsOpen = unsupported || resolutionFailed || _availability is { CanRunSystemRunSandbox: false };
        UnavailableActionMessage.Text = unsupported ? L("SandboxPage_WindowsCompatibilityText") :
            resolutionFailed ? L("SandboxPage_ScopeResolutionBlockedText") :
            string.Join(" ", _availability?.SystemRunSandboxUnsupportedReasons ?? []);
        UnavailablePrimaryButton.Content = L(unsupported ? "SandboxPage_OpenWindowsUpdate" : "SandboxPage_Retry");
        UnavailablePrimaryButton.Tag = unsupported ? "windowsupdate" : "retry";
        UnavailablePrimaryButton.Visibility = Visibility.Visible;
        // Permission editing remains available when a probe fails. Probing never mutates saved grants.
        SandboxControlsContainer.IsHitTestVisible = true;
        SandboxControlsContainer.Opacity = 1;
        PresetCard.IsHitTestVisible = true;
        PresetCard.Opacity = 1;
        SandboxPermissionsControls.IsEnabled = !unsupported;
        PresetLockedButton.IsEnabled = !unsupported;
        PresetBalancedButton.IsEnabled = !unsupported;
        PresetPermissiveButton.IsEnabled = !unsupported;
        SandboxControlsContainer.Opacity = unsupported ? 0.45 : 1;
        PresetCard.Opacity = unsupported ? 0.45 : 1;
    }

    private void OnUnavailableActionClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(async () =>
        {
            if ((sender as Button)?.Tag as string == "windowsupdate")
            {
                await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:windowsupdate"));
                return;
            }
            _availability = null;
            _folderContext = null;
            UpdateStatus();
            await RefreshAvailabilityAsync();
        }, new AppLogger(), nameof(OnUnavailableActionClick));

    private void ApplyPreset(SystemRunAccessPreset preset)
    {
        if (CurrentApp.Settings is not { } settings) return;
        settings.ApplySystemRunPreset(preset);
        settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        PresetCustomFoldersWarning.IsOpen = false;
        LoadState();
    }

    private void OnPresetLockedClick(object sender, RoutedEventArgs e) => RequestPreset(SystemRunAccessPreset.Strict);
    private void OnPresetBalancedClick(object sender, RoutedEventArgs e) => RequestPreset(SystemRunAccessPreset.Balanced);
    private void OnPresetPermissiveClick(object sender, RoutedEventArgs e) => RequestPreset(SystemRunAccessPreset.Open);

    private void RequestPreset(SystemRunAccessPreset preset) =>
        AsyncEventHandlerGuard.Run(() => ConfirmPresetAsync(preset), new AppLogger(), nameof(ConfirmPresetAsync));

    private async Task ConfirmPresetAsync(SystemRunAccessPreset preset)
    {
        if (_availability?.IsWindowsUnsupported == true || _dialogOpen || CurrentApp.Settings is not { } settings ||
            SystemRunPermissionPresets.Detect(settings.SnapshotSystemRunSettings()) == preset) return;
        var dialog = new ContentDialog
        {
            Title = L($"SandboxPage_Preset{preset}.Text"),
            Content = L($"SandboxPage_Preset{preset}Text.Text") + "\n\n" + L("SandboxPage_PresetHelp.Text"),
            PrimaryButtonText = L("SandboxPage_ApplyPreset"),
            CloseButtonText = L("SandboxPage_AllowWindowsUiDialogCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ApplyPreset(preset);
        }
        finally { _dialogOpen = false; }
    }

    private void UpdatePresetHighlight()
    {
        if (CurrentApp.Settings is not { } settings) return;
        var preset = SystemRunPermissionPresets.Detect(settings.SnapshotSystemRunSettings());
        SetPresetCardActive(PresetLockedButton, preset == SystemRunAccessPreset.Strict);
        SetPresetCardActive(PresetBalancedButton, preset == SystemRunAccessPreset.Balanced);
        SetPresetCardActive(PresetPermissiveButton, preset == SystemRunAccessPreset.Open);
        CustomPresetLabel.Visibility = preset is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetPresetCardActive(Button button, bool active)
    {
        button.BorderThickness = new Thickness(active ? 2 : 1);
        var accent = Application.Current.Resources["AccentFillColorDefaultBrush"];
        var stroke = Application.Current.Resources["CardStrokeColorDefaultBrush"];
        button.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)(active ? accent : stroke);
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            var borderKey = "ButtonBorderBrush" + state;
            var backgroundKey = "ButtonBackground" + state;
            if (active)
            {
                button.Resources[borderKey] = accent;
                button.Resources[backgroundKey] = Application.Current.Resources["ControlFillColorDefaultBrush"];
            }
            else { button.Resources.Remove(borderKey); button.Resources.Remove(backgroundKey); }
        }
        VisualStateManager.GoToState(button, "Normal", false);
    }

    private static void SelectAccess(ComboBox combo, SandboxFolderAccess? access) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag ==
            (access is SandboxFolderAccess.ReadOnly or SandboxFolderAccess.ReadWrite ? access.ToString() : "None"));

    private static SandboxFolderAccess? ReadAccess(ComboBox combo) =>
        Enum.TryParse<SandboxFolderAccess>((string?)((ComboBoxItem?)combo.SelectedItem)?.Tag, out var access) ? access : null;

    private void Save()
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SystemRunFilesystemScope ??= SystemRunFilesystemScope.SelectedFolders;
        settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        UpdatePresetHighlight();
        UpdateStatus();
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        if (Enum.TryParse<SystemRunFilesystemScope>((string?)((ComboBoxItem?)ScopeCombo.SelectedItem)?.Tag, out var scope))
        { settings.SystemRunFilesystemScope = scope; Save(); }
    }

    private void OnNetInternetToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SystemRunAllowOutbound = NetInternetToggle.IsOn;
        Save();
    }

    private void OnWindowsUiToggled(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(OnWindowsUiToggledAsync, new AppLogger(), nameof(OnWindowsUiToggled));

    private async Task OnWindowsUiToggledAsync()
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        var value = WindowsUiToggle.IsOn;
        if (value && !settings.SystemRunAllowWindowsUi)
        {
            if (_dialogOpen) { RestoreWindowsUi(false); return; }
            var dialog = new ContentDialog
            {
                Title = L("SandboxPage_AllowWindowsUiDialogTitle"),
                Content = L("SandboxPage_AllowWindowsUiDialogContent"),
                PrimaryButtonText = L("SandboxPage_AllowWindowsUiDialogPrimary"),
                CloseButtonText = L("SandboxPage_AllowWindowsUiDialogCancel"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot,
            };
            _dialogOpen = true;
            try { if (await dialog.ShowAsync() != ContentDialogResult.Primary) { RestoreWindowsUi(false); return; } }
            catch (System.Runtime.InteropServices.COMException) { RestoreWindowsUi(false); return; }
            finally { _dialogOpen = false; }
        }
        settings.SystemRunAllowWindowsUi = value;
        WindowsUiWarningBar.IsOpen = value;
        Save();
    }

    private void RestoreWindowsUi(bool value)
    {
        _suppress = true;
        try { WindowsUiToggle.IsOn = value; }
        finally { _suppress = false; }
        WindowsUiWarningBar.IsOpen = value;
    }

    private void OnDocsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SandboxDocumentsAccess = ReadAccess(DocsAccessCombo); Save();
    }
    private void OnDownloadsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SandboxDownloadsAccess = ReadAccess(DownloadsAccessCombo); Save();
    }
    private void OnDesktopAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SandboxDesktopAccess = ReadAccess(DesktopAccessCombo); Save();
    }
    private void RefreshCustomFoldersUi()
    {
        CustomFoldersList.Visibility = CustomFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CustomFoldersEmpty.Visibility = CustomFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnAddCustomFolder(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(PickCustomFolderAsync, new AppLogger(), nameof(OnAddCustomFolder));
    private async Task PickCustomFolderAsync()
    {
        if (CurrentApp.ActiveHubWindow is not { } window) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || CustomFolders.Any(f => f.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase))) return;
        CustomFolders.Add(new(folder.Path, SandboxFolderAccess.ReadOnly));
        if (CurrentApp.Settings is { } settings)
            settings.SandboxCustomFolders = settings.SandboxCustomFolders.Append(
                new SandboxCustomFolder { Path = folder.Path, Access = SandboxFolderAccess.ReadOnly }).ToList();
        RefreshCustomFoldersUi();
        Save();
    }
    private void OnCustomFolderAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || sender is not ComboBox combo || combo.DataContext is not CustomFolderRow row ||
            CurrentApp.Settings is not { } settings) return;
        if (!row.InitialSelectionFired) { row.InitialSelectionFired = true; return; }
        var folders = settings.SandboxCustomFolders;
        if (combo.SelectedIndex == 0)
        {
            settings.SandboxCustomFolders = folders.Where(f => !f.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            CustomFolders.Remove(row); RefreshCustomFoldersUi();
        }
        else
        {
            var access = combo.SelectedIndex == 2 ? SandboxFolderAccess.ReadWrite : SandboxFolderAccess.ReadOnly;
            settings.SandboxCustomFolders = folders.Where(f => !f.Path.Equals(row.Path, StringComparison.OrdinalIgnoreCase))
                .Append(new SandboxCustomFolder { Path = row.Path, Access = access }).ToList();
            row.AccessIndex = combo.SelectedIndex;
        }
        Save();
    }
    private void OnRemoveCustomFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path) return;
        var row = CustomFolders.FirstOrDefault(f => f.Path == path);
        if (row is not null) CustomFolders.Remove(row);
        if (CurrentApp.Settings is { } settings)
            settings.SandboxCustomFolders = settings.SandboxCustomFolders.Where(
                f => !f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
        RefreshCustomFoldersUi(); Save();
    }
    private void OnClipboardChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || sender is not RadioButton radio || radio.Tag is not string tag || CurrentApp.Settings is not { } settings) return;
        settings.SandboxClipboard = Enum.TryParse<SandboxClipboardMode>(tag, out var value) ? value : SandboxClipboardMode.None;
        Save();
    }
    private void OnTimeoutChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SandboxTimeoutMs = (int)Math.Round(e.NewValue) * 1000;
        TimeoutLabel.Text = LocalizationHelper.Format("SandboxPage_TimeoutFormat", settings.SandboxTimeoutMs / 1000);
        Save();
    }
    private void OnMaxOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        if (long.TryParse((string?)((ComboBoxItem?)MaxOutputCombo.SelectedItem)?.Tag, out var bytes))
        { settings.SandboxMaxOutputBytes = bytes; Save(); }
    }
    public sealed class CustomFolderRow
    {
        public string Path { get; }
        public int AccessIndex { get; set; }
        public bool InitialSelectionFired { get; set; }
        public CustomFolderRow(string path, SandboxFolderAccess access)
        { Path = path; AccessIndex = access == SandboxFolderAccess.ReadWrite ? 2 : access == SandboxFolderAccess.ReadOnly ? 1 : 0; }
    }
}
