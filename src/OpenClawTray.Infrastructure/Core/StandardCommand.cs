using Windows.System;

namespace OpenClawTray.Infrastructure.Core;

/// <summary>
/// Factory methods for the 16 standard application commands with pre-filled metadata
/// (label, icon, keyboard accelerator). Each command has sync and async overloads:
///   var cut = StandardCommand.Cut(() => CutSelection());
///   var save = StandardCommand.Save(async () => await SaveAsync());
/// Labels use plain English by default. Override with <c>with</c> for localization:
///   var cut = StandardCommand.Cut(action) with { Label = intl.Message(keys.Cut) };
/// </summary>
public static class StandardCommand
{
    // ── Clipboard ───────────────────────────────────────────────────

    public static Command Cut(Action execute, bool canExecute = true) =>
        new() { Label = "Cut", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Cut"), Accelerator = new(VirtualKey.X, VirtualKeyModifiers.Control) };

    public static Command Cut(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Cut", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Cut"), Accelerator = new(VirtualKey.X, VirtualKeyModifiers.Control) };

    public static Command Copy(Action execute, bool canExecute = true) =>
        new() { Label = "Copy", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Copy"), Accelerator = new(VirtualKey.C, VirtualKeyModifiers.Control) };

    public static Command Copy(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Copy", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Copy"), Accelerator = new(VirtualKey.C, VirtualKeyModifiers.Control) };

    public static Command Paste(Action execute, bool canExecute = true) =>
        new() { Label = "Paste", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Paste"), Accelerator = new(VirtualKey.V, VirtualKeyModifiers.Control) };

    public static Command Paste(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Paste", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Paste"), Accelerator = new(VirtualKey.V, VirtualKeyModifiers.Control) };

    // ── Undo / Redo ─────────────────────────────────────────────────

    public static Command Undo(Action execute, bool canExecute = true) =>
        new() { Label = "Undo", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Undo"), Accelerator = new(VirtualKey.Z, VirtualKeyModifiers.Control) };

    public static Command Undo(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Undo", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Undo"), Accelerator = new(VirtualKey.Z, VirtualKeyModifiers.Control) };

    public static Command Redo(Action execute, bool canExecute = true) =>
        new() { Label = "Redo", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Redo"), Accelerator = new(VirtualKey.Y, VirtualKeyModifiers.Control) };

    public static Command Redo(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Redo", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Redo"), Accelerator = new(VirtualKey.Y, VirtualKeyModifiers.Control) };

    // ── Edit ────────────────────────────────────────────────────────

    public static Command Delete(Action execute, bool canExecute = true) =>
        new() { Label = "Delete", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Delete"), Accelerator = new(VirtualKey.Delete) };

    public static Command Delete(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Delete", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Delete"), Accelerator = new(VirtualKey.Delete) };

    public static Command SelectAll(Action execute, bool canExecute = true) =>
        new() { Label = "Select all", Execute = execute, CanExecute = canExecute, Accelerator = new(VirtualKey.A, VirtualKeyModifiers.Control) };

    public static Command SelectAll(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Select all", ExecuteAsync = executeAsync, CanExecute = canExecute, Accelerator = new(VirtualKey.A, VirtualKeyModifiers.Control) };

    // ── File ────────────────────────────────────────────────────────

    public static Command Save(Action execute, bool canExecute = true) =>
        new() { Label = "Save", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Save"), Accelerator = new(VirtualKey.S, VirtualKeyModifiers.Control) };

    public static Command Save(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Save", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Save"), Accelerator = new(VirtualKey.S, VirtualKeyModifiers.Control) };

    public static Command Open(Action execute, bool canExecute = true) =>
        new() { Label = "Open", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("OpenFile"), Accelerator = new(VirtualKey.O, VirtualKeyModifiers.Control) };

    public static Command Open(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Open", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("OpenFile"), Accelerator = new(VirtualKey.O, VirtualKeyModifiers.Control) };

    public static Command Close(Action execute, bool canExecute = true) =>
        new() { Label = "Close", Execute = execute, CanExecute = canExecute, Accelerator = new(VirtualKey.W, VirtualKeyModifiers.Control) };

    public static Command Close(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Close", ExecuteAsync = executeAsync, CanExecute = canExecute, Accelerator = new(VirtualKey.W, VirtualKeyModifiers.Control) };

    // ── Sharing ─────────────────────────────────────────────────────

    public static Command Share(Action execute, bool canExecute = true) =>
        new() { Label = "Share", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Share") };

    public static Command Share(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Share", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Share") };

    // ── Media ───────────────────────────────────────────────────────

    public static Command Play(Action execute, bool canExecute = true) =>
        new() { Label = "Play", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Play") };

    public static Command Play(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Play", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Play") };

    public static Command Pause(Action execute, bool canExecute = true) =>
        new() { Label = "Pause", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Pause") };

    public static Command Pause(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Pause", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Pause") };

    public static Command Stop(Action execute, bool canExecute = true) =>
        new() { Label = "Stop", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Stop") };

    public static Command Stop(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Stop", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Stop") };

    public static Command Forward(Action execute, bool canExecute = true) =>
        new() { Label = "Forward", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Forward") };

    public static Command Forward(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Forward", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Forward") };

    public static Command Backward(Action execute, bool canExecute = true) =>
        new() { Label = "Backward", Execute = execute, CanExecute = canExecute, Icon = new SymbolIconData("Back") };

    public static Command Backward(Func<Task> executeAsync, bool canExecute = true) =>
        new() { Label = "Backward", ExecuteAsync = executeAsync, CanExecute = canExecute, Icon = new SymbolIconData("Back") };
}
