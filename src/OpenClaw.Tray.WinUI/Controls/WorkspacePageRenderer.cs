using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;

namespace OpenClawTray.Controls;

/// <summary>Native card and action construction, without owning navigation or gateway state.</summary>
internal sealed class WorkspacePageRenderer(Style cardStyle, Func<string, string> text, Action<string> showError)
{
    internal Border Card(string title, string description, params FrameworkElement[] actions)
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(Label(title, "SubtitleTextBlockStyle"));
        stack.Children.Add(Label(description));
        foreach (var action in actions) stack.Children.Add(action);
        return new Border { Style = cardStyle, Child = stack };
    }

    internal static TextBlock Label(string value, string style = "BodyTextBlockStyle") =>
        new() { Text = value, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources[style] };

    internal Button CreateActionButton(string key, Action action)
    {
        var button = new Button { Content = text(key), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, text(key));
        button.Click += (_, _) => action();
        return button;
    }

    internal Button AsyncButton(string key, Func<Task> action)
    {
        var button = new Button { Content = text(key), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, text(key));
        button.Click += (_, _) => AsyncEventHandlerGuard.Run(async () =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { showError(ex.Message); }
            finally { button.IsEnabled = true; }
        }, new AppLogger(), $"Workspace.{key}");
        return button;
    }

    internal static Button SessionRow(string title, Action select)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => select();
        return button;
    }

    internal static Grid Cards(double width, IEnumerable<FrameworkElement> cards)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        var children = cards.ToArray();
        foreach (var card in children) grid.Children.Add(card);
        void Layout(double availableWidth)
        {
            var columns = availableWidth >= 680 ? 2 : 1;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < (children.Length + columns - 1) / columns; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < children.Length; i++)
            {
                Grid.SetColumn(children[i], i % columns);
                Grid.SetRow(children[i], i / columns);
            }
        }
        Layout(width);
        grid.SizeChanged += (_, e) =>
        {
            if ((e.PreviousSize.Width >= 680) != (e.NewSize.Width >= 680)) Layout(e.NewSize.Width);
        };
        return grid;
    }
}
