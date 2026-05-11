using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClawTray.Controls;

public sealed partial class SchemaConfigEditor : UserControl
{
    private JsonElement _schema;
    private JsonElement _config;
    private readonly Dictionary<string, object?> _changes = new();

    private static readonly Regex CamelCaseSplitPattern = new(
        "([a-z])([A-Z])",
        RegexOptions.Compiled);

    private static readonly SolidColorBrush SecondaryBrush =
        new(ColorHelper.FromArgb(255, 140, 150, 170));

    public event EventHandler<Dictionary<string, object?>>? ConfigChanged;

    public SchemaConfigEditor()
    {
        InitializeComponent();
    }

    public void LoadSchema(JsonElement schema, JsonElement config)
    {
        _schema = schema;
        _config = config;
        _changes.Clear();
        FieldsPanel.Children.Clear();

        try
        {
            RenderSchemaNode("", schema, config, FieldsPanel, 0);
        }
        catch { }

        // If schema rendering produced nothing, fall back to rendering config as editable fields
        if (FieldsPanel.Children.Count == 0 && config.ValueKind == JsonValueKind.Object)
        {
            RenderConfigDirectly("", config, FieldsPanel, 0);
        }
    }

    public Dictionary<string, object?> GetChanges() => new(_changes);

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

    private static string? SafeGetString(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private void RenderSchemaNode(string path, JsonElement schema, JsonElement config,
        StackPanel parent, int depth)
    {
        if (ExtractSchemaType(schema) == "object"
            && schema.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                var childConfig = config.ValueKind == JsonValueKind.Object
                    && config.TryGetProperty(prop.Name, out var cv)
                    ? cv
                    : default;
                var childSchema = prop.Value;

                var childType = ExtractSchemaType(childSchema);

                if (childType == "object" && childSchema.TryGetProperty("properties", out _))
                {
                    RenderObjectSection(childPath, prop.Name, childSchema, childConfig, parent, depth);
                }
                else
                {
                    RenderField(childPath, prop.Name, childSchema, childConfig, parent);
                }
            }
        }
    }

    private void RenderObjectSection(string path, string name, JsonElement schema,
        JsonElement config, StackPanel parent, int depth)
    {
        var title = GetLabel(path, name);
        var description = SafeGetString(schema, "description");

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = true,
            Margin = new Thickness(0, 2, 0, 2)
        };

