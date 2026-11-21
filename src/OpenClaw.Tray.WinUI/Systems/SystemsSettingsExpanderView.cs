// Hosts Toolkit Gateway rows in Reactor. Usage: Component<SystemsSettingsExpanderView, SystemsSettingsExpanderProps>(props).
using System;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Systems;

internal sealed record SystemsSettingsExpanderProps(
    string Id, string Name, string Help, Element Header, Element Actions, Element Details,
    Element MachineDetails, bool IsExpanded, Action<bool> Expanded, Action<Control> Track);

/// <summary>Retains the Toolkit disclosure and reconciles its content without recreating row controls.</summary>
internal sealed class SystemsSettingsExpanderView : Component<SystemsSettingsExpanderProps>
{
    public override Element Render()
    {
        var row = UseMemo(() => new NativeRow(), []);
        UseEffect((Func<Action>)(() => row.Dispose), row);
        return XamlHost(() => row.Root, _ => row.Update(Props), nameof(SystemsSettingsExpanderView));
    }

    private sealed class NativeRow : IDisposable
    {
        private readonly ReactorHostControl header = new();
        private readonly ReactorHostControl actions = new();
        private readonly ReactorHostControl details = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly ReactorHostControl machineDetails = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly SettingsExpander expander;
        private readonly long expandedToken;
        private Action<bool>? expanded;
        private SystemsSettingsExpanderProps? current;
        private bool updating;
        public SettingsExpander Root => expander;

        public NativeRow()
        {
            expander = new SettingsExpander
            {
                HeaderIcon = new FontIcon { Glyph = FluentIconCatalog.ServerEnvironment, FontSize = 20 },
                Header = header,
                Content = actions,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            // This compact action must remain beside the caret, including at narrow widths.
            expander.Resources["SettingsCardWrapThreshold"] = 0d;
            expander.Resources["SettingsCardWrapNoIconThreshold"] = 0d;
            expander.Items.Add(new SettingsCard { Header = details, Padding = new Thickness(16) });
            var machineDetailsCard = new SettingsCard
            {
                Header = machineDetails,
                Padding = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(machineDetailsCard, "SystemsMachineDetailsCard");
            expander.Items.Add(machineDetailsCard);
            expandedToken = expander.RegisterPropertyChangedCallback(SettingsExpander.IsExpandedProperty, (_, _) =>
            {
                if (!updating)
                    expanded?.Invoke(expander.IsExpanded);
            });
            expander.Loaded += (_, _) => NameDisclosure();
        }

        public void Update(SystemsSettingsExpanderProps props)
        {
            current = props;
            expanded = props.Expanded;
            AutomationProperties.SetAutomationId(expander, "SystemsRow_" + props.Id);
            AutomationProperties.SetName(expander, props.Name);
            AutomationProperties.SetHelpText(expander, props.Help);
            header.Mount(_ => props.Header);
            actions.Mount(_ => props.Actions);
            details.Mount(_ => props.Details);
            machineDetails.Mount(_ => props.MachineDetails);
            updating = true;
            try { expander.IsExpanded = props.IsExpanded; }
            finally { updating = false; }
            props.Track(expander);
            NameDisclosure();
        }

        // This Header is a hosted element, so the disclosure button must be named explicitly.
        private void NameDisclosure()
        {
            if (current is not null)
                SettingsExpanderAutomation.Apply(expander, current.Name, current.Help);
        }

        public void Dispose()
        {
            expander.UnregisterPropertyChangedCallback(SettingsExpander.IsExpandedProperty, expandedToken);
            header.Dispose();
            actions.Dispose();
            details.Dispose();
            machineDetails.Dispose();
        }
    }
}
