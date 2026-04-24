using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// Controls when error styling becomes visible.
/// </summary>
public enum ShowWhen
{
    /// <summary>Always show errors, regardless of touched/dirty state.</summary>
    Always,
    /// <summary>Only show errors after the field has been focused then blurred.</summary>
    WhenTouched,
    /// <summary>Only show errors after the field's value has changed from initial.</summary>
    WhenDirty,
    /// <summary>Only show errors after the first submit attempt.</summary>
    AfterFirstSubmit,
    /// <summary>Never show error styling (errors still exist in context).</summary>
    Never
}

/// <summary>
/// Default error styling configuration. Uses WinUI theme resource names.
/// </summary>
public static class ErrorStyling
{
    /// <summary>
    /// WinUI theme resource key for Error-severity border brush (red).
    /// Resolved at runtime: SystemFillColorCriticalBrush
    /// </summary>
    public const string ErrorBrushKey = "SystemFillColorCriticalBrush";

    /// <summary>
    /// WinUI theme resource key for Warning-severity border brush (yellow/orange).
    /// Resolved at runtime: SystemFillColorCautionBrush
    /// </summary>
    public const string WarningBrushKey = "SystemFillColorCautionBrush";

    /// <summary>
    /// WinUI theme resource key for Info-severity styling (blue/gray).
    /// Resolved at runtime: SystemFillColorSolidNeutralBrush
    /// </summary>
    public const string InfoBrushKey = "SystemFillColorSolidNeutralBrush";

    /// <summary>
    /// Default border thickness applied when an error is present.
    /// </summary>
    public static readonly Microsoft.UI.Xaml.Thickness ErrorBorderThickness = new(2);

    /// <summary>
    /// Gets the theme resource key for the specified severity.
    /// </summary>
    public static string GetBrushKey(Severity severity) => severity switch
    {
        Severity.Error => ErrorBrushKey,
        Severity.Warning => WarningBrushKey,
        Severity.Info => InfoBrushKey,
        _ => ErrorBrushKey,
    };

    /// <summary>
    /// Determines whether error styling should be visible for a field,
    /// based on the field's state and the ShowWhen policy.
    /// </summary>
    public static bool ShouldShowErrors(
        ValidationContext ctx,
        string fieldName,
        ShowWhen showWhen,
        bool submitAttempted = false)
    {
        if (!ctx.HasMessages(fieldName))
            return false;

        return showWhen switch
        {
            ShowWhen.Always => true,
            ShowWhen.WhenTouched => ctx.IsTouched(fieldName),
            ShowWhen.WhenDirty => ctx.IsDirty(fieldName),
            ShowWhen.AfterFirstSubmit => submitAttempted,
            ShowWhen.Never => false,
            _ => true,
        };
    }
}

/// <summary>
/// Fluent extension methods for error styling on elements.
/// </summary>
public static class ErrorStylingExtensions
{
    /// <summary>
    /// Configures custom styling to apply when the field has an error.
    /// The configure action receives the element builder and can set any modifier.
    /// </summary>
    public static T OnError<T>(this T el, Func<T, T> configure) where T : Element
    {
        var attached = el.GetAttached<ErrorStylingAttached>() ?? ErrorStylingAttached.Default;
        return (T)el.SetAttached(attached with { OnErrorTransform = obj => configure((T)obj) });
    }

    /// <summary>
    /// Configures custom styling to apply when the field has a warning.
    /// </summary>
    public static T OnWarning<T>(this T el, Func<T, T> configure) where T : Element
    {
        var attached = el.GetAttached<ErrorStylingAttached>() ?? ErrorStylingAttached.Default;
        return (T)el.SetAttached(attached with { OnWarningTransform = obj => configure((T)obj) });
    }

    /// <summary>
    /// Applies error styling to an element based on ValidationContext state.
    /// Returns the element with appropriate border styling applied.
    /// </summary>
    public static T WithErrorStyling<T>(
        this T el,
        ValidationContext ctx,
        string fieldName,
        ShowWhen showWhen = ShowWhen.WhenTouched,
        bool submitAttempted = false) where T : Element
    {
        if (!ErrorStyling.ShouldShowErrors(ctx, fieldName, showWhen, submitAttempted))
            return el;

        var severity = ctx.HighestSeverity(fieldName);
        if (severity is null)
            return el;

        // Check for custom styling overrides
        var customStyling = el.GetAttached<ErrorStylingAttached>();
        if (customStyling?.OnErrorTransform is not null && severity == Severity.Error)
            return (T)customStyling.OnErrorTransform(el);
        if (customStyling?.OnWarningTransform is not null && severity == Severity.Warning)
            return (T)customStyling.OnWarningTransform(el);

        // Apply default border styling via ThemeRef
        var brushKey = ErrorStyling.GetBrushKey(severity.Value);
        var themeRef = new ThemeRef(brushKey);

        var bindings = el.ThemeBindings is not null
            ? new Dictionary<string, ThemeRef>(el.ThemeBindings) { ["BorderBrush"] = themeRef }
            : new Dictionary<string, ThemeRef> { ["BorderBrush"] = themeRef };

        var modifiers = el.Modifiers ?? new ElementModifiers();
        modifiers = modifiers with { BorderThickness = ErrorStyling.ErrorBorderThickness };

        return el with { ThemeBindings = bindings, Modifiers = modifiers } is T typed ? typed : el;
    }
}

/// <summary>
/// Attached metadata for custom error/warning styling transforms.
/// </summary>
public sealed record ErrorStylingAttached(
    Func<Element, Element>? OnErrorTransform = null,
    Func<Element, Element>? OnWarningTransform = null)
{
    public static readonly ErrorStylingAttached Default = new();
}
