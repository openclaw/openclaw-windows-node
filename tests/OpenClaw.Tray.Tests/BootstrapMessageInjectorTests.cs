using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class BootstrapMessageInjectorTests : IDisposable
{
    private readonly string _isolatedDir;

    public BootstrapMessageInjectorTests()
    {
        _isolatedDir = Path.Combine(Path.GetTempPath(), "OpenClawTray.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_isolatedDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_isolatedDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void ShouldInject_ReturnsTrue_OnFreshSettings()
    {
        var settings = new SettingsManager(_isolatedDir);
        Assert.False(settings.HasInjectedFirstRunBootstrap);
        Assert.True(BootstrapMessageInjector.ShouldInject(settings));
    }

    [Fact]
    public void MarkInjected_FlipsGate_AndPersists()
    {
        var settings = new SettingsManager(_isolatedDir);

        BootstrapMessageInjector.MarkInjected(settings);

        Assert.True(settings.HasInjectedFirstRunBootstrap);
        Assert.False(BootstrapMessageInjector.ShouldInject(settings));

        // Reload from disk — the flag must round-trip through SettingsData.
        var reloaded = new SettingsManager(_isolatedDir);
        Assert.True(reloaded.HasInjectedFirstRunBootstrap);
        Assert.False(BootstrapMessageInjector.ShouldInject(reloaded));
    }

    [Fact]
    public void MarkInjected_IsIdempotent()
    {
        var settings = new SettingsManager(_isolatedDir);
        BootstrapMessageInjector.MarkInjected(settings);
        BootstrapMessageInjector.MarkInjected(settings);
        BootstrapMessageInjector.MarkInjected(settings);
        Assert.True(settings.HasInjectedFirstRunBootstrap);
    }

    [Fact]
    public async Task InjectAsync_ReturnsFalse_WhenExecutorIsNull()
    {
        var settings = new SettingsManager(_isolatedDir);
        var result = await BootstrapMessageInjector.InjectAsync(null, settings, initialDelayMs: 0);
        Assert.False(result);
        Assert.False(settings.HasInjectedFirstRunBootstrap);
    }

    [Fact]
    public async Task InjectAsync_ReturnsFalse_WhenGateAlreadyConsumed()
    {
        var settings = new SettingsManager(_isolatedDir);
        BootstrapMessageInjector.MarkInjected(settings);
        var result = await BootstrapMessageInjector.InjectAsync(null, settings, initialDelayMs: 0);
        Assert.False(result);
    }

    [Fact]
    public async Task InjectAsync_FlipsGate_OnSuccessfulExecution()
    {
        var settings = new SettingsManager(_isolatedDir);
        string? capturedScript = null;
        BootstrapMessageInjector.ScriptExecutor executor = script =>
        {
            capturedScript = script;
            return Task.FromResult("\"sent\"");
        };

        var result = await BootstrapMessageInjector.InjectAsync(executor, settings, initialDelayMs: 0);

        Assert.True(result);
        Assert.True(settings.HasInjectedFirstRunBootstrap);
        Assert.NotNull(capturedScript);
        Assert.Contains("BOOTSTRAP.md", capturedScript!);
    }

    [Fact]
    public async Task InjectAsync_DoesNotFireTwice()
    {
        var settings = new SettingsManager(_isolatedDir);
        int callCount = 0;
        BootstrapMessageInjector.ScriptExecutor executor = _ =>
        {
            callCount++;
            return Task.FromResult("\"sent\"");
        };

        await BootstrapMessageInjector.InjectAsync(executor, settings, initialDelayMs: 0);
        await BootstrapMessageInjector.InjectAsync(executor, settings, initialDelayMs: 0);
        await BootstrapMessageInjector.InjectAsync(executor, settings, initialDelayMs: 0);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void BuildInjectionScript_EncodesMessageSafely()
    {
        // Adversarial message: would break naive ${...} or quoted concatenation.
        var hostile = "abc\"; alert(1); //${evil}\\";
        var script = BootstrapMessageInjector.BuildInjectionScript(hostile);

        // Raw hostile substring must NOT appear unescaped inside a JS string literal.
        Assert.DoesNotContain("abc\"; alert(1); //${evil}\\", script);
        // JSON-escaped form embeds the encoded payload (escaped quote).
        Assert.Contains("\\\"", script);
    }
}
