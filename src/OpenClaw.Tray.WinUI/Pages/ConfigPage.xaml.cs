using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class ConfigPage : Page
{
    private static readonly JsonElement s_emptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    private const double JsonPreviewAutoCollapseWidth = 1040;
    private const double JsonPreviewAutoExpandWidth = 1120;
    private const double JsonPreviewMinWidth = 340;

    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private static string L(string key) => LocalizationHelper.GetString(key);
    private static string Lf(string key, params object?[] args) => LocalizationHelper.Format(key, args);
    private AppState? _appState;
    private JsonElement? _lastConfig;
    private JsonElement? _lastSchema;
    private ConfigEditorSnapshot _serverSnapshot = ConfigEditorSnapshot.Empty;
    private ConfigEditorSnapshot _editSnapshot = ConfigEditorSnapshot.Empty;
    private IOperatorGatewayClient? _permissionClient;
    private GatewayConnectionManager? _observedConnectionManager;

    private string _selectedPath = "";
    private string? _searchPath;
    private (string Section, string Field)? _searchField;
    private string _searchText = "";
    private bool _showSchemaFallback;
    private bool _loading;
    private bool _saving;
    private bool _observing;
    private bool _refreshConfigOnNextInitialize;
    private bool _configLoadFailureVisible;
    private bool _draftConnectionChanged;
    private int _configLoadGeneration;
    private ConfigEditorLoadTicket? _snapshotTicket;
    private bool _jsonPreviewVisible = true;
    private bool _jsonPreviewCollapsedByUser;
    private bool _refreshConfigAfterReconnect;
    private bool _refreshConfigWhenGatewayAvailable;
    private ContentDialog? _reconnectDialog;
    private TextBlock? _reconnectDialogMessage;
    private GridLength _jsonPreviewExpandedWidth = new(360);
    private int _detailRenderVersion;
    private string _selectedJsonCopyText = "";
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _reconnectCompletionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _reconnectTimeoutTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly DispatcherTimer _statusDismissTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private readonly Dictionary<TreeViewNode, (string Path, JsonElement Element)> _nodeMap = new();
    private readonly Dictionary<string, object?> _pendingChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _validationErrors = new(StringComparer.Ordinal);

    public ConfigPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Unloaded += (_, _) =>
        {
            _searchPath = null;
            _searchField = null;
            CancelDetailRender();
            _observing = false;
            _configLoadGeneration++;
            _loading = false;
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            SubscribeConnectionManager(null);
            UnsubscribePermissionClient();
            _searchDebounceTimer.Stop();
            _reconnectCompletionTimer.Stop();
            _reconnectTimeoutTimer.Stop();
            _statusDismissTimer.Stop();
        };
        StatusInfoBar.Closed += (_, _) =>
        {
            _statusDismissTimer.Stop();
            _configLoadFailureVisible = false;
            SetInfoBarOpen(StatusInfoBar, false);
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RenderTree();
            RestoreSelectedDetail();
        };
        _reconnectCompletionTimer.Tick += (_, _) =>
        {
            _reconnectCompletionTimer.Stop();
            CompleteReconnectIfReady();
        };
        _reconnectTimeoutTimer.Tick += (_, _) =>
        {
            _reconnectTimeoutTimer.Stop();
            HandleReconnectTimeout();
        };
        _statusDismissTimer.Tick += (_, _) =>
        {
            _statusDismissTimer.Stop();
            SetInfoBarOpen(StatusInfoBar, false);
        };
    }

    public void Initialize()
    {
        _observing = true;
        SubscribeConnectionManager(CurrentApp.ConnectionManager);
        if (_appState != null)
            _appState.PropertyChanged -= OnAppStateChanged;

        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        SubscribePermissionClient(CurrentApp.GatewayClient);
        Logger.Info("[ConfigPage] Initialize");
        if (CompleteReconnectIfReady())
            return;

        if (_refreshConfigOnNextInitialize || !_serverSnapshot.HasRoot || !_lastSchema.HasValue || !SnapshotMatchesCurrentConnection())
        {
            _refreshConfigOnNextInitialize = false;
            RefreshFromGateway();
        }
        else
        {
            SetInfoBarOpen(ConnectionInfoBar, CurrentApp.GatewayClient == null);
            UpdatePermissionBanner();
            UpdateMetaAndButtons();
        }
    }

    private bool ApplyLoadedConfig(JsonElement config, JsonElement schema)
    {
        var previousConfig = _lastConfig;
        var previousSchema = _lastSchema;
        var previousServer = _serverSnapshot;
        var previousEdit = _editSnapshot;
        try
        {
            _lastConfig = config;
            _lastSchema = schema;
            _serverSnapshot = ConfigEditorModel.CaptureSnapshot(config);
            if (_pendingChanges.Count == 0)
                _editSnapshot = _serverSnapshot;

            if (config.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                ConfigSubtitle.Text = Lf("ConfigPage_EditingPathFormat", path.GetString());

            UpdateConnectionBanner();
            UpdatePermissionBanner();
            RenderTree();
            RestoreSelectedDetail();
            UpdateSelectedJsonPreviewForCurrentSelection();
            if (_configLoadFailureVisible)
            {
                _configLoadFailureVisible = false;
                SetInfoBarOpen(StatusInfoBar, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastConfig = previousConfig;
            _lastSchema = previousSchema;
            _serverSnapshot = previousServer;
            _editSnapshot = previousEdit;
            // Exception messages can embed config values. Record only the type.
            Logger.Error($"[ConfigPage] Config rendering failed ({ex.GetType().Name})");
            ShowConfigRenderError(L("ConfigPage_RenderConfigUnavailableTitle"),
                L("ConfigPage_RenderConfigUnavailableMessage"));
            return false;
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.SelfProfileRevision):
                InvalidateLoadedConnection();
                RefreshFromGateway();
                break;
            case nameof(AppState.Status):
                SubscribePermissionClient(CurrentApp.GatewayClient);
                UpdateConnectionBanner();
                UpdatePermissionBanner();
                var configPermissionState = GetConfigPermissionState();
                if (configPermissionState == ConfigPermissionState.Disconnected)
                {
                    InvalidateLoadedConnection();
                }
                if (configPermissionState == ConfigPermissionState.Disconnected && _refreshConfigAfterReconnect)
                {
                    ShowStatus(
                        L("ConfigPage_StatusGatewayRestartingTitle"),
                        L("ConfigPage_StatusGatewayRestartingMessage"),
                        InfoBarSeverity.Informational);
                    ShowReconnectDialog(LocalizationHelper.GetString("ConfigPage_ReconnectDialogWaiting"));
                }
                else if (_refreshConfigAfterReconnect &&
                         configPermissionState is (ConfigPermissionState.ReadOnly or ConfigPermissionState.ReadWrite))
                {
                    CompleteReconnectIfReady();
                    return;
                }
                UpdateMetaAndButtons();
                break;
        }
    }

    private void SubscribeConnectionManager(GatewayConnectionManager? manager)
    {
        if (ReferenceEquals(_observedConnectionManager, manager))
            return;

        // Unload detaches the owner actually observed, without resolving a
        // production App from an embedding host or a changed application owner.
        if (_observedConnectionManager != null)
            _observedConnectionManager.OperatorClientChanged -= OnOperatorClientChanged;
        _observedConnectionManager = manager;
        if (_observedConnectionManager != null)
            _observedConnectionManager.OperatorClientChanged += OnOperatorClientChanged;
    }

    private void SubscribePermissionClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(_permissionClient, client))
            return;

        UnsubscribePermissionClient();
        _permissionClient = client;
        if (_permissionClient != null)
        {
            _permissionClient.HandshakeSucceeded += OnGatewayHandshakeSucceeded;
        }
    }

    private void UnsubscribePermissionClient()
    {
        if (_permissionClient != null)
        {
            _permissionClient.HandshakeSucceeded -= OnGatewayHandshakeSucceeded;
        }
        _permissionClient = null;
    }

    private void OnGatewayHandshakeSucceeded(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (!_observing || !ReferenceEquals(sender, CurrentApp.GatewayClient))
                return;
            if (!SnapshotMatchesCurrentConnection())
                InvalidateLoadedConnection();
            UpdatePermissionBanner();
            var permissionState = GetConfigPermissionState();
            if (CompleteReconnectIfReady())
                return;

            if (permissionState is ConfigPermissionState.ReadOnly or ConfigPermissionState.ReadWrite &&
                !_loading &&
                (!_serverSnapshot.HasRoot || !_lastSchema.HasValue))
            {
                RefreshFromGateway();
            }
            else
            {
                UpdateMetaAndButtons();
            }
        });
    }

    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (!_observing || !ReferenceEquals(e.NewClient, CurrentApp.GatewayClient)) return;
            SubscribePermissionClient(CurrentApp.GatewayClient);
            RefreshFromGateway();
        });
    }

    // AppState retains this revision while the cached page is unloaded.
    private bool SnapshotMatchesCurrentConnection() =>
        _snapshotTicket is { } ticket && ticket.MatchesConnection(CurrentApp.GatewayClient,
            CurrentApp.GatewayClient?.SessionMutationConnectionEpoch, _appState?.SelfProfileRevision ?? 0);

    private void InvalidateLoadedConnection()
    {
        _configLoadGeneration++;
        _detailRenderVersion++;
        _loading = false;
        _draftConnectionChanged |= _pendingChanges.Count > 0;
        _snapshotTicket = null;
        _lastConfig = null;
        _lastSchema = null;
        _serverSnapshot = ConfigEditorSnapshot.Empty;
        if (!_draftConnectionChanged)
            _editSnapshot = ConfigEditorSnapshot.Empty;
        _selectedPath = "";
        _showSchemaFallback = true;
        _nodeMap.Clear();
        ConfigTree.RootNodes.Clear();
        DetailPanel.Children.Clear();
        DetailLoadingOverlay.Visibility = Visibility.Collapsed;
        DetailBreadcrumb.Text = "";
        DetailPath.Text = "";
        DetailType.Text = "";
        ResetSectionButton.Visibility = Visibility.Collapsed;
        ConfigSubtitle.Text = L("ConfigSubtitle.Text");
        RenderJsonDiff("{}", "{}");
        ApplyPaneVisibility();
    }

    private void RenderTree()
    {
        _nodeMap.Clear();
        ConfigTree.RootNodes.Clear();

        if (_lastSchema.HasValue)
        {
            _showSchemaFallback = false;
            ApplyPaneVisibility();

            var schemaRoot = GetSchemaRoot(_lastSchema.Value);
            var preview = GetPreviewSnapshot();
            var configRoot = preview.HasRoot ? preview.Root : (JsonElement?)null;

            var rootElement = configRoot ?? s_emptyObject;
            var rootNode = new TreeViewNode { IsExpanded = true };
            BuildSchemaTreeNodes(rootNode.Children, schemaRoot, configRoot, "");
            var rootVisible = string.IsNullOrWhiteSpace(_searchText) ||
                              MatchesSearch("", schemaRoot) ||
                              rootNode.Children.Count > 0;
            if (rootVisible)
            {
                var dirtyCount = CountPathsUnder("", _pendingChanges.Keys);
                var invalidCount = CountPathsUnder("", _validationErrors.Keys);
                var label = "Full config";
                if (dirtyCount > 0) label += $" • {dirtyCount} changed";
                if (invalidCount > 0) label += $" • {invalidCount} issue{(invalidCount == 1 ? "" : "s")}";
                rootNode.Content = $"📁 {label}";
                _nodeMap[rootNode] = ("", rootElement);
                ConfigTree.RootNodes.Add(rootNode);
            }
            UpdateSearchMetaText();
        }
        else
        {
            _showSchemaFallback = true;
            ApplyPaneVisibility();
            UpdateSearchMetaText();
        }
    }

    private bool BuildSchemaTreeNodes(IList<TreeViewNode> parent, JsonElement schema, JsonElement? config, string basePath)
    {
        var anyVisible = false;
        foreach (var section in ConfigEditorModel.ReadSchemaSections(schema, config)
                     .OrderBy(section => section.Name, StringComparer.OrdinalIgnoreCase))
        {
            var path = string.IsNullOrEmpty(basePath) ? section.Name : $"{basePath}.{section.Name}";
            var configValue = section.Config;

            var node = new TreeViewNode();
            var childVisible = BuildSchemaTreeNodes(node.Children, section.Schema, configValue, path);
            var selfMatches = MatchesSearch(path, section.Schema);
            if (!selfMatches && !childVisible && !string.IsNullOrWhiteSpace(_searchText))
                continue;

            var dirtyCount = CountPathsUnder(path, _pendingChanges.Keys);
            var invalidCount = CountPathsUnder(path, _validationErrors.Keys);
            var label = FriendlyLabel(section.Name);
            if (dirtyCount > 0) label += $" • {dirtyCount} changed";
            if (invalidCount > 0) label += $" • {invalidCount} issue{(invalidCount == 1 ? "" : "s")}";

            node.Content = $"📁 {label}";
            node.IsExpanded = !string.IsNullOrWhiteSpace(_searchText) && (selfMatches || childVisible);
            _nodeMap[node] = (path, configValue ?? s_emptyObject);
            parent.Add(node);
            anyVisible = true;
        }

        return anyVisible;
    }

    private void RestoreSelectedDetail()
    {
        var preview = GetPreviewSnapshot();
        if (!preview.HasRoot || !_lastSchema.HasValue)
            return;

        if (_searchPath is { } requested)
        {
            _searchPath = null;
            var section = ConfigEditorModel.ResolveSearchSection(GetSchemaRoot(_lastSchema.Value), requested);
            if (section is null)
            {
                _searchField = null;
                ShowStatus(L("Search_TargetUnavailable"), "", InfoBarSeverity.Warning);
                return;
            }
            _selectedPath = section;
            _searchField = requested == section ? null :
                (section, section.Length == 0 ? requested : requested[(section.Length + 1)..]);
        }

        if (TryGetElementAtPath(_serverSnapshot.Root, _selectedPath, out var element))
        {
            ShowDetail(_selectedPath, element);
            return;
        }

        if (ConfigEditorModel.ResolveSearchSection(GetSchemaRoot(_lastSchema.Value), _selectedPath) == _selectedPath &&
            ConfigEditorModel.CanMaterializeSchemaSection(_serverSnapshot.Root, _selectedPath))
        {
            ShowDetail(_selectedPath, s_emptyObject);
            return;
        }
        _selectedPath = "";
        ShowDetail("", preview.Root);
    }

    internal void RevealSearchPath(string path)
    {
        CancelDetailRender();
        _searchField = null;
        _searchPath = path;
        RestoreSelectedDetail();
        if (_searchField is null) DetailPanel.StartBringIntoView();
    }

    private void ShowDetail(string path, JsonElement element)
    {
        if (!_lastSchema.HasValue)
            return;

        if (_searchField is { } target && target.Section != path) _searchField = null;
        DetailPanel.Children.Clear();
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        _selectedPath = path;
        DetailBreadcrumb.Text = BuildBreadcrumb(path);
        DetailPath.Text = string.IsNullOrEmpty(path) ? "Full config" : FriendlyLabel(path.Split('.').Last());

        var nodeSchema = ConfigEditorModel.ResolveSchemaAtPath(GetSchemaRoot(_lastSchema.Value), path);
        var displayElement = ConfigEditorModel.ApplyRelativeChanges(element, path, _pendingChanges);
        var sectionDirty = CountPathsUnder(path, _pendingChanges.Keys) > 0;
        ResetSectionButton.Visibility = sectionDirty ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectedJsonPreview(path, element, displayElement);

        if (string.IsNullOrEmpty(path))
        {
            DetailType.Text = displayElement.ValueKind == JsonValueKind.Object
                ? $"Root object · {displayElement.EnumerateObject().Count()} top-level properties"
                : displayElement.ValueKind.ToString();
            if (TryBuildRootLeafSchema(nodeSchema, out var leafSchema))
            {
                var editor = new SchemaConfigEditor();
                editor.LoadSchema(leafSchema, displayElement);
                editor.IsEnabled = !IsConfigEditingLocked(GetConfigPermissionState());
                editor.ConfigChanged += (_, args) => ApplyEditorChanges(path, args);
                DetailPanel.Children.Add(editor);
                RevealSearchField(editor);
            }
            else
            {
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = "Select a child section to edit fields. The full root config is available in the JSON preview.",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                });
            }
            return;
        }

        if (nodeSchema.HasValue && nodeSchema.Value.TryGetProperty("description", out var desc) &&
            desc.ValueKind == JsonValueKind.String)
        {
            DetailType.Text = desc.GetString() ?? "";
        }
        else
        {
            DetailType.Text = displayElement.ValueKind == JsonValueKind.Object
                ? $"Object · {displayElement.EnumerateObject().Count()} properties"
                : displayElement.ValueKind.ToString();
        }

        if (nodeSchema.HasValue && displayElement.ValueKind == JsonValueKind.Object)
        {
            var editor = new SchemaConfigEditor();
            editor.LoadSchema(nodeSchema.Value, displayElement);
            editor.IsEnabled = !IsConfigEditingLocked(GetConfigPermissionState());
            editor.ConfigChanged += (_, args) => ApplyEditorChanges(path, args);
            DetailPanel.Children.Add(editor);
            RevealSearchField(editor);
            return;
        }

        DetailPanel.Children.Add(new TextBlock
        {
            Text = "This section is shown in the JSON preview because the schema shape is not editable in the form yet.",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void RevealSearchField(SchemaConfigEditor editor)
    {
        if (_searchField is not { } target) return;
        editor.Loaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!IsLoaded || !DetailPanel.Children.Contains(editor) || _searchField != target ||
                    _selectedPath != target.Section) return;
                _searchField = null;
                editor.UpdateLayout();
                if (!editor.RevealField(target.Field))
                    ShowStatus(L("Search_TargetUnavailable"), "", InfoBarSeverity.Warning);
            });
        };
    }

    private void ApplyEditorChanges(string sectionPath, SchemaConfigChangedEventArgs args)
    {
        if (IsConfigEditingLocked(GetConfigPermissionState()))
            return;

        if (_pendingChanges.Count == 0 && _serverSnapshot.HasRoot)
            _editSnapshot = _serverSnapshot;

        foreach (var (path, value) in args.Changes)
        {
            var fullPath = string.IsNullOrEmpty(sectionPath) ? path : $"{sectionPath}.{path}";
            if (ReferenceEquals(value, SchemaConfigEditor.RemovePendingValue))
            {
                _pendingChanges.Remove(fullPath);
                _validationErrors.Remove(fullPath);
            }
            else
            {
                _pendingChanges[fullPath] = value;
            }
        }

        RemoveSectionEntries(sectionPath, _validationErrors);
        foreach (var (path, error) in args.ValidationErrors)
        {
            var fullPath = string.IsNullOrEmpty(sectionPath) ? path : $"{sectionPath}.{path}";
            _validationErrors[fullPath] = error;
        }

        RenderTree();
        UpdateSelectedJsonPreviewForCurrentSelection();
        UpdateMetaAndButtons();
    }

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnSaveAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnSave));

    private async Task OnSaveAsync()
    {
        if (_saving) return;
        if (_draftConnectionChanged || !SnapshotMatchesCurrentConnection() ||
            _snapshotTicket?.Epoch is not long connectionEpoch ||
            _snapshotTicket?.WriteAdmission is not { IsCurrent: true } writeAdmission)
        {
            InvalidateLoadedConnection();
            ShowDraftConnectionChanged();
            UpdateMetaAndButtons();
            return;
        }

        if (_pendingChanges.Count == 0)
        {
            ShowStatus(
                L("ConfigPage_StatusNoChangesTitle"),
                L("ConfigPage_StatusNoChangesMessage"),
                InfoBarSeverity.Informational);
            return;
        }

        if (_validationErrors.Count > 0)
        {
            var first = _validationErrors.First();
            ShowStatus(
                L("ConfigPage_StatusFixValidationErrorsTitle"),
                Lf("ConfigPage_StatusValidationErrorMessageFormat", first.Key, first.Value),
                InfoBarSeverity.Error);
            UpdateMetaAndButtons();
            return;
        }

        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            ShowStatus(
                L("ConfigPage_StatusNotConnectedTitle"),
                L("ConfigPage_StatusNotConnectedMessage"),
                InfoBarSeverity.Error);
            UpdateMetaAndButtons();
            return;
        }

        if (!OperatorScopeHelper.CanWriteConfig(client.GrantedOperatorScopes))
        {
            ShowStatus(
                L("ConfigPage_ConfigIsReadOnly"),
                L("ConfigPage_StatusReadOnlyMessage"),
                InfoBarSeverity.Warning);
            UpdatePermissionBanner();
            UpdateMetaAndButtons();
            return;
        }

        var saveBase = _editSnapshot.HasRoot ? _editSnapshot : _serverSnapshot;
        if (!saveBase.HasRoot)
        {
            ShowStatus(
                L("ConfigPage_StatusConfigNotLoadedTitle"),
                L("ConfigPage_StatusConfigNotLoadedMessage"),
                InfoBarSeverity.Error);
            UpdateMetaAndButtons();
            return;
        }

        _saving = true;
        UpdateMetaAndButtons();
        ShowStatus(
            L("ConfigPage_StatusSavingConfigTitle"),
            Lf("ConfigPage_StatusSavingConfigMessageFormat", _pendingChanges.Count),
            InfoBarSeverity.Informational);

        try
        {
            var updated = ConfigEditorModel.ApplyChanges(saveBase.Root, _pendingChanges);
            var saveIdentity = _snapshotTicket!.Value;
            var result = await client.PatchConfigOnConnectionAsync(updated, saveBase.BaseHash, connectionEpoch, writeAdmission);
            if (!saveIdentity.MatchesConnection(CurrentApp.GatewayClient, client.SessionMutationConnectionEpoch,
                    _appState?.SelfProfileRevision ?? 0))
            {
                // This is UI acknowledgement admission only. Do not let an old
                // save clear a retained draft or restart UI on a new connection.
                InvalidateLoadedConnection();
                ShowDraftConnectionChanged();
                return;
            }
            if (!result.Ok)
            {
                if (result.LooksLikeStaleBaseHash)
                {
                    _editSnapshot = ConfigEditorSnapshot.Empty;
                    RefreshFromGateway();
                    ShowStatus(
                        L("ConfigPage_StatusStaleConfigTitle"),
                        Lf(
                            "ConfigPage_StatusStaleConfigMessageFormat",
                            result.Error ?? L("ConfigPage_StatusStaleConfigDefaultDetail")),
                        InfoBarSeverity.Warning);
                }
                else
                {
                    ShowStatus(
                        L("ConfigPage_StatusSaveFailedTitle"),
                        result.Error ?? L(result.TimedOut
                            ? "ConfigPage_BannerGatewayNoResponse"
                            : "ConfigPage_StatusSaveRejectedMessage"),
                        InfoBarSeverity.Error);
                }
                return;
            }

            _pendingChanges.Clear();
            _validationErrors.Clear();
            _editSnapshot = ConfigEditorSnapshot.Empty;
            _refreshConfigAfterReconnect = true;
            _refreshConfigWhenGatewayAvailable = true;
            ShowReconnectDialog(LocalizationHelper.GetString("ConfigPage_ReconnectDialogAccepted"));
            ShowStatus(
                L("ConfigPage_StatusGatewayRestartingTitle"),
                L("ConfigPage_StatusGatewayRestartingMessage"),
                InfoBarSeverity.Informational);
            _reconnectCompletionTimer.Stop();
            _reconnectCompletionTimer.Start();
            _reconnectTimeoutTimer.Stop();
            _reconnectTimeoutTimer.Start();
        }
        catch (Exception ex)
        {
            ShowStatus(
                L("ConfigPage_StatusSaveFailedTitle"),
                Lf("ConfigPage_StatusSaveExceptionMessageFormat", ex.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            _saving = false;
            UpdateMetaAndButtons();
            RestoreSelectedDetail();
            UpdateSelectedJsonPreviewForCurrentSelection();
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshFromGateway();
    }

    private void RefreshFromGateway()
    {
        // A save may settle after navigation. Do not start a load whose result
        // observation will necessarily reject, then strand the cached spinner.
        if (!_observing)
        {
            _refreshConfigOnNextInitialize = true;
            return;
        }
        _configLoadGeneration++;
        _loading = false;
        var client = CurrentApp.GatewayClient;
        if (_snapshotTicket.HasValue && !SnapshotMatchesCurrentConnection())
            InvalidateLoadedConnection();
        UpdateConnectionBanner();
        var permissionState = GetConfigPermissionState();
        if (permissionState == ConfigPermissionState.NoRead)
            ClearConfigViewForNoRead();
        if (_draftConnectionChanged)
            ShowDraftConnectionChanged();
        else if (client != null && permissionState is ConfigPermissionState.ReadOnly or ConfigPermissionState.ReadWrite)
        {
            // Reads belong to this page request, not unrelated AppState events.
            var ticket = new ConfigEditorLoadTicket(++_configLoadGeneration, client,
                client.SessionMutationConnectionEpoch, _appState?.SelfProfileRevision ?? 0, client.ConfigWriteAdmission);
            _loading = true;
            _ = LoadConfigAsync(client, ticket);
        }
        UpdatePermissionBanner();
        UpdateMetaAndButtons();
    }

    private async Task LoadConfigAsync(IOperatorGatewayClient client, ConfigEditorLoadTicket ticket)
    {
        var result = await ConfigEditorLoader.LoadAsync((method, timeout) =>
            client.SendWizardRequestAsync(method, parameters: null, timeoutMs: timeout)).ConfigureAwait(false);
        RunOnUiThread(() =>
        {
            if (!ticket.IsCurrent(_observing, _configLoadGeneration, CurrentApp.GatewayClient,
                    client.SessionMutationConnectionEpoch, _appState?.SelfProfileRevision ?? 0,
                    client.IsConnectedToGateway && client.HasHandshakeSnapshot))
                return;
            _loading = false;
            if (!OperatorScopeHelper.CanReadConfig(client.GrantedOperatorScopes))
            {
                ClearConfigViewForNoRead();
                UpdatePermissionBanner();
            }
            else if (result.Succeeded)
            {
                _snapshotTicket = ticket;
                if (ApplyLoadedConfig(result.Config!.Value, result.Schema!.Value) && _appState != null)
                {
                    // Keep the existing shared cache authoritative for other pages.
                    _appState.Config = result.Config;
                    _appState.ConfigSchema = result.Schema;
                }
            }
            else
            {
                var schemaFailed = !result.Schema.HasValue;
                ShowStatus(L(schemaFailed ? "ConfigPage_RenderSchemaUnavailableTitle" : "ConfigPage_RenderConfigUnavailableTitle"),
                    L(schemaFailed ? "ConfigPage_RenderSchemaUnavailableMessage" : "ConfigPage_RenderConfigUnavailableMessage"),
                    InfoBarSeverity.Error);
                _configLoadFailureVisible = true;
                // A failed refresh leaves the last valid form and draft intact.
                if (!_serverSnapshot.HasRoot || !_lastSchema.HasValue)
                {
                    _showSchemaFallback = true;
                    ApplyPaneVisibility();
                }
            }
            UpdateMetaAndButtons();
        });
    }

    private void ShowDraftConnectionChanged() => ShowStatus(L("ConfigPage_StatusConfigNotLoadedTitle"),
        L("ConfigPage_DraftConnectionChanged"), InfoBarSeverity.Warning);

    private void UpdateConnectionBanner()
    {
        SetInfoBarOpen(ConnectionInfoBar, GetConfigPermissionState() == ConfigPermissionState.Disconnected);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = ConfigSearchBox.Text.Trim();
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnDiscardChanges(object sender, RoutedEventArgs e)
    {
        _pendingChanges.Clear();
        _validationErrors.Clear();
        _editSnapshot = ConfigEditorSnapshot.Empty;
        var reload = _draftConnectionChanged;
        _draftConnectionChanged = false;
        ShowStatus(
            L("ConfigPage_StatusChangesDiscardedTitle"),
            L("ConfigPage_StatusChangesDiscardedMessage"),
            InfoBarSeverity.Informational);
        if (reload)
            RefreshFromGateway();
        else
        {
            RenderTree();
            RestoreSelectedDetail();
            UpdateSelectedJsonPreviewForCurrentSelection();
            UpdateMetaAndButtons();
        }
    }

    private void OnResetSection(object sender, RoutedEventArgs e)
    {
        RemoveSectionEntries(_selectedPath, _pendingChanges);
        RemoveSectionEntries(_selectedPath, _validationErrors);
        var sectionLabel = string.IsNullOrEmpty(_selectedPath) ? L("ConfigPage_FullConfig") : _selectedPath;
        ShowStatus(
            L("ConfigPage_StatusSectionResetTitle"),
            Lf("ConfigPage_StatusSectionResetMessageFormat", sectionLabel),
            InfoBarSeverity.Informational);
        RenderTree();
        RestoreSelectedDetail();
        UpdateSelectedJsonPreviewForCurrentSelection();
        UpdateMetaAndButtons();
    }

    private void OnOpenConnection(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).Navigate("connection");
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).OpenDashboard("config");
    }

    private void OnCopySelectedJson(object sender, RoutedEventArgs e)
    {
        var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_selectedJsonCopyText);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ShowStatus(
            L("ConfigPage_StatusCopiedJsonDiffTitle"),
            L("ConfigPage_StatusCopiedJsonDiffMessage"),
            InfoBarSeverity.Success);
    }

    private void OnToggleJsonPreview(object sender, RoutedEventArgs e)
    {
        _jsonPreviewCollapsedByUser = _jsonPreviewVisible;
        _jsonPreviewVisible = !_jsonPreviewVisible;
        ApplyJsonPreviewVisibility();
    }

    private void OnSchemaTreeGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_jsonPreviewVisible && e.NewSize.Width < JsonPreviewAutoCollapseWidth)
        {
            _jsonPreviewVisible = false;
            ApplyJsonPreviewVisibility();
        }
        else if (!_jsonPreviewVisible && !_jsonPreviewCollapsedByUser && e.NewSize.Width >= JsonPreviewAutoExpandWidth)
        {
            _jsonPreviewVisible = true;
            ApplyJsonPreviewVisibility();
        }
    }

    private void ApplyPaneVisibility()
    {
        if (EditorPane is null || NoSchemaPanel is null) return;

        EditorPane.Visibility = !_showSchemaFallback ? Visibility.Visible : Visibility.Collapsed;
        NoSchemaPanel.Visibility = _showSchemaFallback ? Visibility.Visible : Visibility.Collapsed;
        ConfigSearchBox.Visibility = !_showSchemaFallback ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSelectedJsonPreviewForCurrentSelection()
    {
        if (SelectedJsonDiffText is null)
            return;

        var previewSnapshot = GetPreviewSnapshot();
        if (!previewSnapshot.HasRoot)
        {
            SelectedJsonCaption.Text = "Select a section to preview its JSON.";
            RenderJsonDiff("{}", "{}");
            return;
        }

        var element = TryGetElementAtPath(previewSnapshot.Root, _selectedPath, out var current)
            ? current
            : s_emptyObject;
        UpdateSelectedJsonPreview(_selectedPath, element, ConfigEditorModel.ApplyRelativeChanges(element, _selectedPath, _pendingChanges));
    }

    private ConfigEditorSnapshot GetPreviewSnapshot() =>
        _pendingChanges.Count > 0 && _editSnapshot.HasRoot ? _editSnapshot : _serverSnapshot;

    private void UpdateSelectedJsonPreview(string path, JsonElement gatewayElement, JsonElement proposedElement)
    {
        if (SelectedJsonDiffText is null)
            return;

        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var gatewayJson = JsonSerializer.Serialize(gatewayElement, options);
            var dirtyCount = CountPathsUnder(path, _pendingChanges.Keys);
            var label = string.IsNullOrEmpty(path) ? "Full config" : path;
            if (string.IsNullOrEmpty(path))
            {
                var proposedJson = dirtyCount > 0
                    ? JsonSerializer.Serialize(ConfigEditorModel.ApplyChanges(gatewayElement, _pendingChanges), options)
                    : gatewayJson;
                RenderJsonDiff(gatewayJson, proposedJson);
                SelectedJsonCaption.Text = dirtyCount > 0
                    ? $"{label}: {dirtyCount} unsaved change{(dirtyCount == 1 ? "" : "s")} highlighted below."
                    : $"{label}: no unsaved edits.";
            }
            else
            {
                var proposedJson = JsonSerializer.Serialize(proposedElement, options);
                RenderJsonDiff(gatewayJson, proposedJson);
                SelectedJsonCaption.Text = dirtyCount > 0
                    ? $"{label}: {dirtyCount} unsaved change{(dirtyCount == 1 ? "" : "s")} highlighted below."
                    : $"{label}: proposed value matches the last loaded gateway config.";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ConfigPage] Failed to build selected JSON preview: {ex.Message}");
            SelectedJsonCaption.Text = "Selected section JSON preview is unavailable.";
            RenderJsonDiff(gatewayElement.GetRawText(), proposedElement.GetRawText());
        }
    }

    private void RenderJsonDiff(string gatewayJson, string proposedJson)
    {
        SelectedJsonDiffText.Blocks.Clear();
        var paragraph = new Paragraph();
        var defaultBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var addedBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGreen);
        var removedBrush = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);

        var copyLines = new List<string>();
        void AddLine(JsonDiffLine line)
        {
            var changeBrush = line.Kind == JsonDiffLineKind.Removed ? removedBrush : addedBrush;
            copyLines.Add(line.CopyText);
            paragraph.Inlines.Add(new Run
            {
                Text = line.Prefix,
                Foreground = line.Kind == JsonDiffLineKind.Unchanged ? defaultBrush : changeBrush
            });
            foreach (var segment in line.Segments)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = segment.Text,
                    Foreground = line.Kind == JsonDiffLineKind.Unchanged || !segment.IsChanged ? defaultBrush : changeBrush
                });
            }
            paragraph.Inlines.Add(new LineBreak());
        }

        foreach (var line in JsonDiffFormatter.CreateDiff(gatewayJson, proposedJson))
            AddLine(line);

        _selectedJsonCopyText = string.Join(Environment.NewLine, copyLines);
        SelectedJsonDiffText.Blocks.Add(paragraph);
    }

    private void ApplyJsonPreviewVisibility()
    {
        if (_jsonPreviewVisible)
        {
            JsonPreviewColumn.MinWidth = JsonPreviewMinWidth;
            JsonPreviewColumn.Width = _jsonPreviewExpandedWidth;
        }
        else
        {
            if (JsonPreviewColumn.Width.Value > 0)
                _jsonPreviewExpandedWidth = JsonPreviewColumn.Width;
            JsonPreviewColumn.MinWidth = 0;
            JsonPreviewColumn.Width = new GridLength(0);
        }

        SelectedJsonPreviewPane.Visibility = _jsonPreviewVisible ? Visibility.Visible : Visibility.Collapsed;
        JsonPreviewSplitter.Visibility = _jsonPreviewVisible ? Visibility.Visible : Visibility.Collapsed;
        ToggleJsonPreviewButton.Content = LocalizationHelper.GetString(_jsonPreviewVisible
            ? "ToggleJsonPreviewButton.Content"
            : "ToggleJsonPreviewButton_Show.Content");
    }

    private void UpdateSearchMetaText()
    {
        if (SearchMetaText is null)
            return;

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            SearchMetaText.Text = "";
            return;
        }

        var visibleCount = CountVisibleTreeNodes(ConfigTree.RootNodes);
        SearchMetaText.Text = visibleCount == 0
            ? "No matches"
            : $"{visibleCount} shown";
    }

    private void UpdateMetaAndButtons()
    {
        LoadingState.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        var dirtyCount = _pendingChanges.Count;
        var invalidCount = _validationErrors.Count;
        var permissionState = GetConfigPermissionState();
        var canWriteConfig = permissionState == ConfigPermissionState.ReadWrite;
        var editingLocked = IsConfigEditingLocked(permissionState);
        ConfigMetaText.Text = dirtyCount == 0
            ? L("ConfigPage_MetaNoUnsavedChanges")
            : invalidCount > 0
                ? Lf("ConfigPage_MetaUnsavedChangesWithValidationFormat", dirtyCount, invalidCount)
                : Lf("ConfigPage_MetaUnsavedChangesFormat", dirtyCount);

        SaveStatus.Text = BuildSaveStatusText(permissionState, dirtyCount, invalidCount);
        SaveStatusIcon.Glyph = BuildSaveStatusGlyph(permissionState, dirtyCount, invalidCount);
        AccessSummaryText.Text = BuildAccessSummaryText(permissionState, dirtyCount, invalidCount);
        SetDetailEditingEnabled(!editingLocked);
        ResetSectionButton.IsEnabled = !editingLocked;

        SaveButton.IsEnabled = !editingLocked &&
            _serverSnapshot.HasRoot && _lastSchema.HasValue && canWriteConfig && dirtyCount > 0 && invalidCount == 0;
        DiscardButton.IsEnabled = !_saving && !_loading && dirtyCount > 0;
        RefreshButton.IsEnabled = !_saving && !_loading &&
            permissionState is ConfigPermissionState.ReadOnly or ConfigPermissionState.ReadWrite;
    }

    private bool IsConfigEditingLocked(ConfigPermissionState permissionState) =>
        _saving || _loading || _draftConnectionChanged || !SnapshotMatchesCurrentConnection() ||
        _refreshConfigAfterReconnect ||
        permissionState is ConfigPermissionState.Disconnected or ConfigPermissionState.Checking or ConfigPermissionState.NoRead;

    private void SetDetailEditingEnabled(bool isEnabled)
    {
        foreach (var child in DetailPanel.Children)
        {
            if (child is Control control)
                control.IsEnabled = isEnabled;
        }
    }

    private string BuildSaveStatusText(ConfigPermissionState permissionState, int dirtyCount, int invalidCount)
    {
        if (_saving)
            return L("ConfigPage_SaveStatusSaving");
        if (permissionState == ConfigPermissionState.Disconnected)
            return _refreshConfigAfterReconnect
                ? L("ConfigPage_SaveStatusGatewayRestarting")
                : L("ConfigPage_SaveStatusConnectToGateway");
        if (permissionState == ConfigPermissionState.Checking)
            return L("ConfigPage_SaveStatusCheckingPermissions");
        if (permissionState == ConfigPermissionState.NoRead)
            return L("ConfigPage_SaveStatusMissingRead");
        if (permissionState == ConfigPermissionState.ReadOnly && dirtyCount > 0)
            return L("ConfigPage_SaveStatusReadOnlyMissingWrite");
        if (invalidCount > 0)
            return L("ConfigPage_SaveStatusFixValidationBeforeSaving");
        if (dirtyCount > 0)
            return L("ConfigPage_SaveStatusUnsavedChanges");
        return L("ConfigPage_SaveStatusNoUnsavedChanges");
    }

    private string BuildSaveStatusGlyph(ConfigPermissionState permissionState, int dirtyCount, int invalidCount)
    {
        if (_saving)
            return "\uE895";
        if (permissionState is ConfigPermissionState.Disconnected or ConfigPermissionState.NoRead)
            return "\uE783";
        if (permissionState == ConfigPermissionState.Checking || _refreshConfigAfterReconnect)
            return "\uE895";
        if (invalidCount > 0 || permissionState == ConfigPermissionState.ReadOnly && dirtyCount > 0)
            return "\uE7BA";
        if (dirtyCount > 0)
            return "\uE70F";
        return "\uE73E";
    }

    private void CompleteGatewayReconnect(bool refreshLatestConfig)
    {
        _refreshConfigAfterReconnect = false;
        _refreshConfigWhenGatewayAvailable = false;
        _reconnectCompletionTimer.Stop();
        _reconnectTimeoutTimer.Stop();
        DismissReconnectDialog();
        ShowStatus(
            L("ConfigPage_StatusGatewayReconnectedTitle"),
            refreshLatestConfig
                ? L("ConfigPage_StatusGatewayReconnectedRefreshingMessage")
                : L("ConfigPage_StatusGatewayReconnectedLoadedMessage"),
            InfoBarSeverity.Success);

        if (refreshLatestConfig)
            RefreshFromGateway();
    }

    private bool CompleteReconnectIfReady()
    {
        if ((!_refreshConfigAfterReconnect && !_refreshConfigWhenGatewayAvailable) ||
            GetConfigPermissionState() is not (ConfigPermissionState.ReadOnly or ConfigPermissionState.ReadWrite))
            return false;

        CompleteGatewayReconnect(refreshLatestConfig: true);
        return true;
    }

    private void HandleReconnectTimeout()
    {
        if (!_refreshConfigAfterReconnect)
            return;

        if (CompleteReconnectIfReady())
            return;

        _refreshConfigAfterReconnect = false;
        _refreshConfigWhenGatewayAvailable = true;
        _reconnectCompletionTimer.Stop();
        DismissReconnectDialog();
        ShowStatus(
            L("ConfigPage_StatusGatewayStillReconnectingTitle"),
            L("ConfigPage_StatusGatewayStillReconnectingMessage"),
            InfoBarSeverity.Warning);
        UpdateMetaAndButtons();
    }

    private void ShowReconnectDialog(string message)
    {
        if (_reconnectDialog != null)
        {
            if (_reconnectDialogMessage != null)
                _reconnectDialogMessage.Text = message;
            return;
        }

        if (XamlRoot == null)
            return;

        _reconnectDialogMessage = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("ConfigPage_ReconnectDialogBody"),
            TextWrapping = TextWrapping.Wrap
        });

        content.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(_reconnectDialogMessage);

        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("ConfigPage_ReconnectDialogTitle"),
            Content = content,
            XamlRoot = XamlRoot
        };

        _reconnectDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_reconnectDialog, dialog))
            {
                _reconnectDialog = null;
                _reconnectDialogMessage = null;
            }
        };

        _ = ShowReconnectDialogAsync(dialog);
    }

    private async Task ShowReconnectDialogAsync(ContentDialog dialog)
    {
        try
        {
            await dialog.ShowAsync();
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"[ConfigPage] Could not show reconnect dialog because another dialog is open: {ex.Message}");
            if (ReferenceEquals(_reconnectDialog, dialog))
            {
                _reconnectDialog = null;
                _reconnectDialogMessage = null;
            }
        }
        catch (COMException ex)
        {
            Logger.Warn($"[ConfigPage] Could not show reconnect dialog because its XamlRoot is unavailable: {ex.Message}");
            if (ReferenceEquals(_reconnectDialog, dialog))
            {
                _reconnectDialog = null;
                _reconnectDialogMessage = null;
            }
        }
    }

    private void DismissReconnectDialog()
    {
        if (_reconnectDialog == null)
            return;

        try
        {
            _reconnectDialog.Hide();
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"[ConfigPage] Could not dismiss reconnect dialog: {ex.Message}");
        }
        catch (COMException ex)
        {
            Logger.Warn($"[ConfigPage] Could not dismiss reconnect dialog: {ex.Message}");
        }
    }

    private static string BuildAccessSummaryText(ConfigPermissionState permissionState, int dirtyCount, int invalidCount)
    {
        var access = permissionState switch
        {
            ConfigPermissionState.Disconnected => L("ConfigPage_AccessDisconnected"),
            ConfigPermissionState.Checking => L("ConfigPage_AccessChecking"),
            ConfigPermissionState.NoRead => L("ConfigPage_AccessNoRead"),
            ConfigPermissionState.ReadOnly => L("ConfigPage_AccessReadOnly"),
            ConfigPermissionState.ReadWrite => L("ConfigPage_AccessReadWrite"),
            _ => L("ConfigPage_AccessUnknown")
        };
        var changes = Lf("ConfigPage_AccessChangesFormat", dirtyCount);
        var validation = invalidCount == 0
            ? L("ConfigPage_AccessValidationClean")
            : Lf("ConfigPage_AccessValidationIssuesFormat", invalidCount);
        return Lf("ConfigPage_AccessSummaryFormat", access, changes, validation);
    }

    private void ShowConfigRenderError(string title, string message)
    {
        _loading = false;
        _showSchemaFallback = true;
        ApplyPaneVisibility();
        ShowStatus(title, message, InfoBarSeverity.Error);
        _configLoadFailureVisible = true;
        UpdateMetaAndButtons();
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        _configLoadFailureVisible = false;
        _statusDismissTimer.Stop();
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        SetInfoBarOpen(StatusInfoBar, true);

        if (!_refreshConfigAfterReconnect &&
            severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
        {
            _statusDismissTimer.Start();
        }
    }

    private static void SetInfoBarOpen(InfoBar bar, bool open)
    {
        bar.IsOpen = open;
        bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum ConfigPermissionState
    {
        Disconnected,
        Checking,
        NoRead,
        ReadOnly,
        ReadWrite,
    }

    private ConfigPermissionState GetConfigPermissionState()
    {
        var client = CurrentApp.GatewayClient;
        if (client == null || !client.IsConnectedToGateway)
            return ConfigPermissionState.Disconnected;

        if (!client.HasHandshakeSnapshot)
            return ConfigPermissionState.Checking;

        var scopes = client.GrantedOperatorScopes;
        if (!OperatorScopeHelper.CanReadConfig(scopes))
            return ConfigPermissionState.NoRead;

        return OperatorScopeHelper.CanWriteConfig(scopes)
            ? ConfigPermissionState.ReadWrite
            : ConfigPermissionState.ReadOnly;
    }

    private void UpdatePermissionBanner()
    {
        if (PermissionInfoBar is null)
            return;

        switch (GetConfigPermissionState())
        {
            case ConfigPermissionState.Checking:
                PermissionInfoBar.Title = LocalizationHelper.GetString("ConfigPage_CheckingConfigPermissions");
                PermissionInfoBar.Message = L("ConfigPage_CheckingConfigPermissionsMessage");
                PermissionInfoBar.Severity = InfoBarSeverity.Informational;
                SetInfoBarOpen(PermissionInfoBar, true);
                break;
            case ConfigPermissionState.NoRead:
                ClearConfigViewForNoRead();
                PermissionInfoBar.Title = LocalizationHelper.GetString("ConfigPage_ConfigUnavailable");
                PermissionInfoBar.Message = L("ConfigPage_ConfigUnavailableMessage");
                PermissionInfoBar.Severity = InfoBarSeverity.Error;
                SetInfoBarOpen(PermissionInfoBar, true);
                break;
            case ConfigPermissionState.ReadOnly:
                PermissionInfoBar.Title = LocalizationHelper.GetString("ConfigPage_ConfigIsReadOnly");
                PermissionInfoBar.Message = L("ConfigPage_ConfigIsReadOnlyMessage");
                PermissionInfoBar.Severity = InfoBarSeverity.Warning;
                SetInfoBarOpen(PermissionInfoBar, true);
                break;
            default:
                SetInfoBarOpen(PermissionInfoBar, false);
                break;
        }
    }

    private void ClearConfigViewForNoRead()
    {
        _detailRenderVersion++;
        _configLoadGeneration++;
        _draftConnectionChanged = false;
        _snapshotTicket = null;
        _loading = false;
        _lastConfig = null;
        _lastSchema = null;
        _serverSnapshot = ConfigEditorSnapshot.Empty;
        _editSnapshot = ConfigEditorSnapshot.Empty;
        _pendingChanges.Clear();
        _validationErrors.Clear();
        _selectedPath = "";
        _showSchemaFallback = true;
        _nodeMap.Clear();
        ConfigTree.RootNodes.Clear();
        DetailPanel.Children.Clear();
        DetailPlaceholder.Text = "Config cannot be loaded because this operator token does not have read permission.";
        DetailPlaceholder.Visibility = Visibility.Visible;
        DetailBreadcrumb.Text = "";
        DetailPath.Text = "Config unavailable";
        DetailType.Text = "Missing operator.read permission";
        ResetSectionButton.Visibility = Visibility.Collapsed;
        RenderJsonDiff("{}", "{}");
        SelectedJsonCaption.Text = "Config preview is unavailable without read permission.";
        ApplyPaneVisibility();
    }

    private static bool TryBuildRootLeafSchema(JsonElement? rootSchema, out JsonElement leafSchema)
    {
        leafSchema = default;
        if (!rootSchema.HasValue ||
            !rootSchema.Value.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return false;

        var leafProperties = new JsonObject();
        foreach (var prop in properties.EnumerateObject())
        {
            var propType = ExtractSchemaType(prop.Value);
            if (propType == "object" || prop.Value.TryGetProperty("properties", out _))
                continue;

            leafProperties[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        if (leafProperties.Count == 0)
            return false;

        var schemaObject = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = leafProperties
        };

        if (rootSchema.Value.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            var requiredLeaves = new JsonArray();
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    item.GetString() is { } name &&
                    leafProperties.ContainsKey(name))
                    requiredLeaves.Add(name);
            }

            if (requiredLeaves.Count > 0)
                schemaObject["required"] = requiredLeaves;
        }

        using var document = JsonDocument.Parse(schemaObject.ToJsonString());
        leafSchema = document.RootElement.Clone();
        return true;
    }

    private void RunOnUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private static JsonElement GetSchemaRoot(JsonElement schema)
    {
        return schema.TryGetProperty("schema", out var schemaRoot) ? schemaRoot : schema;
    }

    private static string? ExtractSchemaType(JsonElement schemaNode)
    {
        if (!schemaNode.TryGetProperty("type", out var typeEl)) return null;
        if (typeEl.ValueKind == JsonValueKind.String) return typeEl.GetString();
        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s) && s != "null") return s;
            }
        }
        return null;
    }

    private static bool TryGetElementAtPath(JsonElement root, string path, out JsonElement element)
    {
        element = root;
        if (root.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return false;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(segment, out element))
                return false;
        }

        return true;
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) =>
        AsyncEventHandlerGuard.Run(
            () => OnTreeItemInvokedAsync(args),
            new OpenClawTray.AppLogger(),
            nameof(OnTreeItemInvoked));

    private async Task OnTreeItemInvokedAsync(TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && _nodeMap.TryGetValue(node, out var entry))
        {
            _searchPath = null;
            _searchField = null;
            await ShowDetailWithProgress(entry.Path, entry.Element);
        }
    }

    private async Task ShowDetailWithProgress(string path, JsonElement element)
    {
        var renderVersion = ++_detailRenderVersion;
        DetailPath.Text = string.IsNullOrEmpty(path) ? "Full config" : path;
        DetailType.Text = "Loading section...";
        ResetSectionButton.Visibility = Visibility.Collapsed;
        DetailLoadingText.Text = string.IsNullOrEmpty(path)
            ? LocalizationHelper.GetString("ConfigPage_LoadingFullConfigPreview")
            : LocalizationHelper.Format("ConfigPage_LoadingSectionFormat", path);
        DetailLoadingOverlay.Visibility = Visibility.Visible;
        await Task.Delay(50);

        if (renderVersion != _detailRenderVersion)
            return;

        try
        {
            ShowDetail(path, element);
            UpdateMetaAndButtons();
        }
        finally
        {
            if (renderVersion == _detailRenderVersion)
                DetailLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelDetailRender()
    {
        _detailRenderVersion++;
        DetailLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private bool MatchesSearch(string path, JsonElement schemaNode)
    {
        if (string.IsNullOrWhiteSpace(_searchText))
            return true;
        if (schemaNode.ValueKind != JsonValueKind.Object)
            return false;

        if (path.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            return true;

        if (schemaNode.TryGetProperty("title", out var title) &&
            title.ValueKind == JsonValueKind.String &&
            (title.GetString()?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true))
            return true;

        if (schemaNode.TryGetProperty("description", out var desc) &&
            desc.ValueKind == JsonValueKind.String &&
            (desc.GetString()?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true))
            return true;

        if (schemaNode.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                if (MatchesSearch(childPath, property.Value))
                    return true;
            }
        }

        return false;
    }

    private static int CountPathsUnder(string sectionPath, IEnumerable<string> paths)
    {
        var prefix = sectionPath + ".";
        if (string.IsNullOrEmpty(sectionPath))
            return paths.Count();

        return paths.Count(p => string.Equals(p, sectionPath, StringComparison.Ordinal) ||
                                p.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static void RemoveSectionEntries<T>(string sectionPath, Dictionary<string, T> values)
    {
        var prefix = sectionPath + ".";
        if (string.IsNullOrEmpty(sectionPath))
        {
            values.Clear();
            return;
        }

        foreach (var key in values.Keys
                     .Where(k => string.Equals(k, sectionPath, StringComparison.Ordinal) ||
                                 k.StartsWith(prefix, StringComparison.Ordinal))
                     .ToList())
        {
            values.Remove(key);
        }
    }

    private static string FriendlyLabel(string name)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        result = result.Replace("_", " ");
        return result.Length == 0 ? name : char.ToUpperInvariant(result[0]) + result[1..];
    }

    private static string BuildBreadcrumb(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Full config";

        return "Full config / " + string.Join(" / ", path.Split('.').Select(FriendlyLabel));
    }

    private static int CountVisibleTreeNodes(IList<TreeViewNode> nodes)
    {
        var count = 0;
        foreach (var node in nodes)
        {
            count++;
            count += CountVisibleTreeNodes(node.Children);
        }
        return count;
    }
}
