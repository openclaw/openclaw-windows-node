using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Pages;

public sealed partial class DefaultModelPage : Page
{
    private DefaultModelPageViewModel? _viewModel;
    private IReadOnlyList<DefaultModelOption>? _renderedOptions;
    private bool _suppressSelection;
    // Hosts Gateway sign-in steps; the view model owns the session.
    private readonly ProviderSetupDialog _providerDialog = new();
    private IReadOnlyList<ModelProviderCatalogEntry>? _renderedCatalog;
    private IReadOnlyList<ModelProviderLoginOption>? _renderedLoginOptions;
    private bool _suppressProviderSelection;
    private Task? _providerDialogTask;

    public DefaultModelPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _providerDialog.ContinueRequested += OnProviderDialogContinue;
        _providerDialog.RefreshRequested += OnProviderDialogRefresh;
        _providerDialog.CancelRequested += OnProviderDialogCancel;
        _providerDialog.ExternalLinkRequested += OnProviderDialogExternalLink;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = args.NewValue as DefaultModelPageViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshFromViewModel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The view model is attached during navigation, before the ComboBox template applies,
        // and WinUI does not render a selection made then. Re-select once loaded.
        _suppressSelection = true;
        DefaultModelSelector.SelectedItem = null;
        _suppressSelection = false;
        RefreshFromViewModel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _providerDialogTask = null;
        _providerDialog.Dismiss();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RefreshFromViewModel();

