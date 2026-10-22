using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using System.Text.Json;

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
        }).RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

    private void FinishDistroRemoval()
    {
        var path = _inspector.Current.BasePath!;
        _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
        Directory.Delete(path, recursive: true);
    }

    [Theory]
    [InlineData("shared-token")]
    [InlineData("bootstrap-token")]
    [InlineData("signature")]
    [InlineData("browser-port")]
    [InlineData("label")]
    public async Task ParentConsentCannotBeRebasedToChangedRecordAtChildAdmission(string change)
    {
        var changed = change switch
        {
            "shared-token" => _target with { SharedGatewayToken = "new-fixture-token" },
            "bootstrap-token" => _target with { BootstrapToken = "new-fixture-bootstrap" },
            "signature" => _target with { RequiresV2Signature = true },
            "browser-port" => _target with { BrowserControlPort = 19999 },
            "label" => _target with { FriendlyName = "Changed" },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(GatewayDashboardBinding.Capture(_target), GatewayDashboardBinding.Capture(changed));
        var confirmed = LocalGatewaySettings.CaptureRemovalAuthority(_target);
        _registry.AddOrUpdate(changed);
        _registry.Save();
        var remover = new WslGatewayRemoval(_inspector, (_, _) =>
            throw new InvalidOperationException("No keepalive may stop for stale consent."));

        var result = await remover.RunAsync(_ctx, _target.Id, confirmed);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains("changed", result.Message);
        Assert.Empty(_commands.Operations);
        Assert.True(Directory.Exists(_inspector.Current.BasePath!));
        _registry.Load();
        Assert.Equal(changed, _registry.GetActive());
        Assert.Equal("key-b", File.ReadAllText(Path.Combine(_registry.GetIdentityDirectory(_target.Id), "device-key-ed25519.json")));
    }

    [Fact]
    public async Task TimestampOnlyDriftDoesNotInvalidateParentConsent()
    {
        _registry.AddOrUpdate(_target with { LastConnected = DateTime.UtcNow });
        _registry.Save();

        Assert.Equal(PipelineOutcome.Success, (await Run()).Outcome);
        Assert.Equal(["stop", "remove"], _calls);
    }

    [Fact]
    public async Task EndpointOnlyConsentIsRejectedBeforeAnyDestructiveWork()
    {
        var remover = new WslGatewayRemoval(_inspector, (_, _) =>
            throw new InvalidOperationException("Endpoint-only consent cannot stop keepalive."));
        var result = await remover.RunAsync(_ctx, _target.Id, GatewayDashboardBinding.Capture(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Empty(_commands.Operations);
        AssertRetryTarget();
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
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task FailedIdentityCommitAndRestorePreservesRecoveryInstructionsInJsonResult()
    {
        var profile = _registry.GetIdentityDirectory(_target.Id);
        var remover = new WslGatewayRemoval(_inspector, (_, _) => Task.CompletedTask,
            (_, _) => { FinishDistroRemoval(); return Task.CompletedTask; },
            registryFileSystem: new FailedIdentityCommitFileSystem(profile));

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        var staged = Assert.Single(Directory.GetDirectories(_ctx.DataDir, $"gateway-removal-{_target.Id}-*"));
        Assert.Contains(staged, result.Message);
        Assert.Contains("Do not reconnect until the profile is restored", result.Message);
        Assert.Equal("key-b", File.ReadAllText(Path.Combine(staged, "device-key-ed25519.json")));
        _registry.Load();
        Assert.Equal(_target, _registry.GetActive());
        Assert.NotNull(_registry.GetById("wsl-a"));
        using var json = JsonDocument.Parse(Program.SerializeJsonOutput(result, _ctx.Config, "journal.jsonl"));
        Assert.Equal("Failed", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(result.Message, json.RootElement.GetProperty("message").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("exitCode").GetInt32());
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
        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));
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
            (await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target))).Outcome);
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
        var result = await remover.RunAsync(_ctx, legacy.Id, LocalGatewaySettings.CaptureRemovalAuthority(legacy));
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
            (await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target))).Outcome);
        AssertRetryTarget();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedRemovalRequestDoesNotReachLifecycle(bool wrongBinding)
    {
        var remover = new WslGatewayRemoval(_inspector,
            (_, _) => throw new InvalidOperationException("Must not stop an unconfirmed target"),
            (_, _) => throw new InvalidOperationException("Must not remove an unconfirmed target"));

        var result = await remover.RunAsync(_ctx,
            wrongBinding ? _target.Id : "other-gateway",
            wrongBinding ? "wrong-binding" : LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Empty(_commands.Operations);
        AssertRetryTarget();
    }

    [Fact]
    public async Task ResidualDeletionRetriesSucceedWhenAuthorityIsUnchanged()
    {
        var installPath = _inspector.Current.BasePath!;
        var sentinel = Path.Combine(installPath, "ext4.vhdx");
        File.WriteAllText(sentinel, "owned disk");
        using var lockedFile = new FileStream(sentinel, FileMode.Open, FileAccess.Read, FileShare.None);
        var delays = 0;
        _commands.Invoke = arguments =>
        {
            if (arguments[0] == "--unregister")
                _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
            return new(0, "", "", TimeSpan.Zero, false);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) => Task.CompletedTask,
            deletionDelay: (_, _) =>
            {
                delays++;
                lockedFile.Dispose();
                return Task.CompletedTask;
            });

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(1, delays);
        Assert.False(Directory.Exists(installPath));
        _registry.Load();
        Assert.Null(_registry.GetById(_target.Id));
        Assert.NotNull(_registry.GetById("wsl-a"));
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("credentials")]
    public async Task SavedTargetRevokedAfterReceiptPreventsKeepaliveShutdown(string change)
    {
        GatewayRegistrySnapshot? changed = null;
        var inspections = 0;
        _inspector.BeforeInspect = () =>
        {
            if (++inspections != 2) return;
            Assert.True(File.Exists(Path.Combine(_ctx.LocalDataDir,
                $"wsl-removal-{GatewayDashboardBinding.Capture(_target)}.json")));
            changed = ChangeSavedTarget(change);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) =>
            throw new InvalidOperationException("Revoked target must not stop keepalive"));

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains("Gateway changed", result.Message);
        Assert.Empty(_commands.Operations);
        Assert.True(Directory.Exists(_inspector.Current.BasePath!));
        AssertSavedSnapshotAndIdentities(changed!);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("rebound")]
    [InlineData("credentials")]
    [InlineData("ownership")]
    [InlineData("selected")]
    [InlineData("browser-port")]
    public async Task SavedTargetChangeDuringKeepalivePreventsRemoval(string change)
    {
        GatewayRegistrySnapshot? changed = null;
        var installPath = _inspector.Current.BasePath!;
        var remover = new WslGatewayRemoval(_inspector,
            (_, _) =>
            {
                changed = ChangeSavedTarget(change);
                return Task.CompletedTask;
            },
            (_, _) => throw new InvalidOperationException("Changed target must not reach removal"));

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains("Gateway changed", result.Message);
        Assert.Empty(_commands.Operations);
        Assert.True(Directory.Exists(installPath));
        AssertSavedSnapshotAndIdentities(changed!);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("rebound")]
    [InlineData("credentials")]
    [InlineData("ownership")]
    [InlineData("selected")]
    [InlineData("browser-port")]
    public async Task SavedTargetChangeDuringTerminationPreventsUnregister(string change)
    {
        GatewayRegistrySnapshot? changed = null;
        var installPath = _inspector.Current.BasePath!;
        _commands.Invoke = arguments =>
        {
            Assert.Equal("--terminate", arguments[0]);
            changed = ChangeSavedTarget(change);
            return new(0, "", "", TimeSpan.Zero, false);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) => Task.CompletedTask);

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["--terminate"], _commands.Operations);
        Assert.True(Directory.Exists(installPath));
        AssertSavedSnapshotAndIdentities(changed!);
    }

    [Fact]
    public async Task UnrelatedRecordEditsAndConnectionTimestampDoNotRevokeRemoval()
    {
        _commands.Invoke = arguments =>
        {
            if (arguments[0] == "--unregister")
                _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
            return new(0, "", "", TimeSpan.Zero, false);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) =>
        {
            var writer = new GatewayRegistry(_ctx.DataDir);
            writer.Load();
            writer.AddOrUpdate(_target with { LastConnected = DateTime.UtcNow });
            writer.AddOrUpdate(writer.GetById("wsl-a")! with { FriendlyName = "Updated unrelated Gateway" });
            writer.Save();
            return Task.CompletedTask;
        });

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(["--terminate", "--unregister"], _commands.Operations);
        _registry.Load();
        Assert.Null(_registry.GetById(_target.Id));
        Assert.Equal("Updated unrelated Gateway", _registry.GetById("wsl-a")!.FriendlyName);
    }

    [Fact]
    public async Task SavedTargetChangeAfterUnregisterPreventsResidualDeletion()
    {
        var installPath = _inspector.Current.BasePath!;
        var sentinel = Path.Combine(installPath, "ext4.vhdx");
        File.WriteAllText(sentinel, "preserve residual disk");
        GatewayRegistrySnapshot? changed = null;
        _commands.Invoke = arguments =>
        {
            if (arguments[0] == "--unregister")
            {
                _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
                changed = ChangeSavedTarget("credentials");
            }
            return new(0, "", "", TimeSpan.Zero, false);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) => Task.CompletedTask);

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("preserve residual disk", File.ReadAllText(sentinel));
        AssertSavedSnapshotAndIdentities(changed!);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("same-registration")]
    [InlineData("unavailable")]
    [InlineData("saved-target")]
    [InlineData("invalid-registry")]
    public async Task ResidualDeletionRevalidatesAuthorityBetweenAttempts(string change)
    {
        var registration = _inspector.Current;
        var installPath = registration.BasePath!;
        var sentinel = Path.Combine(installPath, "ext4.vhdx");
        File.WriteAllText(sentinel, "replacement disk");
        using var lockedFile = new FileStream(sentinel, FileMode.Open, FileAccess.Read, FileShare.None);
        var delays = 0;
        GatewayRegistrySnapshot? changed = null;
        _commands.Invoke = arguments =>
        {
            if (arguments[0] == "--unregister")
                _inspector.Current = new(WslRegistrationInspectionStatus.NotFound);
            return new(0, "", "", TimeSpan.Zero, false);
        };
        var remover = new WslGatewayRemoval(_inspector, (_, _) => Task.CompletedTask,
            deletionDelay: (delay, _) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2), delay);
                delays++;
                lockedFile.Dispose();
                switch (change)
                {
                    case "replacement":
                        _inspector.Current = registration with { RegistrationId = "replacement" };
                        break;
                    case "same-registration":
                        _inspector.Current = registration;
                        break;
                    case "unavailable":
                        _inspector.Current = new(WslRegistrationInspectionStatus.Unavailable);
                        break;
                    case "saved-target":
                        changed = ChangeSavedTarget("rebound");
                        break;
                    case "invalid-registry":
                        File.WriteAllText(Path.Combine(_ctx.DataDir, "gateways.json"), "{invalid");
                        break;
                }
                return Task.CompletedTask;
            });

        var result = await remover.RunAsync(_ctx, _target.Id, LocalGatewaySettings.CaptureRemovalAuthority(_target));

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(1, delays);
        Assert.Equal(["--terminate", "--unregister"], _commands.Operations);
        Assert.Equal("replacement disk", File.ReadAllText(sentinel));
        if (change == "invalid-registry")
        {
            Assert.Equal("{invalid", File.ReadAllText(Path.Combine(_ctx.DataDir, "gateways.json")));
            Assert.Equal("key-b", File.ReadAllText(Path.Combine(_registry.GetIdentityDirectory(_target.Id), "device-key-ed25519.json")));
        }
        else if (changed is not null)
            AssertSavedSnapshotAndIdentities(changed);
        else
            AssertRetryTarget();
    }

    private GatewayRegistrySnapshot ChangeSavedTarget(string change)
    {
        var writer = new GatewayRegistry(_ctx.DataDir);
        writer.Load();
        switch (change)
        {
            case "removed": writer.Remove(_target.Id); break;
            case "rebound": writer.AddOrUpdate(_target with { SetupManagedDistroName = "GatewayC" }); break;
            case "credentials": writer.AddOrUpdate(_target with { SharedGatewayToken = "replacement-fixture-token" }); break;
            case "ownership": writer.AddOrUpdate(_target with { IsLocal = false }); break;
            case "selected": writer.SetActive("wsl-a"); break;
            case "browser-port": writer.AddOrUpdate(_target with { BrowserControlPort = 18800 }); break;
            default: throw new ArgumentException("Unknown test target change.", nameof(change));
        }
        writer.Save();
        return writer.GetSnapshot();
    }

    private void AssertSavedSnapshotAndIdentities(GatewayRegistrySnapshot expected)
    {
        _registry.Load();
        Assert.Equal(expected.ActiveId, _registry.ActiveGatewayId);
        Assert.Equal(expected.Records, _registry.GetAll());
        Assert.NotNull(_registry.GetById("wsl-a"));
        Assert.Equal("key-b", File.ReadAllText(Path.Combine(_registry.GetIdentityDirectory(_target.Id), "device-key-ed25519.json")));
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
        public Action? BeforeInspect { get; set; }
        public WslRegistrationInspection Inspect(string distroName)
        {
            BeforeInspect?.Invoke();
            return Current;
        }
    }

    private sealed class FailedIdentityCommitFileSystem(string profile) : IFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
        public void DeleteFile(string path) => File.Delete(path);
        public void MoveFile(string source, string destination, bool overwrite)
        {
            Assert.False(Directory.Exists(profile));
            Directory.CreateDirectory(profile);
            throw new IOException("Injected registry commit failure after identity staging");
        }
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
