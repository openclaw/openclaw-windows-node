using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Wires A2UI <c>label</c> / <c>description</c> properties to WinUI
/// AutomationProperties so Narrator and other assistive tech can announce
/// content. v0.8 components carry these properties on most interactive and
/// display elements; without this hook, controls whose Content is just an
/// Icon or a path-bound bitmap are invisible to screen readers.
/// </summary>
internal static class AutomationHelpers
{
    /// <summary>
    /// Apply the component's <c>label</c> as Name and <c>description</c> as
    /// HelpText. Resolves both through <see cref="RenderContext.ResolveString"/>
    /// so path-bound values produce a current snapshot at render time. No-op if
    /// neither property is present.
    /// </summary>
    public static void Apply(FrameworkElement element, A2UIComponentDef c, RenderContext ctx,
        string labelKey = "label", string descriptionKey = "description")
    {
        var label = ctx.ResolveString(ctx.GetValue(c, labelKey));
        if (!string.IsNullOrEmpty(label))
            AutomationProperties.SetName(element, label);

        var description = ctx.ResolveString(ctx.GetValue(c, descriptionKey));
        if (!string.IsNullOrEmpty(description))
            AutomationProperties.SetHelpText(element, description);
    }

    /// <summary>Fallback when no A2UI property is available. Used when we have a known string (e.g., the action name).</summary>
    public static void SetName(FrameworkElement element, string? name)
    {
        if (!string.IsNullOrEmpty(name))
            AutomationProperties.SetName(element, name);
    }
}
