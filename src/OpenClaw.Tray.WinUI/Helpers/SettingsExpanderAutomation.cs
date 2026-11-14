// Names the disclosure button inside a Toolkit SettingsExpander. Usage: SettingsExpanderAutomation.NameDisclosure(expander, "Gateway name", "Optional help").
using System.Collections.Generic;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Helpers;

/// <summary>
/// The Toolkit names its inner disclosure button only from a string Header. A SettingsExpander whose
/// Header is an element must name that button explicitly, or assistive technology reads an unnamed button.
/// </summary>
internal static class SettingsExpanderAutomation
{
    /// <summary>Names the disclosure now if its template is applied, and again whenever the expander loads.</summary>
    public static void NameDisclosure(SettingsExpander expander, string name, string? helpText = null)
    {
        Apply(expander, name, helpText);
        expander.Loaded += (_, _) => Apply(expander, name, helpText);
    }

    /// <summary>Names the disclosure if its template is applied; safe to call repeatedly.</summary>
    public static void Apply(SettingsExpander expander, string name, string? helpText = null)
    {
        if (FindDisclosure(expander) is not { } disclosure)
            return;
        AutomationProperties.SetName(disclosure, name);
        if (helpText is not null)
            AutomationProperties.SetHelpText(disclosure, helpText);
    }

    private static Microsoft.UI.Xaml.Controls.Expander? FindDisclosure(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is Microsoft.UI.Xaml.Controls.Expander found)
                return found;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, index));
        }
        return null;
    }
}
