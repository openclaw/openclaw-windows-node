using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;

namespace OpenClawTray.Services;

internal sealed record TrayMenuCallbacks(
    Action<string> DispatchAction,
    Action<ToggleSwitch> TrackConnectionToggle,
    Func<bool> IsConnectionToggleSuspended);

internal sealed class TrayMenuRenderer
{
    private readonly TrayMenuPresentation _presentation;
    private readonly TrayMenuCallbacks _callbacks;
    private readonly ResourceDictionary _resources;

    internal TrayMenuRenderer(TrayMenuPresentation presentation, TrayMenuCallbacks callbacks)
    {
        _presentation = presentation;
        _callbacks = callbacks;
        _resources = Application.Current.Resources;
    }

    internal void Render(TrayMenuWindow menu)
    {
        foreach (var item in _presentation.Items)
        {
            switch (item.Kind)
            {
                case TrayMenuElementKind.BrandHeader:
                    menu.AddCustomElement(BuildBrandHeader(menu, item));
                    break;
                case TrayMenuElementKind.DashboardGlance:
                    menu.AddCustomElement(BuildDashboardGlance(item));
                    break;
                case TrayMenuElementKind.GatewayCard:
                    menu.AddFlyoutCustomItem(
                        BuildGatewayCard(item),
                        BuildFlyoutItems(item.Children, item.Kind),
                        item.ActionId);
                    break;
                case TrayMenuElementKind.DeviceCard:
                    menu.AddFlyoutCustomItem(
                        BuildDeviceCard(item),
                        BuildFlyoutItems(item.Children, item.Kind),
                        item.ActionId);
                    break;
                case TrayMenuElementKind.SessionsSummary:
                case TrayMenuElementKind.UsageSummary:
                    menu.AddFlyoutCustomItem(
                        BuildSummaryRow(item),
                        BuildFlyoutItems(item.Children, item.Kind),
                        item.ActionId);
                    break;
                case TrayMenuElementKind.Flyout:
                    menu.AddFlyoutMenuItem(
                        item.Text,
                        BuildIcon(item.Icon),
                        BuildFlyoutItems(item.Children, item.Kind),
                        action: item.ActionId);
                    break;
                case TrayMenuElementKind.Action:
                    if (item.Accelerator is null)
                        menu.AddMenuItem(item.Text, BuildIcon(item.Icon), item.ActionId ?? "");
                    else
                        menu.AddMenuItemWithHint(item.Text, BuildIcon(item.Icon), item.ActionId ?? "", item.Accelerator);
                    break;
                case TrayMenuElementKind.Separator:
                    menu.AddSeparator();
                    break;
            }
        }
    }

