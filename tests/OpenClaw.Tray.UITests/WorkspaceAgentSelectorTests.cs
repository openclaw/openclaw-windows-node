using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

// Exercises the native ComboBox's cached selected presentation, not just its item metadata.
[Collection(UICollection.Name)]
public sealed class WorkspaceAgentSelectorTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedIdentityRefreshesSelectedPresentationWithoutChangingAgent(bool menuOpen)
    {
        await ui.ResetContainerAsync();
        ComboBox? selector = null;
        try
        {
            await ui.RunOnUIAsync(async () =>
            {
                var template = (DataTemplate)XamlReader.Load("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                  xmlns:controls="using:OpenClawTray.Controls">
                        <controls:AgentIdentityBadge/>
                    </DataTemplate>
                    """);
                selector = new ComboBox
                {
                    Width = 260,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                var item = new ComboBoxItem { ContentTemplate = template };
                var original = new WorkspaceAgent("main", "main", "agent:main:main");
                WorkspaceWindow.UpdateAssistantItem(selector, item, original);
                selector.Items.Add(item);
                selector.SelectedItem = item;
                ui.Container.Children.Add(selector);
                await WaitForRenderedConditionAsync(() =>
                    FindDescendants<TextBlock>(selector).Any(text => text.Name == "AgentName" && text.Text == "main"),
                    "initial selected agent label");
                if (menuOpen)
                {
                    selector.IsDropDownOpen = true;
                    await WaitForRenderedConditionAsync(() => selector.IsDropDownOpen, "agent menu open");
                }

                var resolved = original with { Name = "Assistant" };
                WorkspaceWindow.UpdateAssistantItem(selector, item, resolved);
                Assert.Same(item, selector.SelectedItem);
                Assert.Equal(menuOpen, selector.IsDropDownOpen);
                selector.IsDropDownOpen = false;
                await WaitForRenderedConditionAsync(() =>
                    FindDescendants<TextBlock>(selector).Any(text => text.Name == "AgentName" && text.Text == "Assistant"),
                    "resolved name in the closed selector without opening it to refresh");
                Assert.Equal(resolved, selector.SelectionBoxItem);
                Assert.Contains(FindDescendants<TextBlock>(selector),
                    text => text.Name == "AgentId" && text.Text == "main");
                await VisualTestCapture.CaptureAsync(selector, $"ResolvedAgentSelector-{menuOpen}");
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (selector is not null) selector.IsDropDownOpen = false;
            });
            await ui.ResetContainerAsync();
        }
    }
}
