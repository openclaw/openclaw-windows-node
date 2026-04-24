using System.Diagnostics.CodeAnalysis;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using OpenClawTray.Infrastructure.Controls.Validation;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// A wrapper element that provides label, required indicator, description, and error display
/// for a child form control. Acts as an inline visualizer: when the child has errors,
/// the description text is replaced with error messages.
/// </summary>
public sealed record FormFieldElement(
    Element Content,
    string? Label = null,
    bool Required = false,
    string? Description = null) : Element
{
    /// <summary>
    /// The field name to query from the ValidationContext. When null, auto-detected
    /// from any ValidationAttached on the Content element.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Controls when error messages are shown (default: WhenTouched).
    /// </summary>
    public ShowWhen ShowWhen { get; init; } = ShowWhen.WhenTouched;
}

/// <summary>
/// DSL factory methods for FormField.
/// </summary>
public static class FormFieldDsl
{
    /// <summary>
    /// Wraps a form control with a label, required indicator, and description/error area.
    /// </summary>
    public static FormFieldElement FormField(
        Element content,
        string? label = null,
        bool required = false,
        string? description = null,
        string? fieldName = null,
        ShowWhen showWhen = ShowWhen.WhenTouched) =>
        new(content, label, required, description)
        {
            FieldName = fieldName,
            ShowWhen = showWhen
        };

    /// <summary>
    /// Creates an auto-wired FormField from a FieldDescriptor.
    /// Resolves the editor from TypeRegistry, sets label/description from the descriptor,
    /// detects required from validators, and wires validation.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "TypeRegistry.Resolve uses Activator.CreateInstance; acceptable for non-AOT builds.")]
    public static FormFieldElement FormField(
        FieldDescriptor field,
        object value,
        Action<object> onChange,
        OpenClawTray.Infrastructure.Controls.TypeRegistry? registry = null)
    {
        // Resolve editor
        Element content;
        if (field.Editor is not null)
        {
            content = field.Editor(value, onChange);
        }
        else if (registry is not null)
        {
            var meta = registry.Resolve(field.FieldType);
            content = meta.Editor is not null
                ? meta.Editor(value, onChange)
                : TextBlock(value?.ToString() ?? "(null)");
        }
        else
        {
            content = TextBlock(value?.ToString() ?? "(null)");
        }

        var label = field.DisplayName ?? field.Name;
        var description = field.Description;
        var required = field.Validators?.Any(v => v is RequiredValidator) == true;

        return new FormFieldElement(content, label, required, description)
        {
            FieldName = field.Name,
        };
    }
}

/// <summary>
/// Helpers for FormField rendering logic.
/// </summary>
public static class FormFieldHelpers
{
    /// <summary>
    /// Gets the display label, including the required indicator (*) if applicable.
    /// </summary>
    public static string GetDisplayLabel(string? label, bool required)
    {
        if (label is null) return "";
        return required ? $"{label} *" : label;
    }

    /// <summary>
    /// Gets the text to display in the description/error area.
    /// When errors are present and should be shown, returns error text.
    /// Otherwise returns the description text.
    /// </summary>
    public static (string? Text, bool IsError) GetDescriptionOrError(
        ValidationContext? ctx,
        string? fieldName,
        string? description,
        ShowWhen showWhen,
        bool submitAttempted = false)
    {
        if (ctx is null || fieldName is null)
            return (description, false);

        if (!ErrorStyling.ShouldShowErrors(ctx, fieldName, showWhen, submitAttempted))
            return (description, false);

        var messages = ctx.GetMessages(fieldName);
        if (messages.Count == 0)
            return (description, false);

        // Show first error message (or concatenate multiple)
        if (messages.Count == 1)
            return (messages[0].Text, true);

        return (string.Join(" • ", messages.Select(m => m.Text)), true);
    }

    /// <summary>
    /// Gets the automation name for the child control from the label text.
    /// </summary>
    public static string? GetAutomationName(string? label) =>
        label?.Replace(" *", ""); // Strip required indicator for screen readers

    /// <summary>
    /// Detects the field name from ValidationAttached on the content element.
    /// </summary>
    public static string? DetectFieldName(Element content)
    {
        var attached = content.GetAttached<ValidationAttached>();
        return attached?.FieldName;
    }

    /// <summary>
    /// Resolves the effective field name from either explicit or auto-detected.
    /// </summary>
    public static string? ResolveFieldName(string? explicitFieldName, Element content) =>
        explicitFieldName ?? DetectFieldName(content);
}
