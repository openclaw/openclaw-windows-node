using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using OpenClawTray.FunctionalUI.Hosting;
using static OpenClaw.Tray.UITests.TestSupport;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class FunctionalUiFlyoutIdentityTests
{
    private static readonly SessionRow[] InitialSessions =
    [
        new("alpha", "Alpha"),
        new("beta", "Beta"),
    ];

    private static readonly SessionRow[] UpdatedSessions =
    [
        new("alpha", "Alpha"),
        new("beta", "Beta renamed"),
        new("gamma", "Gamma"),
    ];

    private readonly UIThreadFixture _ui;

    public FunctionalUiFlyoutIdentityTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task OpenContentFlyout_SurvivesStatusRenders_UpdatesRows_AndPrunesWhenRemoved()
    {
        await _ui.ResetContainerAsync();

        UiRenderer? renderer = null;
        UIElement? initialRoot = null;
        Button? initialPicker = null;
        Flyout? initialFlyout = null;
        FrameworkElement? initialContent = null;
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                TestApp.EnsureFluentBrushFallbacks(Application.Current.Resources);
                _ui.Container.Width = 600;
                _ui.Container.Height = 400;

                renderer = new UiRenderer(() => { });
                initialRoot = Render(
                    renderer,
                    BuildTree("Ready", InitialSessions, "alpha", _ => { }, includePicker: true));
                Assert.IsAssignableFrom<FrameworkElement>(initialRoot).Loaded +=
                    (_, _) => loaded.TrySetResult(true);
                _ui.Container.Children.Add(initialRoot);
                _ui.Container.UpdateLayout();
            });

            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await _ui.RunOnUIAsync(() =>
            {
                initialPicker = FindPicker(initialRoot!);
                initialFlyout = Assert.IsType<Flyout>(initialPicker.Flyout);
                initialContent = Assert.IsAssignableFrom<FrameworkElement>(initialFlyout.Content);
                initialFlyout.Opened += (_, _) => opened.TrySetResult(true);
                initialFlyout.Closed += (_, _) => closed.TrySetResult(true);
                initialFlyout.ShowAt(initialPicker);
            });

            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _ui.PauseAsync("Session picker open before thinking renders", ms: 1_000);

            for (var render = 1; render <= 3; render++)
            {
                var status = $"Agent thinking {render}";
                await _ui.RunOnUIAsync(() =>
                {
                    var rerenderedRoot = Render(
                        renderer!,
                        BuildTree(status, InitialSessions, "alpha", _ => { }, includePicker: true));
                    var rerenderedPicker = FindPicker(rerenderedRoot);

                    Assert.Same(initialRoot, rerenderedRoot);
                    Assert.Same(initialPicker, rerenderedPicker);
                    Assert.Same(initialFlyout, rerenderedPicker.Flyout);
                    Assert.Same(initialContent, initialFlyout!.Content);
                    Assert.True(initialFlyout.IsOpen);
                    Assert.Contains(
                        FindLogical<TextBlock>(rerenderedRoot),
                        text => string.Equals(text.Text, status, StringComparison.Ordinal));
                });
            }

            await _ui.PauseAsync("Session picker still open after thinking renders", ms: 15_000);

            var selections = new List<string>();
            await _ui.RunOnUIAsync(() =>
            {
                var updatedRoot = Render(
                    renderer!,
                    BuildTree(
                        "Agent thinking 4",
                        UpdatedSessions,
                        "beta",
                        id => selections.Add(id),
                        includePicker: true));
                var updatedPicker = FindPicker(updatedRoot);

                Assert.Same(initialFlyout, updatedPicker.Flyout);
                Assert.Same(initialContent, initialFlyout!.Content);
                Assert.True(initialFlyout.IsOpen);

                var rows = FindLogical<Button>(initialContent!).ToArray();
                Assert.Equal(3, rows.Length);
                Assert.Equal("[ ] Alpha", Assert.IsType<string>(rows[0].Content));
                Assert.Equal("[selected] Beta renamed", Assert.IsType<string>(rows[1].Content));
                Assert.Equal("[ ] Gamma", Assert.IsType<string>(rows[2].Content));

                var gammaRow = Assert.Single(
                    rows,
                    row => string.Equals(
                        AutomationProperties.GetName(row),
                        "Session Gamma",
                        StringComparison.Ordinal));
                Assert.IsType<ButtonElement>(gammaRow.Tag).OnClick!.Invoke();
                Assert.Equal(["gamma"], selections);
            });

            await _ui.PauseAsync("Updated session selected from the open picker", ms: 1_000);

            await _ui.RunOnUIAsync(() =>
            {
                _ = Render(
                    renderer!,
                    BuildTree("Ready", UpdatedSessions, "beta", _ => { }, includePicker: false));
            });

            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await _ui.RunOnUIAsync(() =>
            {
                Assert.False(initialFlyout!.IsOpen);

                var restoredRoot = Render(
                    renderer!,
                    BuildTree("Ready", UpdatedSessions, "beta", _ => { }, includePicker: true));
                var restoredPicker = FindPicker(restoredRoot);
                Assert.NotSame(initialFlyout, restoredPicker.Flyout);
            });
        }
        finally
        {
            await _ui.RunOnUIAsync(() =>
            {
                initialFlyout?.Hide();
                renderer?.Dispose();
                _ui.Container.Children.Clear();
            });
        }
    }

    private static UIElement Render(UiRenderer renderer, Element tree)
    {
        var effects = new List<Action>();
        var root = renderer.Render(tree, "root", effects);
        foreach (var effect in effects)
            effect();
        return root;
    }

    private static Element BuildTree(
        string status,
        IReadOnlyList<SessionRow> sessions,
        string selectedId,
        Action<string> onSelected,
        bool includePicker)
    {
        var children = new List<Element?>
        {
            TextBlock(status).WithKey("status"),
        };

        if (includePicker)
        {
            var rows = sessions
                .Select(session => Button(
                        session.Id == selectedId
                            ? $"[selected] {session.Label}"
                            : $"[ ] {session.Label}",
                        () => onSelected(session.Id))
                    .AutomationName($"Session {session.Label}")
                    .WithKey($"session-{session.Id}"))
                .Cast<Element?>()
                .ToArray();

            children.Add(
                Button("Session")
                    .AutomationName("Session picker")
                    .WithFlyout(ContentFlyout(VStack(2, rows), FlyoutPlacementMode.Bottom))
                    .WithKey("session-picker"));
        }

        return VStack(8, children.ToArray());
    }

    private static Button FindPicker(DependencyObject root) =>
        Assert.Single(
            FindLogical<Button>(root),
            button => string.Equals(
                AutomationProperties.GetName(button),
                "Session picker",
                StringComparison.Ordinal));

    private sealed record SessionRow(string Id, string Label);
}
