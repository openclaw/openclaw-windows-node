using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Source-shape guard: a missing <c>IChatComposerFactory</c> DI registration must
/// surface as a composition failure (via <c>GetRequiredService</c>, letting the
/// existing app-level unhandled-exception/crash-log handler catch it), not be
/// silently treated as the "disconnected, no provider yet" placeholder state. Only
/// an as-yet-uninitialized <c>App.Services</c> container (a normal startup race,
/// during which the provider is also legitimately absent) still falls back to the
/// placeholder. See docs/ARCHITECTURE.md and AGENTS.md for the composition-root
/// conventions this guards (matching the existing
/// <c>sp.GetRequiredService&lt;NavigationScopeManager&gt;()</c> pattern already used
/// by <c>App.xaml.cs</c>).
/// </summary>
public sealed class ChatComposerFactoryDiFailFastTests
{
    [Theory]
    [InlineData("Pages", "ChatPage.xaml.cs")]
    [InlineData("Windows", "ChatWindow.xaml.cs")]
    public void HostComposition_RequiresComposerFactory_ViaGetRequiredService(string folder, string fileName)
    {
        var source = ReadSource(folder, fileName);

        // Fails fast once the container exists: GetRequiredService, not GetService.
        Assert.Contains("services.GetRequiredService<IChatComposerFactory>()", source);
        Assert.DoesNotContain("GetService<IChatComposerFactory>()", source);

        // The null-coalescing fallback is reached only when the container itself
        // has not been built yet (app?.Services is null) — the same timing window
        // during which the chat provider is also legitimately absent — not when a
        // real registration lookup failed (that throws instead of returning null).
        Assert.Contains("app?.Services is { } services", source);
        Assert.Contains("? services.GetRequiredService<IChatComposerFactory>()", source);
        Assert.Contains(": null;", source);
    }

    [Theory]
    [InlineData("Pages", "ChatPage.xaml.cs")]
    [InlineData("Windows", "ChatWindow.xaml.cs")]
    public void HostComposition_StillTreatsUninitializedContainerAsNoProviderPlaceholder(
        string folder,
        string fileName)
    {
        var source = ReadSource(folder, fileName);

        // The placeholder-panel branch still exists and is still gated on both the
        // provider and the (now fail-fast) composerFactory being null — i.e. the
        // only remaining null case for composerFactory (an uninitialized
        // container) is intentionally still folded into the same "no provider yet"
        // placeholder path, not a distinct silent branch.
        Assert.Contains("if (provider is null || composerFactory is null)", source);
    }

    private static string ReadSource(string folder, string fileName)
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", folder, fileName));
    }
}
