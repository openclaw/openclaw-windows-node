using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Presentation;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

// Mounted-control contract: a group draws dividers and outer corners only for the rows on screen.
[Collection(UICollection.Name)]
public sealed class SettingsCardGroupTests(UIThreadFixture ui)
{
    private const double OuterRowRadius = 7;

    [Fact]
    public async Task Rows_FollowVisibilityForDividersAndOuterCorners()
    {
        await ui.ResetContainerAsync();
        SettingsCard? first = null, middle = null, last = null;
        Border? group = null;
        await ui.RunOnUIAsync(() =>
        {
            first = new SettingsCard { Header = "First" };
            middle = new SettingsCard { Header = "Middle" };
            last = new SettingsCard { Header = "Last" };
            group = new Border { Width = 400, Child = new StackPanel { Children = { first, middle, last } } };
            SettingsCardGroup.SetIsEnabled(group, true);
            ui.Container.Children.Add(group);
            ui.Container.UpdateLayout();
        });
        try
        {
            // The Toolkit default border is 1px on every side, so only the first row's 0 top edge proves the group applied.
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => first!.BorderThickness.Top == 0, "settings card group applied"));
            await ui.RunOnUIAsync(() =>
            {
                AssertRow(first!, divider: 0, top: OuterRowRadius, bottom: 0);
                AssertRow(middle!, divider: 1, top: 0, bottom: 0);
                AssertRow(last!, divider: 1, top: 0, bottom: OuterRowRadius);
                last!.Visibility = Visibility.Collapsed;
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => middle!.CornerRadius.BottomLeft == OuterRowRadius, "middle row became the last visible row"));
            await ui.RunOnUIAsync(() =>
            {
                AssertRow(first!, divider: 0, top: OuterRowRadius, bottom: 0);
                AssertRow(middle!, divider: 1, top: 0, bottom: OuterRowRadius);
                first!.Visibility = Visibility.Collapsed;
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => middle!.CornerRadius.TopLeft == OuterRowRadius, "middle row became the only visible row"));
            await ui.RunOnUIAsync(() => AssertRow(middle!, divider: 0, top: OuterRowRadius, bottom: OuterRowRadius));
        }
        finally
        {
            await ui.RunOnUIAsync(() => ui.Container.Children.Remove(group!));
        }
    }

    private static void AssertRow(SettingsCard row, double divider, double top, double bottom)
    {
        Assert.Equal(new Thickness(0, divider, 0, 0), row.BorderThickness);
        Assert.Equal(new CornerRadius(top, top, bottom, bottom), row.CornerRadius);
    }
}
