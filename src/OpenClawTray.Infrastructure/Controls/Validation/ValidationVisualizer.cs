using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Controls.Validation;

/// <summary>
/// The visual style of a validation visualizer.
/// </summary>
public enum VisualizerStyle
{
    /// <summary>Inline error text below the control.</summary>
    Inline,
    /// <summary>Bullet list of all errors from the content subtree.</summary>
    Summary,
    /// <summary>WinUI InfoBar with severity-appropriate color.</summary>
    InfoBar,
    /// <summary>Custom render function receives all caught messages.</summary>
    Custom
}

/// <summary>
/// An element that catches validation errors from its content subtree and displays them.
/// Errors caught by a visualizer are removed from further bubbling.
/// </summary>
public sealed record ValidationVisualizerElement(
    VisualizerStyle Style,
    Element Content) : Element
{
    /// <summary>
    /// Custom render function for Custom-style visualizers.
    /// Receives all caught messages and returns an Element to render.
    /// </summary>
    public Func<IReadOnlyList<ValidationMessage>, Element>? CustomRender { get; init; }

    /// <summary>
    /// Optional severity filter. When set, only catches messages of this severity.
    /// Messages with other severities continue to bubble.
    /// </summary>
    public Severity? SeverityFilter { get; init; }

    /// <summary>
    /// Controls when the visualizer shows its content.
    /// </summary>
    public ShowWhen ShowWhen { get; init; } = ShowWhen.Always;

    /// <summary>
    /// Optional heading text for the visualizer.
    /// </summary>
    public string? Title { get; init; }
}

/// <summary>
/// DSL factory methods for validation visualizers.
/// </summary>
public static class ValidationVisualizerDsl
{
    /// <summary>
    /// Creates a validation visualizer that catches errors from its content subtree.
    /// </summary>
    public static ValidationVisualizerElement ValidationVisualizer(
        VisualizerStyle style,
        Element content,
        string? title = null,
        Severity? severityFilter = null,
        ShowWhen showWhen = ShowWhen.Always) =>
        new(style, content)
        {
            Title = title,
            SeverityFilter = severityFilter,
            ShowWhen = showWhen
        };

    /// <summary>
    /// Creates a custom validation visualizer with a render function.
    /// </summary>
    public static ValidationVisualizerElement ValidationVisualizer(
        Func<IReadOnlyList<ValidationMessage>, Element> render,
        Element content,
        Severity? severityFilter = null,
        ShowWhen showWhen = ShowWhen.Always) =>
        new(VisualizerStyle.Custom, content)
        {
            CustomRender = render,
            SeverityFilter = severityFilter,
            ShowWhen = showWhen
        };
}

/// <summary>
/// Fluent extension for inline error display on elements with .Validate().
/// </summary>
public static class ShowErrorsExtension
{
    /// <summary>
    /// Wraps this element in an inline visualizer that shows error text below the control.
    /// </summary>
    public static ValidationVisualizerElement ShowErrors<T>(this T el,
        ShowWhen showWhen = ShowWhen.Always) where T : Element =>
        new(VisualizerStyle.Inline, el) { ShowWhen = showWhen };
}

/// <summary>
/// Helpers for filtering and processing validation messages through the visualizer hierarchy.
/// </summary>
public static class ErrorBubbling
{
    /// <summary>
    /// Filters messages based on a visualizer's severity filter.
    /// Returns (caught, uncaught) — caught messages are displayed, uncaught continue to bubble.
    /// </summary>
    public static (IReadOnlyList<ValidationMessage> Caught, IReadOnlyList<ValidationMessage> Uncaught)
        FilterMessages(IReadOnlyList<ValidationMessage> messages, Severity? severityFilter)
    {
        if (severityFilter is null)
            return (messages, Array.Empty<ValidationMessage>());

        var caught = new List<ValidationMessage>();
        var uncaught = new List<ValidationMessage>();

        foreach (var msg in messages)
        {
            if (msg.Severity == severityFilter.Value)
                caught.Add(msg);
            else
                uncaught.Add(msg);
        }

        return (caught, uncaught);
    }

    /// <summary>
    /// Determines whether a visualizer should display its content based on ShowWhen and context state.
    /// </summary>
    public static bool ShouldDisplay(
        IReadOnlyList<ValidationMessage> messages,
        ShowWhen showWhen,
        ValidationContext? ctx = null,
        bool submitAttempted = false)
    {
        if (messages.Count == 0)
            return false;

        return showWhen switch
        {
            ShowWhen.Always => true,
            ShowWhen.WhenTouched => ctx is not null && messages.Any(m => ctx.IsTouched(m.Field)),
            ShowWhen.WhenDirty => ctx is not null && messages.Any(m => ctx.IsDirty(m.Field)),
            ShowWhen.AfterFirstSubmit => submitAttempted,
            ShowWhen.Never => false,
            _ => true,
        };
    }

    /// <summary>
    /// Gets the highest severity from a list of messages.
    /// </summary>
    public static Severity? HighestSeverity(IReadOnlyList<ValidationMessage> messages)
    {
        if (messages.Count == 0) return null;
        Severity highest = messages[0].Severity;
        for (int i = 1; i < messages.Count; i++)
        {
            if (messages[i].Severity > highest)
                highest = messages[i].Severity;
        }
        return highest;
    }
}
