using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

[Bindable]
public sealed class ChatModelPickerRow(ChatModelChoice choice)
{
    public ChatModelChoice Choice { get; } = choice;
    public string Title => Choice.DisplayName;
    public string Description => string.Join(" \u00b7 ", new[]
    {
        ChatModelLabels.BuildMetaSegment(Choice with { Provider = null }),
        ChatModelLabels.BuildStateMarker(Choice),
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

[Bindable]
public sealed class ChatModelPickerGroup(string label, IReadOnlyList<ChatModelPickerRow> items)
{
    public string Label { get; } = label;
    public IReadOnlyList<ChatModelPickerRow> Items { get; } = items;
}

/// <summary>Grouped native selection UI. Only explicit item activation commits a model change.</summary>
internal sealed class NativeChatModelPicker : Control, IDisposable
{
    internal StackPanel Layout { get; } = new() { Spacing = 8 };
    private readonly AutoSuggestBox _search = new() { QueryIcon = new SymbolIcon(Symbol.Find) };
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        IsItemClickEnabled = true,
        MaxHeight = 360,
    };
    private readonly TextBlock _current = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap };
    private ChatModelPickerProps? _props;
    private Action<ChatModelChoice>? _select;
    private ChatModelPickerRow[] _rows = [];
    private CollectionViewSource? _groups;
    private bool _eventsAttached;
    private long _textChangeToken;

    public NativeChatModelPicker()
    {
        var resources = Application.Current.Resources;
        Template = (ControlTemplate)resources["ChatModelPickerControlTemplate"];
        _search.PlaceholderText = ChatMarkdownPresentation.Localized("Chat_Model_Search", "Search models");
        AutomationProperties.SetAutomationId(_search, "ChatModelSearch");
        AutomationProperties.SetName(_search, _search.PlaceholderText);
        AutomationProperties.SetAutomationId(_list, "ChatModelList");
        AutomationProperties.SetName(_list,
            ChatMarkdownPresentation.Localized("Chat_Composer_Accessibility_Model", "Model"));
        _list.ItemTemplate = (DataTemplate)resources["ChatModelItemTemplate"];
        _list.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = (DataTemplate)resources["ChatModelGroupHeaderTemplate"],
            HidesIfEmpty = true,
        });
        _current.Style = _empty.Style = (Style)resources["CaptionTextBlockStyle"];
        _empty.Text = ChatMarkdownPresentation.Localized("Chat_Model_NoMatches", "No matching models");
        Layout.Children.Add(_search);
        Layout.Children.Add(_current);
        Layout.Children.Add(_list);
        Layout.Children.Add(_empty);
        AttachEvents();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ((ContentPresenter)GetTemplateChild("PickerRoot")).Content = Layout;
    }

    private void AttachEvents()
    {
        if (_eventsAttached) return;
        _textChangeToken = _search.RegisterPropertyChangedCallback(AutoSuggestBox.TextProperty, OnSearchChanged);
        _search.QuerySubmitted += OnQuerySubmitted;
        _search.PreviewKeyDown += OnSearchKeyDown;
        _list.ItemClick += OnItemClick;
        _list.ContainerContentChanging += OnContainerContentChanging;
        _eventsAttached = true;
    }

    internal void Update(ChatModelPickerProps props, Action<ChatModelChoice> select)
    {
        var rebuild = _props is null || !_props.Choices.SequenceEqual(props.Choices);
        var selectionChanged = _props?.CurrentModel != props.CurrentModel
            || _props?.CurrentProvider != props.CurrentProvider;
        _props = props;
        _select = select;
        AttachEvents();
        Width = Math.Min(320, Math.Max(200, props.AvailableWidth - 56));
        _list.IsEnabled = props.Enabled;
        var missingCurrent = !string.IsNullOrWhiteSpace(props.CurrentModel)
            && !props.Choices.Any(choice => choice.MatchesModel(props.CurrentModel, props.CurrentProvider));
        _current.Visibility = missingCurrent ? Visibility.Visible : Visibility.Collapsed;
        _current.Text = missingCurrent
            ? $"{ChatMarkdownPresentation.Localized("Chat_Model_Current", "Current model")}: {ChatModelChoice.BuildSelectionId(props.CurrentModel!, props.CurrentProvider)}"
            : string.Empty;
        if (rebuild)
            Filter();
        else if (selectionChanged)
            RestoreCommittedSelection();
    }

    private void OnSearchChanged(DependencyObject sender, DependencyProperty property) => Filter();

    private void Filter()
    {
        if (_props is null) return;
        var query = _search.Text.Trim();
        _rows = _props.Choices.Where(choice => query.Length == 0
            || $"{choice.DisplayName} {choice.Provider} {ChatModelLabels.FormatProviderName(choice.Provider)} {choice.Id}"
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(choice => new ChatModelPickerRow(choice)).ToArray();
        _groups = new CollectionViewSource
        {
            IsSourceGrouped = true,
            ItemsPath = new PropertyPath(nameof(ChatModelPickerGroup.Items)),
            Source = _rows.GroupBy(row => row.Choice.Provider ?? string.Empty)
                .Select(group => new ChatModelPickerGroup(
                    string.IsNullOrWhiteSpace(group.Key)
                        ? ChatMarkdownPresentation.Localized("Chat_Model_Other", "Other models")
                        : ChatModelLabels.FormatProviderName(group.Key),
                    group.ToArray())).ToArray(),
        };
        _list.ItemsSource = _groups.View;
        _list.Visibility = _rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = _rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreCommittedSelection();
    }

    internal void RestoreCommittedSelection() =>
        _list.SelectedItem = _rows.FirstOrDefault(row =>
            row.Choice.MatchesModel(_props?.CurrentModel, _props?.CurrentProvider));

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.ClearValue(AutomationProperties.AutomationIdProperty);
            args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
            return;
        }
        if (args.Item is not ChatModelPickerRow row) return;
        args.ItemContainer.IsEnabled = row.Choice.IsSelectable;
        AutomationProperties.SetAutomationId(args.ItemContainer, "ChatModelChoice_" + row.Choice.SelectionId);
        AutomationProperties.SetName(args.ItemContainer, $"{row.Title}. {row.Description}");
    }

    private void OnItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is ChatModelPickerRow row)
            Commit(row);
    }

    private void Commit(ChatModelPickerRow row)
    {
        if (_props is null) return;
        var choice = _props.Choices.FirstOrDefault(item => item.SelectionId == row.Choice.SelectionId);
        if (!_props.Enabled || choice?.IsSelectable != true)
        {
            System.Diagnostics.Trace.WriteLine("[chat] Model activation ignored because the current choice is unavailable.");
            return;
        }
        _select?.Invoke(choice);
        _search.Text = string.Empty;
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var choices = _rows.Where(row => row.Choice.IsSelectable).Take(2).ToArray();
        if (choices.Length == 1)
            Commit(choices[0]);
        else
            FocusResult();
    }

    private void OnSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == global::Windows.System.VirtualKey.Down)
            args.Handled = FocusResult();
    }

    private bool FocusResult()
    {
        if (!_list.IsEnabled) return false;
        var row = _list.SelectedItem is ChatModelPickerRow { Choice.IsSelectable: true } selected
            ? selected : _rows.FirstOrDefault(item => item.Choice.IsSelectable);
        if (row is null) return false;
        _list.ScrollIntoView(row);
        _list.UpdateLayout();
        return _list.ContainerFromItem(row) is ListViewItem item && item.Focus(FocusState.Keyboard);
    }

    public void Dispose()
    {
        _props = null;
        _select = null;
        if (_eventsAttached)
            _search.UnregisterPropertyChangedCallback(AutoSuggestBox.TextProperty, _textChangeToken);
        _search.QuerySubmitted -= OnQuerySubmitted;
        _search.PreviewKeyDown -= OnSearchKeyDown;
        _list.ItemClick -= OnItemClick;
        _list.ContainerContentChanging -= OnContainerContentChanging;
        _eventsAttached = false;
        _list.ItemsSource = null;
        _groups = null;
        _rows = [];
    }
}
