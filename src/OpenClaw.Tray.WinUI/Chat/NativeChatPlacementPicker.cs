using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using static OpenClawTray.Chat.ChatMarkdownPresentation;

namespace OpenClawTray.Chat;

[Bindable]
public sealed class ChatPlacementPickerRow(
    string id, string title, string description, bool enabled, ChatWorkspace? workspace = null,
    string glyph = FluentIconCatalog.Folder)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public bool Enabled { get; } = enabled;
    public ChatWorkspace? Workspace { get; } = workspace;
    public string Glyph { get; } = glyph;
}

internal sealed class NativeChatPlacementPicker : Control, IDisposable
{
    internal StackPanel Layout { get; } = new() { Spacing = 8 };
    private readonly TextBlock _heading = new();
    private readonly AutoSuggestBox _search = new() { QueryIcon = new SymbolIcon(Symbol.Find) };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsItemClickEnabled = true,
        MaxHeight = 280
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new();
    private readonly Button _browse = new();
    private readonly Button _up = new();
    private readonly Button _useFolder = new();
    private readonly Button _back = new();
    private readonly Button _repository = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private ChatPlacementPickerProps? _props;
    private ChatPlacementState? _state;
    private ChatPlacementPickerRow[] _rows = [];
    private Action? _close;
    private bool _updatingSearch;
    private readonly long _textToken;

    public NativeChatPlacementPicker()
    {
        var resources = Application.Current.Resources;
        Template = (ControlTemplate)resources["ChatModelPickerControlTemplate"];
        _list.ItemTemplate = (DataTemplate)resources["ChatPlacementItemTemplate"];
        _heading.Style = _status.Style = (Style)resources["CaptionTextBlockStyle"];
        AutomationProperties.SetHeadingLevel(_heading, AutomationHeadingLevel.Level2);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        SetButton(_retry, "Chat_Placement_Retry", "Retry", "ChatPlacementRetry");
        SetButton(_browse, "Chat_Placement_Browse", "Browse folders", "ChatWorkspaceBrowse");
        SetButton(_up, "Chat_Placement_Up", "Up", "ChatWorkspaceUp");
        SetButton(_useFolder, "Chat_Placement_UseFolder", "Use this folder", "ChatWorkspaceUseFolder");
        SetButton(_back, "Chat_Placement_Back", "Back to projects", "ChatWorkspaceBack");
        SetButton(_repository, "Chat_Placement_Clone", "Clone repository on send", "ChatWorkspaceClone");
        _textToken = _search.RegisterPropertyChangedCallback(AutoSuggestBox.TextProperty, (_, _) =>
        {
            if (!_updatingSearch) Filter();
        });
        _search.QuerySubmitted += OnQuerySubmitted;
        _search.PreviewKeyDown += OnSearchKeyDown;
        _list.ItemClick += OnItemClick;
        _list.ContainerContentChanging += OnContainerContentChanging;
        _retry.Click += OnRetry;
        _browse.Click += OnBrowse;
        _up.Click += OnUp;
        _useFolder.Click += OnUseFolder;
        _back.Click += OnBack;
        _repository.Click += OnRepository;
        Layout.Children.Add(_heading);
        Layout.Children.Add(_search);
        Layout.Children.Add(_up);
        Layout.Children.Add(_list);
        Layout.Children.Add(_status);
        Layout.Children.Add(_repository);
        Layout.Children.Add(_browse);
        Layout.Children.Add(_useFolder);
        Layout.Children.Add(_back);
        Layout.Children.Add(_retry);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ((ContentPresenter)GetTemplateChild("PickerRoot")).Content = Layout;
    }

