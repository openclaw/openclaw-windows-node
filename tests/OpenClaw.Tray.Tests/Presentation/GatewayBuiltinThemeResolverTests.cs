using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Themes;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Themes;
using OpenClawTray.Theming;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// The Companion's built-in theme source: the connected Gateway's palettes first, the bundled table second. The
/// WebView2 probe is faked here with what the Gateway's dashboard computed (its fidelity is a UI test); these cover
/// when resolution runs, what it publishes, and what it never publishes.
/// </summary>
public sealed class GatewayBuiltinThemeResolverTests : IDisposable
{
    private readonly TempDirectory _directory = new("builtin-theme-resolver-");
    private readonly RecordingUiDispatcher _dispatcher = new();
    private readonly FakeCatalog _catalog = new();

    private ServedBuiltinThemeCache Cache() => new(Path.Combine(_directory.Path, "Themes", "gateway-builtin-themes.json"));

    [Fact]
    public async Task TheConnectedGatewaysPalettesReplaceTheTable_AndAreReusedForTheSameBuildAndOffline()
    {
        var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Newer);
        var probe = new FakeProbe(recording);
        using var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache());
        var changes = 0;
        resolver.Changed += (_, _) => changes++;
        Assert.Null(resolver.Current);
        Assert.Null(GatewayBuiltinThemeResolver.LastCached(Cache()));

        resolver.SetConnection(Gateway(recording.Read, epoch: 1), "gw");
        await resolver.Pending;

        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, changes);
        Assert.Equal(recording.BuildId, resolver.Current?.Key);
        Assert.True(resolver.TryGet("tide", out var tide));
        Assert.Equal(Hex(recording.Dashboard["tide"].Light, "Canvas"), tide.Light!.Canvas.ToHex());
        Assert.True(BuiltinGatewayThemes.TryGet("tide", out var table));
        Assert.NotEqual(table.Light!.Canvas, tide.Light.Canvas);

        // A restart applies the last Gateway's palettes before connecting, and the same build needs no new probe.
        // The startup theme, applied before the resolver exists, resolves from the same cache.
        Assert.True(GatewayBuiltinThemeResolver.LastCached(Cache())!.TryGet("tide", out var startup));
        Assert.Equal(tide.Light.Canvas, startup.Light!.Canvas);
        using var restarted = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache());
        Assert.Equal(recording.BuildId, restarted.Current?.Key);
        restarted.SetConnection(Gateway(recording.Read, epoch: 1), "gw");
        await restarted.Pending;
        Assert.Equal(1, probe.Calls);
        Assert.Equal(recording.BuildId, restarted.Current?.Key);
    }

    [Fact]
    public async Task AGatewayWithoutAReachableControlUiOrAWorkingProbe_KeepsTheTable()
    {
        var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Stable);
        var failing = new FakeProbe(recording) { Failure = new InvalidOperationException("WebView2 runtime not found") };
        using var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, failing, Cache());
        var changes = 0;
        resolver.Changed += (_, _) => changes++;

        resolver.SetConnection(Gateway(recording.Read, epoch: 1), "gw");
        await resolver.Pending;
        Assert.Null(resolver.Current);
        Assert.Equal(1, failing.Calls);

        // The Control UI disabled (every path 404): nothing to probe.
        resolver.SetConnection(Gateway((_, _, _, _) => Task.FromResult<string?>(null), epoch: 2), "gw");
        await resolver.Pending;
        Assert.Null(resolver.Current);
        Assert.Equal(1, failing.Calls);
        Assert.Equal(2, changes);
        Assert.True(resolver.TryGet("tide", out var tide));
        Assert.True(BuiltinGatewayThemes.TryGet("tide", out var table));
        Assert.Same(table, tide);
        Assert.False(resolver.TryGet("nosuch", out _));
    }

    [Fact]
    public async Task ASupersededConnectionNeverPublishes()
    {
        var older = ControlUiThemeRecording.Load(ControlUiThemeRecording.Older);
        var probe = new FakeProbe(older) { Gate = new TaskCompletionSource() };
        using var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache());

        resolver.SetConnection(Gateway(older.Read, epoch: 1), "gw-old");
        var first = resolver.Pending;
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        resolver.SetConnection(Gateway((_, _, _, _) => Task.FromResult<string?>(null), epoch: 1), "gw-new");
        await resolver.Pending;
        probe.Gate.SetResult();
        await first;

        Assert.Null(resolver.Current);
        Assert.True(probe.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CatalogDescriptorsRebrandWithoutAProbe_AndABuiltinNotYetProbedIsResolvedOnce()
    {
        var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Stable);
        var probe = new FakeProbe(recording);
        using var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache());
        resolver.SetConnection(Gateway(recording.Read, epoch: 1), "gw");
        await resolver.Pending;
        Assert.True(resolver.TryGet("phosphor", out var fromTable));
        Assert.False(fromTable.Branding.Mascot);

        // 2026.9 Gateways list built-ins without branding: their dashboard keeps the claw, and so does the Companion.
        _catalog.GatewayBuiltins = [.. recording.ThemeIds.Select(id => Descriptor(id, id == "phosphor" ? "Phosphor (Gateway)" : id))];
        _catalog.Raise();
        Assert.Equal(1, probe.Calls);
        Assert.True(resolver.TryGet("phosphor", out var fromGateway));
        Assert.Equal("Phosphor (Gateway)", fromGateway.Name);
        Assert.Equal(ThemeBranding.Default, fromGateway.Branding);

        // A built-in this build has not been probed for is resolved once more, with every id.
        _catalog.GatewayBuiltins = [.. _catalog.GatewayBuiltins, Descriptor("aurora", "Aurora")];
        _catalog.Raise();
        await resolver.Pending;
        Assert.Equal(2, probe.Calls);
        Assert.Contains("aurora", probe.LastIds);
        Assert.Contains("tide", probe.LastIds);
    }

    [Fact]
    public async Task ABuiltinTheGatewayDoesNotStyle_KeepsTheTable_AlsoAfterARestart()
    {
        // e.g. themes/tide.css missing: the probe still reads complete base (claw) values for it, flagged unstyled.
        var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Stable);
        var probe = new FakeProbe(recording) { Unstyled = { "tide" } };
        Assert.True(BuiltinGatewayThemes.TryGet("tide", out var table));
        using (var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache()))
        {
            resolver.SetConnection(Gateway(recording.Read, epoch: 1), "gw");
            await resolver.Pending;
            Assert.NotNull(resolver.Current);
            Assert.True(resolver.TryGet("tide", out var live));
            Assert.Same(table, live);
        }

        using var restarted = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache());
        Assert.Equal(recording.BuildId, restarted.Current?.Key);
        Assert.True(restarted.TryGet("tide", out var cached));
        Assert.Same(table, cached);
        Assert.True(GatewayBuiltinThemeResolver.LastCached(Cache())!.TryGet("tide", out var startup));
        Assert.Same(table, startup);
    }

    [Fact]
    public async Task AThemeStylesheetThatFailedToLoad_KeepsTheTable_AndIsRetriedOnTheNextConnectionToTheSameBuild()
    {
        var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Newer);
        // themes/tide.css fails (say a 503). The probe is not told: core rules that name tide incidentally can make its
        // base values look styled, so the resolver itself must not apply them.
        var probe = new FakeProbe(recording);
        ControlUiTextReader flaky = (path, mediaType, maxBytes, cancellationToken) =>
            path == "themes/tide.css" ? Task.FromResult<string?>(null) : recording.Read(path, mediaType, maxBytes, cancellationToken);
        using var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache());
        Assert.True(BuiltinGatewayThemes.TryGet("tide", out var table));

        resolver.SetConnection(Gateway(flaky, epoch: 1), "gw");
        await resolver.Pending;
        Assert.Equal(recording.BuildId, resolver.Current?.Key);
        Assert.True(resolver.TryGet("tide", out var fallback));
        Assert.Same(table, fallback);
        Assert.True(resolver.TryGet("dash", out var dash));
        Assert.Equal(Hex(recording.Dashboard["dash"].Light, "Canvas"), dash.Light!.Canvas.ToHex());
        Assert.Null(GatewayBuiltinThemeResolver.LastCached(Cache()));

        resolver.SetConnection(Gateway(recording.Read, epoch: 2), "gw");
        await resolver.Pending;
        Assert.Equal(2, probe.Calls);
        Assert.True(resolver.TryGet("tide", out var tide));
        Assert.Equal(Hex(recording.Dashboard["tide"].Light, "Canvas"), tide.Light!.Canvas.ToHex());
    }

    [Fact]
    public async Task ASupersededResolution_CannotMakeAnOlderBuildTheLastCachedOne()
    {
        var older = ControlUiThemeRecording.Load(ControlUiThemeRecording.Older);
        var newer = ControlUiThemeRecording.Load(ControlUiThemeRecording.Newer);
        var cache = new GatedCache(Cache(), older.BuildId!);
        using var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, new FakeProbe(newer), cache);

        // The older Gateway's resolution passes its generation check and is paused while persisting.
        var first = Task.Run(() => resolver.SetConnection(Gateway(older.Read, epoch: 1), "gw-old"));
        await cache.GatedSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A newer connection meanwhile: it may not overtake that write, or the stale one would land last.
        var second = new Thread(() =>
        {
            resolver.SetConnection(Gateway(newer.Read, epoch: 2), "gw-new");
            resolver.Pending.GetAwaiter().GetResult();
        });
        second.Start();
        Assert.True(SpinWait.SpinUntil(
            () => !second.IsAlive || second.ThreadState.HasFlag(ThreadState.WaitSleepJoin), TimeSpan.FromSeconds(10)));
        cache.ReleaseGatedSave.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(second.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(newer.BuildId, cache.TryLoadLast()?.Key);
        Assert.Equal(newer.BuildId, resolver.Current?.Key);
    }

    [Fact]
    public async Task ABuiltinListedWhileAProbeRuns_IsProbedWhenItFinishes_AndOnlyOnce()
    {
        var recording = ControlUiThemeRecording.Load(ControlUiThemeRecording.Stable);
        var probe = new FakeProbe(recording) { Gate = new TaskCompletionSource() };
        using var resolver = new GatewayBuiltinThemeResolver(_dispatcher, _catalog, probe, Cache());
        resolver.SetConnection(Gateway(recording.Read, epoch: 1), "gw");
        var first = resolver.Pending;
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _catalog.GatewayBuiltins = [.. recording.ThemeIds.Select(id => Descriptor(id, id)), Descriptor("aurora", "Aurora")];
        _catalog.Raise();
        probe.Gate.SetResult();
        await first;
        await resolver.Pending;

        Assert.Equal(2, probe.Calls);
        Assert.Contains("aurora", probe.LastIds);
        // An identical catalog refresh later needs no further probe.
        _catalog.Raise();
        await resolver.Pending;
        Assert.Equal(2, probe.Calls);
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        _directory.Dispose();
    }

    private static GatewayThemeDescriptor Descriptor(string id, string name) =>
        new(id, name, name, GatewayThemeSource.Builtin, [ThemeColorMode.Light, ThemeColorMode.Dark], null);

    private static string Hex(JsonElement palette, string field) => palette.GetProperty(field).GetString()!;

    private static IOperatorGatewayClient Gateway(ControlUiTextReader read, long epoch)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var proxy = (GatewayProxy)(object)client;
        proxy.ReadControlUi = read;
        proxy.Epoch = epoch;
        return client;
    }

    public class GatewayProxy : DispatchProxy
    {
        public ControlUiTextReader ReadControlUi = (_, _, _, _) => Task.FromResult<string?>(null);
        public long Epoch;

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => true,
            "get_SessionMutationConnectionEpoch" => (long?)Epoch,
            "ReadControlUiTextAsync" => ReadControlUi((string)args![0]!, (string)args[1]!, (int)args[2]!, (CancellationToken)args[3]!),
            _ => throw new NotSupportedException(method?.Name),
        };
    }

    // Answers with what the Gateway's dashboard computed for the recorded built-ins. Like ThemeProbe.js, an id no sheet
    // styles (unknown, or listed in Unstyled) still gets complete base-palette (claw) values, plus unstyled failures.
    private sealed class FakeProbe(ControlUiThemeRecording recording) : IBuiltinThemeProbe
    {
        public int Calls;
        public Exception? Failure;
        public TaskCompletionSource? Gate;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> LastIds = [];
        public CancellationToken LastToken;
        public HashSet<string> Unstyled { get; } = [];

        public async Task<string> ResolveAsync(string document, IReadOnlyList<string> themeIds, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastIds = themeIds;
            LastToken = cancellationToken;
            Started.TrySetResult();
            if (Gate is { } gate)
                await gate.Task;
            if (Failure is { } failure)
                throw failure;
            bool IsStyled(string id) => recording.Dashboard.ContainsKey(id) && !Unstyled.Contains(id);
            var result = themeIds.ToDictionary(id => id, id =>
            {
                var theme = recording.Dashboard[IsStyled(id) ? id : "claw"];
                return new { light = theme.Light, dark = theme.Dark };
            });
            var failures = themeIds.Where(id => !IsStyled(id))
                .SelectMany(id => new[] { $"{id}/light/unstyled", $"{id}/dark/unstyled" });
            return JsonSerializer.Serialize(new { result, failures });
        }
    }

    // Pauses the save of one build until released, to interleave a newer connection with it.
    private sealed class GatedCache(ServedBuiltinThemeCache inner, string gatedKey) : IServedBuiltinThemeCache
    {
        public TaskCompletionSource GatedSaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseGatedSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServedThemeCacheEntry? TryLoad(string key) => inner.TryLoad(key);
        public ServedThemeCacheEntry? TryLoadLast() => inner.TryLoadLast();

        public void Save(ServedThemeCacheEntry entry)
        {
            if (entry.Key == gatedKey)
            {
                GatedSaveEntered.TrySetResult();
                ReleaseGatedSave.Task.Wait(TimeSpan.FromSeconds(10));
            }
            inner.Save(entry);
        }
    }

    private sealed class FakeCatalog : IGatewayThemeCatalog
    {
        public IReadOnlyList<GatewayThemeOption> Options => [];
        public GatewayThemeOption? GatewayCurrent => null;
        public GatewayAppearancePreferences? GatewayPreferences => null;
        public IReadOnlyList<GatewayThemeDescriptor>? GatewayBuiltins { get; set; }
        public event EventHandler? Changed;
        public Task RefreshAsync() => Task.CompletedTask;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
