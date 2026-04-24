using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Shared binding helpers for wiring a <see cref="Command"/> into command-capable
/// WinUI controls (<see cref="ButtonBase"/> derivatives, <see cref="SwipeItem"/>, …).
/// Keeps the per-control factory overloads thin: apply label/onClick at construction
/// time and defer Description / Icon / Accelerator / AccessKey to a mount-time setter
/// so per-site overrides (e.g. <c>.AccessKey("X")</c> after <c>.Command(cmd)</c>) win
/// via the normal modifier-after-command ordering.
/// </summary>
internal static class CommandBindings
{
    /// <summary>
    /// Applies command metadata that is common to every command-capable WinUI control:
    /// <see cref="Control.IsEnabled"/>, <see cref="ToolTipService.ToolTip"/>,
    /// <see cref="UIElement.AccessKey"/>, and <see cref="UIElement.KeyboardAccelerators"/>.
    /// Accepts <see cref="Control"/> so it can target both <see cref="ButtonBase"/>
    /// derivatives and WinUI controls that don't derive from ButtonBase
    /// (e.g. <see cref="SplitButton"/>, <see cref="ToggleSplitButton"/>).
    /// </summary>
    internal static void ApplyButtonBaseCommon(Control btn, Command cmd)
    {
        btn.IsEnabled = cmd.IsEnabled;
        if (cmd.Description is not null)
        {
            ToolTipService.SetToolTip(btn, cmd.Description);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(btn, cmd.Description);
        }
        if (cmd.AccessKey is not null) btn.AccessKey = cmd.AccessKey;

        // Remove any prior command-added accelerator before adding the new one, so
        // rerunning this setter on update/reconcile doesn't stack duplicates that
        // would cause the command to fire multiple times per chord.
        if (_commandAccelerators.TryGetValue(btn, out var prior))
        {
            btn.KeyboardAccelerators.Remove(prior);
            _commandAccelerators.Remove(btn);
        }
        if (cmd.Accelerator is not null)
        {
            var accel = new KeyboardAccelerator
            {
                Key = cmd.Accelerator.Key,
                Modifiers = cmd.Accelerator.Modifiers,
            };
            btn.KeyboardAccelerators.Add(accel);
            _commandAccelerators.Add(btn, accel);
        }
    }

    private static readonly ConditionalWeakTable<Control, KeyboardAccelerator> _commandAccelerators = new();

    /// <summary>
    /// Invokes <see cref="Command.Execute"/> or fires-and-forgets
    /// <see cref="Command.ExecuteAsync"/>. Used by factory overloads that need to
    /// wire a click handler from a bare <see cref="Command"/>.
    /// </summary>
    internal static void Invoke(Command cmd)
    {
        if (cmd.Execute is not null) cmd.Execute();
        else if (cmd.ExecuteAsync is not null) _ = cmd.ExecuteAsync();
    }
}