    internal void Update(ChatPlacementPickerProps props, Action close)
    {
        var state = props.Controller.State;
        var rebuild = _props is null || _state?.Catalog != state.Catalog ||
            _state?.Directory != state.Directory || _state?.Browsing != state.Browsing;
        var selectionChanged = _state?.Selection != state.Selection;
        var browsingChanged = _state?.Browsing != state.Browsing;
        _props = props;
        _close = close;
        _state = state;
        Width = Math.Min(360, Math.Max(200, props.AvailableWidth - 56));
        var browsing = !props.EnvironmentPicker && state.Browsing;
        _heading.Text = props.EnvironmentPicker
            ? Localized("Chat_Placement_Devices", "Your devices")
            : browsing ? Localized("Chat_Placement_Folders", "Gateway folders")
                : Localized("Chat_Placement_Projects", "Projects");
        _search.PlaceholderText = props.EnvironmentPicker
            ? Localized("Chat_Placement_SearchDevices", "Search environments")
            : browsing ? Localized("Chat_Placement_Path", "Enter a folder path")
                : Localized("Chat_Placement_SearchProjects", "Search projects or paste a Git URL");
        AutomationProperties.SetAutomationId(_search, props.EnvironmentPicker ? "ChatEnvironmentSearch" : "ChatWorkspaceSearch");
        AutomationProperties.SetName(_search, _search.PlaceholderText);
        AutomationProperties.SetAutomationId(_list, props.EnvironmentPicker ? "ChatEnvironmentList" : "ChatWorkspaceList");
        AutomationProperties.SetName(_list, _heading.Text);
        _search.IsEnabled = _list.IsEnabled = props.Controller.CanEdit && !state.Loading && !state.BrowsingLoading;
        if (browsingChanged || browsing && rebuild && !state.BrowsingLoading && state.Directory is not null)
        {
            _updatingSearch = true;
            _search.Text = browsing ? state.Directory?.Path ?? "" : "";
            _updatingSearch = false;
        }
        _browse.Visibility = !props.EnvironmentPicker && !browsing ? Visibility.Visible : Visibility.Collapsed;
        _up.Visibility = _useFolder.Visibility = _back.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        _up.IsEnabled = props.Controller.CanEdit && !state.BrowsingLoading && state.Directory?.Parent is not null;
        _useFolder.IsEnabled = browsing && props.Controller.CanEdit && !state.BrowsingLoading &&
            state.Directory is not null;
        _browse.IsEnabled = props.Controller.CanEdit && state.Catalog is not null;
        _repository.IsEnabled = props.Controller.CanEdit && !state.Loading;
        _back.IsEnabled = props.Controller.CanEdit;
        _retry.Visibility = state.Error is not null ? Visibility.Visible : Visibility.Collapsed;
        _retry.IsEnabled = props.Controller.CanEdit && !state.Loading && !state.BrowsingLoading;
        if (rebuild) Filter();
        else
        {
            if (selectionChanged) RestoreSelection();
            UpdateStatus();
        }
    }

