using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Presentation;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OpenClawTray.Pages;

public sealed partial class SandboxPage : Page
{
    private static App CurrentApp => (App)Application.Current;
    private static string L(string key) => LocalizationHelper.GetString(key);
    // XAML raises change events before LoadState can restore the saved values.
    private bool _suppress = true;
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
        finally
        {
            _probeInFlight = false;
            if (_availability is { } availability)
                CurrentApp.Services?.GetRequiredService<SandboxStatusStore>().Publish(availability);
            LoadFolders();
            UpdateStatus();
        }
    }

    private void LoadState()
    {
        if (CurrentApp.Settings is not { } settings) return;
        _suppress = true;
        try
        {
            NetInternetToggle.IsOn = settings.SystemRunAllowOutbound;
            LoadFolders();
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
        UpdatePresetHighlight();
        UpdateStatus();
    }

    private void UpdateLegacyWarning()
    {
        if (CurrentApp.Settings is not { } settings) return;
        PresetCustomFoldersWarning.IsOpen = settings.SandboxCustomFolders.Any(f => f.Access == SandboxFolderAccess.Blocked) ||
            settings.SandboxDocumentsAccess == SandboxFolderAccess.Blocked ||
            settings.SandboxDownloadsAccess == SandboxFolderAccess.Blocked ||
            settings.SandboxDesktopAccess == SandboxFolderAccess.Blocked;
        if (PresetCustomFoldersWarning.IsOpen)
        {
            PresetCustomFoldersWarning.Title = L("SandboxPage_LegacyBlockedTitle");
            PresetCustomFoldersWarning.Message = L("SandboxPage_LegacyFolderReview");
        }
    }

    private void UpdateStatus()
    {
        var scope = CurrentApp.Settings?.SystemRunFilesystemScope;
        var userFiles = scope is SystemRunFilesystemScope.UserFilesReadOnly or SystemRunFilesystemScope.UserFilesReadWrite;
        var unsupported = _availability?.IsWindowsUnsupported == true;
        var resolutionFailed = userFiles && _folderContext?.UserFolders.Any(f => f.Error is not null) == true;
        if (unsupported)
        {
            SandboxStatusSubtext.Text = L("SandboxPage_WindowsCompatibilityText");
        }
        else if (resolutionFailed)
        {
            SandboxStatusSubtext.Text = L("SandboxPage_ScopeResolutionBlockedText");
        }
        else
        {
            SandboxStatusSubtext.Text = L(_availability is null ? "SandboxPage_CheckingText" :
                _availability.CanRunSystemRunSandbox ? "SandboxPage_StatusOnSubtext" : "SandboxPage_StatusUnavailableBlockedSubtext");
        }
        FolderStatus.Text = _folderContext is null
            ? L(_availability is null ? "SandboxPage_CheckingUserFolders" : "SandboxPage_FolderUnavailable")
            : userFiles && _folderContext.UserFolders.Any(f => f.Name == "OneDrive" && f.Path is null && f.Error is null)
                ? L("SandboxPage_OneDriveNotIncluded") : "";
        FolderStatus.Visibility = FolderStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UnavailableActionBar.IsOpen = unsupported || resolutionFailed || _availability is { CanRunSystemRunSandbox: false };
        UnavailableActionBar.Title = L(unsupported ? "SandboxPage_WindowsCompatibilityTitle" :
            resolutionFailed ? "SandboxPage_ScopeResolutionBlockedTitle" : "SandboxPage_SandboxUnavailable.Title");
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
            Content = L($"SandboxPage_Preset{preset}Details") + "\n\n" + L("SandboxPage_PresetHelp.Text"),
            PrimaryButtonText = L("SandboxPage_ApplyPreset"),
            CloseButtonText = L("CancelButton.Content"),
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

    private void Save()
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SystemRunFilesystemScope ??= SystemRunFilesystemScope.SelectedFolders;
        settings.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        UpdatePresetHighlight();
        UpdateStatus();
    }

    private void OnNetInternetToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || CurrentApp.Settings is not { } settings) return;
        settings.SystemRunAllowOutbound = NetInternetToggle.IsOn;
        Save();
    }

    private void LoadFolders()
    {
        if (CurrentApp.Settings is not { } settings) return;
        var wasSuppressed = _suppress;
        _suppress = true;
        try
        {
            CustomFolders.Clear();
            foreach (var folder in MxcFolderPermissions.Resolve(settings.SnapshotSystemRunSettings(), _folderContext))
                CustomFolders.Add(new(folder));
            RefreshCustomFoldersUi();
        }
        finally { _suppress = wasSuppressed; }
    }

    private void RefreshCustomFoldersUi()
    {
        CustomFoldersList.Visibility = CustomFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var pendingPreset = _folderContext is null && CurrentApp.Settings?.SystemRunFilesystemScope is
            SystemRunFilesystemScope.UserFilesReadOnly or SystemRunFilesystemScope.UserFilesReadWrite;
        CustomFoldersEmpty.Visibility = CustomFolders.Count == 0 && !pendingPreset ? Visibility.Visible : Visibility.Collapsed;
        UpdateLegacyWarning();
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
        if (folder is null) return;
        var existing = CustomFolders.FirstOrDefault(f => MxcFolderPermissions.SamePath(f.Permission.Path, folder.Path));
        if (existing is not null)
        {
            CustomFoldersList.ScrollIntoView(existing);
            CustomFoldersList.Focus(FocusState.Programmatic);
            return;
        }
        if (CurrentApp.Settings is { } settings)
            settings.SandboxCustomFolders = settings.SandboxCustomFolders.Append(
                new SandboxCustomFolder { Path = folder.Path, Access = SandboxFolderAccess.ReadOnly }).ToList();
        Save();
        LoadFolders();
    }

    private void OnFolderAccessLoaded(object sender, RoutedEventArgs e) => InitializeFolderAccess((ComboBox)sender);
    private void OnFolderAccessDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        InitializeFolderAccess((ComboBox)sender);

    private static void InitializeFolderAccess(ComboBox combo)
    {
        // Tag identifies the initialized container, including virtualized/recycled rows.
        combo.Tag = null;
        combo.SelectedIndex = combo.DataContext is CustomFolderRow row ? row.AccessIndex : -1;
        combo.Tag = combo.DataContext;
    }

    private void OnCustomFolderAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || sender is not ComboBox combo || !combo.IsLoaded ||
            combo.DataContext is not CustomFolderRow row || !ReferenceEquals(combo.Tag, row)) return;
        if (combo.SelectedIndex is < 0 or > 2 ||
            (combo.SelectedIndex == row.AccessIndex && !row.Permission.HasLegacyBlocked)) return;
        var access = combo.SelectedIndex switch
        {
            0 => SandboxFolderAccess.NoAccess,
            1 => SandboxFolderAccess.ReadOnly,
            _ => SandboxFolderAccess.ReadWrite,
        };
        row.AccessIndex = combo.SelectedIndex;
        // Rebuild only after SelectionChanged unwinds, never remove its live container in the event.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (CustomFolders.Contains(row)) SetFolderAccess(row, access);
        });
    }
    private void SetFolderAccess(CustomFolderRow row, SandboxFolderAccess access)
    {
        if (CurrentApp.Settings is not { } settings) return;
        var edited = MxcFolderPermissions.Edit(settings.SnapshotSystemRunSettings(), row.Permission, access);
        settings.SandboxDocumentsAccess = edited.SandboxDocumentsAccess;
        settings.SandboxDesktopAccess = edited.SandboxDesktopAccess;
        settings.SandboxDownloadsAccess = edited.SandboxDownloadsAccess;
        settings.SandboxCustomFolders = edited.SandboxCustomFolders ?? [];
        Save();
        LoadFolders();
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
        public MxcFolderPermission Permission { get; }
        public string DisplayPath { get; }
        public string Detail { get; }
        public Visibility DetailVisibility => Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        public bool CanEdit => Permission.Error is null && Permission.Path.Length > 0 && !Permission.IsRuntime;
        public string AccessLabel => LocalizationHelper.Format("SandboxPage_FolderAccessFormat", DisplayPath);
        public int AccessIndex { get; set; }
        public CustomFolderRow(MxcFolderPermission permission)
        {
            Permission = permission;
            DisplayPath = permission.KnownFolders.Count == 0 ? permission.Path :
                string.Join(", ", permission.KnownFolders.Select(name => L($"SandboxPage_{name}.Text"))) + "\n" + permission.Path;
            Detail = string.Join("\n", new[] {
                permission.HasLegacyBlocked ? L("SandboxPage_LegacyFolderReview") : null,
                permission.IsRuntime ? L("SandboxPage_RuntimeFolder") : null, permission.Error,
            }.Where(s => !string.IsNullOrEmpty(s)));
            AccessIndex = permission.Access switch
            {
                SandboxFolderAccess.NoAccess => 0,
                SandboxFolderAccess.ReadOnly => 1,
                SandboxFolderAccess.ReadWrite => 2,
                _ => -1,
            };
        }
    }
}
