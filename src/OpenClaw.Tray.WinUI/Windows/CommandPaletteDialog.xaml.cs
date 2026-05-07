using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Windows;

/// <summary>
/// A command palette overlay (Ctrl+K / Ctrl+F) for quick navigation and actions.
/// Light-dismiss: clicking outside or pressing Escape closes it.
/// </summary>
public sealed partial class CommandPaletteDialog : ContentDialog
{
    private readonly List<CommandItem> _allCommands;
    private readonly Action<CommandItem> _onExecute;

    public CommandPaletteDialog(List<CommandItem> commands, Action<CommandItem> onExecute)
    {
        InitializeComponent();
        _allCommands = commands;
        _onExecute = onExecute;
        ResultsList.ItemsSource = commands.Take(10).ToList();
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Light dismiss: find the smoke layer (background overlay) and close on click
        var parent = VisualTreeHelper.GetParent(this);
        while (parent != null)
        {
            if (parent is Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
            {
                popup.IsLightDismissEnabled = true;
                break;
            }
            // The ContentDialog smoke layer is a Border or Grid — attach Tapped handler
            if (parent is FrameworkElement fe && fe.GetType().Name == "ContentDialogSmokeLayer")
            {
                fe.Tapped += (s, e) => Hide();
                break;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }

        // Focus the search box
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var query = sender.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(query)
            ? _allCommands.Take(10).ToList()
            : _allCommands
                .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();

        ResultsList.ItemsSource = filtered;
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // When the user presses Enter in the search box, execute the first visible result
        if (ResultsList.ItemsSource is List<CommandItem> items && items.Count > 0)
        {
            ExecuteAndClose(items[0]);
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandItem item)
        {
            ExecuteAndClose(item);
        }
    }

    private void ExecuteAndClose(CommandItem item)
    {
        Hide();
        _onExecute(item);
    }
}

/// <summary>
/// Represents a single entry in the command palette.
/// </summary>
public class CommandItem
{
    public string Icon { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Navigation tag for NavigateTo (e.g. "home", "sessions").</summary>
    public string Tag { get; set; } = "";
    /// <summary>Optional custom action (overrides tag-based navigation).</summary>
    public Action? Execute { get; set; }
}