    private void Filter()
    {
        if (_props is null || _state is null) return;
        var query = _search.Text.Trim();
        if (_props.EnvironmentPicker)
        {
            _rows = (_state.Catalog?.Environments ?? [])
                .Where(environment => $"{environment.Label} {environment.Id}".Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(environment => !environment.IsSelectable)
                .ThenBy(environment => environment.Label, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(environment => environment.Id, StringComparer.Ordinal)
                .Select(environment => new ChatPlacementPickerRow(environment.Id, environment.Label,
                    DescribeEnvironment(environment), environment.IsSelectable, glyph: FluentIconCatalog.System)).ToArray();
        }
        else if (_state.Browsing)
        {
            _rows = (_state.Directory?.Folders ?? [])
                .Select(folder => new ChatPlacementPickerRow(folder.Id, folder.Label, folder.Path ?? "", true, folder))
                .ToArray();
        }
        else
        {
            var workspaces = _state.Catalog is { } catalog
                ? new[] { catalog.DefaultWorkspace }.Concat(catalog.Workspaces)
                : [];
            _rows = workspaces
                .Where(workspace => $"{WorkspaceLabel(workspace)} {workspace.Path}".Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(workspace => new ChatPlacementPickerRow(workspace.Id, WorkspaceLabel(workspace),
                    workspace.Path ?? "", true, workspace)).ToArray();
        }
        _list.ItemsSource = _rows;
        _repository.Visibility = !_props.EnvironmentPicker && !_state.Browsing && ChatPlacementController.IsGitUrl(query)
            ? Visibility.Visible : Visibility.Collapsed;
        _repository.IsEnabled = _props.Controller.CanEdit && !_state.Loading;
        ToolTipService.SetToolTip(_repository, query);
        RestoreSelection();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_state is null) return;
        _status.Text = _state.Error is { } error ? ErrorText(error) : (_state.Loading || _state.BrowsingLoading
            ? Localized("Chat_Placement_Loading", "Loading locations...")
            : _rows.Length == 0 ? Localized("Chat_Placement_NoMatches", "No matching locations") : "");
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RestoreSelection()
    {
        var id = _state?.SelectionIsKnown != true ? null
            : _props?.EnvironmentPicker == true ? _state.Selection?.Environment.Id : _state.Selection?.Workspace.Id;
        _list.SelectedItem = _rows.FirstOrDefault(row => row.Id == id);
    }

    internal static string ErrorText(ChatPlacementError error) =>
        LocalizationHelper.GetString("Chat_Placement_Error_" + error);

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ClearValue(AutomationProperties.AutomationIdProperty);
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
            ToolTipService.SetToolTip(args.ItemContainer, null);
            return;
        }
        if (args.Item is not ChatPlacementPickerRow row) return;
        args.ItemContainer.IsEnabled = row.Enabled;
        AutomationProperties.SetAutomationId(args.ItemContainer, "ChatPlacementChoice_" + row.Id);
        AutomationProperties.SetName(args.ItemContainer, $"{row.Title}. {row.Description}");
        ToolTipService.SetToolTip(args.ItemContainer, row.Description);
    }

    private void OnItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is ChatPlacementPickerRow row) Commit(row);
    }

    private void Commit(ChatPlacementPickerRow row)
    {
        if (_props?.Controller.CanEdit != true || !row.Enabled) return;
        if (_props.EnvironmentPicker)
            _props.Controller.SelectEnvironment(row.Id);
        else if (_state?.Browsing == true && row.Workspace?.Path is { } path)
        {
            _ = _props.Controller.BrowseAsync(path);
            return;
        }
        else if (row.Workspace is { } workspace)
            _props.Controller.SelectWorkspace(workspace);
        _close?.Invoke();
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_props?.Controller.CanEdit != true) return;
        if (!_props.EnvironmentPicker && _state?.Browsing == true)
        {
            _ = _props.Controller.BrowseAsync(_search.Text.Trim());
            return;
        }
        var matches = _rows.Where(row => row.Enabled).Take(2).ToArray();
        if (matches.Length == 1) Commit(matches[0]);
        else FocusResult();
    }

    private void OnSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == global::Windows.System.VirtualKey.Down)
            args.Handled = FocusResult();
    }

    private bool FocusResult()
    {
        if (!_list.IsEnabled) return false;
        var row = _list.SelectedItem is ChatPlacementPickerRow { Enabled: true } selected
            ? selected : _rows.FirstOrDefault(item => item.Enabled);
        if (row is null) return false;
        _list.ScrollIntoView(row);
        _list.UpdateLayout();
        return _list.ContainerFromItem(row) is ListViewItem item && item.Focus(FocusState.Keyboard);
    }

    private void OnRetry(object sender, RoutedEventArgs args)
    {
        if (_props is null) return;
        if (_state?.Browsing == true) _ = _props.Controller.BrowseAsync(_search.Text.Trim());
        else _ = _props.Controller.RefreshAsync();
    }

    private void OnBrowse(object sender, RoutedEventArgs args)
    {
        if (_props is not null) _ = _props.Controller.BrowseAsync(_state?.Selection?.Workspace.Path);
    }

    private void OnUp(object sender, RoutedEventArgs args)
    {
        if (_props is not null && _state?.Directory?.Parent is { } parent) _ = _props.Controller.BrowseAsync(parent);
    }

    private void OnUseFolder(object sender, RoutedEventArgs args)
    {
        if (_props?.Controller.CanEdit != true || _state is not { Browsing: true, BrowsingLoading: false, Directory: { } directory })
            return;
        _props.Controller.SelectWorkspace(new("folder:" + directory.Path, GatewayChatPlacementClient.FolderName(directory.Path), directory.Path));
        _close?.Invoke();
    }

    private void OnBack(object sender, RoutedEventArgs args) => _props?.Controller.CloseBrowser();

    private void OnRepository(object sender, RoutedEventArgs args)
    {
        var url = _search.Text.Trim();
        if (_props?.Controller.CanEdit != true || !ChatPlacementController.IsGitUrl(url)) return;
        _props.Controller.SelectWorkspace(new("repository:" + url, url, GitUrl: url));
        _close?.Invoke();
    }

    internal static string WorkspaceLabel(ChatWorkspace workspace) =>
        string.IsNullOrWhiteSpace(workspace.Label)
            ? Localized("Chat_Placement_DefaultWorkspace", "Default workspace") : workspace.Label;

    private static string DescribeEnvironment(ChatEnvironment environment) =>
        environment.Detail ?? environment.Unavailable switch
        {
            ChatEnvironmentUnavailable.Offline => Localized("Chat_Placement_Offline", "Offline"),
            ChatEnvironmentUnavailable.HostingDisabled => Localized("Chat_Placement_HostingDisabled", "Session hosting is not enabled"),
            ChatEnvironmentUnavailable.CapacityUnknown => Localized("Chat_Placement_CapacityUnknown", "Session capacity is not advertised"),
            ChatEnvironmentUnavailable.NoCapacity => Localized("Chat_Placement_NoCapacity", "No available session slots"),
            ChatEnvironmentUnavailable.CommandUnavailable => Localized("Chat_Placement_CommandUnavailable", "Required command is not approved"),
            ChatEnvironmentUnavailable.UpdateRequired => Localized("Chat_Placement_UpdateRequired", "Host update required"),
            ChatEnvironmentUnavailable.RuntimeUnsupported => Localized("Chat_Placement_RuntimeUnsupported", "This agent runtime does not support device sessions"),
            _ => environment.IsLocal
                ? Localized("Chat_Placement_Gateway", "Gateway host")
                : Localized("Chat_Placement_Remote", "Remote session host")
        };

    private static void SetButton(Button button, string key, string fallback, string id)
    {
        button.Content = Localized(key, fallback);
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, (string)button.Content);
    }

    public void Dispose()
    {
        _search.UnregisterPropertyChangedCallback(AutoSuggestBox.TextProperty, _textToken);
        _search.QuerySubmitted -= OnQuerySubmitted;
        _search.PreviewKeyDown -= OnSearchKeyDown;
        _list.ItemClick -= OnItemClick;
        _list.ContainerContentChanging -= OnContainerContentChanging;
        _retry.Click -= OnRetry;
        _browse.Click -= OnBrowse;
        _up.Click -= OnUp;
        _useFolder.Click -= OnUseFolder;
        _back.Click -= OnBack;
        _repository.Click -= OnRepository;
        _list.ItemsSource = null;
        _props = null;
        _close = null;
    }
}
