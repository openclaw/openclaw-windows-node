using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class ConfigPage : Page
{
    private HubWindow? _hub;
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
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        if (hub.GatewayClient != null)
        {
            _ = hub.GatewayClient.RequestConfigSchemaAsync();
            _ = hub.GatewayClient.RequestConfigAsync();
        }
    }

    public void UpdateConfig(JsonElement config)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _lastConfig = config;
            if (config.TryGetProperty("baseHash", out var bh))
                _baseHash = bh.GetString();

            RenderTree(config);
        });
    }

    public void UpdateConfigSchema(JsonElement schema)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _lastSchema = schema;
        });
    }

    private void RenderTree(JsonElement config)
    {
        _nodeMap.Clear();
        ConfigTree.RootNodes.Clear();

        var configRoot = config;
        if (config.TryGetProperty("config", out var inner))
            configRoot = inner;

        BuildTreeNodes(ConfigTree.RootNodes, configRoot, "");

        // Expand all nodes
        ExpandAll(ConfigTree.RootNodes);
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
        // Merge changes from schema editor and pending changes
        var changes = new Dictionary<string, object?>(_pendingChanges);
        foreach (var kv in SchemaEditor.GetChanges())
            changes[kv.Key] = kv.Value;

        if (changes.Count == 0)
        {
            SaveStatus.Text = "No changes";
            return;
        }

        if (_hub?.GatewayClient != null)
        {
            SaveButton.IsEnabled = false;
            SaveStatus.Text = "Saving...";
            var ok = await _hub.GatewayClient.PatchConfigAsync(changes);
            SaveStatus.Text = ok ? "✓ Saved" : "✗ Save failed — changes preserved";
            SaveButton.IsEnabled = true;

            if (ok)
            {
                _pendingChanges.Clear();
                _ = _hub.GatewayClient.RequestConfigAsync();
            }
        }
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
        if (_hub?.GatewayClient != null)
        {
            SaveStatus.Text = "";
            _ = _hub.GatewayClient.RequestConfigSchemaAsync();
            _ = _hub.GatewayClient.RequestConfigAsync();
        }
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
        if (nodeSchema.HasValue && nodeSchema.Value.TryGetProperty("description", out var desc))
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

        // Use schema editor for this subtree if schema is available
        if (nodeSchema.HasValue && element.ValueKind == JsonValueKind.Object)
        {
            var editor = new Controls.SchemaConfigEditor();
            editor.LoadSchema(nodeSchema.Value, element);
            editor.ConfigChanged += (s, changes) =>
            {
                // Prefix changes with the current path
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
        if (_hub?.GatewayClient == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var success = await _hub.GatewayClient.SetConfigAsync(configPath, newValue);
                DispatcherQueue?.TryEnqueue(() =>
                {
                    SaveStatus.Text = success
                        ? $"✅ Sent {configPath}"
                        : $"❌ Failed to save {configPath}";
                    if (success && _hub?.GatewayClient != null)
                        _ = _hub.GatewayClient.RequestConfigAsync();
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
