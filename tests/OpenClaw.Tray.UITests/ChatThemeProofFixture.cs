// Retains production chat resources across one serial test class.
// Use IClassFixture<ChatThemeProofFixture> with the UIThreadFixture collection;
// call AssertCurrent on the UI thread before mounting each chat host.
using Microsoft.UI.Xaml;

namespace OpenClaw.Tray.UITests;

/// <summary>Owns one selected theme's resources until every host in the class has closed.</summary>
public sealed class ChatThemeProofFixture(UIThreadFixture ui) : IAsyncLifetime
{
    private ChatThemeProofScope? _scope;
    private ResourceDictionary? _resources;
    private string? _theme;

    internal ElementTheme ElementTheme { get; private set; }

    /// <summary>Installs and retains the production resource dictionary on the shared UI thread.</summary>
    public Task InitializeAsync() => ui.RunOnUIAsync(() =>
    {
        _theme = Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") ?? "Light";
        _scope = new ChatThemeProofScope(_theme);
        _resources = Application.Current.Resources;
        ElementTheme = _scope.ElementTheme;
    });

    internal void AssertCurrent()
    {
        Assert.True(ui.Dispatcher.HasThreadAccess);
        Assert.NotNull(_scope);
        Assert.Equal(_theme, Environment.GetEnvironmentVariable("OPENCLAW_PROOF_THEME") ?? "Light");
        Assert.Same(_resources, Application.Current.Resources);
    }

    /// <summary>Restores the preceding resource dictionary after class teardown, not between cases.</summary>
    public Task DisposeAsync() => ui.RunOnUIAsync(() =>
    {
        if (_scope is null) return;
        try
        {
            AssertCurrent();
        }
        finally
        {
            _scope.Dispose();
            _scope = null;
            _resources = null;
        }
    });
}
