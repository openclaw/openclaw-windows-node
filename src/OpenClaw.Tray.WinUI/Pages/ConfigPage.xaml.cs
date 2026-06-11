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
    private AppState? _appState;
    private JsonElement? _lastConfig;
    private JsonElement? _lastSchema;
    private ConfigEditorSnapshot _serverSnapshot = ConfigEditorSnapshot.Empty;
    private ConfigEditorSnapshot _editSnapshot = ConfigEditorSnapshot.Empty;
    private IOperatorGatewayClient? _permissionClient;

    private string _selectedPath = "";
    private string _searchText = "";
    private bool _showSchemaFallback;
    private bool _loading;
    private bool _saving;
    private bool _initialized;
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
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            UnsubscribePermissionClient();
            _searchDebounceTimer.Stop();
            _reconnectCompletionTimer.Stop();
            _reconnectTimeoutTimer.Stop();
            _statusDismissTimer.Stop();
        };
        StatusInfoBar.Closed += (_, _) =>
        {
            _statusDismissTimer.Stop();
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
        if (_appState != null)
            _appState.PropertyChanged -= OnAppStateChanged;

        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        SubscribePermissionClient(CurrentApp.GatewayClient);
        Logger.Info("[ConfigPage] Initialize");
        if (CompleteReconnectIfReady())
            return;

        if (!_initialized || !_serverSnapshot.HasRoot || !_lastSchema.HasValue)
        {
            _initialized = true;
            RefreshFromGateway();
        }
        else
        {
            SetInfoBarOpen(ConnectionInfoBar, CurrentApp.GatewayClient == null);
            UpdatePermissionBanner();
            UpdateMetaAndButtons();
        }
    }

    public void UpdateConfig(JsonElement config)
    {
        var configSnapshot = config.Clone();
        RunOnUiThread(() =>
        {
            try
            {
                Logger.Info("[ConfigPage] UpdateConfig received");
                if (GetConfigPermissionState() == ConfigPermissionState.NoRead)
                {
                    ClearConfigViewForNoRead();
                    UpdatePermissionBanner();
                    UpdateMetaAndButtons();
                    return;
                }

                _lastConfig = configSnapshot;
                _serverSnapshot = ConfigEditorModel.CaptureSnapshot(configSnapshot);
                if (_pendingChanges.Count == 0)
                    _editSnapshot = _serverSnapshot;

                if (configSnapshot.TryGetProperty("path", out var pathEl) &&
                    pathEl.ValueKind == JsonValueKind.String)
                    ConfigSubtitle.Text = $"Editing {pathEl.GetString()} via schema-guided form.";

                _loading = false;
                SetInfoBarOpen(ConnectionInfoBar, CurrentApp.GatewayClient == null);
                UpdatePermissionBanner();
                RenderTree();
                RestoreSelectedDetail();
                UpdateSelectedJsonPreviewForCurrentSelection();
                UpdateMetaAndButtons();
            }
            catch (Exception ex)
            {
                Logger.Error($"[ConfigPage] Failed to render config: {ex}");
                ShowConfigRenderError("Config unavailable", "The gateway config could not be rendered. Refresh and try again.");
            }
        });
    }

    public void UpdateConfigSchema(JsonElement schema)
    {
        var schemaSnapshot = schema.Clone();
        RunOnUiThread(() =>
        {
            try
            {
                if (GetConfigPermissionState() == ConfigPermissionState.NoRead)
                {
                    ClearConfigViewForNoRead();
                    UpdatePermissionBanner();
                    UpdateMetaAndButtons();
                    return;
                }

                _lastSchema = schemaSnapshot;
                _loading = false;
                UpdatePermissionBanner();
                RenderTree();
                RestoreSelectedDetail();
                UpdateSelectedJsonPreviewForCurrentSelection();
                UpdateMetaAndButtons();
            }
            catch (Exception ex)
            {
                Logger.Error($"[ConfigPage] Failed to render config schema: {ex}");
                ShowConfigRenderError("Schema unavailable", "The gateway schema could not be rendered. Raw JSON remains available as a read-only preview.");
            }
        });
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Config):
                if (_appState!.Config.HasValue) UpdateConfig(_appState.Config.Value);
                break;
            case nameof(AppState.ConfigSchema):
                if (_appState!.ConfigSchema.HasValue) UpdateConfigSchema(_appState.ConfigSchema.Value);
                break;
            case nameof(AppState.Status):
                SubscribePermissionClient(CurrentApp.GatewayClient);
                UpdateConnectionBanner();
                UpdatePermissionBanner();
                var configPermissionState = GetConfigPermissionState();
                if (configPermissionState == ConfigPermissionState.Disconnected && _refreshConfigAfterReconnect)
                {
                    ShowStatus("Gateway restarting", "Saving changes. The gateway is restarting and will reconnect automatically.", InfoBarSeverity.Informational);
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

    private void SubscribePermissionClient(IOperatorGatewayClient? client)
    {
        if (ReferenceEquals(_permissionClient, client))
            return;

        UnsubscribePermissionClient();
        _permissionClient = client;
        if (_permissionClient != null)
            _permissionClient.HandshakeSucceeded += OnGatewayHandshakeSucceeded;
    }

    private void UnsubscribePermissionClient()
    {
        if (_permissionClient != null)
            _permissionClient.HandshakeSucceeded -= OnGatewayHandshakeSucceeded;
        _permissionClient = null;
    }

    private void OnGatewayHandshakeSucceeded(object? sender, EventArgs e)
    {
        RunOnUiThread(() =>
        {
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

    private void RenderTree()
    {
        _nodeMap.Clear();
        ConfigTree.RootNodes.Clear();

        if (_lastSchema.HasValue)
        {
            _showSchemaFallback = false;
            ApplyPaneVisibility();

            var schemaRoot = GetSchemaRoot(_lastSchema.Value);
            var configRoot = _serverSnapshot.HasRoot ? _serverSnapshot.Root : (JsonElement?)null;

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
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return false;

        var anyVisible = false;
        foreach (var prop in properties.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var path = string.IsNullOrEmpty(basePath) ? prop.Name : $"{basePath}.{prop.Name}";
            var propType = ExtractSchemaType(prop.Value);
            if (propType != "object" && !prop.Value.TryGetProperty("properties", out _))
                continue;

            var configValue = config.HasValue && config.Value.TryGetProperty(prop.Name, out var cv)
                ? (JsonElement?)cv
                : null;

            var node = new TreeViewNode();
            var childVisible = BuildSchemaTreeNodes(node.Children, prop.Value, configValue, path);
            var selfMatches = MatchesSearch(path, prop.Value);
            if (!selfMatches && !childVisible && !string.IsNullOrWhiteSpace(_searchText))
                continue;

            var dirtyCount = CountPathsUnder(path, _pendingChanges.Keys);
            var invalidCount = CountPathsUnder(path, _validationErrors.Keys);
            var label = FriendlyLabel(prop.Name);
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
        if (!_serverSnapshot.HasRoot || !_lastSchema.HasValue)
            return;

        if (TryGetElementAtPath(_serverSnapshot.Root, _selectedPath, out var element))
        {
            ShowDetail(_selectedPath, element);
            return;
        }

        _selectedPath = "";
        ShowDetail("", _serverSnapshot.Root);
    }

    private void ShowDetail(string path, JsonElement element)
    {
        if (!_lastSchema.HasValue)
            return;

        DetailPanel.Children.Clear();
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        _selectedPath = path;
        DetailBreadcrumb.Text = BuildBreadcrumb(path);
        DetailPath.Text = string.IsNullOrEmpty(path) ? "Full config" : FriendlyLabel(path.Split('.').Last());

        var nodeSchema = ResolveSchemaAtPath(GetSchemaRoot(_lastSchema.Value), path);
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
                editor.ConfigChanged += (_, args) => ApplyEditorChanges(path, args);
                DetailPanel.Children.Add(editor);
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
            return;
        }

        DetailPanel.Children.Add(new TextBlock
        {
            Text = "This section is shown in the JSON preview because the schema shape is not editable in the form yet.",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });
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

        if (_pendingChanges.Count == 0)
        {
            ShowStatus("No changes", "There is nothing to save.", InfoBarSeverity.Informational);
            return;
        }

        if (_validationErrors.Count > 0)
        {
            var first = _validationErrors.First();
            ShowStatus("Fix validation errors", $"{first.Key}: {first.Value}", InfoBarSeverity.Error);
            UpdateMetaAndButtons();
            return;
        }

        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            ShowStatus("Not connected", "Connect to a gateway before saving config.", InfoBarSeverity.Error);
            UpdateMetaAndButtons();
            return;
        }

        if (!OperatorScopeHelper.CanWriteConfig(client.GrantedOperatorScopes))
        {
            ShowStatus("Config is read-only", "This operator token does not have operator.write permission, so config changes cannot be saved.", InfoBarSeverity.Warning);
            UpdatePermissionBanner();
            UpdateMetaAndButtons();
            return;
        }

        var saveBase = _editSnapshot.HasRoot ? _editSnapshot : _serverSnapshot;
        if (!saveBase.HasRoot)
        {
            ShowStatus("Config not loaded", "Refresh the gateway config before saving.", InfoBarSeverity.Error);
            UpdateMetaAndButtons();
            return;
        }

        _saving = true;
        UpdateMetaAndButtons();
        ShowStatus("Saving config…", $"Writing {_pendingChanges.Count} change(s) to the gateway.", InfoBarSeverity.Informational);

        try
        {
            var updated = ConfigEditorModel.ApplyChanges(saveBase.Root, _pendingChanges);
            var result = await client.PatchConfigDetailedAsync(updated, saveBase.BaseHash);
            if (!result.Ok)
            {
                if (result.LooksLikeStaleBaseHash)
                {
                    _editSnapshot = ConfigEditorSnapshot.Empty;
                    _ = client.RequestConfigAsync();
                    ShowStatus(
                        "Gateway config changed elsewhere",
                        $"Your edits are preserved. The latest config is being refreshed; review the form and try Save again. Details: {result.Error ?? "stale config hash"}",
                        InfoBarSeverity.Warning);
                }
                else
                {
                    ShowStatus(
                        "Save failed",
                        result.Error ?? "The gateway rejected the config update. Your changes are preserved.",
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
            ShowStatus("Gateway restarting", "Saving changes. The gateway is restarting and will reconnect automatically.", InfoBarSeverity.Informational);
            _reconnectCompletionTimer.Stop();
            _reconnectCompletionTimer.Start();
            _reconnectTimeoutTimer.Stop();
            _reconnectTimeoutTimer.Start();
        }
        catch (Exception ex)
        {
            ShowStatus("Save failed", $"{ex.Message} Your changes are preserved.", InfoBarSeverity.Error);
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
        UpdateConnectionBanner();
        var permissionState = GetConfigPermissionState();
        if (CurrentApp.GatewayClient == null)
        {
            _loading = false;
            UpdatePermissionBanner();
            UpdateMetaAndButtons();
            return;
        }

        if (permissionState == ConfigPermissionState.Checking)
        {
            _loading = false;
            SaveStatus.Text = "Checking config permissions…";
            UpdatePermissionBanner();
            UpdateMetaAndButtons();
            return;
        }

        if (permissionState == ConfigPermissionState.NoRead)
        {
            ClearConfigViewForNoRead();
            UpdatePermissionBanner();
            UpdateMetaAndButtons();
            return;
        }

        _loading = true;
        LoadingState.Visibility = Visibility.Visible;
        SaveStatus.Text = "Refreshing…";
        UpdatePermissionBanner();
        _ = CurrentApp.GatewayClient.RequestConfigSchemaAsync();
        _ = CurrentApp.GatewayClient.RequestConfigAsync();
        UpdateMetaAndButtons();
    }

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
        ShowStatus("Changes discarded", "The form is back to the last config loaded from the gateway.", InfoBarSeverity.Informational);
        RenderTree();
        RestoreSelectedDetail();
        UpdateSelectedJsonPreviewForCurrentSelection();
        UpdateMetaAndButtons();
    }

    private void OnResetSection(object sender, RoutedEventArgs e)
    {
        RemoveSectionEntries(_selectedPath, _pendingChanges);
        RemoveSectionEntries(_selectedPath, _validationErrors);
        var sectionLabel = string.IsNullOrEmpty(_selectedPath) ? "Full config" : _selectedPath;
        ShowStatus("Section reset", $"{sectionLabel} is back to the last loaded gateway value.", InfoBarSeverity.Informational);
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
        ShowStatus("Copied JSON diff", "The selected section diff was copied to the clipboard.", InfoBarSeverity.Success);
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
            ? "No unsaved changes."
            : $"{dirtyCount} unsaved change{(dirtyCount == 1 ? "" : "s")}" +
              (invalidCount > 0 ? $" · {invalidCount} validation issue{(invalidCount == 1 ? "" : "s")}" : "");

        SaveStatus.Text = BuildSaveStatusText(permissionState, dirtyCount, invalidCount);
        SaveStatusIcon.Glyph = BuildSaveStatusGlyph(permissionState, dirtyCount, invalidCount);
        AccessSummaryText.Text = BuildAccessSummaryText(permissionState, dirtyCount, invalidCount);
        SetDetailEditingEnabled(!editingLocked);
        ResetSectionButton.IsEnabled = !editingLocked;

        SaveButton.IsEnabled = !_saving && !_loading && canWriteConfig && dirtyCount > 0 && invalidCount == 0;
        DiscardButton.IsEnabled = !editingLocked && dirtyCount > 0;
    }

    private bool IsConfigEditingLocked(ConfigPermissionState permissionState) =>
        _saving ||
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
            return "Saving…";
        if (permissionState == ConfigPermissionState.Disconnected)
            return _refreshConfigAfterReconnect ? "Gateway restarting…" : "Connect to a gateway to edit";
        if (permissionState == ConfigPermissionState.Checking)
            return "Checking permissions…";
        if (permissionState == ConfigPermissionState.NoRead)
            return "Config unavailable: missing operator.read";
        if (permissionState == ConfigPermissionState.ReadOnly && dirtyCount > 0)
            return "Read-only: missing operator.write";
        if (invalidCount > 0)
            return "Fix validation errors before saving";
        if (dirtyCount > 0)
            return "Unsaved changes";
        return "No unsaved changes";
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
            "Gateway reconnected",
            refreshLatestConfig
                ? "Configuration saved and the gateway connection is back. Refreshing the latest config."
                : "Configuration saved and the latest gateway config is loaded.",
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
            "Gateway still reconnecting",
            "The config save was accepted, but the gateway has not reconnected yet. You can keep this page open or use Connection to check status.",
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
            ConfigPermissionState.Disconnected => "Disconnected",
            ConfigPermissionState.Checking => "Checking access",
            ConfigPermissionState.NoRead => "No config read access",
            ConfigPermissionState.ReadOnly => "Read-only access",
            ConfigPermissionState.ReadWrite => "Read/write access",
            _ => "Unknown access"
        };
        var changes = $"{dirtyCount} change{(dirtyCount == 1 ? "" : "s")}";
        var validation = invalidCount == 0
            ? "Local validation clean"
            : $"{invalidCount} validation issue{(invalidCount == 1 ? "" : "s")}";
        return $"{access} · {changes} · {validation}";
    }

    private void ShowConfigRenderError(string title, string message)
    {
        _loading = false;
        _showSchemaFallback = true;
        ApplyPaneVisibility();
        ShowStatus(title, message, InfoBarSeverity.Error);
        UpdateMetaAndButtons();
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
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
                PermissionInfoBar.Message = "Waiting for the gateway to report this operator's permissions.";
                PermissionInfoBar.Severity = InfoBarSeverity.Informational;
                SetInfoBarOpen(PermissionInfoBar, true);
                break;
            case ConfigPermissionState.NoRead:
                ClearConfigViewForNoRead();
                PermissionInfoBar.Title = LocalizationHelper.GetString("ConfigPage_ConfigUnavailable");
                PermissionInfoBar.Message = "This operator token lacks operator.read permission, so the gateway config cannot be loaded here.";
                PermissionInfoBar.Severity = InfoBarSeverity.Error;
                SetInfoBarOpen(PermissionInfoBar, true);
                break;
            case ConfigPermissionState.ReadOnly:
                PermissionInfoBar.Title = LocalizationHelper.GetString("ConfigPage_ConfigIsReadOnly");
                PermissionInfoBar.Message = "This operator token can read config but lacks operator.write permission. You can inspect and validate drafts, but Save is disabled.";
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
        _loading = false;
        _lastConfig = null;
        _lastSchema = null;
        _serverSnapshot = ConfigEditorSnapshot.Empty;
        _editSnapshot = ConfigEditorSnapshot.Empty;
        _pendingChanges.Clear();
        _validationErrors.Clear();
        _selectedPath = "";
        _showSchemaFallback = true;
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

    private static JsonElement? ResolveSchemaAtPath(JsonElement schema, string path)
    {
        if (string.IsNullOrEmpty(path)) return schema;
        var current = schema;
        foreach (var segment in path.Split('.'))
        {
            if (current.TryGetProperty("properties", out var props) &&
                props.TryGetProperty(segment, out var child))
            {
                current = child;
            }
            else
            {
                return null;
            }
        }
        return current;
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

    private bool MatchesSearch(string path, JsonElement schemaNode)
    {
        if (string.IsNullOrWhiteSpace(_searchText))
            return true;

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
