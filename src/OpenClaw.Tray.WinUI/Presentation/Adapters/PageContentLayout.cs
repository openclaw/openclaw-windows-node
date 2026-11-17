// Applies shared page-column geometry to XAML. Usage: presentation:PageContentLayout.IsEnabled="True".
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Presentation;

/// <summary>Sizes XAML page columns from the containing page, using the same policy as Reactor pages.</summary>
public static class PageContentLayout
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PageContentLayout), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(object), typeof(PageContentLayout), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Grid and not StackPanel)
            throw new ArgumentException("Page content layout requires a Grid or StackPanel.", nameof(target));
        if (target.GetValue(StateProperty) is ColumnState previous)
            previous.Detach();
        target.SetValue(StateProperty, (bool)args.NewValue ? new ColumnState((FrameworkElement)target) : null);
    }

    private sealed class ColumnState
    {
        private readonly FrameworkElement _column;
        private Page? _page;

        public ColumnState(FrameworkElement column)
        {
            _column = column;
            column.Loaded += OnLoaded;
            column.Unloaded += OnUnloaded;
            if (column.IsLoaded) AttachPage();
        }

        public void Detach()
        {
            _column.Loaded -= OnLoaded;
            _column.Unloaded -= OnUnloaded;
            DetachPage();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => AttachPage();
        private void OnUnloaded(object sender, RoutedEventArgs args) => DetachPage();

        private void AttachPage()
        {
            DetachPage();
            for (DependencyObject? parent = _column; parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not Page page) continue;
                _page = page;
                page.SizeChanged += OnPageSizeChanged;
                Apply();
                return;
            }
            throw new InvalidOperationException("Page content layout must be inside a Page.");
        }

        private void DetachPage()
        {
            if (_page is not null) _page.SizeChanged -= OnPageSizeChanged;
            _page = null;
        }

        private void OnPageSizeChanged(object sender, SizeChangedEventArgs args) => Apply();

        private void Apply()
        {
            var width = _page!.ActualWidth;
            var padding = PageResponsiveLayout.HorizontalPadding(PageResponsiveLayout.TierFor(width));
            _column.Width = PageResponsiveLayout.ContentWidth(width) + 2 * padding;
            _column.MaxWidth = PageResponsiveLayout.ContentMaxWidth + 2 * padding;
            _column.HorizontalAlignment = HorizontalAlignment.Center;
            var insets = new Thickness(padding, PageResponsiveLayout.PaddingTop, padding, PageResponsiveLayout.PaddingBottom);
            if (_column is Grid grid) grid.Padding = insets;
            else if (_column is StackPanel stack) stack.Padding = insets;
        }
    }
}
