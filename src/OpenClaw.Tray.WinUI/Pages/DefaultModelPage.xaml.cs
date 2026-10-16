using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    public DefaultModelPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
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
    }

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
}
