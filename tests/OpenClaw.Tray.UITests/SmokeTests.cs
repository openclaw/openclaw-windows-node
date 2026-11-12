using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SmokeTests
{
    private readonly UIThreadFixture _ui;
    public SmokeTests(UIThreadFixture ui) => _ui = ui;

    private static readonly TimeSpan LifetimeTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_UI_FIXTURE_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(90);

    [Fact]
    public async Task FixtureCleanup_KeepsDispatcherAliveForLatePageRelease()
    {
        var retained = new StrongBox<Page?>();
        var weakPage = await _ui.RunOnUIAsync(() =>
        {
            retained.Value = new Page { Content = new TextBlock { Text = "Late release" } };
            _ui.Container.Children.Add(retained.Value);
            return Task.FromResult(new WeakReference<Page>(retained.Value));
        });

        try
        {
            _ui.Dispose();

            var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_ui.Dispatcher.TryEnqueue(() => dispatched.SetResult()),
                "Collection cleanup must leave the process-wide XAML dispatcher available for late native releases.");
            await dispatched.Task.WaitAsync(LifetimeTimeout);
            await _ui.RunOnUIAsync(() =>
            {
                Assert.Empty(_ui.Container.Children);
                Assert.False(_ui.TestWindow.AppWindow.IsVisible);
                Assert.Equal("Late release", Assert.IsType<TextBlock>(retained.Value!.Content).Text);
                retained.Value = null;
            });

            // The first drain releases managed owners; the second collects their interop wrappers.
            for (var pass = 0; pass < 2; pass++)
            {
                GC.Collect();
                await Task.Run(GC.WaitForPendingFinalizers).WaitAsync(LifetimeTimeout);
                await _ui.YieldToRenderAsync().WaitAsync(LifetimeTimeout);
            }
            Assert.False(weakPage.TryGetTarget(out _));
            await _ui.RunOnUIAsync(() => Assert.NotNull(Application.Current.Resources["BodyTextBlockStyle"]));
        }
        finally
        {
            retained.Value = null;
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_ui.Dispatcher.TryEnqueue(() =>
            {
                _ui.TestWindow.Activate();
                restored.SetResult();
            }))
                await restored.Task.WaitAsync(LifetimeTimeout);
        }
    }

    [Theory]
    [InlineData(typeof(MarkdownRendererListTests))]
    [InlineData(typeof(MarkdownRendererCoalesceTests))]
    public void MarkdownRendererTests_ShareUiBootstrapCollection(Type testClass)
    {
        var collection = Assert.Single(testClass.CustomAttributes,
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal(UICollection.Name, Assert.IsType<string>(
            Assert.Single(collection.ConstructorArguments).Value));
    }

    [Fact]
    public async Task Fixture_BootsApplicationAndWindow()
    {
        await _ui.RunOnUIAsync(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.NotNull(_ui.TestWindow);
            Assert.NotNull(_ui.Container);
        });
    }

    [Fact]
    public async Task Container_AcceptsTextBlockAndLaysOut()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var tb = new TextBlock { Text = "hello" };
            _ui.Container.Children.Add(tb);

            // Force a layout pass so we can measure that the element is live.
            _ui.Container.UpdateLayout();
            Assert.Equal("hello", tb.Text);
            Assert.NotNull(tb.XamlRoot); // attached to a real XamlRoot
        });
    }

    [Fact]
    public async Task ApplicationResources_HasBodyTextBlockStyle()
    {
        // Sanity: the renderers look up XamlControlsResources keys; if this
        // fails, A2UI tests will silently render unstyled text.
        await _ui.RunOnUIAsync(() =>
        {
            var res = Application.Current.Resources["BodyTextBlockStyle"];
            Assert.IsAssignableFrom<Style>(res);
        });
    }

}