    private void RefreshFromViewModel()
    {
        if (_viewModel is not { } vm)
            return;
        DisconnectedInfoBar.IsOpen = !vm.IsConnected;
        ReadOnlyInfoBar.IsOpen = vm.IsConnected && !vm.CanWrite;

        if (!ReferenceEquals(_renderedOptions, vm.Options))
        {
            _renderedOptions = vm.Options;
            _suppressSelection = true;
            DefaultModelSelector.Items.Clear();
            foreach (DefaultModelOption option in vm.Options)
            {
                // A provider that still needs sign-in cannot become the default; the current
                // primary stays selectable so the combo can show it.
                DefaultModelSelector.Items.Add(new ComboBoxItem
                {
                    Tag = option.Id,
                    Content = Format(option),
                    IsEnabled = !option.RequiresAuth || option.Id == vm.CurrentOptionId,
                });
            }
            _suppressSelection = false;
        }
        _suppressSelection = true;
        DefaultModelSelector.SelectedItem = DefaultModelSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, vm.SelectedOptionId, StringComparison.Ordinal));
        _suppressSelection = false;
        DefaultModelSelector.IsEnabled = vm.IsConnected && vm.CanWrite && !vm.IsApplying;

        if (vm.Error is { } error)
        {
            DefaultModelStatusText.Text = error;
            DefaultModelStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            DefaultModelStatusText.Visibility = Visibility.Visible;
        }
        else if (vm.StatusApplied)
        {
            DefaultModelStatusText.Text = LocalizationHelper.GetString("DefaultModelPage_Applied");
            DefaultModelStatusText.ClearValue(TextBlock.ForegroundProperty);
            DefaultModelStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            DefaultModelStatusText.Visibility = Visibility.Collapsed;
        }

        ApplyDefaultModelButton.IsEnabled = vm.CanApply;
        DefaultModelBusyIndicator.IsActive = vm.IsApplying;
        DefaultModelBusyIndicator.Visibility = vm.IsApplying ? Visibility.Visible : Visibility.Collapsed;
        RefreshProvidersFromViewModel(vm);
    }

    private void RefreshProvidersFromViewModel(DefaultModelPageViewModel vm)
    {
        ModelProviderSetupViewModel p = vm.Providers;
        ModelProviderCapabilities caps = p.Capabilities;
        AddProviderCard.Visibility = vm.IsConnected && caps.CanManage ? Visibility.Visible : Visibility.Collapsed;
        GatewayProviderKindRadio.IsEnabled = caps.CanUseGatewayProviders;
        CustomProviderKindRadio.IsEnabled = caps.CanAddCustom;
        _suppressProviderSelection = true;
        if (ProviderKindSelector.SelectedIndex != 1 && !caps.CanUseGatewayProviders && caps.CanAddCustom)
            ProviderKindSelector.SelectedIndex = 1;
        else if (ProviderKindSelector.SelectedIndex == 1 && !caps.CanAddCustom && caps.CanUseGatewayProviders)
            ProviderKindSelector.SelectedIndex = 0;
        _suppressProviderSelection = false;
        bool showCustom = ProviderKindSelector.SelectedIndex == 1;
        GatewayProviderPanel.Visibility = !showCustom && p.CatalogState == ModelProviderCatalogState.Ready
            ? Visibility.Visible : Visibility.Collapsed;
        CustomProviderPanel.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;

        string? catalogStatus = showCustom ? null : p.CatalogState switch
        {
            ModelProviderCatalogState.Loading => LocalizationHelper.GetString("DefaultModelPage_ProviderCatalogLoading"),
            ModelProviderCatalogState.Empty => LocalizationHelper.GetString("DefaultModelPage_ProviderCatalogEmpty"),
            ModelProviderCatalogState.Failed => LocalizationHelper.Format("DefaultModelPage_ProviderCatalogFailedFormat", p.CatalogErrorDetail ?? string.Empty),
            ModelProviderCatalogState.Unavailable => LocalizationHelper.GetString("DefaultModelPage_ProviderCatalogUnsupported"),
            _ => null,
        };
        ProviderCatalogStatusText.Text = catalogStatus ?? string.Empty;
        ProviderCatalogStatusText.Visibility = catalogStatus is null ? Visibility.Collapsed : Visibility.Visible;

        _suppressProviderSelection = true;
        if (!ReferenceEquals(_renderedCatalog, p.Catalog))
        {
            _renderedCatalog = p.Catalog;
            ProviderCatalogSelector.Items.Clear();
            foreach (ModelProviderCatalogEntry entry in p.Catalog)
            {
                ProviderCatalogSelector.Items.Add(new ComboBoxItem
                {
                    Tag = entry.Provider,
                    Content = entry.IsConfigured
                        ? LocalizationHelper.Format("DefaultModelPage_ProviderConnectedFormat", entry.DisplayName)
                        : entry.DisplayName,
                });
            }
        }
        ProviderCatalogSelector.SelectedItem = FindItem(ProviderCatalogSelector, p.SelectedProviderId);
        if (!ReferenceEquals(_renderedLoginOptions, p.LoginOptions))
        {
            _renderedLoginOptions = p.LoginOptions;
            ProviderLoginSelector.Items.Clear();
            foreach (ModelProviderLoginOption option in p.LoginOptions)
                ProviderLoginSelector.Items.Add(new ComboBoxItem { Tag = option.Id, Content = option.Label });
        }
        ProviderLoginSelector.SelectedItem = FindItem(ProviderLoginSelector, p.SelectedLoginOptionId);
        _suppressProviderSelection = false;

        ProviderApiKeyPanel.Visibility = caps.CanSetApiKey && p.SelectedProvider?.ApiKeySupported == true
            ? Visibility.Visible : Visibility.Collapsed;
        SaveProviderApiKeyButton.IsEnabled = p.CanSaveApiKey;
        ProviderSignInPanel.Visibility = p.LoginOptions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartProviderSignInButton.IsEnabled = p.CanStartLogin;
        AddCustomProviderButton.IsEnabled = p.CanAddCustom;

        (string? status, bool isError) = FormatProviderStatus(p);
        ProviderStatusText.Text = status ?? string.Empty;
        ProviderStatusText.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (isError)
            ProviderStatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        else
            ProviderStatusText.ClearValue(TextBlock.ForegroundProperty);

        RefreshProviderDialog(p);
    }

    private void RefreshProviderDialog(ModelProviderSetupViewModel p)
    {
        if (!p.LoginActive)
        {
            if (_providerDialogTask is not null)
            {
                _providerDialogTask = null;
                _providerDialog.Dismiss();
            }
            return;
        }
        string? error = p.LoginError switch
        {
            ModelProviderLoginError.Request => p.LoginErrorDetail,
            ModelProviderLoginError.TimedOut => LocalizationHelper.GetString("DefaultModelPage_ProviderSignInTimedOut"),
            ModelProviderLoginError.UnsupportedStep => LocalizationHelper.GetString("DefaultModelPage_ProviderSignInUnsupported"),
            ModelProviderLoginError.CancelUnconfirmed => LocalizationHelper.GetString("DefaultModelPage_ProviderSignInCancelUnconfirmed"),
            _ => null,
        };
        string status = p.LoginBusy ? LocalizationHelper.GetString("DefaultModelPage_ProviderSignInWaiting") : string.Empty;
        _providerDialog.Update(p.LoginStep, p.LoginPhase, p.LoginBusy, canCancel: true, p.LoginCancelling,
            status, error, p.LoginTitle);
        if (XamlRoot is null)
            return;
        Task showing = _providerDialog.ShowOwnedAsync(XamlRoot, ActualTheme);
        if (!ReferenceEquals(showing, _providerDialogTask))
        {
            _providerDialogTask = showing;
            _ = ObserveProviderDialogAsync(showing, p);
        }
    }

    private static async Task ObserveProviderDialogAsync(Task showing, ModelProviderSetupViewModel p)
    {
        try
        {
            await showing;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog already owns this window; abandon the sign-in instead of hiding it.
            Logger.Warn($"Provider sign-in dialog could not open: {ex.Message}");
            AsyncEventHandlerGuard.Run(p.CancelLoginAsync, new AppLogger(), nameof(ObserveProviderDialogAsync));
        }
    }

    private static (string? Text, bool IsError) FormatProviderStatus(ModelProviderSetupViewModel p)
    {
        string subject = p.StatusSubject ?? string.Empty;
        return p.Status switch
        {
            ModelProviderStatus.ApiKeySaved => (Append(LocalizationHelper.Format("DefaultModelPage_ProviderApiKeySaved", subject), p.StatusDetail), false),
            ModelProviderStatus.CustomProviderAdded => (LocalizationHelper.Format("DefaultModelPage_CustomProviderAdded", subject), false),
            ModelProviderStatus.SignedIn => (LocalizationHelper.Format("DefaultModelPage_ProviderSignedIn", subject), false),
            ModelProviderStatus.SignedInRestartRequired => (LocalizationHelper.Format("DefaultModelPage_ProviderSignedInRestart", subject), false),
            ModelProviderStatus.SignInCancelled => (LocalizationHelper.GetString("DefaultModelPage_ProviderSignInCancelled"), false),
            ModelProviderStatus.SignInFailed => (p.StatusDetail is { } detail
                ? LocalizationHelper.Format("DefaultModelPage_ProviderRequestFailedFormat", detail)
                : LocalizationHelper.GetString("DefaultModelPage_ProviderSignInFailed"), true),
            ModelProviderStatus.RequestFailed => (LocalizationHelper.Format("DefaultModelPage_ProviderRequestFailedFormat", p.StatusDetail ?? string.Empty), true),
            ModelProviderStatus.Invalid => (LocalizationHelper.GetString(p.ValidationError switch
            {
                CustomProviderValidationError.InvalidProviderId => "DefaultModelPage_CustomInvalidProviderId",
                CustomProviderValidationError.ProviderIdReserved => "DefaultModelPage_CustomProviderIdReserved",
                CustomProviderValidationError.ProviderIdInUse => "DefaultModelPage_CustomProviderIdInUse",
                CustomProviderValidationError.InvalidBaseUrl => "DefaultModelPage_CustomInvalidBaseUrl",
                CustomProviderValidationError.ApiKeyRequired => "DefaultModelPage_CustomApiKeyRequired",
                CustomProviderValidationError.MissingModels => "DefaultModelPage_CustomMissingModels",
                _ => "DefaultModelPage_CustomInvalidModelId",
            }), true),
            _ => (null, false),
        };

        static string Append(string text, string? detail) => detail is null ? text : $"{text} {detail}";
    }

    private static ComboBoxItem? FindItem(ComboBox combo, string? tag) =>
        combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));

    private static string Format(DefaultModelOption option)
    {
        string label = option.IsLocalAi
            ? LocalizationHelper.Format("DefaultModelPage_LocalAiOptionFormat", option.DisplayName)
            : option.Provider is null ? option.DisplayName : $"{option.DisplayName} ({option.Provider})";
        return option.RequiresAuth ? LocalizationHelper.Format("DefaultModelPage_RequiresAuthFormat", label) : label;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _viewModel is null)
            return;
        if (DefaultModelSelector.SelectedItem is ComboBoxItem { Tag: string id })
            _viewModel.SelectedOptionId = id;
    }

    private void OnApply(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(() => _viewModel?.ApplyAsync() ?? Task.FromResult(false), new AppLogger(), nameof(OnApply));
    private void OnRefresh(object sender, RoutedEventArgs e) => _viewModel?.Refresh();
    private void OnOpenConnection(object sender, RoutedEventArgs e) => _viewModel?.OpenConnection();
    private void OnManageLocalAi(object sender, RoutedEventArgs e) => _viewModel?.OpenLocalAi();

    private void OnProviderKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressProviderSelection && _viewModel is not null)
            RefreshFromViewModel();
    }

    private void OnProviderCatalogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressProviderSelection && _viewModel is not null &&
            ProviderCatalogSelector.SelectedItem is ComboBoxItem { Tag: string provider })
            _viewModel.Providers.SelectedProviderId = provider;
    }

    private void OnProviderLoginSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressProviderSelection && _viewModel is not null &&
            ProviderLoginSelector.SelectedItem is ComboBoxItem { Tag: string option })
            _viewModel.Providers.SelectedLoginOptionId = option;
    }

    private void OnSaveProviderApiKey(object sender, RoutedEventArgs e)
    {
        string apiKey = ProviderApiKeyBox.Password;
        ProviderApiKeyBox.Password = string.Empty;
        if (_viewModel is { } vm)
            AsyncEventHandlerGuard.Run(() => vm.Providers.SaveApiKeyAsync(apiKey), new AppLogger(), nameof(OnSaveProviderApiKey));
    }

    private void OnStartProviderSignIn(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } vm)
            AsyncEventHandlerGuard.Run(vm.Providers.StartLoginAsync, new AppLogger(), nameof(OnStartProviderSignIn));
    }

    private void OnAddCustomProvider(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm)
            return;
        string providerId = CustomProviderIdBox.Text;
        string baseUrl = CustomProviderBaseUrlBox.Text;
        string api = (CustomProviderApiSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? ModelProviderWire.OpenAiCompletionsApi;
        string apiKey = CustomProviderApiKeyBox.Password;
        string models = CustomProviderModelsBox.Text;
        AsyncEventHandlerGuard.Run(async () =>
        {
            bool added = await vm.Providers.AddCustomProviderAsync(providerId, baseUrl, api, apiKey, models);
            CustomProviderApiKeyBox.Password = string.Empty;
            if (!added)
                return;
            CustomProviderIdBox.Text = string.Empty;
            CustomProviderBaseUrlBox.Text = string.Empty;
            CustomProviderModelsBox.Text = string.Empty;
        }, new AppLogger(), nameof(OnAddCustomProvider));
    }

    private void OnProviderDialogContinue()
    {
        if (_viewModel is not { } vm || vm.Providers.LoginStep is not { } step || !_providerDialog.CanSubmit)
            return;
        JsonElement? answer = _providerDialog.TakeAnswer();
        AsyncEventHandlerGuard.Run(() => vm.Providers.SubmitLoginAnswerAsync(step.Id, answer), new AppLogger(), nameof(OnProviderDialogContinue));
    }

    private void OnProviderDialogRefresh()
    {
        if (_viewModel is { } vm)
            AsyncEventHandlerGuard.Run(vm.Providers.RefreshLoginAsync, new AppLogger(), nameof(OnProviderDialogRefresh));
    }

    private void OnProviderDialogCancel()
    {
        if (_viewModel is { } vm)
            AsyncEventHandlerGuard.Run(vm.Providers.CancelLoginAsync, new AppLogger(), nameof(OnProviderDialogCancel));
    }

    private void OnProviderDialogExternalLink(string? url) =>
        AsyncEventHandlerGuard.Run(() => OpenExternalLinkAsync(url), new AppLogger(), nameof(OnProviderDialogExternalLink));

    private static async Task OpenExternalLinkAsync(string? url)
    {
        if (!GatewayAiSetupPresentation.TryGetExternalUri(url, out Uri uri))
        {
            Logger.Warn("Provider sign-in link was not a valid http(s) URL.");
            return;
        }
        if (!await global::Windows.System.Launcher.LaunchUriAsync(uri))
            Logger.Warn("Provider sign-in link could not be opened.");
    }
}
