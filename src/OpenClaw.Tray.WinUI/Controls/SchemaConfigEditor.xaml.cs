using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClawTray.Controls;

public sealed class SchemaConfigChangedEventArgs(
    Dictionary<string, object?> changes,
    Dictionary<string, string> validationErrors)
    : EventArgs
{
    public Dictionary<string, object?> Changes { get; } = changes;
    public Dictionary<string, string> ValidationErrors { get; } = validationErrors;
}

public sealed partial class SchemaConfigEditor : UserControl
{
    private JsonElement _schema;
    private JsonElement _config;
    private bool _loading;
    private readonly Dictionary<string, object?> _changes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _validationErrors = new(StringComparer.Ordinal);
    private static readonly TimeSpan PatternValidationTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex CamelCaseSplitPattern = new(
        "([a-z])([A-Z])",
        RegexOptions.Compiled);

    private static readonly SolidColorBrush SecondaryBrush =
        new(ColorHelper.FromArgb(255, 140, 150, 170));

    public event EventHandler<SchemaConfigChangedEventArgs>? ConfigChanged;
    public static object RemovePendingValue { get; } = new();

    public SchemaConfigEditor()
    {
        InitializeComponent();
    }

    public void LoadSchema(JsonElement schema, JsonElement config)
    {
        _loading = true;
        _schema = schema;
        _config = config;
        _changes.Clear();
        _validationErrors.Clear();
        FieldsPanel.Children.Clear();

        try
        {
            RenderSchemaNode("", schema, config, FieldsPanel, 0);
        }
        catch (Exception ex)
        {
            Logger.Error($"[SchemaConfigEditor] Failed to render schema editor: {ex}");
        }

        // If schema rendering produced nothing, fall back to rendering config as editable fields
        if (FieldsPanel.Children.Count == 0 && config.ValueKind == JsonValueKind.Object)
        {
            RenderConfigDirectly("", config, FieldsPanel, 0);
        }
        _loading = false;
    }