        var headerPanel = new StackPanel { Spacing = 2 };
        headerPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(description))
        {
            headerPanel.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                Foreground = SecondaryBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }
        expander.Header = headerPanel;

        var childPanel = new StackPanel { Spacing = 4, Padding = new Thickness(0, 4, 0, 4) };
        RenderSchemaNode(path, schema, config, childPanel, depth + 1);
        expander.Content = childPanel;

        parent.Children.Add(expander);
    }

    private void RenderField(string path, string name, JsonElement schema,
        JsonElement config, StackPanel parent)
    {
        var label = GetLabel(path, name);
        var description = SafeGetString(schema, "description");
        var type = ExtractSchemaType(schema) ?? "string";
        var isSensitive = IsSensitive(path);

        // Resolve default value if config is missing
        var effectiveConfig = config;
        if (effectiveConfig.ValueKind == JsonValueKind.Undefined
            && schema.TryGetProperty("default", out var defaultVal))
        {
            effectiveConfig = defaultVal;
        }

        UIElement control;

        if (schema.TryGetProperty("enum", out var enumEl)
            && enumEl.ValueKind == JsonValueKind.Array)
        {
            control = RenderEnumField(path, label, description, enumEl, effectiveConfig);
        }
        else if (type == "boolean")
        {
            control = RenderBoolField(path, label, description, effectiveConfig);
        }
        else if (type == "integer" || type == "number")
        {
            control = RenderNumberField(path, label, description, type!, schema, effectiveConfig);
        }
        else if (type == "array" && schema.TryGetProperty("items", out var itemsSchema))
        {
            control = RenderArrayField(path, label, description, itemsSchema, effectiveConfig);
        }
        else // string (default)
        {
            control = isSensitive
                ? RenderSensitiveField(path, label, description, effectiveConfig)
                : RenderStringField(path, label, description, effectiveConfig);
        }

        parent.Children.Add(control);
    }

    private UIElement RenderEnumField(string path, string label, string? description,
        JsonElement enumEl, JsonElement config)
    {
        var combo = new ComboBox { Header = label, MinWidth = 200 };
        if (!string.IsNullOrEmpty(description))
            ToolTipService.SetToolTip(combo, description);

        var currentVal = config.ValueKind == JsonValueKind.String ? config.GetString() : null;
        foreach (var item in enumEl.EnumerateArray())
        {
            var val = item.GetString() ?? "";
            combo.Items.Add(val);
            if (val == currentVal) combo.SelectedItem = val;
        }

        combo.SelectionChanged += (s, e) =>
        {
            _changes[path] = combo.SelectedItem as string;
            ConfigChanged?.Invoke(this, _changes);
        };
        return combo;
    }

    private UIElement RenderBoolField(string path, string label, string? description,
        JsonElement config)
    {
        var toggle = new ToggleSwitch { Header = label };
        if (!string.IsNullOrEmpty(description))
            ToolTipService.SetToolTip(toggle, description);
        toggle.IsOn = config.ValueKind == JsonValueKind.True;
        toggle.Toggled += (s, e) =>
        {
            _changes[path] = toggle.IsOn;
            ConfigChanged?.Invoke(this, _changes);
        };
        return toggle;
    }

    private UIElement RenderNumberField(string path, string label, string? description,
        string type, JsonElement schema, JsonElement config)
    {
        var numBox = new NumberBox
        {
            Header = label,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 200
        };
        if (!string.IsNullOrEmpty(description))
            ToolTipService.SetToolTip(numBox, description);
        if (config.ValueKind == JsonValueKind.Number)
            numBox.Value = config.GetDouble();
        if (schema.TryGetProperty("minimum", out var min))
            numBox.Minimum = min.GetDouble();
        if (schema.TryGetProperty("maximum", out var max))
            numBox.Maximum = max.GetDouble();

        numBox.ValueChanged += (s, e) =>
        {
            _changes[path] = type == "integer" ? (object)(int)numBox.Value : numBox.Value;
            ConfigChanged?.Invoke(this, _changes);
        };
        return numBox;
    }

    private UIElement RenderStringField(string path, string label, string? description,
        JsonElement config)
    {
        var textBox = new TextBox { Header = label, MinWidth = 300 };
        if (!string.IsNullOrEmpty(description))
            ToolTipService.SetToolTip(textBox, description);
        if (config.ValueKind == JsonValueKind.String)
            textBox.Text = config.GetString() ?? "";
        else if (config.ValueKind != JsonValueKind.Undefined
                 && config.ValueKind != JsonValueKind.Null)
            textBox.Text = config.ToString();

        textBox.TextChanged += (s, e) =>
        {
            _changes[path] = textBox.Text;
            ConfigChanged?.Invoke(this, _changes);
        };
        return textBox;
    }

    private UIElement RenderSensitiveField(string path, string label, string? description,
        JsonElement config)
    {
        var pwBox = new PasswordBox { Header = label, Width = 350 };
        if (!string.IsNullOrEmpty(description))
            ToolTipService.SetToolTip(pwBox, description);
        if (config.ValueKind == JsonValueKind.String)
            pwBox.Password = config.GetString() ?? "";

        pwBox.PasswordChanged += (s, e) =>
        {
            _changes[path] = pwBox.Password;
            ConfigChanged?.Invoke(this, _changes);
        };
        return pwBox;
    }

    private UIElement RenderArrayField(string path, string label, string? description,
        JsonElement itemsSchema, JsonElement config)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                Foreground = SecondaryBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var itemsPanel = new StackPanel { Spacing = 2 };

        if (config.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in config.EnumerateArray())
            {
                AddArrayItem(itemsPanel, path, item.GetString() ?? "");
            }
        }

        panel.Children.Add(itemsPanel);

        var addBtn = new Button
        {
            Content = "+ Add",
            Margin = new Thickness(0, 4, 0, 0)
        };
        addBtn.Click += (s, e) =>
        {
            AddArrayItem(itemsPanel, path, "");
            UpdateArrayChanges(itemsPanel, path);
        };
        panel.Children.Add(addBtn);

        return panel;
    }

    private void AddArrayItem(StackPanel itemsPanel, string path, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var textBox = new TextBox { Text = value, MinWidth = 250 };
        textBox.TextChanged += (s, e) => UpdateArrayChanges(itemsPanel, path);

        var removeBtn = new Button
        {
            Content = "\u2715",
            Width = 28,
            Height = 28,
            Padding = new Thickness(0)
        };
        removeBtn.Click += (s, e) =>
        {
            itemsPanel.Children.Remove(row);
            UpdateArrayChanges(itemsPanel, path);
        };

        row.Children.Add(textBox);
        row.Children.Add(removeBtn);
        itemsPanel.Children.Add(row);
    }

    private void UpdateArrayChanges(StackPanel itemsPanel, string path)
    {
        var values = new List<string>();
        foreach (var child in itemsPanel.Children)
        {
            if (child is StackPanel row && row.Children.Count > 0
                && row.Children[0] is TextBox tb)
            {
                values.Add(tb.Text);
            }
        }
        _changes[path] = values.ToArray();
        ConfigChanged?.Invoke(this, _changes);
    }

    private static string GetLabel(string path, string name)
    {
        var result = CamelCaseSplitPattern.Replace(name, "$1 $2");
        result = result.Replace("_", " ").Replace(".", " \u203A ");
        // Title-case the first character
        if (result.Length > 0)
            result = char.ToUpperInvariant(result[0]) + result[1..];
        return result;
    }

    private static bool IsSensitive(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains("token") || lower.Contains("secret")
            || lower.Contains("password") || lower.Contains("apikey")
            || lower.Contains("api_key");
    }

    /// <summary>Fallback: render config values directly as editable fields when no schema available.</summary>
    private void RenderConfigDirectly(string path, JsonElement config, StackPanel parent, int depth)
    {
        if (config.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in config.EnumerateObject())
        {
            var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
            var value = prop.Value;

            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var expander = new Expander
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        IsExpanded = true,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    expander.Header = new TextBlock { Text = GetLabel(childPath, prop.Name), FontWeight = FontWeights.SemiBold };
                    var childPanel = new StackPanel { Spacing = 4, Padding = new Thickness(0, 4, 0, 4) };
                    RenderConfigDirectly(childPath, value, childPanel, depth + 1);
                    expander.Content = childPanel;
                    parent.Children.Add(expander);
                    break;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    var toggle = new ToggleSwitch { Header = GetLabel(childPath, prop.Name), IsOn = value.ValueKind == JsonValueKind.True };
                    toggle.Toggled += (s, e) => { _changes[childPath] = toggle.IsOn; ConfigChanged?.Invoke(this, _changes); };
                    parent.Children.Add(toggle);
                    break;

                case JsonValueKind.Number:
                    var numBox = new NumberBox
                    {
                        Header = GetLabel(childPath, prop.Name),
                        Value = value.GetDouble(),
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                        MinWidth = 200
                    };
                    numBox.ValueChanged += (s, e) => { _changes[childPath] = numBox.Value; ConfigChanged?.Invoke(this, _changes); };
                    parent.Children.Add(numBox);
                    break;

                case JsonValueKind.String:
                    if (IsSensitive(childPath))
                    {
                        var pwBox = new PasswordBox { Header = GetLabel(childPath, prop.Name), Width = 350 };
                        pwBox.Password = value.GetString() ?? "";
                        pwBox.PasswordChanged += (s, e) => { _changes[childPath] = pwBox.Password; ConfigChanged?.Invoke(this, _changes); };
                        parent.Children.Add(pwBox);
                    }
                    else
                    {
                        var textBox = new TextBox { Header = GetLabel(childPath, prop.Name), Text = value.GetString() ?? "", MinWidth = 300 };
                        textBox.TextChanged += (s, e) => { _changes[childPath] = textBox.Text; ConfigChanged?.Invoke(this, _changes); };
                        parent.Children.Add(textBox);
                    }
                    break;

                case JsonValueKind.Array:
                    var arrayLabel = new TextBlock { Text = GetLabel(childPath, prop.Name), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
                    parent.Children.Add(arrayLabel);
                    var arrayText = new TextBox
                    {
                        Text = value.ToString(),
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 100,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11
                    };
                    parent.Children.Add(arrayText);
                    break;
            }
        }
    }
}
