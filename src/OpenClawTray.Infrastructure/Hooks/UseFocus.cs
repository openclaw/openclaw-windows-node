using OpenClawTray.Infrastructure.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Hooks;

/// <summary>
/// Manages focus for a set of registered form fields.
/// Supports programmatic focus control, enter-to-advance, and field ordering.
/// </summary>
public sealed class FocusManager
{
    private readonly List<string> _fieldOrder = new();
    private readonly Dictionary<string, Control> _controls = new();
    private Action? _onSubmit;

    /// <summary>
    /// Registered field names in order.
    /// </summary>
    public IReadOnlyList<string> Fields => _fieldOrder;

    /// <summary>
    /// Registers a field name in ordering. Call on every render to maintain order.
    /// </summary>
    public void Register(string fieldName)
    {
        if (!_fieldOrder.Contains(fieldName))
            _fieldOrder.Add(fieldName);
    }

    /// <summary>
    /// Associates a WinUI control with a field name. Called from .Set() during mount/update.
    /// </summary>
    public void SetControl(string fieldName, Control control)
    {
        _controls[fieldName] = control;
    }

    /// <summary>
    /// Sets the callback for submit (Enter on last field).
    /// </summary>
    public void SetSubmitHandler(Action handler) => _onSubmit = handler;

    /// <summary>
    /// Focuses a specific field by name.
    /// </summary>
    public void FocusField(string fieldName)
    {
        if (_controls.TryGetValue(fieldName, out var control))
            control.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Focuses the first field from the given list that exists in this manager.
    /// Useful with ValidationContext.InvalidFields.
    /// </summary>
    public void FocusFirst(IReadOnlyList<string> fieldNames)
    {
        foreach (var name in fieldNames)
        {
            if (_controls.ContainsKey(name))
            {
                FocusField(name);
                return;
            }
        }
    }

    /// <summary>
    /// Focuses the field after the given field in registration order.
    /// If the current field is the last one, triggers submit.
    /// </summary>
    public void FocusNext(string? currentField = null)
    {
        if (_fieldOrder.Count == 0) return;

        if (currentField is null)
        {
            FocusField(_fieldOrder[0]);
            return;
        }

        var index = _fieldOrder.IndexOf(currentField);
        if (index < 0) return;

        if (index + 1 < _fieldOrder.Count)
        {
            FocusField(_fieldOrder[index + 1]);
        }
        else
        {
            _onSubmit?.Invoke();
        }
    }

    /// <summary>
    /// Focuses the field before the given field in registration order.
    /// </summary>
    public void FocusPrevious(string? currentField = null)
    {
        if (_fieldOrder.Count == 0) return;

        if (currentField is null)
        {
            FocusField(_fieldOrder[^1]);
            return;
        }

        var index = _fieldOrder.IndexOf(currentField);
        if (index <= 0) return;

        FocusField(_fieldOrder[index - 1]);
    }

    /// <summary>
    /// Returns true if the given field is the last registered field.
    /// </summary>
    public bool IsLastField(string fieldName) =>
        _fieldOrder.Count > 0 && _fieldOrder[^1] == fieldName;

    /// <summary>
    /// Returns true if the given field is the first registered field.
    /// </summary>
    public bool IsFirstField(string fieldName) =>
        _fieldOrder.Count > 0 && _fieldOrder[0] == fieldName;

    /// <summary>
    /// Clears all registrations.
    /// </summary>
    public void Clear()
    {
        _fieldOrder.Clear();
        _controls.Clear();
    }
}

/// <summary>
/// Extension methods for the UseFocus hook.
/// </summary>
public static class UseFocusExtensions
{
    /// <summary>
    /// Creates and returns a FocusManager scoped to this component.
    /// The manager persists across re-renders.
    /// </summary>
    public static FocusManager UseFocus(this RenderContext ctx)
    {
        var (fm, _) = ctx.UseState(new FocusManager());
        return fm;
    }

    /// <summary>
    /// Creates and returns a FocusManager for this component.
    /// </summary>
    public static FocusManager UseFocus(this Component component)
        => component.Context.UseFocus();
}

/// <summary>
/// Attached metadata for focus registration on elements.
/// </summary>
public sealed record FocusAttached(
    string FieldName,
    FocusManager Manager,
    bool AutoFocus = false);

/// <summary>
/// Fluent extensions for attaching focus management to elements.
/// Uses .Set() to capture the WinUI control reference during mount/update.
/// </summary>
public static class FocusExtensions
{
    /// <summary>
    /// Registers this TextField with a FocusManager for programmatic focus control.
    /// </summary>
    public static TextFieldElement Focus(this TextFieldElement el, FocusManager fm, string fieldName, bool autoFocus = false)
    {
        fm.Register(fieldName);
        return el.Set(tb =>
        {
            fm.SetControl(fieldName, tb);
            if (autoFocus)
                tb.Loaded += (_, _) => tb.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>
    /// Registers this NumberBox with a FocusManager for programmatic focus control.
    /// </summary>
    public static NumberBoxElement Focus(this NumberBoxElement el, FocusManager fm, string fieldName, bool autoFocus = false)
    {
        fm.Register(fieldName);
        return el.Set(nb =>
        {
            fm.SetControl(fieldName, nb);
            if (autoFocus)
                nb.Loaded += (_, _) => nb.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>
    /// Registers any element with a FocusManager via attached metadata (for custom types).
    /// </summary>
    public static T FocusMeta<T>(this T el, FocusManager fm, string fieldName, bool autoFocus = false) where T : Element
    {
        fm.Register(fieldName);
        return (T)el.SetAttached(new FocusAttached(fieldName, fm, autoFocus));
    }

    /// <summary>
    /// Gets the FocusAttached metadata from an element.
    /// </summary>
    public static FocusAttached? GetFocus<T>(this T el) where T : Element =>
        el.GetAttached<FocusAttached>();
}