    private UIElement BuildBrandHeader(TrayMenuWindow menu, TrayMenuElement item)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 10, 12, 8),
            ColumnSpacing = 8,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                        new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-48_altform-unplated.png")),
                    Width = 28,
                    Height = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = item.Text,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    FontSize = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsTextSelectionEnabled = false,
                },
            },
        };
        AutomationProperties.SetName(brand, item.AutomationName ?? item.Text);
        Grid.SetColumn(brand, 0);
        grid.Children.Add(brand);

        var state = _presentation.ConnectionToggle;
        var toggle = menu.CreateMenuToggleSwitch(state.IsOn, state.AutomationName, state.IsEnabled);
        toggle.Margin = new Thickness(0);
        ToolTipService.SetToolTip(toggle, state.ToolTip);
        toggle.Toggled += (_, _) =>
        {
            if (!_callbacks.IsConnectionToggleSuspended())
                _callbacks.DispatchAction(toggle.IsOn ? "reconnect" : "disconnect");
        };
        _callbacks.TrackConnectionToggle(toggle);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);
        return grid;
    }

    private UIElement BuildDashboardGlance(TrayMenuElement item)
    {
        var secondary = Brush("TextFillColorSecondaryBrush");
        var caption = Style("CaptionTextBlockStyle");
        var outer = new StackPanel
        {
            Padding = new Thickness(12, 4, 12, 8),
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var line = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(BuildStatusDot(item.Accent));
        var headline = new TextBlock
        {
            Text = item.Text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false,
        };
        Grid.SetColumn(headline, 1);
        line.Children.Add(headline);
        if (item.Secondary is not null)
        {
            var heartbeat = Caption(item.Secondary, secondary, caption);
            Grid.SetColumn(heartbeat, 2);
            line.Children.Add(heartbeat);
        }
        outer.Children.Add(line);

        if (!string.IsNullOrEmpty(item.Detail))
            outer.Children.Add(Caption(item.Detail, secondary, caption));

        if (item.Children.FirstOrDefault(child => child.Kind == TrayMenuElementKind.ActiveSession) is { } session)
        {
            var sessionLine = new Grid
            {
                ColumnSpacing = 6,
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            sessionLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sessionLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sessionLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Caption(session.Detail ?? "", secondary, caption);
            sessionLine.Children.Add(label);
            var title = new TextBlock
            {
                Text = session.Text,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false,
            };
            Grid.SetColumn(title, 1);
            sessionLine.Children.Add(title);
            if (session.Tertiary is not null)
            {
                var context = Caption(session.Tertiary, secondary, caption);
                Grid.SetColumn(context, 2);
                sessionLine.Children.Add(context);
            }
            outer.Children.Add(sessionLine);
            if (!string.IsNullOrEmpty(session.Secondary))
                outer.Children.Add(Caption(session.Secondary, secondary, caption));
        }

        AutomationProperties.SetName(outer, item.AutomationName ?? item.Text);
        AutomationProperties.SetAccessibilityView(outer, AccessibilityView.Content);
        return outer;
    }

    private UIElement BuildGatewayCard(TrayMenuElement item)
    {
        var outer = new StackPanel
        {
            Padding = new Thickness(12, 6, 12, 8),
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Children.Add(BuildStatusDot(item.Accent));
        name.Children.Add(CardTitle(item.Text));
        header.Children.Add(name);
        if (item.Badge is not null)
        {
            var badge = BuildBadge(item.Badge);
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
        }
        outer.Children.Add(header);
        outer.Children.Add(CardDetail(item.Detail ?? ""));
        if (!string.IsNullOrEmpty(item.Error))
            outer.Children.Add(ErrorText(item.Error));
        AutomationProperties.SetName(outer, item.AutomationName ?? item.Text);
        return outer;
    }

    private UIElement BuildDeviceCard(TrayMenuElement item)
    {
        var outer = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 8),
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Children.Add(BuildStatusDot(item.Accent));
        name.Children.Add(CardTitle(item.Text));
        header.Children.Add(name);
        if (item.Badge is not null)
        {
            var badge = BuildBadge(item.Badge);
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
        }
        outer.Children.Add(header);
        if (!string.IsNullOrEmpty(item.Detail))
            outer.Children.Add(CardDetail(item.Detail));
        AutomationProperties.SetName(outer, item.AutomationName ?? item.Text);
        return outer;
    }

    private UIElement BuildSummaryRow(TrayMenuElement item)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 8,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = CardTitle(item.Text);
        grid.Children.Add(title);
        var summary = CardDetail(item.Detail ?? "");
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);
        AutomationProperties.SetName(grid, item.AutomationName ?? item.Text);
        return grid;
    }

    private IReadOnlyList<TrayMenuFlyoutItem> BuildFlyoutItems(
        IReadOnlyList<TrayMenuElement> descriptors,
        TrayMenuElementKind parentKind)
    {
        var items = new List<TrayMenuFlyoutItem>(descriptors.Count);
        foreach (var item in descriptors)
        {
            switch (item.Kind)
            {
                case TrayMenuElementKind.Header:
                    items.Add(new TrayMenuFlyoutItem { Text = item.Text, IsHeader = true });
                    break;
                case TrayMenuElementKind.Toggle:
                    items.Add(new TrayMenuFlyoutItem
                    {
                        Text = item.Text,
                        Icon = IconGlyph(item.Icon),
                        Description = item.Detail,
                        Action = item.ActionId ?? "",
                        IsToggle = true,
                        IsOn = item.IsChecked == true,
                    });
                    break;
                case TrayMenuElementKind.Text:
                    items.Add(new TrayMenuFlyoutItem { Text = item.Text });
                    break;
                default:
                    items.Add(new TrayMenuFlyoutItem
                    {
                        CustomContent = BuildFlyoutContent(item, parentKind),
                    });
                    break;
            }
        }
        return items;
    }

    private UIElement BuildFlyoutContent(
        TrayMenuElement item,
        TrayMenuElementKind parentKind) => item.Kind switch
    {
        TrayMenuElementKind.StatusCard => BuildStatusCard(
            item,
            parentKind == TrayMenuElementKind.GatewayCard ? 280 : 260),
        TrayMenuElementKind.ErrorText => BuildErrorRow(item),
        TrayMenuElementKind.KeyValue => BuildKeyValue(item),
        TrayMenuElementKind.Spacer => new Border { Height = 10 },
        TrayMenuElementKind.SessionCard => BuildSessionCard(item),
        TrayMenuElementKind.Capability => BuildCapability(item),
        TrayMenuElementKind.UsageTotals => BuildUsageTotals(item),
        TrayMenuElementKind.UsageProvider => BuildUsageProvider(item),
        _ => new TextBlock { Text = item.Text },
    };

    private UIElement BuildStatusCard(TrayMenuElement item, double minimumWidth)
    {
        var outer = new StackPanel
        {
            Padding = new Thickness(12, 2, 12, 6),
            Spacing = 2,
            MinWidth = minimumWidth,
        };
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.Children.Add(BuildStatusDot(item.Accent));
        line.Children.Add(new TextBlock
        {
            Text = item.Text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = false,
        });
        outer.Children.Add(line);
        if (!string.IsNullOrEmpty(item.Detail))
            outer.Children.Add(CardDetail(item.Detail));
        return outer;
    }

    private UIElement BuildErrorRow(TrayMenuElement item)
    {
        var outer = new StackPanel { Padding = new Thickness(12, 2, 12, 4) };
        outer.Children.Add(ErrorText(item.Text));
        return outer;
    }

    private UIElement BuildKeyValue(TrayMenuElement item)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 2, 12, 2),
            ColumnSpacing = 12,
            MinWidth = 260,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var key = new TextBlock
        {
            Text = item.Text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextFillColorSecondaryBrush"),
            IsTextSelectionEnabled = false,
        };
        grid.Children.Add(key);
        var value = new TextBlock
        {
            Text = item.Detail ?? "",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false,
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    private UIElement BuildSessionCard(TrayMenuElement item)
    {
        var secondary = Brush("TextFillColorSecondaryBrush");
        var caption = Style("CaptionTextBlockStyle");
        var outer = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 10),
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 260,
        };
        var line1 = new Grid { ColumnSpacing = 6 };
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = CardTitle(item.Text);
        line1.Children.Add(name);
        if (item.Tertiary is not null)
        {
            var age = Caption(item.Tertiary, secondary, caption);
            Grid.SetColumn(age, 1);
            line1.Children.Add(age);
        }
        outer.Children.Add(line1);
        var line2 = new Grid { ColumnSpacing = 8 };
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line2.Children.Add(Caption(item.Detail ?? "", secondary, caption));
        var ratio = Caption(item.Secondary ?? "", secondary, caption);
        Grid.SetColumn(ratio, 1);
        line2.Children.Add(ratio);
        outer.Children.Add(line2);
        outer.Children.Add(BuildMiniBar(item.ProgressPercent ?? 0));
        AutomationProperties.SetName(outer, item.AutomationName ?? item.Text);
        return outer;
    }

    private UIElement BuildCapability(TrayMenuElement item)
    {
        var grid = new Grid
        {
            Padding = new Thickness(12, 4, 12, 4),
            ColumnSpacing = 10,
            MinWidth = 260,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = BuildIcon(item.Icon);
        if (icon is FrameworkElement iconElement)
        {
            iconElement.HorizontalAlignment = HorizontalAlignment.Center;
            iconElement.VerticalAlignment = VerticalAlignment.Top;
            iconElement.Margin = new Thickness(0, 2, 0, 0);
            grid.Children.Add(iconElement);
        }
        var content = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(CardTitle(item.Text));
        if (!string.IsNullOrEmpty(item.Detail))
        {
            var commands = CardDetail(item.Detail);
            commands.TextWrapping = TextWrapping.Wrap;
            commands.MaxWidth = 240;
            content.Children.Add(commands);
        }
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private UIElement BuildUsageTotals(TrayMenuElement item)
    {
        var outer = new StackPanel
        {
            Padding = new Thickness(12, 8, 12, 10),
            Spacing = 2,
            MinWidth = 260,
        };
        if (!string.IsNullOrEmpty(item.Text))
        {
            outer.Children.Add(new TextBlock
            {
                Text = item.Text,
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsTextSelectionEnabled = false,
            });
        }
        if (!string.IsNullOrEmpty(item.Detail))
            outer.Children.Add(CardDetail(item.Detail));
        return outer;
    }

    private UIElement BuildUsageProvider(TrayMenuElement item)
    {
        var outer = new StackPanel
        {
            Padding = new Thickness(12, 6, 12, 8),
            Spacing = 3,
            MinWidth = 260,
        };
        outer.Children.Add(new TextBlock
        {
            Text = item.Text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = false,
        });
        if (!string.IsNullOrEmpty(item.Error))
            outer.Children.Add(ErrorText(item.Error));
        foreach (var window in item.Children)
        {
            var block = new StackPanel { Spacing = 2 };
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(CardDetail(window.Text));
            var percent = CardDetail(window.Detail ?? "");
            Grid.SetColumn(percent, 1);
            header.Children.Add(percent);
            block.Children.Add(header);
            block.Children.Add(BuildMiniBar(window.ProgressPercent ?? 0));
            outer.Children.Add(block);
        }
        return outer;
    }

    private FrameworkElement BuildMiniBar(double percent)
    {
        var value = Math.Clamp(percent, 0.0, 100.0);
        var accent = value >= 95
            ? Brush("SystemFillColorCriticalBrush")
            : value >= 80
                ? Brush("SystemFillColorCautionBrush")
                : Brush("SystemFillColorSuccessBrush");
        var frame = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush("ControlAltFillColorTertiaryBrush"),
            BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
            MinWidth = 60,
        };
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0.0001, value), GridUnitType.Star),
        });
        fill.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0.0001, 100.0 - value), GridUnitType.Star),
        });
        var filled = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = value <= 0 ? 0 : 1,
        };
        fill.Children.Add(filled);
        frame.Child = fill;
        return frame;
    }

    private Microsoft.UI.Xaml.Shapes.Ellipse BuildStatusDot(TrayMenuAccent accent) => new()
    {
        Width = 8,
        Height = 8,
        VerticalAlignment = VerticalAlignment.Center,
        Fill = accent switch
        {
            TrayMenuAccent.Success => Brush("SystemFillColorSuccessBrush"),
            TrayMenuAccent.Caution => Brush("SystemFillColorCautionBrush"),
            TrayMenuAccent.Critical => Brush("SystemFillColorCriticalBrush"),
            _ => Brush("SystemFillColorNeutralBrush"),
        },
    };

    private Border BuildBadge(string text) => new()
    {
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        Background = Brush("ControlFillColorSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = Brush("TextFillColorSecondaryBrush"),
            IsTextSelectionEnabled = false,
        },
    };

    private TextBlock CardTitle(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        FontSize = 13,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        IsTextSelectionEnabled = false,
    };

    private TextBlock CardDetail(string text) => new()
    {
        Text = text,
        Style = Style("CaptionTextBlockStyle"),
        FontSize = 11,
        Foreground = Brush("TextFillColorSecondaryBrush"),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        IsTextSelectionEnabled = false,
    };

    private TextBlock ErrorText(string text) => new()
    {
        Text = text,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = Brush("SystemFillColorCriticalBrush"),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 260,
        IsTextSelectionEnabled = false,
    };

    private static TextBlock Caption(
        string text,
        Microsoft.UI.Xaml.Media.Brush brush,
        Style style) => new()
        {
            Text = text,
            Style = style,
            FontSize = 11,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = false,
        };

    private IconElement? BuildIcon(TrayMenuIconIdentity icon)
    {
        var glyph = IconGlyph(icon);
        return glyph is null ? null : FluentIconCatalog.Build(glyph);
    }

    private static string? IconGlyph(TrayMenuIconIdentity icon) => icon switch
    {
        TrayMenuIconIdentity.Approvals => FluentIconCatalog.Approvals,
        TrayMenuIconIdentity.Permissions => FluentIconCatalog.Permissions,
        TrayMenuIconIdentity.Dashboard => FluentIconCatalog.Dashboard,
        TrayMenuIconIdentity.Chat => FluentIconCatalog.Chat,
        TrayMenuIconIdentity.Canvas => FluentIconCatalog.CanvasAct,
        TrayMenuIconIdentity.Diagnostics => FluentIconCatalog.Bug,
        TrayMenuIconIdentity.Setup => FluentIconCatalog.Setup,
        TrayMenuIconIdentity.Settings => FluentIconCatalog.Settings,
        TrayMenuIconIdentity.About => FluentIconCatalog.About,
        TrayMenuIconIdentity.Close => FluentIconCatalog.Exit,
        TrayMenuIconIdentity.System => FluentIconCatalog.System,
        TrayMenuIconIdentity.Terminal => FluentIconCatalog.Terminal,
        TrayMenuIconIdentity.Browser => FluentIconCatalog.Browser,
        TrayMenuIconIdentity.Camera => FluentIconCatalog.Camera,
        TrayMenuIconIdentity.Screen => FluentIconCatalog.Screen,
        TrayMenuIconIdentity.Location => FluentIconCatalog.Location,
        TrayMenuIconIdentity.Voice => FluentIconCatalog.Voice,
        TrayMenuIconIdentity.Speech => FluentIconCatalog.Speech,
        TrayMenuIconIdentity.Clipboard => "",
        TrayMenuIconIdentity.TextToSpeech => FluentIconCatalog.Voice,
        TrayMenuIconIdentity.Device => FluentIconCatalog.Devices,
        TrayMenuIconIdentity.App => "",
        TrayMenuIconIdentity.Document => "",
        _ => null,
    };

    private Microsoft.UI.Xaml.Media.Brush Brush(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)_resources[key];

    private Style Style(string key) => (Style)_resources[key];
}
