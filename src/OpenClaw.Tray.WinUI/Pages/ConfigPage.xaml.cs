using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class ConfigPage : Page
{
    private static readonly JsonElement s_emptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;
    private JsonElement? _lastConfig;
    private JsonElement? _lastSchema;
    private string? _baseHash;

    private JsonElement? _selectedElement;
    private string _selectedPath = "";
    private readonly Dictionary<TreeViewNode, (string Path, JsonElement Element)> _nodeMap = new();
    private readonly Dictionary<string, object?> _pendingChanges = new();

    public ConfigPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;
        OpenClawTray.Services.Logger.Info("[ConfigPage] Initialize");
        if (CurrentApp.GatewayClient != null)
        {
            _ = CurrentApp.GatewayClient.RequestConfigSchemaAsync();
            _ = CurrentApp.GatewayClient.RequestConfigAsync();
        }
    }

    public void UpdateConfig(JsonElement config)
    {
        var configSnapshot = config.Clone();
        try
        {
            OpenClawTray.Services.Logger.Info("[ConfigPage] UpdateConfig received");
            _lastConfig = configSnapshot;

            // Get baseHash from the config.get response
            // The gateway returns a 'hash' field which is SHA256 of the raw file content
            if (configSnapshot.TryGetProperty("baseHash", out var bh) && bh.ValueKind == JsonValueKind.String)
            {
                _baseHash = bh.GetString();
            }
            else if (configSnapshot.TryGetProperty("hash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
            {
                _baseHash = hashEl.GetString();
            }
            else if (configSnapshot.TryGetProperty("raw", out var rawEl) && rawEl.ValueKind == JsonValueKind.String)
            {
                // Fallback: compute from raw content
                var rawContent = rawEl.GetString();
                if (rawContent != null)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(rawContent);
                    var hash = System.Security.Cryptography.SHA256.HashData(bytes);
                    _baseHash = Convert.ToHexStringLower(hash);
                }
            }

            // Show file path in subtitle if available
            if (configSnapshot.TryGetProperty("path", out var pathEl))
                ConfigSubtitle.Text = $"Editing {pathEl.GetString()} via schema-driven form";

            RenderTree();
            UpdateRawJson();
        }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Error($"[ConfigPage] Failed to render config: {ex}");
            ShowConfigRenderError();
        }
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
        }
    }

    public void UpdateConfigSchema(JsonElement schema)
    {
        var schemaSnapshot = schema.Clone();
        try
        {
            _lastSchema = schemaSnapshot;
            // Re-render tree if config is already loaded
            if (_lastConfig.HasValue)
                RenderTree();
        }
        catch (Exception ex)
        {
            OpenClawTray.Services.Logger.Error($"[ConfigPage] Failed to render config schema: {ex}");
            ShowConfigRenderError();
        }
    }

    private void RenderTree()
    {
        _nodeMap.Clear();
        ConfigTree.RootNodes.Clear();

        if (_lastSchema.HasValue)
        {
            // Schema-driven tree
            NoSchemaPanel.Visibility = Visibility.Collapsed;
            SchemaTreeGrid.Visibility = Visibility.Visible;

            var schema = _lastSchema.Value;
            var schemaRoot = schema.TryGetProperty("schema", out var sr) ? sr : schema;
            var configRoot = _lastConfig.HasValue ? ExtractConfigRoot(_lastConfig.Value) : (JsonElement?)null;

            BuildSchemaTreeNodes(ConfigTree.RootNodes, schemaRoot, configRoot, "");
            ExpandAll(ConfigTree.RootNodes);
        }
        else if (_lastConfig.HasValue)
        {
            // No schema — show fallback panel
            SchemaTreeGrid.Visibility = Visibility.Collapsed;
            NoSchemaPanel.Visibility = Visibility.Visible;
        }
        else
        {
            SchemaTreeGrid.Visibility = Visibility.Collapsed;
            NoSchemaPanel.Visibility = Visibility.Visible;
        }
    }

    private static JsonElement ExtractConfigRoot(JsonElement configResponse)
    {
        if (configResponse.TryGetProperty("parsed", out var parsed))
            return parsed;
        if (configResponse.TryGetProperty("config", out var config))
            return config;
        return configResponse;
    }

    /// <summary>
    /// JSON Schema's "type" keyword may be either a string ("object") or an
    /// array of strings (["string","null"]). Returns the first non-null type
    /// when an array is encountered, or null if "type" is missing/unsupported.
    /// </summary>
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

    private void ShowConfigRenderError()
    {
        SchemaTreeGrid.Visibility = Visibility.Collapsed;
        NoSchemaPanel.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = false;
        SaveStatus.Text = "Config unavailable";
    }

    private void BuildSchemaTreeNodes(IList<TreeViewNode> parent, JsonElement schema, JsonElement? config, string basePath)
    {
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in properties.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var path = string.IsNullOrEmpty(basePath) ? prop.Name : $"{basePath}.{prop.Name}";
            var node = new TreeViewNode();

            var desc = prop.Value.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                ? descEl.GetString() : null;
            var label = string.IsNullOrEmpty(desc) ? prop.Name : $"{prop.Name} — {desc}";

            var propType = ExtractSchemaType(prop.Value);
            var configValue = config.HasValue && config.Value.TryGetProperty(prop.Name, out var cv) ? (JsonElement?)cv : null;

            if (propType == "object" || (prop.Value.TryGetProperty("properties", out _)))
            {
                node.Content = $"📁 {prop.Name}";
                node.IsExpanded = true;
                // Store config value if available, otherwise empty object (not the schema!)
                var nodeElement = configValue ?? s_emptyObject;
                _nodeMap[node] = (path, nodeElement);
                BuildSchemaTreeNodes(node.Children, prop.Value, configValue, path);
                parent.Add(node);
            }
            // Leaf nodes are not shown in the tree — they appear as edit controls
            // in the detail panel when their parent folder is selected
        }
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).OpenDashboard("config");
    }

    private static void ExpandAll(IList<TreeViewNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = true;
            if (node.Children.Count > 0)
                ExpandAll(node.Children);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        // Capture changes from the currently displayed editor
        if (DetailPanel.Children.Count > 0 && DetailPanel.Children[0] is Controls.SchemaConfigEditor activeEditor)
        {
            foreach (var kv in activeEditor.GetChanges())
            {
                var fullPath = string.IsNullOrEmpty(_selectedPath)
                    ? kv.Key
                    : (string.IsNullOrEmpty(kv.Key) ? _selectedPath : $"{_selectedPath}.{kv.Key}");
                _pendingChanges[fullPath] = kv.Value;
            }
        }

        if (_pendingChanges.Count == 0)
        {
            SaveStatus.Text = "No changes";
            return;
        }

        if (CurrentApp.GatewayClient == null || !_lastConfig.HasValue)
        {
            SaveStatus.Text = "Not connected";
            return;
        }

        // Build the full config with changes applied
        // The config.get response has { path, exists, raw, parsed } — use parsed
        var configRoot = _lastConfig.Value.TryGetProperty("parsed", out var pr) ? pr
            : (_lastConfig.Value.TryGetProperty("config", out var cr) ? cr : _lastConfig.Value);
        var configDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(configRoot.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new Dictionary<string, object?>();

        // Apply dot-path changes to the config dict
        foreach (var kv in _pendingChanges)
        {
            SetNestedValue(configDict, kv.Key, kv.Value);
        }

        // Serialize back and send as raw
        var updatedJson = JsonSerializer.Serialize(configDict, new JsonSerializerOptions { WriteIndented = true });
        var updatedElement = JsonDocument.Parse(updatedJson).RootElement;

        SaveButton.IsEnabled = false;
        SaveStatus.Text = "Saving...";
        try
        {
            var ok = await CurrentApp.GatewayClient.PatchConfigAsync(updatedElement, _baseHash);
            SaveStatus.Text = ok ? "✓ Saved" : "✗ Save failed — changes preserved";

            if (ok)
            {
                _pendingChanges.Clear();
                _ = CurrentApp.GatewayClient.RequestConfigAsync();
            }
        }
        catch (Exception) { SaveStatus.Text = "✗ Save failed — changes preserved"; }
        finally { SaveButton.IsEnabled = true; }
    }

    private static void SetNestedValue(Dictionary<string, object?> dict, string dotPath, object? value)
    {
        var segments = dotPath.Split('.');
        var current = dict;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current.TryGetValue(segments[i], out var existing) && existing is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                var child = JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText()) ?? new();
                current[segments[i]] = child;
                current = child;
            }
            else if (existing is Dictionary<string, object?> childDict)
            {
                current = childDict;
            }
            else
            {
                var newChild = new Dictionary<string, object?>();
                current[segments[i]] = newChild;
                current = newChild;
            }
        }
        current[segments[^1]] = value;
    }

    /// <summary>Walk the JSON Schema tree to find the sub-schema at a dot-separated path.</summary>
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

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.GatewayClient != null)
        {
            SaveStatus.Text = "";
            _ = CurrentApp.GatewayClient.RequestConfigSchemaAsync();
            _ = CurrentApp.GatewayClient.RequestConfigAsync();
        }
    }

    private void UpdateRawJson()
    {
        // RawJsonText lives inside the second TabViewItem, whose content WinUI
        // does not realize until the tab is first selected. Until then the
        // x:Name field is null and touching .Text would NRE on the dispatcher
        // queue — silently tearing down the app. OnConfigTabChanged calls us
        // again once the tab is realized, so deferring here is safe.
        if (RawJsonText is null) return;

        if (_lastConfig.HasValue)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var configRoot = _lastConfig.Value.TryGetProperty("parsed", out var pr) ? pr
                    : (_lastConfig.Value.TryGetProperty("config", out var cr) ? cr : _lastConfig.Value);
                RawJsonText.Text = JsonSerializer.Serialize(configRoot, options);
            }
            catch
            {
                RawJsonText.Text = _lastConfig.Value.GetRawText();
            }
        }
        else
        {
            RawJsonText.Text = "No config loaded.";
        }
    }

    private void OnConfigTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // Refresh raw JSON when switching to it
        if (ConfigTabs.SelectedIndex == 1)
            UpdateRawJson();
    }

    // ── Fallback TreeView methods (used when schema is unavailable) ──

    private void BuildTreeNodes(IList<TreeViewNode> parent, JsonElement element, string basePath, int depth = 0)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(basePath) ? prop.Name : $"{basePath}.{prop.Name}";
            var node = new TreeViewNode();

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                bool hasObjectOrArrayChild = false;
                foreach (var child in prop.Value.EnumerateObject())
                {
                    if (child.Value.ValueKind == JsonValueKind.Object || child.Value.ValueKind == JsonValueKind.Array)
                    { hasObjectOrArrayChild = true; break; }
                }

                node.Content = $"📁 {prop.Name}";
                node.IsExpanded = depth < 1;
                _nodeMap[node] = (path, prop.Value);
                BuildTreeNodes(node.Children, prop.Value, path, depth + 1);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                node.Content = $"📋 {prop.Name} [{prop.Value.GetArrayLength()}]";
                _nodeMap[node] = (path, prop.Value);
                int idx = 0;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var childNode = new TreeViewNode();
                        var itemPath = $"{path}[{idx}]";
                        var label = TryGetLabel(item) ?? $"[{idx}]";
                        childNode.Content = $"  {label}";
                        _nodeMap[childNode] = (itemPath, item);
                        node.Children.Add(childNode);
                    }
                    idx++;
                }
            }
            else
            {
                continue;
            }

            parent.Add(node);
        }
    }

    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && _nodeMap.TryGetValue(node, out var entry))
        {
            _selectedElement = entry.Element;
            _selectedPath = entry.Path;
            ShowDetail(entry.Path, entry.Element);
        }
    }

    private void ShowDetail(string path, JsonElement element)
    {
        DetailPanel.Children.Clear();
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailPath.Text = path;

        // Try to find schema for this path
        JsonElement? nodeSchema = null;
        if (_lastSchema.HasValue)
        {
            var schema = _lastSchema.Value;
            var schemaRoot = schema.TryGetProperty("schema", out var sr) ? sr : schema;
            nodeSchema = ResolveSchemaAtPath(schemaRoot, path);
        }

        // Show description from schema if available
        if (nodeSchema.HasValue && nodeSchema.Value.TryGetProperty("description", out var desc)
            && desc.ValueKind == JsonValueKind.String)
        {
            DetailType.Text = desc.GetString() ?? "";
        }
        else
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object: DetailType.Text = $"Object · {element.EnumerateObject().Count()} properties"; break;
                case JsonValueKind.Array: DetailType.Text = $"Array · {element.GetArrayLength()} items"; break;
                default: DetailType.Text = element.ValueKind.ToString(); break;
            }
        }

        // Use schema editor for this subtree
        if (nodeSchema.HasValue && element.ValueKind == JsonValueKind.Object)
        {
            var editor = new Controls.SchemaConfigEditor();
            editor.LoadSchema(nodeSchema.Value, element);
            editor.ConfigChanged += (s, changes) =>
            {
                foreach (var kv in changes)
                {
                    var fullPath = string.IsNullOrEmpty(kv.Key) ? path : $"{path}.{kv.Key}";
                    _pendingChanges[fullPath] = kv.Value;
                }
            };
            DetailPanel.Children.Add(editor);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                DetailType.Text = $"Object · {element.EnumerateObject().Count()} properties";
                foreach (var prop in element.EnumerateObject())
                {
                    var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var keyBlock = new TextBlock
                    {
                        Text = prop.Name,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 13
                    };
                    Grid.SetColumn(keyBlock, 0);
                    row.Children.Add(keyBlock);

                    var propPath = $"{path}.{prop.Name}";
                    var editControl = CreateEditableControl(prop.Value, propPath);
                    Grid.SetColumn(editControl, 1);
                    row.Children.Add(editControl);

                    DetailPanel.Children.Add(row);

                    DetailPanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                        Margin = new Thickness(0, 2, 0, 2),
                        Opacity = 0.3
                    });
                }
                break;

            case JsonValueKind.Array:
                DetailType.Text = $"Array · {element.GetArrayLength()} items";
                int idx = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var card = new Border
                    {
                        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 4, 0, 4)
                    };

                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var sp = new StackPanel { Spacing = 4 };
                        sp.Children.Add(new TextBlock
                        {
                            Text = TryGetLabel(item) ?? $"Item {idx}",
                            FontWeight = FontWeights.SemiBold,
                            FontSize = 13
                        });
                        foreach (var sub in item.EnumerateObject())
                        {
                            var subRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                            subRow.Children.Add(new TextBlock
                            {
                                Text = sub.Name,
                                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                                FontSize = 12, Width = 140
                            });
                            subRow.Children.Add(new TextBlock
                            {
                                Text = FormatValue(sub.Value),
                                FontFamily = new FontFamily("Consolas"),
                                FontSize = 12,
                                IsTextSelectionEnabled = true,
                                TextWrapping = TextWrapping.Wrap
                            });
                            sp.Children.Add(subRow);
                        }
                        card.Child = sp;
                    }
                    else
                    {
                        card.Child = new TextBlock
                        {
                            Text = FormatValue(item),
                            FontFamily = new FontFamily("Consolas"),
                            IsTextSelectionEnabled = true
                        };
                    }
                    DetailPanel.Children.Add(card);
                    idx++;
                }
                break;

            default:
                DetailType.Text = element.ValueKind.ToString();
                var valueText = new TextBlock
                {
                    Text = FormatValue(element),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 16,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 8)
                };
                DetailPanel.Children.Add(valueText);
                break;
        }
    }

    private FrameworkElement CreateEditableControl(JsonElement value, string configPath)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var str = value.GetString() ?? "";
                var isSecret = configPath.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                              configPath.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                              configPath.Contains("secret", StringComparison.OrdinalIgnoreCase);
                var textBox = new TextBox
                {
                    Text = str,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    MinWidth = 200,
                    Tag = configPath
                };
                if (isSecret && str.Length > 8)
                {
                    textBox.Text = str[..4] + "••••••••";
                    textBox.IsReadOnly = true;
                    textBox.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                }
                else
                {
                    textBox.LostFocus += (s, e) => OnValueEdited(configPath, textBox.Text);
                }
                return textBox;

            case JsonValueKind.Number:
                var numBox = new TextBox
                {
                    Text = value.GetRawText(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13,
                    MinWidth = 100,
                    Tag = configPath
                };
                numBox.LostFocus += (s, e) =>
                {
                    if (int.TryParse(numBox.Text, out var intVal))
                        OnValueEdited(configPath, intVal);
                    else if (double.TryParse(numBox.Text, out var dblVal))
                        OnValueEdited(configPath, dblVal);
                };
                return numBox;

            case JsonValueKind.True:
            case JsonValueKind.False:
                var toggle = new ToggleSwitch
                {
                    IsOn = value.GetBoolean(),
                    OnContent = "true",
                    OffContent = "false",
                    MinWidth = 0,
                    Tag = configPath
                };
                toggle.Toggled += (s, e) => OnValueEdited(configPath, toggle.IsOn);
                return toggle;

            case JsonValueKind.Null:
                return new TextBlock
                {
                    Text = "null",
                    FontStyle = global::Windows.UI.Text.FontStyle.Italic,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                };

            case JsonValueKind.Object:
                var objPanel = new StackPanel { Spacing = 2 };
                foreach (var sub in value.EnumerateObject())
                {
                    var subRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    subRow.Children.Add(new TextBlock
                    {
                        Text = $"{sub.Name}:",
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                    subRow.Children.Add(new TextBlock
                    {
                        Text = FormatValue(sub.Value),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12
                    });
                    objPanel.Children.Add(subRow);
                }
                return objPanel;

            case JsonValueKind.Array:
                var arr = value;
                bool allStr = true;
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String) { allStr = false; break; }

                if (allStr && arr.GetArrayLength() <= 30)
                {
                    return new TextBlock
                    {
                        Text = string.Join(", ", arr.EnumerateArray().Select(v => v.GetString())),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    };
                }
                return new TextBlock
                {
                    Text = $"[ {arr.GetArrayLength()} items ]",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 12
                };

            default:
                return new TextBlock { Text = value.GetRawText(), FontFamily = new FontFamily("Consolas"), FontSize = 13 };
        }
    }

    private void OnValueEdited(string configPath, object newValue)
    {
        if (CurrentApp.GatewayClient == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var success = await CurrentApp.GatewayClient.SetConfigAsync(configPath, newValue);
                DispatcherQueue?.TryEnqueue(() =>
                {
                    SaveStatus.Text = success
                        ? $"✅ Sent {configPath}"
                        : $"❌ Failed to save {configPath}";
                    if (success && CurrentApp.GatewayClient != null)
                        _ = CurrentApp.GatewayClient.RequestConfigAsync();
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue?.TryEnqueue(() => SaveStatus.Text = $"❌ {ex.Message}");
            }
        });
    }

    private static string? TryGetLabel(JsonElement obj)
    {
        foreach (var key in new[] { "name", "id", "displayName", "key", "title" })
        {
            if (obj.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                return val.GetString();
        }
        return null;
    }

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };
}