    public Dictionary<string, object?> GetChanges() => new(_changes);
    public Dictionary<string, string> GetValidationErrors() => new(_validationErrors);

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
                    var required = IsRequired(schema, prop.Name);
                    RenderField(childPath, prop.Name, childSchema, childConfig, parent, required);
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
        JsonElement config, StackPanel parent, bool required)
    {
        var label = GetLabel(path, name);
        var description = SafeGetString(schema, "description");
        var title = SafeGetString(schema, "title");
        if (!string.IsNullOrWhiteSpace(title))
            label = title!;
        var type = ExtractSchemaType(schema) ?? "string";
        var isSensitive = IsSensitive(path);
        var readOnly = schema.TryGetProperty("readOnly", out var readOnlyEl)
            && readOnlyEl.ValueKind == JsonValueKind.True;

        // Resolve default value if config is missing
        var effectiveConfig = config;
        if (effectiveConfig.ValueKind == JsonValueKind.Undefined
            && schema.TryGetProperty("default", out var defaultVal))
        {
            effectiveConfig = defaultVal;
        }

        var fieldPanel = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 10)
        };

        var headerText = required ? $"{label} *" : label;
        var errorBlock = CreateErrorBlock();

        UIElement control;

        if (readOnly)
        {
            control = RenderReadOnlyField(headerText, effectiveConfig);
        }
        else if (schema.TryGetProperty("enum", out var enumEl)
            && enumEl.ValueKind == JsonValueKind.Array)
        {
            control = RenderEnumField(path, headerText, enumEl, effectiveConfig,
                value => StageValue(path, value, schema, required, errorBlock));
        }
        else if (type == "boolean")
        {
            control = RenderBoolField(path, headerText, effectiveConfig,
                value => StageValue(path, value, schema, required, errorBlock));
        }
        else if (type == "integer" || type == "number")
        {
            control = RenderNumberField(path, headerText, type!, schema, effectiveConfig,
                value => StageValue(path, value, schema, required, errorBlock));
        }
        else if (type == "array" && schema.TryGetProperty("items", out var itemsSchema))
        {
            control = RenderArrayField(path, headerText, description, itemsSchema, effectiveConfig, errorBlock,
                value => StageValue(path, value, schema, required, errorBlock));
        }
        else if (type == "object")
        {
            control = RenderJsonObjectField(path, headerText, description, effectiveConfig, errorBlock,
                value => StageValue(path, value, schema, required, errorBlock));
        }
        else // string (default)
        {
            control = isSensitive
                ? RenderSensitiveField(path, headerText, effectiveConfig,
                    value => StageValue(path, value, schema, required, errorBlock))
                : RenderStringField(path, headerText, effectiveConfig,
                    value => StageValue(path, value, schema, required, errorBlock));
        }

        if (readOnly && control is Control readOnlyControl)
            readOnlyControl.IsEnabled = false;

        fieldPanel.Children.Add(control);
        if (!string.IsNullOrEmpty(description))
        {
            fieldPanel.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = SecondaryBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (schema.TryGetProperty("default", out var defaultEl) &&
            defaultEl.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
        {
            fieldPanel.Children.Add(new TextBlock
            {
                Text = $"Default: {FormatScalar(defaultEl)}",
                FontSize = 11,
                Foreground = SecondaryBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        fieldPanel.Children.Add(errorBlock);
        parent.Children.Add(fieldPanel);
    }

    private UIElement RenderEnumField(string path, string label,
        JsonElement enumEl, JsonElement config, Action<object?> onChanged)
    {
        var combo = new ComboBox { Header = label, MinWidth = 200 };

        var currentVal = config.ValueKind == JsonValueKind.String ? config.GetString() : null;
        foreach (var item in enumEl.EnumerateArray())
        {
            var val = item.GetString() ?? "";
            combo.Items.Add(val);
            if (val == currentVal) combo.SelectedItem = val;
        }

        combo.SelectionChanged += (s, e) =>
        {
            if (_loading) return;
            onChanged(combo.SelectedItem as string);
        };
        return combo;
    }

    private UIElement RenderBoolField(string path, string label,
        JsonElement config, Action<object?> onChanged)
    {
        var toggle = new ToggleSwitch { Header = label };
        toggle.IsOn = config.ValueKind == JsonValueKind.True;
        toggle.Toggled += (s, e) =>
        {
            if (_loading) return;
            onChanged(toggle.IsOn);
        };
        return toggle;
    }

    private UIElement RenderNumberField(string path, string label,
        string type, JsonElement schema, JsonElement config, Action<object?> onChanged)
    {
        var numBox = new NumberBox
        {
            Header = label,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 200
        };
        if (config.ValueKind == JsonValueKind.Number)
            numBox.Value = config.GetDouble();
        if (schema.TryGetProperty("minimum", out var min))
            numBox.Minimum = min.GetDouble();
        if (schema.TryGetProperty("maximum", out var max))
            numBox.Maximum = max.GetDouble();

        numBox.ValueChanged += (s, e) =>
        {
            if (_loading) return;
            onChanged(double.IsNaN(numBox.Value) ? null : ConfigEditorModel.CoerceNumber(numBox.Value, type));
        };
        return numBox;
    }

    private UIElement RenderStringField(string path, string label,
        JsonElement config, Action<object?> onChanged)
    {
        var textBox = new TextBox { Header = label, MinWidth = 300 };
        if (config.ValueKind == JsonValueKind.String)
            textBox.Text = config.GetString() ?? "";
        else if (config.ValueKind != JsonValueKind.Undefined
                 && config.ValueKind != JsonValueKind.Null)
            textBox.Text = config.ToString();

        textBox.TextChanged += (s, e) =>
        {
            if (_loading) return;
            onChanged(textBox.Text);
        };
        return textBox;
    }

    private UIElement RenderSensitiveField(string path, string label,
        JsonElement config, Action<object?> onChanged)
    {
        var pwBox = new PasswordBox
        {
            Header = label,
            Width = 350,
            PlaceholderText = config.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(config.GetString())
                ? "Leave blank to keep existing value"
                : ""
        };
        var hasExistingValue = config.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(config.GetString());

        pwBox.PasswordChanged += (s, e) =>
        {
            if (_loading) return;
            onChanged(hasExistingValue && string.IsNullOrEmpty(pwBox.Password)
                ? RemovePendingValue
                : pwBox.Password);
        };
        return pwBox;
    }

    private UIElement RenderArrayField(string path, string label, string? description,
        JsonElement itemsSchema, JsonElement config, TextBlock errorBlock, Action<object?> onChanged)
    {
        var itemType = ExtractSchemaType(itemsSchema) ?? "string";
        if (itemType is not ("string" or "integer" or "number" or "boolean"))
        {
            return RenderJsonArrayField(path, label, description, config, errorBlock, onChanged);
        }

        var panel = new StackPanel { Spacing = 6 };
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

        var itemsPanel = new StackPanel { Spacing = 6 };

        if (config.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in config.EnumerateArray())
            {
                AddArrayItem(itemsPanel, path, itemType, FormatScalar(item), onChanged);
            }
        }

        panel.Children.Add(itemsPanel);

        var addBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE710", FontSize = 12 },
                    new TextBlock { Text = LocalizationHelper.GetString("SchemaConfigEditor_AddItem") }
                }
            },
            Margin = new Thickness(0, 4, 0, 0)
        };
        addBtn.Click += (s, e) =>
        {
            AddArrayItem(itemsPanel, path, itemType, "", onChanged);
            UpdateArrayChanges(itemsPanel, path, itemType, onChanged);
        };
        panel.Children.Add(addBtn);

        return panel;
    }

    private UIElement RenderJsonArrayField(string path, string label, string? description,
        JsonElement config, TextBlock errorBlock, Action<object?> onChanged)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = label,
            Message = "This array uses complex items. Edit its JSON below; local validation will run before Save is enabled."
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

        var textBox = new TextBox
        {
            Text = config.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
                : "[]",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            MinHeight = 120,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        textBox.TextChanged += (s, e) =>
        {
            if (_loading) return;
            try
            {
                using var document = JsonDocument.Parse(textBox.Text);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    _changes[path] = RemovePendingValue;
                    SetValidationError(path, "Must be a JSON array.", errorBlock);
                }
                else
                {
                    onChanged(document.RootElement.Clone());
                    return;
                }
            }
            catch (JsonException ex)
            {
                _changes[path] = RemovePendingValue;
                SetValidationError(path, $"Invalid JSON: {ex.Message}", errorBlock);
            }
            ConfigChanged?.Invoke(this, new SchemaConfigChangedEventArgs(GetChanges(), GetValidationErrors()));
        };
        panel.Children.Add(textBox);
        return panel;
    }

    private UIElement RenderJsonObjectField(string path, string label, string? description,
        JsonElement config, TextBlock errorBlock, Action<object?> onChanged)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = label,
            Message = "Enter a JSON object, e.g. {\"key\": \"value\"}. Changes are validated before Save is enabled."
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

        var textBox = new TextBox
        {
            Text = config.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
                : "{}",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            MinHeight = 80,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        textBox.TextChanged += (s, e) =>
        {
            if (_loading) return;
            try
            {
                using var document = JsonDocument.Parse(textBox.Text);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _changes[path] = RemovePendingValue;
                    SetValidationError(path, "Must be a JSON object.", errorBlock);
                }
                else
                {
                    onChanged(document.RootElement.Clone());
                    return;
                }
            }
            catch (JsonException ex)
            {
                _changes[path] = RemovePendingValue;
                SetValidationError(path, $"Invalid JSON: {ex.Message}", errorBlock);
            }
            ConfigChanged?.Invoke(this, new SchemaConfigChangedEventArgs(GetChanges(), GetValidationErrors()));
        };
        panel.Children.Add(textBox);
        return panel;
    }

    private void AddArrayItem(StackPanel itemsPanel, string path, string itemType, string value, Action<object?> onChanged)
    {
        var row = new Grid
        {
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBox = new TextBox
        {
            Text = value,
            MinWidth = 250,
            Height = 34,
            PlaceholderText = itemType switch
            {
                "boolean" => "true or false",
                "integer" => "Integer value",
                "number" => "Number value",
                _ => "Value"
            }
        };
        textBox.TextChanged += (s, e) =>
        {
            if (_loading) return;
            UpdateArrayChanges(itemsPanel, path, itemType, onChanged);
        };

        var removeBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 },
            Height = 34,
            Width = 34,
            Padding = new Thickness(0)
        };
        ToolTipService.SetToolTip(removeBtn, "Remove item");
        removeBtn.Click += (s, e) =>
        {
            if (row.Parent is Border border)
                itemsPanel.Children.Remove(border);
            UpdateArrayChanges(itemsPanel, path, itemType, onChanged);
        };

        Grid.SetColumn(textBox, 0);
        Grid.SetColumn(removeBtn, 1);
        row.Children.Add(textBox);
        row.Children.Add(removeBtn);
        itemsPanel.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 7, 8, 7),
            Child = row
        });
    }

    private void UpdateArrayChanges(StackPanel itemsPanel, string path, string itemType, Action<object?> onChanged)
    {
        var values = new List<object?>();
        foreach (var child in itemsPanel.Children)
        {
            if (child is Border { Child: Grid row } && row.Children.Count > 0
                && row.Children[0] is TextBox tb)
            {
                values.Add(CoerceArrayItem(tb.Text, itemType));
            }
        }
        onChanged(values.ToArray());
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
        var normalizedPath = path.ToLowerInvariant();
        return normalizedPath.Contains("token") || normalizedPath.Contains("secret")
            || normalizedPath.Contains("password") || normalizedPath.Contains("apikey")
            || normalizedPath.Contains("api_key");
    }

    private static bool IsRequired(JsonElement parentSchema, string propName)
    {
        if (!parentSchema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), propName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private void StageValue(string path, object? value, JsonElement schema, bool required, TextBlock errorBlock)
    {
        _changes[path] = value;
        var error = ValidateValue(value, schema, required);
        SetValidationError(path, error, errorBlock);
        ConfigChanged?.Invoke(this, new SchemaConfigChangedEventArgs(GetChanges(), GetValidationErrors()));
    }

    private static TextBlock CreateErrorBlock() => new()
    {
        Foreground = new SolidColorBrush(Colors.Firebrick),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed
    };

    private void SetValidationError(string path, string? error, TextBlock errorBlock)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            _validationErrors.Remove(path);
            errorBlock.Text = "";
            errorBlock.Visibility = Visibility.Collapsed;
            return;
        }

        _validationErrors[path] = error;
        errorBlock.Text = error;
        errorBlock.Visibility = Visibility.Visible;
    }

    private static string? ValidateValue(object? value, JsonElement schema, bool required)
    {
        if (ReferenceEquals(value, RemovePendingValue))
            return null;

        if (required && IsBlank(value))
            return "This field is required.";

        var expectedType = ExtractSchemaType(schema);
        if (!IsBlank(value))
        {
            if (expectedType == "integer" && value is not int and not long)
                return "Must be an integer.";
            if (expectedType == "number" && value is not int and not long and not double)
                return "Must be a number.";
            if (expectedType == "boolean" && value is not bool)
                return "Must be true or false.";
            if (expectedType == "array" &&
                value is not Array &&
                value is not JsonElement { ValueKind: JsonValueKind.Array })
                return "Must be a list.";
        }

        if (value is string text)
        {
            if (schema.TryGetProperty("minLength", out var minLength) &&
                minLength.TryGetInt32(out var min) &&
                text.Length < min)
                return $"Must be at least {min} characters.";

            if (schema.TryGetProperty("maxLength", out var maxLength) &&
                maxLength.TryGetInt32(out var max) &&
                text.Length > max)
                return $"Must be {max} characters or fewer.";

            if (schema.TryGetProperty("pattern", out var pattern) &&
                pattern.ValueKind == JsonValueKind.String &&
                pattern.GetString() is { Length: > 0 } regex)
            {
                try
                {
                    if (!Regex.IsMatch(text, regex, RegexOptions.None, PatternValidationTimeout))
                        return "Does not match the expected format.";
                }
                catch (RegexMatchTimeoutException)
                {
                    return "Pattern validation timed out.";
                }
                catch (ArgumentException)
                {
                    return "Pattern validation is unavailable because the schema pattern is invalid.";
                }
            }
        }

        if (value is JsonElement jsonArray && jsonArray.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var minItems) &&
                minItems.TryGetInt32(out var min) &&
                jsonArray.GetArrayLength() < min)
                return $"Must include at least {min} item{(min == 1 ? "" : "s")}.";

            if (schema.TryGetProperty("maxItems", out var maxItems) &&
                maxItems.TryGetInt32(out var max) &&
                jsonArray.GetArrayLength() > max)
                return $"Must include {max} item{(max == 1 ? "" : "s")} or fewer.";

            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var item in jsonArray.EnumerateArray())
                {
                    var itemError = ValidateValue(item, itemSchema, false);
                    if (!string.IsNullOrWhiteSpace(itemError))
                        return $"Item {index + 1}: {itemError}";
                    index++;
                }
            }
        }
        else if (value is Array array)
        {
            if (schema.TryGetProperty("minItems", out var minItems) &&
                minItems.TryGetInt32(out var min) &&
                array.Length < min)
                return $"Must include at least {min} item{(min == 1 ? "" : "s")}.";

            if (schema.TryGetProperty("maxItems", out var maxItems) &&
                maxItems.TryGetInt32(out var max) &&
                array.Length > max)
                return $"Must include {max} item{(max == 1 ? "" : "s")} or fewer.";

            if (schema.TryGetProperty("items", out var itemSchema))
            {
                for (var i = 0; i < array.Length; i++)
                {
                    var itemError = ValidateValue(array.GetValue(i), itemSchema, false);
                    if (!string.IsNullOrWhiteSpace(itemError))
                        return $"Item {i + 1}: {itemError}";
                }
            }
        }

        if (value is long or int or double)
        {
            var number = Convert.ToDouble(value);
            if (schema.TryGetProperty("minimum", out var minimum) &&
                minimum.TryGetDouble(out var min) &&
                number < min)
                return $"Must be at least {min}.";

            if (schema.TryGetProperty("maximum", out var maximum) &&
                maximum.TryGetDouble(out var max) &&
                number > max)
                return $"Must be {max} or less.";
        }

        return null;
    }

    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        var removePending when ReferenceEquals(removePending, RemovePendingValue) => true,
        string text => string.IsNullOrWhiteSpace(text),
        Array array => array.Length == 0,
        _ => false
    };

    private static object? CoerceArrayItem(string value, string itemType) => itemType switch
    {
        "integer" when long.TryParse(value, out var integer) => integer,
        "number" when double.TryParse(value, out var number) => number,
        "boolean" when bool.TryParse(value, out var boolean) => boolean,
        _ => value
    };

    private static string FormatScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.ToString()
    };

    private static UIElement RenderReadOnlyField(string label, JsonElement config)
    {
        return new TextBox
        {
            Header = label,
            Text = FormatScalar(config),
            IsReadOnly = true,
            MinWidth = 300,
            Foreground = SecondaryBrush
        };
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
                    toggle.Toggled += (s, e) =>
                    {
                        if (_loading) return;
                        _changes[childPath] = toggle.IsOn;
                        ConfigChanged?.Invoke(this, new SchemaConfigChangedEventArgs(GetChanges(), GetValidationErrors()));
                    };
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
                    numBox.ValueChanged += (s, e) =>
                    {
                        if (_loading) return;
                        _changes[childPath] = numBox.Value;
                        ConfigChanged?.Invoke(this, new SchemaConfigChangedEventArgs(GetChanges(), GetValidationErrors()));
                    };
                    parent.Children.Add(numBox);
                    break;

                case JsonValueKind.String:
                    if (IsSensitive(childPath))
                    {
                        var hasExistingValue = !string.IsNullOrEmpty(value.GetString());
                        var pwBox = new PasswordBox
                        {
                            Header = GetLabel(childPath, prop.Name),
                            Width = 350,
                            PlaceholderText = hasExistingValue ? "Leave blank to keep existing value" : ""
                        };
                        pwBox.PasswordChanged += (s, e) =>
                        {
                            if (_loading) return;
                            _changes[childPath] = hasExistingValue && string.IsNullOrEmpty(pwBox.Password)
                                ? RemovePendingValue
                                : pwBox.Password;
                            ConfigChanged?.Invoke(this, new SchemaConfigChangedEventArgs(GetChanges(), GetValidationErrors()));
                        };
                        parent.Children.Add(pwBox);
                    }
                    else
                    {
                        var textBox = new TextBox { Header = GetLabel(childPath, prop.Name), Text = value.GetString() ?? "", MinWidth = 300 };
                        textBox.TextChanged += (s, e) =>
                        {
                            if (_loading) return;
                            _changes[childPath] = textBox.Text;
                            ConfigChanged?.Invoke(this, new SchemaConfigChangedEventArgs(GetChanges(), GetValidationErrors()));
                        };
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
