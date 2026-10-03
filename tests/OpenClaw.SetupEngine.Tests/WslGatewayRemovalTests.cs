using OpenClaw.Connection;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WslGatewayRemovalTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SetupContext _ctx;
    private readonly GatewayRegistry _registry;
    private readonly Inspector _inspector = new();
    private readonly GatewayRecord _target = new()
    {
        Id = "wsl-b", IsLocal = true, Url = "wss://b.tail123.ts.net",
        SetupManagedDistroName = "GatewayB",
    };
    private readonly List<string> _calls = [];
    private Action? _remove;
    private readonly Commands _commands = new();

    public WslGatewayRemovalTests()
    {
        var data = _temp.Combine("data");
        var local = _temp.Combine("local");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(local);
        var logger = new SetupLogger(filePath: null);
        _ctx = new SetupContext(new SetupConfig { ConfirmDestructive = true },
            logger, new TransactionJournal(filePath: null), _commands,
            CancellationToken.None, data, local);
        _registry = new GatewayRegistry(data);
        _registry.AddOrUpdate(_target);
        _registry.AddOrUpdate(new() { Id = "wsl-a", IsLocal = true, Url = "ws://localhost:18790", SetupManagedDistroName = "GatewayA" });
        _registry.SetActive(_target.Id);
        _registry.Save();
        Directory.CreateDirectory(_registry.GetIdentityDirectory(_target.Id));
        File.WriteAllText(Path.Combine(_registry.GetIdentityDirectory(_target.Id), "device-key-ed25519.json"), "key-b");
        _inspector.Current = new(WslRegistrationInspectionStatus.Found, "registration-b", _temp.Combine("local", "wsl", "GatewayB"));
        Directory.CreateDirectory(_inspector.Current.BasePath!);
    }

    private Task<PipelineResult> Run() => new WslGatewayRemoval(_inspector,
        (_, _) => { _calls.Add("stop"); return Task.CompletedTask; },
        (_, _) =>
        {
            _calls.Add("remove");
            if (_remove is not null) _remove();
            else FinishDistroRemoval();
            return Task.CompletedTask;
        }).RunAsync(_ctx, _target.Id, GatewayDashboardBinding.Capture(_target));

    private void FinishDistroRemoval()
    {
        var path = _inspector.Current.BasePath!;
        _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public async Task PartialFailureRetainsTargetAndIdentityForRetry()
    {
        _remove = () => throw new IOException("VHDX busy");
        Assert.Equal(PipelineOutcome.Failed, (await Run()).Outcome);
        AssertRetryTarget();
        _remove = null;
        Assert.Equal(PipelineOutcome.Success, (await Run()).Outcome);
        _registry.Load();
        Assert.Null(_registry.GetById(_target.Id));
        Assert.Equal(["stop", "remove", "stop", "remove"], _calls);
    }

    [Fact]
    public async Task FailureAfterUnregisterCanResumeWithoutAnyWslCommand()
    {
        _remove = () => { FinishDistroRemoval(); throw new IOException("Post-unregister cleanup failure"); };
        Assert.Equal(PipelineOutcome.Failed, (await Run()).Outcome);
        AssertRetryTarget();
        _remove = () => throw new Exception("Must not unregister again");
        Assert.Equal(PipelineOutcome.Success, (await Run()).Outcome);
        Assert.Equal(["stop", "remove"], _calls);
    }

    [Fact]
    public async Task MissingMarkerAndResidueAfterUnregisterCanRetryFromRemovalReceipt()
    {
        var installPath = _inspector.Current.BasePath!;
        File.WriteAllText(Path.Combine(installPath, "ext4.vhdx"), "fixture");
        _remove = () =>
        {
            _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
            throw new IOException("Disk cleanup failed after unregister");
        };
        Assert.Equal(PipelineOutcome.Failed, (await Run()).Outcome);
        AssertRetryTarget();
        Assert.True(Directory.Exists(installPath));
        _remove = () => throw new Exception("Must not unregister absent distro");
        Assert.Equal(PipelineOutcome.Success, (await Run()).Outcome);
        Assert.False(Directory.Exists(installPath));
        Assert.Equal(["stop", "remove"], _calls);
    }

    [Fact]
    public async Task ReplacementDuringTerminationCannotReachUnregister()
    {
        _commands.Invoke = arguments =>
        {
            Assert.Equal("--terminate", arguments[0]);
            _inspector.Current = _inspector.Current with { RegistrationId = "replacement" };
            return new(0, "", "", TimeSpan.Zero, false);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) => Task.CompletedTask);
        var result = await remover.RunAsync(_ctx, _target.Id, GatewayDashboardBinding.Capture(_target));
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["--terminate"], _commands.Operations);
        AssertRetryTarget();
    }

    [Fact]
    public async Task ProductionDispatchTerminatesAndUnregistersOnlyPinnedRegistration()
    {
        _commands.Invoke = arguments =>
        {
            Assert.Equal("GatewayB", arguments[1]);
            if (arguments[0] == "--unregister") _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
            return new(0, "", "", TimeSpan.Zero, false);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) => Task.CompletedTask);
        Assert.Equal(PipelineOutcome.Success,
            (await remover.RunAsync(_ctx, _target.Id, GatewayDashboardBinding.Capture(_target))).Outcome);
        Assert.Equal(["--terminate", "--unregister"], _commands.Operations);
    }

    [Fact]
    public async Task OtherGatewaysContextLocalAiAndSettingsArePreserved()
    {
        var localAi = Path.Combine(_ctx.LocalDataDir, "LocalAI");
        Directory.CreateDirectory(localAi);
        File.WriteAllText(Path.Combine(localAi, "manifest.json"), "model-owned-by-a");
        File.WriteAllText(Path.Combine(localAi, "gateway-binding.json"), """{"GatewayId":"native-a"}""");
        File.WriteAllText(Path.Combine(_ctx.DataDir, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(_ctx.DataDir, "exec-approvals.json"), "approvals");
        File.WriteAllText(Path.Combine(_ctx.LocalDataDir, "setup-state.json"), """{"DistroName":"GatewayA"}""");
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(_ctx, new("GatewayA", "user-a", "/workspace/a"), default);
        await WindowsNodeBootstrapContextStep.RecordAppliedTargetAsync(_ctx, new("GatewayB", "user-b", "/workspace/b"), default);

        Assert.Equal(PipelineOutcome.Success, (await Run()).Outcome);

        var context = await WindowsNodeBootstrapContextStep.ReadInstallStateAsync(_ctx, default);
        Assert.Equal("GatewayA", Assert.Single(context.Targets).DistroName);
        Assert.Equal("model-owned-by-a", File.ReadAllText(Path.Combine(localAi, "manifest.json")));
        Assert.Equal("""{"GatewayId":"native-a"}""", File.ReadAllText(Path.Combine(localAi, "gateway-binding.json")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(_ctx.DataDir, "settings.json")));
        Assert.Equal("approvals", File.ReadAllText(Path.Combine(_ctx.DataDir, "exec-approvals.json")));
        Assert.Contains("GatewayA", File.ReadAllText(Path.Combine(_ctx.LocalDataDir, "setup-state.json")));
        _registry.Load();
        Assert.NotNull(_registry.GetById("wsl-a"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"GatewayId\":\"wrong\"}")]
    [InlineData("{malformed")]
    public async Task InvalidReceiptReturnsStructuredFailureAndRecoveryPath(string content)
    {
        var path = Path.Combine(_ctx.LocalDataDir, $"wsl-removal-{GatewayDashboardBinding.Capture(_target)}.json");
        File.WriteAllText(path, content);

        var result = await Run();

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains(path, result.Message);
        Assert.Contains("no distro was removed", result.Message);
        Assert.Empty(_calls);
        AssertRetryTarget();
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task RegistrationAtAnotherPathNeverReachesUnregister()
    {
        _inspector.Current = _inspector.Current with { BasePath = _temp.Combine("personal-ubuntu") };
        Assert.Equal(PipelineOutcome.Failed, (await Run()).Outcome);
        Assert.Empty(_calls);
        AssertRetryTarget();
    }

    [Fact]
    public async Task LegacyFriendlyNameWithoutMarkerIsNotDestructiveAuthority()
    {
        var legacy = _target with { SetupManagedDistroName = null, FriendlyName = "Local (GatewayB)", Url = "ws://localhost:18789" };
        _registry.AddOrUpdate(legacy);
        _registry.Save();
        var remover = new WslGatewayRemoval(_inspector,
            (_, _) => throw new Exception("Must not stop"),
            (_, _) => throw new Exception("Must not unregister"));
        var result = await remover.RunAsync(_ctx, legacy.Id, GatewayDashboardBinding.Capture(legacy));
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task RegistrationReplacementAfterKeepaliveStopsIsRejected()
    {
        var remover = new WslGatewayRemoval(_inspector,
            (_, _) =>
            {
                _inspector.Current = _inspector.Current with { RegistrationId = "replacement" };
                return Task.CompletedTask;
            },
            (_, _) => throw new Exception("Must not unregister replacement"));
        Assert.Equal(PipelineOutcome.Failed,
            (await remover.RunAsync(_ctx, _target.Id, GatewayDashboardBinding.Capture(_target))).Outcome);
        AssertRetryTarget();
    }

    [Fact]
    public async Task DryRunDoesNotInvokeLifecycleOrRemoveMetadata()
    {
        _ctx.Config.DryRun = true;
        Assert.Equal(PipelineOutcome.Success, (await Run()).Outcome);
        Assert.Empty(_calls);
        AssertRetryTarget();
    }

    private void AssertRetryTarget()
    {
        _registry.Load();
        Assert.Equal(_target.Id, _registry.ActiveGatewayId);
        Assert.NotNull(_registry.GetById(_target.Id));
        Assert.Equal("key-b", File.ReadAllText(Path.Combine(_registry.GetIdentityDirectory(_target.Id), "device-key-ed25519.json")));
    }

    public void Dispose()
    {
        _ctx.Logger.Dispose();
        _ctx.Journal.Dispose();
        _temp.Dispose();
    }

    private sealed class Inspector : IWslRegistrationInspector
    {
        public WslRegistrationInspection Current { get; set; } = new(WslRegistrationInspectionStatus.NotFound);
        public WslRegistrationInspection Inspect(string distroName) => Current;
    }

    private sealed class Commands : ICommandRunner
    {
        public Func<string[], CommandResult>? Invoke { get; set; }
        public List<string> Operations { get; } = [];
        public Task<CommandResult> RunAsync(string executable, string[] arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null,
            string? stdinInput = null, CancellationToken ct = default, Stream? stdinStream = null)
        {
            Operations.Add(arguments[0]);
            return Task.FromResult(Invoke?.Invoke(arguments) ?? throw new InvalidOperationException("Unexpected command"));
        }
        public Task<CommandResult> RunInWslAsync(string distroName, string command, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null, CancellationToken ct = default,
            string? user = null, bool inputViaStdin = false) => throw new InvalidOperationException("Unexpected in-distro command");
    }
}
