using System.Windows.Input;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Bridges ICommand (MVVM/CommunityToolkit) to Command, enabling migration from
/// ViewModel-based patterns to Reactor's declarative commanding.
///   var ductCmd = CommandInterop.FromCommand(viewModel.SaveCommand, "Save",
///       icon: new SymbolIconData("Save"), accelerator: Accelerator(VirtualKey.S, Control));
/// </summary>
public static class CommandInterop
{
    /// <summary>
    /// Creates a Command from an ICommand. CanExecute is evaluated at creation time.
    /// For CanExecute to update on each render, call this within a component's Render method.
    /// </summary>
    public static Command FromCommand(
        ICommand command,
        string label,
        IconData? icon = null,
        string? description = null,
        KeyboardAcceleratorData? accelerator = null,
        object? parameter = null)
    {
        return new Command
        {
            Label = label,
            Execute = () => command.Execute(parameter),
            CanExecute = command.CanExecute(parameter),
            Icon = icon,
            Description = description,
            Accelerator = accelerator,
        };
    }

    /// <summary>
    /// Creates a parameterized Command from an ICommand. The ICommand receives the
    /// Command's parameter when Execute is called. CanExecute defaults to true because
    /// Command&lt;T&gt;.CanExecute is a static bool and the parameter is not known at
    /// creation time. To evaluate CanExecute dynamically, call this within a component's
    /// Render method with a known parameter and pass command.CanExecute(parameter) explicitly.
    /// </summary>
    public static Command<T> FromCommand<T>(
        ICommand command,
        string label,
        IconData? icon = null,
        string? description = null,
        KeyboardAcceleratorData? accelerator = null)
    {
        return new Command<T>
        {
            Label = label,
            Execute = arg => command.Execute(arg),
            CanExecute = true,
            Icon = icon,
            Description = description,
            Accelerator = accelerator,
        };
    }
}
