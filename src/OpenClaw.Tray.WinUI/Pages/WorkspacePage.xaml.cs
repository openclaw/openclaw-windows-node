using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class WorkspacePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;

    // All entries from the latest list result, in display (sorted) order.
    private readonly List<WorkspaceFilesModel.WorkspaceFileEntry> _allEntries = new();

    // Opaque request path → entry, for selection lookup. Case-sensitive: workspace
    // paths may differ only by case.
    private readonly Dictionary<string, WorkspaceFilesModel.WorkspaceFileEntry> _entriesByPath =
        new(StringComparer.Ordinal);

    private enum FileBodyKind { Loaded, Missing, ImageUnsupported }

    // Opaque request path → resolved body. Only stable outcomes are cached (loaded
    // content or confirmed-missing); transient/unavailable errors are NOT cached
    // so re-selecting the file retries the fetch.
    private readonly Dictionary<string, (FileBodyKind Kind, string? Content)> _fileContent =
        new(StringComparer.Ordinal);

    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private string _browserPath = string.Empty;
    private string? _browserParentPath;
    private string _searchQuery = string.Empty;
    private bool _suppressSearchTextChanged;
    private WorkspaceGatewaySource _workspaceSource = WorkspaceGatewaySource.AgentWorkspace;
    private bool _renderMarkdown = true;
    private readonly object _requestCancellationLock = new();
    private CancellationTokenSource? _listRequestCancellation;
    private CancellationTokenSource? _fileRequestCancellation;
    private WorkspaceScopeDisclosureRequest? _listScopeDisclosureRequest;
    private readonly WorkspaceSessionReloadGate _sessionReloadGate = new();
    private IGatewayConnectionManager? _connectionManager;

    // Monotonic token guarding against out-of-order async results: a list/file
    // load applies only when its token still matches the latest request.
    private int _loadToken;

    /// <summary>Set by HubWindow before <see cref="Initialize"/> to specify the active agent scope.</summary>
    public string AgentId { get; set; } = "main";
    public string CurrentAgentId => AgentId;

    public WorkspacePage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _ = LoadAsync();
        };
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = e.NewSize.Width;
        if (double.IsNaN(available) || available <= 0) return;
        var max = ContentRoot.MaxWidth;
        ContentRoot.Width = double.IsNaN(max) || double.IsInfinity(max)
            ? available
            : Math.Min(available, max);
    }

    public void Initialize()
    {
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState;
        if (_appState != null) _appState.PropertyChanged += OnAppStateChanged;
        if (!ReferenceEquals(_connectionManager, CurrentApp.ConnectionManager))
        {
            if (_connectionManager is not null)
                _connectionManager.OperatorClientChanged -= OnOperatorClientChanged;
            _connectionManager = CurrentApp.ConnectionManager;
            if (_connectionManager is not null)
                _connectionManager.OperatorClientChanged += OnOperatorClientChanged;
        }
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        if (_appState != null)
            _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
        if (_connectionManager is not null)
        {
            _connectionManager.OperatorClientChanged -= OnOperatorClientChanged;
            _connectionManager = null;
        }
        CancelAllRequests();
        Interlocked.Increment(ref _loadToken);
    }

    private string? ResolveSessionKey()
    {
        return WorkspaceSessionResolver.Resolve(
            AgentId,
            _appState?.Sessions,
            CurrentApp.GatewayClient?.MainSessionKey);
    }

    private async Task LoadAsync()
    {
        // Invalidate any in-flight list/file loads up front — before the
        // connected/key early-returns — so a stale result can never render
        // after a newer (even failed) load.
        var token = Interlocked.Increment(ref _loadToken);
        var cancellation = BeginListRequest(out var scopeRequest);

        var client = CurrentApp.GatewayClient;
        var status = CurrentApp.AppState?.Status ?? ConnectionStatus.Disconnected;
        if (client == null || status != ConnectionStatus.Connected)
        {
            ShowDisconnected();
            CompleteRequest(
                ref _listRequestCancellation,
                cancellation,
                ref _listScopeDisclosureRequest,
                scopeRequest);
            return;
        }

        BeginLoading();

        WorkspaceListGatewayResult result;
        var fallbackKeyWasResolved = false;
        string? resolvedFallbackKey = null;
        string? ResolveFallbackKey()
        {
            if (!fallbackKeyWasResolved)
            {
                resolvedFallbackKey = ResolveSessionKey();
                fallbackKeyWasResolved = true;
            }
            return resolvedFallbackKey;
        }

        try
        {
            var search = string.IsNullOrWhiteSpace(_searchQuery) ? null : _searchQuery.Trim();
            var coordinator = new WorkspaceGatewayCoordinator(new WorkspaceGatewayApi(client));
            result = await coordinator.ListAsync(
                AgentId,
                _browserPath,
                search,
                ResolveFallbackKey,
                () => QueueScopeDisclosure(
                    token,
                    client,
                    WorkspaceGatewaySource.LegacyAgentFiles,
                    scopeRequest),
                cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested ||
            token != Volatile.Read(ref _loadToken) ||
            !ReferenceEquals(client, CurrentApp.GatewayClient))
        {
            return;
        }
        catch (Exception ex)
        {
            if (token != Volatile.Read(ref _loadToken) ||
                !ReferenceEquals(client, CurrentApp.GatewayClient))
                return;
            Services.Logger.Warn($"[WorkspacePage] workspace list failed ({ex.GetType().Name})");
            EndLoading();
            if (CurrentApp.AppState?.Status == ConnectionStatus.Connected)
                ShowLoadError();
            else
                ShowDisconnected();
            return;
        }
        finally
        {
            CompleteRequest(
                ref _listRequestCancellation,
                cancellation,
                ref _listScopeDisclosureRequest,
                scopeRequest);
        }

        if (token != Volatile.Read(ref _loadToken) ||
            !ReferenceEquals(client, CurrentApp.GatewayClient))
            return;
        _workspaceSource = result.Source;
        _sessionReloadGate.RecordCompletedLoad(
            result.Source,
            fallbackKeyWasResolved,
            resolvedFallbackKey);
        var reloadForChangedFallbackKey =
            _sessionReloadGate.DependsOnSessionKey &&
            _sessionReloadGate.ShouldReload(ResolveSessionKey());
        switch (result.Source)
        {
            case WorkspaceGatewaySource.AgentWorkspace:
                EndLoading();
                ApplyAgentWorkspaceList(result.AgentWorkspace!);
                break;
            case WorkspaceGatewaySource.SessionFiles:
                EndLoading();
                ApplySessionListResult(result.SessionFiles!);
                break;
            case WorkspaceGatewaySource.LegacyAgentFiles:
                EndLoading();
                if (result.LegacyPayload is JsonElement legacyPayload)
                    ApplyLegacyAgentFilesList(legacyPayload);
                else
                    ShowUnsupported();
                break;
            case WorkspaceGatewaySource.Unsupported:
                EndLoading();
                ShowUnsupported();
                break;
        }

        if (reloadForChangedFallbackKey &&
            token == Volatile.Read(ref _loadToken) &&
            ReferenceEquals(client, CurrentApp.GatewayClient))
        {
            _ = LoadAsync();
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Sessions))
        {
            if (_sessionReloadGate.DependsOnSessionKey &&
                _sessionReloadGate.ShouldReload(ResolveSessionKey()))
            {
                _ = LoadAsync();
            }
        }
        else if (e.PropertyName == nameof(AppState.Status))
        {
            if (_appState?.Status == ConnectionStatus.Connected)
            {
                _ = LoadAsync();
            }
            else
            {
                CancelAllRequests();
                Interlocked.Increment(ref _loadToken);
                ResetScopeDisclosure();
                ShowDisconnected();
            }
        }
    }

    private void ApplyAgentWorkspaceList(AgentWorkspaceListResult result)
    {
        ClearFiles();

        var state = WorkspaceFilesModel.FromAgentWorkspaceList(result);
        WorkspacePathText.Text = LocalizationHelper.GetString("WorkspacePage_AgentWorkspaceLabel");
        ApplyListState(state);
    }

    private void ApplySessionListResult(SessionFileList result)
    {
        ClearFiles();

        var state = WorkspaceFilesModel.FromSessionFileList(result);
        WorkspacePathText.Text = state.WorkspacePath;
        ApplyListState(state);
    }

    private void ApplyListState(WorkspaceFilesModel.WorkspaceListState state)
    {
        _browserPath = state.RequestBrowserPath;
        _browserParentPath = state.RequestBrowserParentPath;
        UpdateBrowserChrome(state);
        if (!state.Supported)
        {
            ShowUnsupported();
            return;
        }

        foreach (var entry in state.Entries)
        {
            _allEntries.Add(entry);
            _entriesByPath[entry.RequestPath] = entry;
        }

        if (_allEntries.Count == 0 && string.IsNullOrWhiteSpace(_searchQuery) && string.IsNullOrEmpty(_browserPath))
        {
            if (_workspaceSource != WorkspaceGatewaySource.AgentWorkspace)
                ShowScopeDisclosure(_workspaceSource);
            ShowNoFiles();
            return;
        }

        ShowScopeDisclosure(_workspaceSource);
        BodyGrid.Visibility = Visibility.Visible;
        ApplyFilter();
    }

    private void ApplyLegacyAgentFilesList(JsonElement payload)
    {
        ClearFiles();

        var state = WorkspaceFilesModel.FromLegacyAgentFilesList(payload);
        WorkspacePathText.Text = state.WorkspacePath;
        _browserPath = state.RequestBrowserPath;
        _browserParentPath = state.RequestBrowserParentPath;
        UpdateBrowserChrome(state);

        foreach (var entry in state.Entries)
        {
            _allEntries.Add(entry);
            _entriesByPath[entry.RequestPath] = entry;
        }

        if (_allEntries.Count == 0)
        {
            ShowScopeDisclosure(WorkspaceGatewaySource.LegacyAgentFiles);
            ShowNoFiles();
            return;
        }

        ShowScopeDisclosure(WorkspaceGatewaySource.LegacyAgentFiles);
        BodyGrid.Visibility = Visibility.Visible;
        ApplyFilter();
    }

    private void BeginLoading()
    {
        HideFallback();
        LoadingRing.IsActive = true;
        LoadingPanel.Visibility = Visibility.Visible;
        ClearFiles();
        BrowserNoticeText.Visibility = Visibility.Collapsed;
    }

    private void EndLoading()
    {
        LoadingRing.IsActive = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        var filtered = WorkspaceFilesModel.Filter(_allEntries, _searchQuery);

        FileList.SelectionChanged -= FileList_SelectionChanged;
        FileList.Items.Clear();
        foreach (var entry in filtered)
            FileList.Items.Add(BuildFileRow(entry));
        FileList.SelectionChanged += FileList_SelectionChanged;

        FileCountText.Text = _allEntries.Count > 0
            ? filtered.Count == _allEntries.Count ? $"({_allEntries.Count})" : $"({filtered.Count} of {_allEntries.Count})"
            : string.Empty;

        bool hasResults = filtered.Count > 0;
        bool searching = !string.IsNullOrWhiteSpace(_searchQuery);
        NoResultsText.Text = LocalizationHelper.GetString(searching
            ? "WorkspacePage_NoSearchResults.Text"
            : "WorkspacePage_NoFolderResults");
        NoResultsText.Visibility = !hasResults ? Visibility.Visible : Visibility.Collapsed;

        if (hasResults)
        {
            SelectInitialRow(filtered);
        }
        else
        {
            FileBodyPresenter.Content = null;
            SelectedFileText.Text = string.Empty;
            SelectedFileMeta.Visibility = Visibility.Collapsed;
            ViewModeSelector.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput &&
            args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            return;
        }
        if (_suppressSearchTextChanged) return;
        _searchQuery = sender.Text ?? string.Empty;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SelectInitialRow(IReadOnlyList<WorkspaceFilesModel.WorkspaceFileEntry> filtered)
    {
        int index = -1;
        for (int i = 0; i < filtered.Count; i++)
        {
            if (filtered[i].CanPreview || !filtered[i].Exists)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            FileList.SelectedIndex = -1;
            SelectedFileText.Text = string.Empty;
            SelectedFileMeta.Visibility = Visibility.Collapsed;
            ViewModeSelector.Visibility = Visibility.Collapsed;
            FileBodyPresenter.Content = null;
            return;
        }

        FileList.SelectedIndex = index;
    }

    private ListViewItem BuildFileRow(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new FontIcon
        {
            Glyph = entry.IsDirectory ? FluentIconCatalog.Folder : FluentIconCatalog.Document,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            IsTextScaleFactorEnabled = false,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var stack = new StackPanel { Spacing = 4 };
        Grid.SetColumn(stack, 1);

        stack.Children.Add(new TextBlock
        {
            Text = entry.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        var meta = BuildRowMeta(entry);
        if (!string.IsNullOrEmpty(meta))
        {
            stack.Children.Add(new TextBlock
            {
                Text = meta,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }

        row.Children.Add(stack);

        var badges = BuildBadges(entry);
        if (badges != null)
        {
            Grid.SetColumn(badges, 2);
            row.Children.Add(badges);
        }

        var item = new ListViewItem { Content = row, Tag = entry.RequestPath };
        AutomationProperties.SetName(item, BuildAutomationName(entry));
        item.ContextFlyout = BuildRowContextFlyout(entry);
        return item;
    }

    private MenuFlyout BuildRowContextFlyout(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var copyLabel = LocalizationHelper.GetString("WorkspacePage_CopyPath");
        var copy = new MenuFlyoutItem
        {
            Text = copyLabel,
            Tag = entry.RequestPath,
            Icon = new FontIcon
            {
                Glyph = FluentIconCatalog.Copy,
                FontSize = 16,
                IsTextScaleFactorEnabled = false,
            },
        };
        copy.Click += CopyPathButton_Click;

        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        return menu;
    }

    private static string BuildAutomationName(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var role = LocalizationHelper.GetString(entry.IsDirectory
            ? "WorkspacePage_FileType_Folder"
            : "WorkspacePage_FileType_File");
        var parts = new List<string> { role, entry.Name };
        var meta = BuildRowMeta(entry);
        if (!string.IsNullOrEmpty(meta)) parts.Add(meta);
        if (!entry.Exists) parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Missing"));
        if (entry.Touched) parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Edited"));
        else if (entry.Read) parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Read"));
        return string.Join(", ", parts);
    }

    // Second-line caption: parent folder · size. Modified time, when present,
    // is shown in the detail header rather than crowding every row.
    private static string BuildRowMeta(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var parts = new List<string>(2);
        var dir = WorkspaceFilesModel.DirectoryOf(entry.RelativePath);
        if (!string.IsNullOrEmpty(dir)) parts.Add(dir);
        var size = WorkspaceFilesModel.FormatSize(entry.Size);
        if (!string.IsNullOrEmpty(size)) parts.Add(size);
        return string.Join(" · ", parts);
    }

    private StackPanel? BuildBadges(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (!entry.Exists)
            badges.Children.Add(BuildBadge("WorkspacePage_Badge_Missing", "SystemFillColorCautionBrush"));
        if (entry.Touched)
            badges.Children.Add(BuildBadge("WorkspacePage_Badge_Edited", "AccentTextFillColorPrimaryBrush"));
        else if (entry.Read)
            badges.Children.Add(BuildBadge("WorkspacePage_Badge_Read", "TextFillColorSecondaryBrush"));

        return badges.Children.Count > 0 ? badges : null;
    }

    private static Border BuildBadge(string resKey, string foregroundBrushKey)
    {
        return new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 0, 8, 0),
            Child = new TextBlock
            {
                Text = LocalizationHelper.GetString(resKey),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources[foregroundBrushKey],
            },
        };
    }

    private void ClearFiles()
    {
        FileList.SelectionChanged -= FileList_SelectionChanged;
        FileList.Items.Clear();
        FileList.SelectionChanged += FileList_SelectionChanged;
        _allEntries.Clear();
        _entriesByPath.Clear();
        _fileContent.Clear();
        FileBodyPresenter.Content = null;
        SelectedFileText.Text = string.Empty;
        SelectedFileMeta.Visibility = Visibility.Collapsed;
        FileCountText.Text = string.Empty;
        NoResultsText.Visibility = Visibility.Collapsed;
        BodyGrid.Visibility = Visibility.Collapsed;
        ViewModeSelector.Visibility = Visibility.Collapsed;
    }

    private string? SelectedRequestPath() =>
        FileList.SelectedItem is ListViewItem { Tag: string path } ? path : null;

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRequestPath() is not string requestPath ||
            !_entriesByPath.TryGetValue(requestPath, out var entry))
        {
            return;
        }

        SelectedFileText.Text = entry.Name;
        UpdateDetailMeta(entry);

        if (entry.IsDirectory)
        {
            ViewModeSelector.Visibility = Visibility.Collapsed;
            BrowseToPath(entry.RequestPath);
            return;
        }

        ViewModeSelector.Visibility = IsMarkdown(entry.Name) ? Visibility.Visible : Visibility.Collapsed;

        if (!entry.CanPreview)
        {
            ViewModeSelector.Visibility = Visibility.Collapsed;
            FileBodyPresenter.Content = BuildNoteBody(
                LocalizationHelper.GetString("WorkspacePage_BrowserOnlyFileNote"));
            return;
        }

        // Files the list already reported as missing on disk never need a fetch.
        if (!entry.Exists)
        {
            _fileContent[requestPath] = (FileBodyKind.Missing, null);
            RenderSelectedFile();
            return;
        }

        if (_fileContent.ContainsKey(requestPath))
        {
            RenderSelectedFile();
        }
        else
        {
            ShowLoadingBody();
            _ = LoadFileAsync(entry);
        }
    }

    private async Task LoadFileAsync(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        // ScopeInfoBar describes the visible list source. Preview transport
        // updates only the right pane and must never replace list provenance.
        var token = Volatile.Read(ref _loadToken);
        var cancellation = BeginFileRequest();
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            ShowFileUnavailable(entry.RequestPath);
            CompleteFileRequest(cancellation);
            return;
        }

        try
        {
            var coordinator = new WorkspaceGatewayCoordinator(new WorkspaceGatewayApi(client));
            var result = await coordinator.GetAsync(
                AgentId,
                entry.RequestPath,
                ResolveSessionKey,
                cancellationToken: cancellation.Token);
            if (token != Volatile.Read(ref _loadToken) ||
                !ReferenceEquals(client, CurrentApp.GatewayClient))
                return;

            if (result.Source == WorkspaceGatewaySource.AgentWorkspace)
            {
                var file = result.AgentWorkspace?.File;
                if (file == null)
                {
                    ShowFileUnavailable(entry.RequestPath);
                    return;
                }

                switch (WorkspaceFilesModel.GetPreviewKind(file))
                {
                    case WorkspaceFilesModel.PreviewKind.Text:
                        SetFileBody(entry.RequestPath, FileBodyKind.Loaded, file.Content);
                        break;
                    case WorkspaceFilesModel.PreviewKind.ImageUnsupported:
                        SetFileBody(entry.RequestPath, FileBodyKind.ImageUnsupported, null);
                        break;
                    default:
                        ShowFileUnavailable(entry.RequestPath);
                        break;
                }
                return;
            }

            if (result.Source == WorkspaceGatewaySource.LegacyAgentFiles)
            {
                var applied = result.LegacyPayload is JsonElement legacyPayload &&
                    ApplyLegacyAgentFileContent(legacyPayload, entry);
                if (!applied && result.LegacyPayload is null)
                    ShowFileUnavailable(entry.RequestPath);
                return;
            }

            if (result.Source == WorkspaceGatewaySource.Unsupported)
            {
                ShowFileUnavailable(entry.RequestPath);
                return;
            }

            var session = result.SessionFile!;
            if (session.Missing)
                SetFileBody(entry.RequestPath, FileBodyKind.Missing, null);
            else if (session.Content is null)
                ShowFileUnavailable(entry.RequestPath);
            else
                SetFileBody(entry.RequestPath, FileBodyKind.Loaded, session.Content);
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested ||
            token != Volatile.Read(ref _loadToken) ||
            !ReferenceEquals(client, CurrentApp.GatewayClient))
        {
        }
        catch (Exception ex)
        {
            if (token != Volatile.Read(ref _loadToken) ||
                !ReferenceEquals(client, CurrentApp.GatewayClient))
                return;
            Services.Logger.Warn($"[WorkspacePage] workspace file request failed ({ex.GetType().Name})");
            ShowFileUnavailable(entry.RequestPath);
        }
        finally
        {
            CompleteFileRequest(cancellation);
        }
    }

    private void QueueScopeDisclosure(
        int token,
        IOperatorGatewayClient client,
        WorkspaceGatewaySource source,
        WorkspaceScopeDisclosureRequest request)
    {
        if (!request.TryQueue(source))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (request.CanApply(source) &&
                token == Volatile.Read(ref _loadToken) &&
                ReferenceEquals(client, CurrentApp.GatewayClient))
            {
                ShowScopeDisclosure(source);
            }
        });
    }

    private CancellationTokenSource BeginListRequest(
        out WorkspaceScopeDisclosureRequest scopeRequest)
    {
        lock (_requestCancellationLock)
        {
            CancelRequest(ref _listRequestCancellation);
            CancelRequest(ref _fileRequestCancellation);
            CompleteScopeRequest(ref _listScopeDisclosureRequest);
            scopeRequest = new WorkspaceScopeDisclosureRequest();
            _listScopeDisclosureRequest = scopeRequest;
            return _listRequestCancellation = new CancellationTokenSource();
        }
    }

    private CancellationTokenSource BeginFileRequest()
    {
        lock (_requestCancellationLock)
        {
            CancelRequest(ref _fileRequestCancellation);
            return _fileRequestCancellation = new CancellationTokenSource();
        }
    }

    private void CancelAllRequests()
    {
        lock (_requestCancellationLock)
        {
            CancelRequest(ref _listRequestCancellation);
            CancelRequest(ref _fileRequestCancellation);
            CompleteScopeRequest(ref _listScopeDisclosureRequest);
        }
    }

    private static void CancelRequest(ref CancellationTokenSource? cancellation)
    {
        var current = cancellation;
        cancellation = null;
        if (current is null)
            return;

        current.Cancel();
        current.Dispose();
    }

    private static void CompleteScopeRequest(
        ref WorkspaceScopeDisclosureRequest? request)
    {
        var current = request;
        request = null;
        current?.Complete();
    }

    private void CompleteRequest(
        ref CancellationTokenSource? cancellationField,
        CancellationTokenSource completedCancellation,
        ref WorkspaceScopeDisclosureRequest? scopeField,
        WorkspaceScopeDisclosureRequest completedScope)
    {
        lock (_requestCancellationLock)
        {
            if (ReferenceEquals(cancellationField, completedCancellation))
            {
                cancellationField = null;
                completedCancellation.Dispose();
            }

            if (ReferenceEquals(scopeField, completedScope))
                scopeField = null;
            completedScope.Complete();
        }
    }

    private void CompleteFileRequest(CancellationTokenSource completed)
    {
        lock (_requestCancellationLock)
        {
            if (!ReferenceEquals(_fileRequestCancellation, completed))
                return;

            _fileRequestCancellation = null;
            completed.Dispose();
        }
    }

    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        CancelAllRequests();
        var token = Interlocked.Increment(ref _loadToken);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token == Volatile.Read(ref _loadToken) &&
                ReferenceEquals(e.NewClient, CurrentApp.GatewayClient))
            {
                ResetScopeDisclosure();
            }
        });
    }

    private bool ApplyLegacyAgentFileContent(
        JsonElement payload,
        WorkspaceFilesModel.WorkspaceFileEntry requestedEntry)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("file", out var fileEl) ||
            fileEl.ValueKind != JsonValueKind.Object)
        {
            ShowFileUnavailable(requestedEntry.RequestPath);
            return false;
        }

        var responsePath = GetString(fileEl, "path");
        var responseName = GetString(fileEl, "name");
        var matchesRequest = !string.IsNullOrEmpty(responsePath)
            ? string.Equals(responsePath, requestedEntry.RequestPath, StringComparison.Ordinal)
            : string.Equals(responseName, requestedEntry.Name, StringComparison.Ordinal);
        if (!matchesRequest)
        {
            ShowFileUnavailable(requestedEntry.RequestPath);
            return false;
        }

        bool missing = GetBool(fileEl, "missing") ?? false;
        if (missing)
        {
            SetFileBody(requestedEntry.RequestPath, FileBodyKind.Missing, null);
            return true;
        }

        var content = GetString(fileEl, "content");
        if (content is null)
        {
            ShowFileUnavailable(requestedEntry.RequestPath);
            return false;
        }
        else
            SetFileBody(requestedEntry.RequestPath, FileBodyKind.Loaded, content);
        return true;
    }

    // Cache a stable body outcome (loaded content or confirmed-missing) and
    // render it if the file is still selected.
    private void SetFileBody(string relativePath, FileBodyKind kind, string? content)
    {
        _fileContent[relativePath] = (kind, content);
        if (string.Equals(SelectedRequestPath(), relativePath, StringComparison.Ordinal))
            RenderSelectedFile();
    }

    // Transient/unavailable error: shown inline but NOT cached, so re-selecting
    // the file retries the fetch instead of permanently reading as "missing".
    private void ShowFileUnavailable(string relativePath)
    {
        if (string.Equals(SelectedRequestPath(), relativePath, StringComparison.Ordinal))
        {
            FileBodyPresenter.Content = BuildNoteBody(
                LocalizationHelper.GetString("WorkspacePage_FileUnavailable"));
        }
    }

    private void UpdateDetailMeta(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var parts = new List<string>(3);
        var size = WorkspaceFilesModel.FormatSize(entry.Size);
        if (!string.IsNullOrEmpty(size)) parts.Add(size);
        if (entry.ModifiedUtc is { } modified)
            parts.Add(modified.ToLocalTime().ToString("g"));
        if (!entry.Exists)
            parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Missing"));

        if (parts.Count > 0)
        {
            SelectedFileMeta.Text = string.Join(" · ", parts);
            SelectedFileMeta.Visibility = Visibility.Visible;
        }
        else
        {
            SelectedFileMeta.Visibility = Visibility.Collapsed;
        }
    }

    private void ViewModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _renderMarkdown = ViewModeSelector.SelectedItem == ViewModeRenderedItem;
        RenderSelectedFile();
    }

    private void ShowLoadingBody()
    {
        var loading = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };
        loading.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
        loading.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("WorkspacePage_LoadingContent"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        FileBodyPresenter.Content = loading;
    }

    private static TextBlock BuildNoteBody(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
    };

    private void RenderSelectedFile()
    {
        if (SelectedRequestPath() is not string relativePath)
        {
            FileBodyPresenter.Content = null;
            return;
        }

        if (!_fileContent.TryGetValue(relativePath, out var body))
        {
            ShowLoadingBody();
            return;
        }

        if (body.Kind == FileBodyKind.Missing || body.Content == null)
        {
            FileBodyPresenter.Content = BuildNoteBody(
                LocalizationHelper.GetString(body.Kind == FileBodyKind.ImageUnsupported
                    ? "WorkspacePage_ImagePreviewUnsupported"
                    : "WorkspacePage_MissingFile"));
            return;
        }

        var name = _entriesByPath.TryGetValue(relativePath, out var entry) ? entry.Name : relativePath;
        if (IsMarkdown(name) && _renderMarkdown)
        {
            FileBodyPresenter.Content = BuildMarkdownView(body.Content);
        }
        else
        {
            FileBodyPresenter.Content = BuildRawView(body.Content);
        }
    }

    private UIElement BuildRawView(string content)
    {
        return new TextBlock
        {
            Text = content,
            Style = (Style)Resources["WorkspaceCodeTextStyle"],
        };
    }

    private static bool IsMarkdown(string fileName)
    {
        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    // Minimal Markdown renderer: ATX headings, paragraphs, lists, fenced
    // code, inline `code`, **bold**, *italic*. Links render as label only.
    // Block styles come from Page.Resources so no raw FontSize is used.

    private UIElement BuildMarkdownView(string markdown)
    {
        var root = new StackPanel { Spacing = 0 };

        var h1 = (Style)Resources["WorkspaceMarkdownH1Style"];
        var h2 = (Style)Resources["WorkspaceMarkdownH2Style"];
        var h3 = (Style)Resources["WorkspaceMarkdownH3Style"];
        var para = (Style)Resources["WorkspaceMarkdownParagraphStyle"];
        var listItem = (Style)Resources["WorkspaceMarkdownListItemStyle"];
        var codeBlock = (Style)Resources["WorkspaceCodeBlockStyle"];

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing ```
                root.Children.Add(new TextBlock
                {
                    Text = code.ToString(),
                    Style = codeBlock
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (TryParseHeading(line, out var headingLevel, out var headingText))
            {
                var headingStyle = headingLevel switch
                {
                    1 => h1,
                    2 => h2,
                    _ => h3, // h3..h6 all share BodyStrong styling
                };
                root.Children.Add(BuildInlineTextBlock(headingText, headingStyle));
                i++;
                continue;
            }

            if (IsListItem(line, out _, out _))
            {
                while (i < lines.Length && IsListItem(lines[i], out var marker, out var body))
                {
                    root.Children.Add(BuildInlineTextBlock(marker + body, listItem));
                    i++;
                }
                continue;
            }

            // Paragraph: absorb continuation lines until a block-ending marker
            var sb = new StringBuilder(line);
            i++;
            while (i < lines.Length)
            {
                var next = lines[i];
                if (string.IsNullOrWhiteSpace(next)) break;
                if (TryParseHeading(next, out _, out _)) break;
                if (next.TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                if (IsListItem(next, out _, out _)) break;
                sb.Append(' ').Append(next.Trim());
                i++;
            }
            root.Children.Add(BuildInlineTextBlock(sb.ToString(), para));
        }

        return root;
    }

    private static TextBlock BuildInlineTextBlock(string text, Style style)
    {
        var tb = new TextBlock { Style = style };
        AppendInlineMarkdown(tb.Inlines, text);
        return tb;
    }

    // ATX heading: 1–6 leading `#`, then at least one space, then the text.
    // Optional trailing `#`s (closing sequence) are stripped.
    private static bool TryParseHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        int i = 0;
        while (i < line.Length && line[i] == '#' && i < 6) i++;
        if (i == 0 || i >= line.Length || line[i] != ' ') return false;
        level = i;
        var body = line[(i + 1)..].TrimEnd();
        // Strip optional closing # # # sequence
        int end = body.Length;
        while (end > 0 && body[end - 1] == '#') end--;
        if (end < body.Length && (end == 0 || body[end - 1] == ' '))
            body = body[..end].TrimEnd();
        text = body;
        return true;
    }

    private static bool IsListItem(string line, out string marker, out string body)
    {
        marker = "";
        body = "";
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            marker = "•  ";
            body = trimmed[2..];
            return true;
        }
        // numbered: digits + "." + space
        int dot = trimmed.IndexOf('.');
        if (dot > 0 && dot < trimmed.Length - 1 && trimmed[dot + 1] == ' ')
        {
            bool allDigits = true;
            for (int k = 0; k < dot; k++)
                if (!char.IsDigit(trimmed[k])) { allDigits = false; break; }
            if (allDigits)
            {
                marker = trimmed[..(dot + 1)] + "  ";
                body = trimmed[(dot + 2)..];
                return true;
            }
        }
        return false;
    }

    private static void AppendInlineMarkdown(InlineCollection inlines, string text)
    {
        text = StripLinks(text);

        int i = 0;
        var buf = new StringBuilder();
        void FlushPlain()
        {
            if (buf.Length == 0) return;
            inlines.Add(new Run { Text = buf.ToString() });
            buf.Clear();
        }

        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain();
                    inlines.Add(new Run
                    {
                        Text = text.Substring(i + 1, end - i - 1),
                        FontFamily = new FontFamily("Consolas")
                    });
                    i = end + 1;
                    continue;
                }
            }
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    FlushPlain();
                    var bold = new Bold();
                    bold.Inlines.Add(new Run { Text = text.Substring(i + 2, end - i - 2) });
                    inlines.Add(bold);
                    i = end + 2;
                    continue;
                }
            }
            // Italic: single asterisk, not part of a bold ** pair
            if (text[i] == '*' &&
                (i == 0 || text[i - 1] != '*') &&
                (i + 1 >= text.Length || text[i + 1] != '*'))
            {
                int end = -1;
                for (int k = i + 1; k < text.Length; k++)
                {
                    if (text[k] == '*' && (k + 1 >= text.Length || text[k + 1] != '*'))
                    {
                        end = k;
                        break;
                    }
                }
                if (end > i)
                {
                    FlushPlain();
                    var italic = new Italic();
                    italic.Inlines.Add(new Run { Text = text.Substring(i + 1, end - i - 1) });
                    inlines.Add(italic);
                    i = end + 1;
                    continue;
                }
            }

            buf.Append(text[i]);
            i++;
        }
        FlushPlain();
    }

    private static string StripLinks(string text)
    {
        // [label](url) → label; non-nested only.
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                int closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    int closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket)
                    {
                        sb.Append(text, i + 1, closeBracket - i - 1);
                        i = closeParen + 1;
                        continue;
                    }
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadAsync();
    }

    private void ParentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserParentPath is not null)
            BrowseToPath(_browserParentPath);
    }

    private void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var data = new DataPackage();
            data.SetText(path);
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[WorkspacePage] Clipboard copy failed: {ex.Message}");
            BrowserNoticeText.Text = LocalizationHelper.GetString("WorkspacePage_CopyPathFailed");
            BrowserNoticeText.Visibility = Visibility.Visible;
        }
    }

    private void BrowseToPath(string? path)
    {
        _browserPath = path ?? string.Empty;
        _suppressSearchTextChanged = true;
        try
        {
            SearchBox.Text = string.Empty;
            _searchQuery = string.Empty;
        }
        finally
        {
            _suppressSearchTextChanged = false;
        }
        _ = LoadAsync();
    }

    private void UpdateBrowserChrome(WorkspaceFilesModel.WorkspaceListState state)
    {
        bool searching = !string.IsNullOrWhiteSpace(state.BrowserSearch);
        ParentFolderButton.IsEnabled = !searching && state.BrowserParentPath is not null;
        CurrentFolderText.Text = searching
            ? LocalizationHelper.Format("WorkspacePage_SearchResultsPath", state.BrowserSearch.Trim())
            : string.IsNullOrEmpty(state.BrowserPath)
                ? LocalizationHelper.GetString("WorkspacePage_RootFolder")
                : state.BrowserPath;

        if (state.BrowserTruncated)
        {
            BrowserNoticeText.Text = LocalizationHelper.GetString("WorkspacePage_BrowserTruncated");
            BrowserNoticeText.Visibility = Visibility.Visible;
        }
        else
        {
            BrowserNoticeText.Visibility = Visibility.Collapsed;
        }
    }

    private void RepairLink_Click(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void HideFallback()
    {
        FallbackInfoBar.IsOpen = false;
        ScopeInfoBar.IsOpen = false;
        RepairLink.Visibility = Visibility.Collapsed;
    }

    private void ShowScopeDisclosure(WorkspaceGatewaySource source)
    {
        var resourceKey = WorkspaceScopeDisclosure.ResourceKeyForList(source);
        if (resourceKey is null)
        {
            ScopeInfoBar.IsOpen = false;
            return;
        }

        ScopeInfoBar.Message = LocalizationHelper.GetString(resourceKey);
        ScopeInfoBar.IsOpen = true;
    }

    private void ResetScopeDisclosure()
    {
        _workspaceSource = WorkspaceGatewaySource.AgentWorkspace;
        _sessionReloadGate.Reset();
        HideFallback();
    }

    private void ShowLoadError()
    {
        EndLoading();
        ClearFiles();
        ScopeInfoBar.IsOpen = false;
        FallbackInfoBar.Severity = InfoBarSeverity.Warning;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_LoadErrorMessage");
        RepairLink.Visibility = Visibility.Collapsed;
        FallbackInfoBar.IsOpen = true;
    }

    // Gateway unreachable: offer a one-tap route to Connection settings so the
    // user can repair pairing instead of hitting a silent dead end.
    private void ShowDisconnected()
    {
        EndLoading();
        ClearFiles();
        WorkspacePathText.Text = string.Empty;
        ScopeInfoBar.IsOpen = false;
        FallbackInfoBar.Severity = InfoBarSeverity.Warning;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_DisconnectedMessage");
        RepairLink.Visibility = Visibility.Visible;
        FallbackInfoBar.IsOpen = true;
    }

    // Connected gateway with no supported workspace source. Keep the repair
    // affordance distinct from transient request failures.
    private void ShowUnsupported()
    {
        EndLoading();
        ScopeInfoBar.IsOpen = false;
        FallbackInfoBar.Severity = InfoBarSeverity.Warning;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_UnsupportedMessage");
        RepairLink.Visibility = Visibility.Visible;
        FallbackInfoBar.IsOpen = true;
        BodyGrid.Visibility = Visibility.Collapsed;
    }

    private void ShowNoFiles()
    {
        FallbackInfoBar.Severity = InfoBarSeverity.Informational;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_NoFilesMessage");
        RepairLink.Visibility = Visibility.Collapsed;
        FallbackInfoBar.IsOpen = true;
        BodyGrid.Visibility = Visibility.Collapsed;
    }

    private static string? GetString(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? GetBool(JsonElement item, string name)
    {
        return item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }
}
