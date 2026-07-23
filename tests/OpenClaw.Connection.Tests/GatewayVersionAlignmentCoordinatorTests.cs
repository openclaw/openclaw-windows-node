using System.Collections.Concurrent;
using OpenClaw.Connection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayVersionAlignmentCoordinatorTests : IDisposable
{
    private const string RequiredVersion = "2026.7.22+companion.2";
    private const string PreviousVersion = "2026.7.21+companion.1";
    private const string LegacyVersion = "2026.7.2-beta.3";
    private const string MachineId = "0123456789abcdef0123456789abcdef";
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"openclaw-rollback-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("")]
    [InlineData("v2026.7.22")]
    [InlineData("latest")]
    [InlineData("2026.07.22")]
    [InlineData("2026.7")]
    public void Constructor_RejectsNonExactRequiredVersion(string version)
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        Assert.Throws<ArgumentException>(() => new GatewayVersionAlignmentCoordinator(runner, version, manager));
    }

    [Fact]
    public async Task ProbeAsync_RequiresProvenOwnedWslPlan()
    {
        var runner = new FakeWslCommandRunner();
        var coordinator = CreateCoordinator(runner);
        var plans = new[]
        {
            GatewayHostAccessPlan.None("gateway"),
            EligiblePlan() with { TerminalTarget = GatewayTerminalTarget.Ssh },
            EligiblePlan() with { CanControlWslGateway = false },
            EligiblePlan() with { DistroName = "OtherDistro" },
            EligiblePlan() with { GatewayId = null }
        };

        foreach (var plan in plans)
            Assert.Equal(GatewayVersionAlignmentState.Ineligible, (await coordinator.UpdateAsync(plan)).State);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ProbeAsync_RejectsControllableNonOwnedDistroWithoutProbe()
    {
        var runner = new FakeWslCommandRunner();
        var result = await CreateCoordinator(runner).ProbeAsync(
            EligiblePlan() with { DistroName = "OtherDistro" });

        Assert.Equal(GatewayVersionAlignmentState.Ineligible, result.State);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ProbeAsync_TreatsDifferentBuildMetadataAsExactMismatch()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok("2026.7.22+companion.1"));

        var result = await CreateCoordinator(runner).ProbeAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.Mismatch, result.State);
        Assert.Equal("2026.7.22+companion.1", result.InstalledVersion);
    }

    [Fact]
    public async Task ProbeAsync_TreatsHigherOrderedCompanionBuildAsNewer()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok("2026.7.22+companion.3"));

        var result = await CreateCoordinator(runner).ProbeAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.NewerThanRequired, result.State);
    }

    [Fact]
    public async Task ProbeAsync_FailsClosedWhenDifferentBuildMetadataCannotBeOrdered()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok("2026.7.22+vendor.3"));

        var result = await CreateCoordinator(runner).ProbeAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VersionOrderUnknown, result.State);
        Assert.NotNull(result.FailureSummary);
    }

    [Fact]
    public async Task ProbeAsync_FailsClosedWhenSignedBuildIdentifierLooksNumeric()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok("2026.7.22+companion.-1"));

        var result = await CreateCoordinator(runner).ProbeAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VersionOrderUnknown, result.State);
    }

    [Fact]
    public async Task ProbeAsync_ValidatesLaterMetadataNamespaceBeforeUsingEarlierNumericOrder()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok("2026.7.22+2.other"));
        var manager = CreateManager(runner);
        var coordinator = new GatewayVersionAlignmentCoordinator(
            runner, "2026.7.22+1.vendor", manager);

        var result = await coordinator.ProbeAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VersionOrderUnknown, result.State);
    }

    [Fact]
    public async Task UpdateAsync_BlocksAutomaticDowngrade()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok("2026.7.23"));

        var result = await CreateCoordinator(runner).UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.NewerThanRequired, result.State);
        Assert.DoesNotContain(runner.Calls, call => call.Kind is "terminate" or "unregister" or "direct");
    }

    [Fact]
    public async Task UpdateAsync_CreatesOfflineVerifiedPointThenUpdatesAndCleansOnlyAfterHealth()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(Ok(UpdateJson(RequiredVersion)));
        runner.EnqueueDistro(Ok(RequiredVersion));
        EnqueuePendingAttestation(runner, RequiredVersion);
        EnqueuePendingAttestation(runner, RequiredVersion);
        var synchronizations = 0;
        var coordinator = CreateCoordinator(runner, (_, _) => { synchronizations++; return Task.CompletedTask; });

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.Updated, result.State);
        Assert.Equal(2, synchronizations);
        Assert.NotNull(result.RollbackPointId);
        var point = Assert.Single(coordinator.ListRollbackPoints());
        Assert.Equal(GatewayRollbackPointPhase.PostUpdateHealthy, point.Phase);
        Assert.True(point.RestoreEligible);
        Assert.True(point.ApproximateSizeBytes > 8);
        Assert.Collection(
            runner.Calls,
            call => AssertDistro(call, ProbeCommand()),
            call => Assert.Equal("registrations", call.Kind),
            call => Assert.Equal("get-configuration", call.Kind),
            call => AssertDistro(call, DefaultUserCommand()),
            call => AssertDistro(call, IdentityCommand()),
            call => AssertDistro(call, ProbeCommand()),
            call => Assert.Equal("terminate", call.Kind),
            call => Assert.Equal(["--export", "OpenClawGateway", call.Arguments[2], "--vhd"], call.Arguments),
            call => AssertDistro(call, ProbeCommand()),
            call => AssertDistro(call, IdentityCommand()),
            call => Assert.Equal("get-configuration", call.Kind),
            call => AssertDistro(call, DefaultUserCommand()),
            call => Assert.Equal("registrations", call.Kind),
            call => Assert.Equal("get-configuration", call.Kind),
            call => AssertDistro(call, IdentityCommand()),
            call => AssertDistro(call, DefaultUserCommand()),
            call => AssertDistro(call, ProbeCommand()),
            call => AssertDistro(call, UpdateCommand(RequiredVersion)),
            call => AssertDistro(call, ProbeCommand()),
            call => Assert.Equal("registrations", call.Kind),
            call => Assert.Equal("get-configuration", call.Kind),
            call => AssertDistro(call, IdentityCommand()),
            call => AssertDistro(call, DefaultUserCommand()),
            call => AssertDistro(call, ProbeCommand()),
            call => Assert.Equal("registrations", call.Kind),
            call => Assert.Equal("get-configuration", call.Kind),
            call => AssertDistro(call, IdentityCommand()),
            call => AssertDistro(call, DefaultUserCommand()),
            call => AssertDistro(call, ProbeCommand()));
        Assert.DoesNotContain(runner.Calls, call => call.Kind == "unregister");
    }

    [Fact]
    public async Task UpdateAsync_MigratesCompleteLegacyAllowlistToCurrentPath()
    {
        const string completeArray = """["system.run","system.which","camera.snap"]""";
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(LegacyVersion));
        runner.EnqueueDistro(Ok(completeArray));
        EnqueueCreateAttestation(runner, LegacyVersion);
        EnqueuePendingAttestation(runner, LegacyVersion);
        runner.EnqueueDistro(Ok(completeArray));
        runner.EnqueueDistro(Ok(UpdateJson(RequiredVersion)));
        runner.EnqueueDistro(Ok(RequiredVersion));
        runner.EnqueueDistro(Ok());
        runner.EnqueueDistro(Ok(completeArray));
        runner.EnqueueDistro(Ok());
        EnqueuePendingAttestation(runner, RequiredVersion);
        EnqueuePendingAttestation(runner, RequiredVersion);
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.Updated, result.State);
        Assert.Equal(completeArray, Assert.Single(ReadOwnedManifests()).NodeCommandAllowSnapshotJson);
        Assert.Contains(runner.Calls, call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand("get gateway.nodes.allowCommands --json")));
        Assert.Contains(runner.Calls, call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand("unset gateway.nodes.allowCommands")));
        Assert.Contains(runner.Calls, call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand(
                "set gateway.nodes.commands.allow '[\"system.run\",\"system.which\",\"camera.snap\"]'")));
        var setIndex = runner.Calls.FindIndex(call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand(
                "set gateway.nodes.commands.allow '[\"system.run\",\"system.which\",\"camera.snap\"]'")));
        var verifyIndex = runner.Calls.FindIndex(call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand("get gateway.nodes.commands.allow --json")));
        var unsetIndex = runner.Calls.FindIndex(call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand("unset gateway.nodes.allowCommands")));
        Assert.True(setIndex >= 0 && verifyIndex > setIndex && unsetIndex > verifyIndex);
    }

    [Fact]
    public async Task UpdateAsync_BlocksWhenCompleteAllowlistCannotBeReadAtFinalBoundary()
    {
        const string capturedArray = """["system.run","system.which","camera.snap"]""";
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(LegacyVersion));
        runner.EnqueueDistro(Ok(capturedArray));
        EnqueueCreateAttestation(runner, LegacyVersion);
        EnqueuePendingAttestation(runner, LegacyVersion);
        runner.EnqueueDistro(new WslCommandResult(1, "", "policy read failed"));
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
        var point = Assert.Single(coordinator.ListRollbackPoints());
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, point.Phase);
        Assert.Equal(GatewayRollbackPointVerificationStatus.Verified, point.VerificationStatus);
        Assert.True(point.RestoreEligible);
    }

    [Fact]
    public async Task UpdateAsync_BlocksPolicyDriftAfterReceiptTransitionBeforeUpdater()
    {
        const string capturedArray = """["system.run","system.which","camera.snap"]""";
        const string changedArray = """["system.run","system.which"]""";
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(LegacyVersion));
        runner.EnqueueDistro(Ok(capturedArray));
        EnqueueCreateAttestation(runner, LegacyVersion);
        EnqueuePendingAttestation(runner, LegacyVersion);
        runner.EnqueueDistro(Ok(changedArray));
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
        var point = Assert.Single(coordinator.ListRollbackPoints());
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, point.Phase);
        Assert.True(point.RestoreEligible);
    }

    [Fact]
    public async Task UpdateAsync_PreservesReceiptWhenCompleteAllowlistMigrationFails()
    {
        const string completeArray = """["system.run","system.which","camera.snap"]""";
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(LegacyVersion));
        runner.EnqueueDistro(Ok(completeArray));
        EnqueueCreateAttestation(runner, LegacyVersion);
        EnqueuePendingAttestation(runner, LegacyVersion);
        runner.EnqueueDistro(Ok(completeArray));
        runner.EnqueueDistro(Ok(UpdateJson(RequiredVersion)));
        runner.EnqueueDistro(Ok(RequiredVersion));
        runner.EnqueueDistro(Ok());
        runner.EnqueueDistro(new WslCommandResult(1, "", "set failed"));
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, result.State);
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(coordinator.ListRollbackPoints()).Phase);
        Assert.Contains("preserved", result.FailureSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand("unset gateway.nodes.allowCommands")));
    }

    [Fact]
    public async Task VerifyAsync_InvalidatesCorruptPointButPreservesPendingReceipt()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        manager.MarkUpdateInProgress(created.Point!.Id);
        var rollbackPath = Path.Combine(
            _tempRoot,
            "gateway-rollback-points",
            "OpenClawGateway",
            created.Point.Id,
            "rollback.vhdx");
        File.WriteAllBytes(rollbackPath, [1, 2, 3]);

        Assert.False(await manager.VerifyAsync(created.Point.Id));

        var invalid = Assert.Single(manager.List());
        Assert.Equal(GatewayRollbackPointVerificationStatus.Failed, invalid.VerificationStatus);
        Assert.False(invalid.RestoreEligible);
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, invalid.Phase);
        Assert.Equal(created.Point.Id, Assert.Single(manager.FindPendingUpdates()).Id);
    }

    [Fact]
    public async Task CreateVerifiedAsync_RejectsReparseBackedPointsRootBeforeExport()
    {
        var redirected = Path.Combine(_tempRoot, "redirected-points");
        var pointsParent = Path.Combine(_tempRoot, "gateway-rollback-points");
        var pointsRoot = Path.Combine(pointsParent, "OpenClawGateway");
        Directory.CreateDirectory(redirected);
        Directory.CreateDirectory(pointsParent);
        try
        {
            Directory.CreateSymbolicLink(pointsRoot, redirected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);

        var result = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);

        Assert.Equal(GatewayRollbackOperationState.InstallPathCollision, result.State);
        Assert.Equal("rollback_points_reparse_boundary", result.FailureCode);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "direct" && call.Arguments.Contains("--export"));
        Assert.Empty(Directory.GetFileSystemEntries(redirected));
    }

    [Fact]
    public async Task CreateVerifiedAsync_RejectsWrongRegisteredBasePathBeforeLifecycleMutation()
    {
        var runner = new FakeWslCommandRunner
        {
            RegisteredBasePath = Path.Combine(_tempRoot, "unexpected-registration-root")
        };
        var result = await CreateManager(runner).CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);

        Assert.Equal(GatewayRollbackOperationState.InstallPathCollision, result.State);
        Assert.Equal("registration_base_path_mismatch", result.FailureCode);
        Assert.DoesNotContain(runner.Calls, call => call.Kind is "terminate" or "direct");
    }

    [Fact]
    public async Task UpdateAsync_FailsClosedWhenPreUpdateHealthFails()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        var coordinator = CreateCoordinator(
            runner,
            (_, _) => Task.FromException(new InvalidOperationException("private detail")));

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.PreUpdateHealthFailed, result.State);
        Assert.Single(runner.Calls);
        Assert.DoesNotContain("private detail", result.FailureSummary);
    }

    [Fact]
    public async Task UpdateAsync_FailsClosedWhenVhdExportCannotBeVerifiedAndRestartsExistingRuntime()
    {
        var runner = new FakeWslCommandRunner { ExportResult = new WslCommandResult(9, "", "secret") };
        runner.EnqueueDistro(Ok(PreviousVersion));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(PreviousVersion));
        runner.EnqueueDistro(Ok(PreviousVersion));
        var synchronizations = 0;
        var coordinator = CreateCoordinator(runner, (_, _) => { synchronizations++; return Task.CompletedTask; });

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.RollbackPointFailed, result.State);
        Assert.Equal(2, synchronizations);
        Assert.DoesNotContain("secret", result.FailureSummary);
        Assert.DoesNotContain(runner.Calls, call => call.Kind == "distro" && call.Arguments.Contains("update --tag"));
        Assert.Equal(0, runner.UnregisterCalls);
    }

    [Fact]
    public async Task UpdateAsync_PreservesVerifiedRecoveryPointWhenUpdateFails()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(new WslCommandResult(12, "token=secret", "token=secret"));
        runner.EnqueueDistro(Ok(PreviousVersion));
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, result.State);
        Assert.NotNull(result.RollbackPointId);
        var point = Assert.Single(coordinator.ListRollbackPoints());
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, point.Phase);
        Assert.True(point.RestoreEligible);
        Assert.DoesNotContain("secret", result.FailureSummary);
        Assert.Equal(0, runner.UnregisterCalls);
    }

    [Fact]
    public async Task UpdateAsync_BlocksWhenLiveIdentityChangesImmediatelyBeforeNewPackageMutation()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(Ok("fedcba9876543210fedcba9876543210"));
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(coordinator.ListRollbackPoints()).Phase);
    }

    [Theory]
    [InlineData("2026.7.21+companion.9")]
    [InlineData("2026.7.21-rc.1+companion.1")]
    [InlineData("2026.7.23+companion.1")]
    public async Task UpdateAsync_BlocksExactVersionDriftBeforeUpdaterWithoutDowngrade(string observedVersion)
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, observedVersion);
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(coordinator.ListRollbackPoints()).Phase);
    }

    [Fact]
    public async Task UpdateAsync_BlocksFinalizationWhenExactVersionChangesAfterPostUpdateProbe()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(Ok(UpdateJson(RequiredVersion)));
        runner.EnqueueDistro(Ok(RequiredVersion));
        EnqueuePendingAttestation(runner, "2026.7.22+companion.3");
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(coordinator.ListRollbackPoints()).Phase);
        Assert.True(coordinator.HasVerifiedPendingUpdate("local-gateway"));
    }

    [Fact]
    public async Task UpdateAsync_BlocksCleanupWhenExactVersionChangesAfterHealthyMarking()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(Ok(UpdateJson(RequiredVersion)));
        runner.EnqueueDistro(Ok(RequiredVersion));
        EnqueuePendingAttestation(runner, RequiredVersion);
        EnqueuePendingAttestation(runner, "2026.7.22+companion.3");
        var coordinator = CreateCoordinator(runner);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.True(coordinator.HasVerifiedPendingUpdate("local-gateway"));
    }

    [Fact]
    public async Task UpdateAsync_ResumesPendingPostUpdateHealthWhenVersionIsAlreadyAligned()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(Ok(UpdateJson(RequiredVersion)));
        runner.EnqueueDistro(Ok(RequiredVersion));
        var syncCalls = 0;
        var coordinator = CreateCoordinator(runner, (_, _) =>
        {
            syncCalls++;
            return syncCalls == 2
                ? Task.FromException(new InvalidOperationException("transient"))
                : Task.CompletedTask;
        });

        var failedHealth = await coordinator.UpdateAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, failedHealth.State);
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(coordinator.ListRollbackPoints()).Phase);
        Assert.True(coordinator.HasVerifiedPendingUpdate("local-gateway"));

        runner.EnqueueDistro(Ok(RequiredVersion));
        EnqueuePendingAttestation(runner, RequiredVersion);
        EnqueuePendingAttestation(runner, RequiredVersion);
        EnqueuePendingAttestation(runner, RequiredVersion);
        var resumed = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.Updated, resumed.State);
        Assert.False(coordinator.HasVerifiedPendingUpdate("local-gateway"));
        Assert.Equal(3, syncCalls);
        Assert.Equal(GatewayRollbackPointPhase.PostUpdateHealthy, Assert.Single(coordinator.ListRollbackPoints()).Phase);
        Assert.Single(runner.Calls, call => call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
    }

    [Fact]
    public async Task UpdateAsync_BlocksAlignedPendingFinalizationWhenLiveIdentityChanged()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        manager.MarkUpdateInProgress(created.Point!.Id);
        runner.ClearCalls();
        runner.EnqueueDistro(Ok(RequiredVersion));
        runner.EnqueueDistro(Ok("fedcba9876543210fedcba9876543210"));
        var synchronizations = 0;
        var coordinator = new GatewayVersionAlignmentCoordinator(
            runner, RequiredVersion, manager, (_, _) => { synchronizations++; return Task.CompletedTask; });

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.Equal(0, synchronizations);
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(manager.List()).Phase);
    }

    [Fact]
    public async Task UpdateAsync_RetriesFailedMismatchedUpdateUsingSameVerifiedPoint()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(new WslCommandResult(12, "", ""));
        runner.EnqueueDistro(Ok(PreviousVersion));
        var coordinator = CreateCoordinator(runner);

        var failed = await coordinator.UpdateAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, failed.State);

        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueuePendingAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(Ok(UpdateJson(RequiredVersion)));
        runner.EnqueueDistro(Ok(RequiredVersion));
        EnqueuePendingAttestation(runner, RequiredVersion);
        EnqueuePendingAttestation(runner, RequiredVersion);
        var retried = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.Updated, retried.State);
        Assert.Equal(failed.RollbackPointId, retried.RollbackPointId);
        Assert.Single(runner.Calls, call => call.Kind == "direct" && call.Arguments.FirstOrDefault() == "--export");
        Assert.Equal(2, runner.Calls.Count(call => call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion))));
    }

    [Fact]
    public async Task UpdateAsync_RetriesPendingUpdateWithoutRequiringBrokenRuntimePreHealth()
    {
        var runtimeBroken = false;
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(_ =>
        {
            runtimeBroken = true;
            return Task.FromResult(new WslCommandResult(12, "", ""));
        });
        runner.EnqueueDistro(Ok(PreviousVersion));
        var coordinator = CreateCoordinator(runner, (_, _) =>
            runtimeBroken
                ? Task.FromException(new InvalidOperationException("runtime unavailable"))
                : Task.CompletedTask);

        var failed = await coordinator.UpdateAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, failed.State);

        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueuePendingAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(_ =>
        {
            runtimeBroken = false;
            return Task.FromResult(Ok(UpdateJson(RequiredVersion)));
        });
        runner.EnqueueDistro(Ok(RequiredVersion));
        EnqueuePendingAttestation(runner, RequiredVersion);
        EnqueuePendingAttestation(runner, RequiredVersion);
        var retried = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.Updated, retried.State);
        Assert.Equal(failed.RollbackPointId, retried.RollbackPointId);
    }

    [Fact]
    public async Task UpdateAsync_BlocksPendingRetryWhenLiveDistroIdentityChanged()
    {
        var runner = new FakeWslCommandRunner();
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueueCreateAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.EnqueueDistro(new WslCommandResult(12, "", ""));
        runner.EnqueueDistro(Ok(PreviousVersion));
        var coordinator = CreateCoordinator(runner);
        var failed = await coordinator.UpdateAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, failed.State);

        runner.EnqueueDistro(Ok(PreviousVersion));
        runner.EnqueueDistro(Ok("fedcba9876543210fedcba9876543210"));
        var blocked = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, blocked.State);
        Assert.Equal(failed.RollbackPointId, blocked.RollbackPointId);
        Assert.Equal(1, runner.Calls.Count(call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion))));
    }

    [Fact]
    public async Task UpdateAsync_BlocksPendingRetryWhenLiveVersionDiffersFromReceiptSource()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        manager.MarkUpdateInProgress(created.Point!.Id);
        runner.ClearCalls();
        const string unrecordedVersion = "2026.7.20+companion.9";
        runner.EnqueueDistro(Ok(unrecordedVersion));
        EnqueuePendingAttestation(runner, unrecordedVersion);
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var blocked = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, blocked.State);
        Assert.Equal(created.Point.Id, blocked.RollbackPointId);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(manager.List()).Phase);
    }

    [Fact]
    public async Task UpdateAsync_BlocksWhenPendingReceiptTargetsAnOlderCompanionVersion()
    {
        const string supersededTarget = "2026.7.22+companion.1";
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, supersededTarget);
        manager.MarkUpdateInProgress(created.Point!.Id);
        runner.ClearCalls();
        runner.EnqueueDistro(Ok(PreviousVersion));
        var syncCalls = 0;
        var coordinator = new GatewayVersionAlignmentCoordinator(
            runner, RequiredVersion, manager, (_, _) => { syncCalls++; return Task.CompletedTask; });

        var probe = await coordinator.ProbeAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, probe.State);
        Assert.Equal(created.Point.Id, probe.RollbackPointId);
        Assert.Empty(runner.Calls);

        var blocked = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, blocked.State);
        Assert.Equal(created.Point.Id, blocked.RollbackPointId);
        Assert.Equal(0, syncCalls);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
    }

    [Theory]
    [InlineData(GatewayRollbackPointPhase.RestoreStaged)]
    [InlineData(GatewayRollbackPointPhase.UnregisterPending)]
    [InlineData(GatewayRollbackPointPhase.DistroUnregistered)]
    [InlineData(GatewayRollbackPointPhase.ImportPending)]
    [InlineData(GatewayRollbackPointPhase.Imported)]
    public async Task UpdateAsync_BlocksEveryUnresolvedRestorePhaseBeforeProbe(
        GatewayRollbackPointPhase phase)
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        SetPointPhase(point.Point!.Id, phase);
        runner.ClearCalls();
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var probe = await coordinator.ProbeAsync(EligiblePlan());
        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, probe.State);
        Assert.Equal(point.Point.Id, probe.RollbackPointId);
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, result.State);
        Assert.Equal(point.Point.Id, result.RollbackPointId);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UpdateAsync_BlocksOwnedDistroRestoreAfterGatewayRecordIdChanges()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "previous-gateway-record", PreviousVersion, RequiredVersion);
        SetPointPhase(point.Point!.Id, GatewayRollbackPointPhase.ImportPending);
        runner.ClearCalls();
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, result.State);
        Assert.Equal(point.Point.Id, result.RollbackPointId);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UpdateAsync_BlocksPendingUpdateAfterGatewayRecordIdChanges()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "previous-gateway-record", PreviousVersion, RequiredVersion);
        manager.MarkUpdateInProgress(point.Point!.Id);
        runner.ClearCalls();
        runner.EnqueueDistro(Ok(PreviousVersion));
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var probe = await coordinator.ProbeAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, probe.State);
        Assert.Equal(point.Point.Id, probe.RollbackPointId);
        Assert.Empty(runner.Calls);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, result.State);
        Assert.Equal(point.Point.Id, result.RollbackPointId);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "distro" && call.Arguments.SequenceEqual(UpdateCommand(RequiredVersion)));
        Assert.Single(manager.List());
    }

    [Fact]
    public async Task RestoreAsync_AllowsExactUpdateInProgressPointAfterGatewayRecordIdChanges()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "previous-gateway-record", PreviousVersion, RequiredVersion);
        manager.MarkUpdateInProgress(point.Point!.Id);
        runner.ClearCalls();
        runner.ListResult = new WslCommandResult(5, "", "");

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "replacement-gateway-record", point.Point.Id, point.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.VerificationFailed, result.State);
        Assert.NotEqual(GatewayRollbackOperationState.OwnershipMismatch, result.State);
        Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, Assert.Single(manager.List()).Phase);
    }

    [Fact]
    public async Task RestoreAsync_RejectsAmbiguousUpdateInProgressReceiptsWithoutMutation()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var first = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "first-gateway-record", PreviousVersion, RequiredVersion);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var second = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "second-gateway-record", PreviousVersion, RequiredVersion);
        manager.MarkUpdateInProgress(first.Point!.Id);
        manager.MarkUpdateInProgress(second.Point!.Id);
        runner.ClearCalls();

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "replacement-gateway-record", first.Point.Id, first.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.AmbiguousRecovery, result.State);
        Assert.Empty(runner.Calls);
        Assert.All(manager.List(), point =>
            Assert.Equal(GatewayRollbackPointPhase.UpdateInProgress, point.Phase));

        var probe = await new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager)
            .ProbeAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, probe.State);
        Assert.Contains("ambiguous", probe.FailureSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task ProbeAndFreshUpdate_BlockUnreadableReceiptDirectory(string? manifestContents)
    {
        const string pointId = "20260723T120000000Z-0123456789abcdef0123456789abcdef";
        var pointDirectory = Path.Combine(
            _tempRoot, "gateway-rollback-points", "OpenClawGateway", pointId);
        Directory.CreateDirectory(pointDirectory);
        if (manifestContents is not null)
            File.WriteAllText(Path.Combine(pointDirectory, "manifest.json"), manifestContents);
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var probe = await coordinator.ProbeAsync(EligiblePlan());
        var update = await coordinator.UpdateAsync(EligiblePlan());
        var create = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, probe.State);
        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, update.State);
        Assert.Equal(GatewayRollbackOperationState.VerificationFailed, create.State);
        Assert.Equal("rollback_receipt_unreadable", create.FailureCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RestoreAsync_RejectsCrossIdRequestForPointOtherThanUniquePendingReceipt()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var pending = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "historical-gateway-record", PreviousVersion, RequiredVersion);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var other = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "other-historical-record", PreviousVersion, RequiredVersion);
        manager.MarkUpdateInProgress(pending.Point!.Id);
        runner.ClearCalls();

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "replacement-gateway-record", other.Point!.Id, other.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.OwnershipMismatch, result.State);
        Assert.Equal(other.Point.Id, result.Point?.Id);
        Assert.Empty(runner.Calls);
        Assert.Equal(
            GatewayRollbackPointPhase.UpdateInProgress,
            Assert.Single(manager.List(), point => point.Id == pending.Point.Id).Phase);
    }

    [Fact]
    public async Task UpdateAsync_FailsAmbiguousBeforeProbeWhenMultipleRestoresAreUnresolved()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var first = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var second = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        SetPointPhase(first.Point!.Id, GatewayRollbackPointPhase.RestoreStaged);
        SetPointPhase(second.Point!.Id, GatewayRollbackPointPhase.ImportPending);
        runner.ClearCalls();
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var result = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.VerificationFailed, result.State);
        Assert.Null(result.RollbackPointId);
        Assert.Contains("ambiguous", result.FailureSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task CreateVerifiedAsync_FailsVersionAttestationAndDeletesUnverifiedVhdPayload()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(PreviousVersion));
        runner.EnqueueDistro(Ok("2026.7.21+unexpected.9"));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));

        var result = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);

        Assert.Equal(GatewayRollbackOperationState.VerificationFailed, result.State);
        Assert.Equal("exported_state_attestation_changed", result.FailureCode);
        var point = Assert.Single(manager.List());
        Assert.Equal(GatewayRollbackPointVerificationStatus.Failed, point.VerificationStatus);
        Assert.False(point.RestoreEligible);
        Assert.False(File.Exists(Path.Combine(
            _tempRoot, "gateway-rollback-points", "OpenClawGateway", point.Id, "rollback.vhdx")));
    }

    [Fact]
    public async Task RestoreAsync_RequiresExactPointConfirmationBeforeLifecycleMutation()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        Assert.True(created.Success);
        runner.ClearCalls();

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, "different-id");

        Assert.Equal(GatewayRollbackOperationState.ConfirmationRequired, result.State);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task CancelRestore_AllowsOnlyRestoreStagedAndDurablyUnblocksFreshUpdate()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "previous-gateway-record", PreviousVersion, RequiredVersion);
        SetPointPhase(point.Point!.Id, GatewayRollbackPointPhase.RestoreStaged);

        var cancelled = manager.CancelRestore("OpenClawGateway", "local-gateway", point.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.Cancelled, cancelled.State);
        Assert.Equal(GatewayRollbackPointPhase.RestoreCancelled, Assert.Single(manager.List()).Phase);
        Assert.Empty(manager.FindUnresolvedRestores());

        runner.ClearCalls();
        runner.EnqueueDistro(Ok(RequiredVersion));
        var update = await new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager)
            .UpdateAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.Aligned, update.State);
    }

    [Fact]
    public async Task CoordinatorCancelRestore_RequiresExactPointConfirmationAndSupportsRecordIdDrift()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "previous-gateway-record", PreviousVersion, RequiredVersion);
        SetPointPhase(point.Point!.Id, GatewayRollbackPointPhase.RestoreStaged);
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var unconfirmed = coordinator.CancelRestore(
            EligiblePlan(), point.Point.Id, "different-point");
        var cancelled = coordinator.CancelRestore(
            EligiblePlan(), point.Point.Id, point.Point.Id);

        Assert.Equal(GatewayVersionAlignmentState.RestoreConfirmationRequired, unconfirmed.State);
        Assert.Equal(GatewayVersionAlignmentState.RestoreCancelled, cancelled.State);
        Assert.Equal(GatewayRollbackPointPhase.RestoreCancelled, Assert.Single(manager.List()).Phase);
    }

    [Theory]
    [InlineData(GatewayRollbackPointPhase.UnregisterPending)]
    [InlineData(GatewayRollbackPointPhase.DistroUnregistered)]
    [InlineData(GatewayRollbackPointPhase.ImportPending)]
    [InlineData(GatewayRollbackPointPhase.Imported)]
    public async Task CancelRestore_RequiresSamePointResumeAfterDestructiveBoundary(
        GatewayRollbackPointPhase phase)
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        SetPointPhase(point.Point!.Id, phase);

        var result = manager.CancelRestore("OpenClawGateway", "local-gateway", point.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.ResumeRequired, result.State);
        Assert.Equal(phase, Assert.Single(manager.List()).Phase);
    }

    [Theory]
    [InlineData(GatewayRollbackPointPhase.UnregisterPending)]
    [InlineData(GatewayRollbackPointPhase.DistroUnregistered)]
    [InlineData(GatewayRollbackPointPhase.ImportPending)]
    [InlineData(GatewayRollbackPointPhase.Imported)]
    public async Task RestoreAsync_RejectsDifferentPointWhileDestructiveRestoreMustResume(
        GatewayRollbackPointPhase phase)
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var unresolved = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var other = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        SetPointPhase(unresolved.Point!.Id, phase);
        runner.ClearCalls();

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", other.Point!.Id, other.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.ResumeRequired, result.State);
        Assert.Equal(unresolved.Point.Id, result.Point!.Id);
        Assert.Empty(runner.Calls);

        var coordinatorResult = await new GatewayVersionAlignmentCoordinator(
                runner, RequiredVersion, manager)
            .RestoreAsync(EligiblePlan(), other.Point.Id, other.Point.Id);
        Assert.Equal(GatewayVersionAlignmentState.RestoreFailed, coordinatorResult.State);
        Assert.Equal(unresolved.Point.Id, coordinatorResult.RollbackPointId);
        Assert.Contains(unresolved.Point.Id, coordinatorResult.FailureSummary, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UpdateAsync_ImportedRestoreBlocksUntilRestoreHealthy()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var point = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        SetPointPhase(point.Point!.Id, GatewayRollbackPointPhase.Imported);
        runner.ClearCalls();
        var coordinator = new GatewayVersionAlignmentCoordinator(runner, RequiredVersion, manager);

        var blocked = await coordinator.UpdateAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.RecoveryAvailable, blocked.State);
        Assert.Empty(runner.Calls);

        manager.MarkRestoreHealthy(point.Point.Id);
        runner.EnqueueDistro(Ok(RequiredVersion));
        var unblocked = await coordinator.UpdateAsync(EligiblePlan());
        Assert.Equal(GatewayVersionAlignmentState.Aligned, unblocked.State);
    }

    [Fact]
    public async Task RestoreAsync_RecreatesOnlyRegistrationThenProvesVersionIdentityAndSynchronization()
    {
        var runner = new FakeWslCommandRunner();
        runner.Configuration = new(2, 0, 5);
        var current = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var manager = CreateManager(runner, () => current);
        var older = await CreateHealthyPointAsync(manager, runner, "2026.6.1");
        current += TimeSpan.FromDays(1);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        var createdPointId = created.Point!.Id;
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 201);
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        runner.EnqueueDistro(Ok(PreviousVersion));
        EnqueuePendingAttestation(runner, PreviousVersion);
        EnqueuePendingAttestation(runner, PreviousVersion);
        runner.BeforeUnregister = () =>
            Assert.Equal(
                GatewayRollbackPointPhase.UnregisterPending,
                manager.List().Single(point => point.Id == createdPointId).Phase);
        runner.BeforeImport = () =>
            Assert.Equal(
                GatewayRollbackPointPhase.ImportPending,
                manager.List().Single(point => point.Id == createdPointId).Phase);
        var syncCalls = 0;
        var coordinator = new GatewayVersionAlignmentCoordinator(
            runner, RequiredVersion, manager, (_, _) => { syncCalls++; return Task.CompletedTask; });

        var result = await coordinator.RestoreAsync(EligiblePlan(), created.Point!.Id, created.Point.Id);

        Assert.Equal(GatewayVersionAlignmentState.Restored, result.State);
        Assert.Equal(PreviousVersion, result.InstalledVersion);
        Assert.Equal(1, runner.UnregisterCalls);
        Assert.Contains(runner.Calls, call => call.Kind == "direct" && call.Arguments[0] == "--import-in-place");
        Assert.Equal(1, runner.ConfigureCalls);
        Assert.Equal(new WslDistroConfiguration(2, 0, 5), runner.Configuration);
        Assert.Equal(1, syncCalls);
        Assert.Equal(GatewayRollbackPointPhase.RestoreHealthy, Assert.Single(coordinator.ListRollbackPoints()).Phase);
        Assert.DoesNotContain(coordinator.ListRollbackPoints(), point => point.Id == older.Id);
    }

    [Fact]
    public async Task RestoreAsync_VerifiesCompleteLegacyAllowlistBeforeRestoreHealthy()
    {
        const string completeArray = """["system.run","system.which","camera.snap"]""";
        var runner = new FakeWslCommandRunner { Configuration = new(2, 0, 5) };
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, LegacyVersion);
        var created = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "historical-gateway-record", LegacyVersion, RequiredVersion);
        manager.RecordNodeCommandAllowSnapshot(created.Point!.Id, completeArray);
        manager.MarkUpdateInProgress(created.Point.Id);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 211);
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        runner.EnqueueDistro(Ok(LegacyVersion));
        runner.EnqueueDistro(Ok(completeArray));
        EnqueuePendingAttestation(runner, LegacyVersion);
        EnqueuePendingAttestation(runner, LegacyVersion);
        var coordinator = new GatewayVersionAlignmentCoordinator(
            runner, RequiredVersion, manager, (_, _) => Task.CompletedTask);

        var result = await coordinator.RestoreAsync(
            EligiblePlan() with { GatewayId = "replacement-gateway-record" },
            created.Point.Id,
            created.Point.Id);

        Assert.Equal(GatewayVersionAlignmentState.Restored, result.State);
        Assert.Equal(GatewayRollbackPointPhase.RestoreHealthy, Assert.Single(coordinator.ListRollbackPoints()).Phase);
        Assert.Contains(runner.Calls, call =>
            call.Kind == "distro" &&
            call.Arguments.SequenceEqual(ConfigCommand("get gateway.nodes.allowCommands --json")));
    }

    [Fact]
    public async Task RestoreAsync_BlocksRestoreHealthyWhenLiveIdentityChangesAfterSynchronization()
    {
        var runner = new FakeWslCommandRunner { Configuration = new(2, 0, 5) };
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 214);
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        runner.EnqueueDistro(Ok(PreviousVersion));
        runner.EnqueueDistro(Ok("fedcba9876543210fedcba9876543210"));
        var coordinator = new GatewayVersionAlignmentCoordinator(
            runner, RequiredVersion, manager, (_, _) => Task.CompletedTask);

        var result = await coordinator.RestoreAsync(
            EligiblePlan(), created.Point!.Id, created.Point.Id);

        Assert.Equal(GatewayVersionAlignmentState.RestoreVerificationFailed, result.State);
        Assert.Equal(GatewayRollbackPointPhase.Imported, Assert.Single(manager.List()).Phase);
    }

    [Fact]
    public async Task RestoreAsync_FailedImportKeepsReceiptAndRetriesWithoutSecondUnregister()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 202);
        runner.EnqueueDistro(Ok(MachineId));
        runner.ImportResults.Enqueue(new WslCommandResult(17, "", "secret"));

        var failed = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.ImportPending, failed.State);
        Assert.Equal(GatewayRollbackPointPhase.ImportPending, Assert.Single(manager.List()).Phase);
        Assert.Equal(1, runner.UnregisterCalls);

        runner.Distros = [];
        runner.ImportResults.Enqueue(Ok());
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        var retried = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "replacement-gateway-record", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.Restored, retried.State);
        Assert.Equal(1, runner.UnregisterCalls);
    }

    [Fact]
    public async Task RestoreAsync_TransientRetryFailurePreservesImportPendingAndNeverUnregistersAgain()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 207);
        runner.EnqueueDistro(Ok(MachineId));
        runner.ImportResults.Enqueue(new WslCommandResult(17, "", ""));
        var importFailed = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);
        Assert.Equal(GatewayRollbackOperationState.ImportPending, importFailed.State);

        runner.ListResult = new WslCommandResult(5, "", "");
        var probeFailed = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);
        Assert.Equal(GatewayRollbackOperationState.VerificationFailed, probeFailed.State);
        Assert.Equal(GatewayRollbackPointPhase.ImportPending, Assert.Single(manager.List()).Phase);

        runner.ListResult = null;
        runner.Distros = [new("OpenClawGateway", "Stopped", 2)];
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        var finalized = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.Restored, finalized.State);
        Assert.Equal(1, runner.UnregisterCalls);
    }

    [Fact]
    public async Task RestoreAsync_ImportPendingPathCollisionDoesNotRegressReceiptPhase()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 208);
        runner.EnqueueDistro(Ok(MachineId));
        runner.ImportResults.Enqueue(new WslCommandResult(17, "", ""));
        var importFailed = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);
        Assert.Equal(GatewayRollbackOperationState.ImportPending, importFailed.State);

        File.WriteAllText(Path.Combine(runner.InstallDirectory!, "unexpected.txt"), "fixture");
        var collision = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.InstallPathCollision, collision.State);
        Assert.Equal(GatewayRollbackPointPhase.ImportPending, Assert.Single(manager.List()).Phase);
        Assert.Equal(1, runner.UnregisterCalls);
    }

    [Fact]
    public async Task RestoreAsync_RecreatesInvalidStageFromVerifiedRollbackPoint()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 205);
        var stagePath = Path.Combine(
            _tempRoot, "gateway-rollback-staging", "OpenClawGateway", $"{created.Point!.Id}.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
        File.WriteAllBytes(stagePath, [1, 2, 3]);
        File.WriteAllBytes(stagePath + ".partial", [4, 5, 6]);
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));

        var restored = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.Restored, restored.State);
        Assert.False(File.Exists(stagePath + ".partial"));
        Assert.Equal(1, runner.UnregisterCalls);
    }

    [Fact]
    public async Task RestoreAsync_RejectsReparseBackedStageBeforeUnregister()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 209);
        var stagePath = Path.Combine(
            _tempRoot, "gateway-rollback-staging", "OpenClawGateway", $"{created.Point!.Id}.vhdx");
        var rollbackPath = Path.Combine(
            _tempRoot, "gateway-rollback-points", "OpenClawGateway", created.Point.Id, "rollback.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!);
        try
        {
            File.CreateSymbolicLink(stagePath, rollbackPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.InstallPathCollision, result.State);
        Assert.Equal("restore_stage_reparse_boundary", result.FailureCode);
        Assert.Equal(0, runner.UnregisterCalls);
    }

    [Fact]
    public async Task RestoreAsync_RevalidatesStageImmediatelyBeforeUnregister()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 212);
        runner.EnqueueDistro(Ok(MachineId));
        var stagePath = Path.Combine(
            _tempRoot, "gateway-rollback-staging", "OpenClawGateway", $"{created.Point!.Id}.vhdx");
        var registrationProbes = 0;
        runner.BeforeListRegistrations = () =>
        {
            registrationProbes++;
            if (registrationProbes == 2)
                WriteFakeVhd(stagePath, marker: 213);
        };

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.VerificationFailed, result.State);
        Assert.Equal("restore_stage_changed_before_unregister", result.FailureCode);
        Assert.Equal(0, runner.UnregisterCalls);
        Assert.Equal(GatewayRollbackPointPhase.RestoreStaged, Assert.Single(manager.List()).Phase);
    }

    [Fact]
    public async Task RestoreAsync_ReverifiesPromotedVhdImmediatelyBeforeImport()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 210);
        runner.EnqueueDistro(Ok(MachineId));
        var registrationProbes = 0;
        runner.BeforeListRegistrations = () =>
        {
            registrationProbes++;
            if (registrationProbes == 3)
                WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 211);
        };

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.VerificationFailed, result.State);
        Assert.Equal("restore_import_vhd_mismatch", result.FailureCode);
        Assert.DoesNotContain(runner.Calls, call =>
            call.Kind == "direct" && call.Arguments.Count > 0 && call.Arguments[0] == "--import-in-place");
        Assert.Equal(GatewayRollbackPointPhase.ImportPending, Assert.Single(manager.List()).Phase);
    }

    [Fact]
    public async Task RestoreAsync_FailsRetryableWhenRegistrationReadbackDoesNotMatch()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 206);
        runner.EnqueueDistro(Ok(MachineId));
        runner.IgnoreConfigurationWrites = true;

        var failed = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.ImportPending, failed.State);
        Assert.Equal("restored_registration_mismatch", failed.FailureCode);
        Assert.Equal(GatewayRollbackPointPhase.ImportPending, Assert.Single(manager.List()).Phase);
    }

    [Fact]
    public async Task RestoreAsync_ImportPendingSameNameWithDifferentIdentityFailsClosed()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 203);
        runner.EnqueueDistro(Ok(MachineId));
        runner.ImportResults.Enqueue(new WslCommandResult(17, "", ""));
        await manager.RestoreExplicitAsync("OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);

        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        runner.EnqueueDistro(Ok("fedcba9876543210fedcba9876543210"));
        var collision = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.SameNameCollision, collision.State);
        Assert.Equal(1, runner.UnregisterCalls);
    }

    [Fact]
    public async Task RestoreAsync_DistroEnumerationFailureStopsBeforeUnregister()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.ListResult = new WslCommandResult(5, "", "private detail");

        var result = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.VerificationFailed, result.State);
        Assert.Equal("distro_list_failed", result.FailureCode);
        Assert.Equal(0, runner.UnregisterCalls);
    }

    [Fact]
    public async Task RestoreAsync_ValidatesRegisteredBasePathAndContentsBeforeUnregister()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        runner.ClearCalls();
        runner.Distros = [new("OpenClawGateway", "Running", 2)];
        WriteFakeVhd(Path.Combine(runner.InstallDirectory!, "ext4.vhdx"), marker: 204);
        runner.RegisteredBasePath = Path.Combine(_tempRoot, "unexpected-registration-root");

        var wrongBasePath = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point!.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.InstallPathCollision, wrongBasePath.State);
        Assert.Equal("registration_base_path_mismatch", wrongBasePath.FailureCode);
        Assert.Equal(0, runner.UnregisterCalls);

        runner.RegisteredBasePath = runner.InstallDirectory;
        File.WriteAllText(Path.Combine(runner.InstallDirectory!, "unexpected.txt"), "not private data");
        var unexpectedContents = await manager.RestoreExplicitAsync(
            "OpenClawGateway", "local-gateway", created.Point.Id, created.Point.Id);

        Assert.Equal(GatewayRollbackOperationState.InstallPathCollision, unexpectedContents.State);
        Assert.Equal("install_path_not_exclusive", unexpectedContents.FailureCode);
        Assert.Equal(0, runner.UnregisterCalls);
    }

    [Fact]
    public async Task Cleanup_CombinesCountFloorAndAgeAndNeverDeletesNewestKnownGood()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var current = now - TimeSpan.FromDays(40);
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner, () => current);

        var old = await CreateHealthyPointAsync(manager, runner, "2026.5.1");
        current = now - TimeSpan.FromDays(10);
        var recent = await CreateHealthyPointAsync(manager, runner, "2026.6.1");
        current = now;
        var latest = await CreateHealthyPointAsync(manager, runner, "2026.7.1");

        var firstCleanup = await manager.CleanupAsync(new(1, TimeSpan.FromDays(30)));
        Assert.Equal(1, firstCleanup);
        Assert.Equal([latest.Id, recent.Id], manager.List().Select(point => point.Id));

        var secondCleanup = await manager.CleanupAsync(new(1, null));
        Assert.Equal(1, secondCleanup);
        Assert.Equal(latest.Id, Assert.Single(manager.List()).Id);
        Assert.NotEqual(old.Id, latest.Id);
    }

    [Fact]
    public async Task Cleanup_IndefiniteRetentionNeverDeletesAndPendingPointIsNotEligibleForCleanup()
    {
        var runner = new FakeWslCommandRunner();
        var manager = CreateManager(runner);
        var first = await CreateHealthyPointAsync(manager, runner, "2026.6.1");
        EnqueueCreateAttestation(runner, "2026.7.1");
        var pending = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", "2026.7.1", RequiredVersion);

        Assert.Equal(0, await manager.CleanupAsync(new(-1, null)));
        Assert.Equal(2, manager.List().Count);
        Assert.Equal(1, await manager.CleanupAsync(new(1, null)));
        Assert.DoesNotContain(manager.List(), point => point.Id == first.Id);
        Assert.Equal(pending.Point!.Id, Assert.Single(manager.List()).Id);
    }

    [Fact]
    public async Task Cleanup_RemovesCrashLeftExportPartialForUnverifiedPoint()
    {
        var runner = new FakeWslCommandRunner
        {
            ExportResult = new WslCommandResult(9, "", "")
        };
        var manager = CreateManager(runner);
        EnqueueCreateAttestation(runner, PreviousVersion);
        var failed = await manager.CreateVerifiedAsync(
            "OpenClawGateway", "local-gateway", PreviousVersion, RequiredVersion);
        var partialPath = Path.Combine(
            _tempRoot,
            "gateway-rollback-points",
            "OpenClawGateway",
            failed.Point!.Id,
            "rollback.vhdx.partial");
        WriteFakeVhd(partialPath);

        await manager.CleanupAsync(GatewayRollbackRetentionPolicy.Default);

        Assert.False(File.Exists(partialPath));
        Assert.Equal(failed.Point.Id, Assert.Single(manager.List()).Id);
    }

    [Fact]
    public async Task ConcurrentOperation_ReturnsBusyWithoutDuplicateMutation()
    {
        var runner = new FakeWslCommandRunner();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.EnqueueDistro(async _ => { started.SetResult(); await release.Task; return Ok(RequiredVersion); });
        var coordinator = CreateCoordinator(runner);

        var first = coordinator.ProbeAsync(EligiblePlan());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = await coordinator.UpdateAsync(EligiblePlan());

        Assert.Equal(GatewayVersionAlignmentState.Busy, duplicate.State);
        release.SetResult();
        Assert.Equal(GatewayVersionAlignmentState.Aligned, (await first).State);
    }

    private void SetPointPhase(string pointId, GatewayRollbackPointPhase phase)
    {
        var manifestPath = Path.Combine(
            _tempRoot,
            "gateway-rollback-points",
            "OpenClawGateway",
            pointId,
            "manifest.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var manifest = JsonSerializer.Deserialize<GatewayRollbackPointManifest>(
            File.ReadAllText(manifestPath),
            options) ?? throw new InvalidOperationException("Fixture manifest could not be read.");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest with { Phase = phase, UpdatedAtUtc = DateTimeOffset.UtcNow }, options));
    }

    private IReadOnlyList<GatewayRollbackPointManifest> ReadOwnedManifests()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        };
        return Directory.EnumerateFiles(
                Path.Combine(_tempRoot, "gateway-rollback-points", "OpenClawGateway"),
                "manifest.json",
                SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<GatewayRollbackPointManifest>(
                File.ReadAllText(path), options)!)
            .ToArray();
    }

    private async Task<GatewayRollbackPointInfo> CreateHealthyPointAsync(
        GatewayRollbackPointManager manager,
        FakeWslCommandRunner runner,
        string version)
    {
        EnqueueCreateAttestation(runner, version);
        var created = await manager.CreateVerifiedAsync("OpenClawGateway", "local-gateway", version, RequiredVersion);
        manager.MarkPostUpdateHealthy(created.Point!.Id);
        return manager.List().Single(point => point.Id == created.Point.Id);
    }

    private GatewayVersionAlignmentCoordinator CreateCoordinator(
        FakeWslCommandRunner runner,
        Func<string, CancellationToken, Task>? synchronize = null) =>
        new(runner, RequiredVersion, CreateManager(runner), synchronize);

    private GatewayRollbackPointManager CreateManager(
        FakeWslCommandRunner runner,
        Func<DateTimeOffset>? clock = null)
    {
        runner.InstallDirectory = Path.Combine(_tempRoot, "wsl", "OpenClawGateway");
        return new(runner, _tempRoot, "OpenClawGateway", clock);
    }

    private static GatewayHostAccessPlan EligiblePlan() => new(
        "local-gateway",
        GatewayTerminalTarget.Wsl,
        "OpenClawGateway",
        null,
        null,
        true,
        "Open terminal",
        "Open terminal",
        null);

    private static string UpdateJson(string version) =>
        $$"""{"status":"ok","mode":"npm","after":{"version":"{{version}}"},"steps":[],"durationMs":1}""";

    private static WslCommandResult Ok(string output = "") => new(0, output, "");

    private static void WriteFakeVhd(string path, byte marker = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. "vhdxfile"u8.ToArray(), marker, .. Enumerable.Range(0, 64).Select(value => (byte)value)]);
    }

    private static IReadOnlyList<string> ProbeCommand() =>
        ["bash", "-lc", $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw --version"];

    private static IReadOnlyList<string> IdentityCommand() => ["sh", "-lc", "cat /etc/machine-id"];
    private static IReadOnlyList<string> DefaultUserCommand() =>
        ["sh", "-lc", "printf '%s\\n%s\\n' \"$(id -un)\" \"$(id -u)\""];

    private static void EnqueueCreateAttestation(FakeWslCommandRunner runner, string version)
    {
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok(version));
        runner.EnqueueDistro(Ok(version));
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
    }

    private static void EnqueuePendingAttestation(FakeWslCommandRunner runner, string exactVersion)
    {
        runner.EnqueueDistro(Ok(MachineId));
        runner.EnqueueDistro(Ok("openclaw\n1000"));
        runner.EnqueueDistro(Ok(exactVersion));
    }

    private static IReadOnlyList<string> UpdateCommand(string version) =>
        ["bash", "-lc", $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw update --tag {version} --yes --json"];

    private static IReadOnlyList<string> ConfigCommand(string operation) =>
        ["bash", "-lc", $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw config {operation}"];

    private static void AssertDistro(CommandCall call, IReadOnlyList<string> command)
    {
        Assert.Equal("distro", call.Kind);
        Assert.Equal("OpenClawGateway", call.DistroName);
        Assert.Equal(command, call.Arguments);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record CommandCall(string Kind, string? DistroName, IReadOnlyList<string> Arguments);

    private sealed class FakeWslCommandRunner : IWslCommandRunner
    {
        private readonly ConcurrentQueue<Func<CancellationToken, Task<WslCommandResult>>> _distroResponses = new();
        public List<CommandCall> Calls { get; } = [];
        public IReadOnlyList<WslDistroInfo> Distros { get; set; } =
            [new("OpenClawGateway", "Running", 2)];
        public Queue<WslCommandResult> ImportResults { get; } = new();
        public WslCommandResult ExportResult { get; set; } = Ok();
        public WslCommandResult? ListResult { get; set; }
        public string? InstallDirectory { get; set; }
        public string? RegisteredBasePath { get; set; }
        public WslDistroConfiguration Configuration { get; set; } = new(2, 1000, 7);
        public int UnregisterCalls { get; private set; }
        public int ConfigureCalls { get; private set; }
        public bool IgnoreConfigurationWrites { get; set; }
        public Action? BeforeUnregister { get; set; }
        public Action? BeforeImport { get; set; }
        public Action? BeforeListRegistrations { get; set; }

        public void EnqueueDistro(WslCommandResult result) => EnqueueDistro(_ => Task.FromResult(result));
        public void EnqueueDistro(Func<CancellationToken, Task<WslCommandResult>> result) => _distroResponses.Enqueue(result);
        public void ClearCalls() => Calls.Clear();

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(new("direct", null, [.. arguments]));
            if (arguments.Count >= 4 && arguments[0] == "--export")
            {
                if (ExportResult.Success)
                    WriteFakeVhd(arguments[2]);
                return Task.FromResult(ExportResult);
            }
            if (arguments.Count == 2 && arguments[0] == "--list" && arguments[1] == "--quiet")
            {
                if (ListResult is not null)
                    return Task.FromResult(ListResult);
                var output = string.Join('\n', Distros.Select(distro => distro.Name));
                return Task.FromResult(Ok(output));
            }
            if (arguments.Count >= 3 && arguments[0] == "--import-in-place")
            {
                BeforeImport?.Invoke();
                var result = ImportResults.Count > 0 ? ImportResults.Dequeue() : Ok();
                if (result.Success)
                {
                    Distros = [new(arguments[1], "Stopped", 2)];
                    Configuration = new(2, 0, 7);
                }
                return Task.FromResult(result);
            }
            throw new InvalidOperationException($"Unexpected direct WSL command: {string.Join(' ', arguments)}");
        }

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add(new("list", null, []));
            return Task.FromResult(Distros);
        }

        public Task<IReadOnlyList<WslDistroRegistration>> ListRegistrationsAsync(CancellationToken cancellationToken = default)
        {
            BeforeListRegistrations?.Invoke();
            Calls.Add(new("registrations", null, []));
            IReadOnlyList<WslDistroRegistration> registrations = Distros
                .Select(distro => new WslDistroRegistration(
                    distro.Name,
                    RegisteredBasePath ?? InstallDirectory ?? string.Empty))
                .ToArray();
            return Task.FromResult(registrations);
        }

        public Task<WslDistroConfigurationResult> GetDistroConfigurationAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new("get-configuration", name, []));
            return Task.FromResult(new WslDistroConfigurationResult(true, Configuration));
        }

        public Task<WslCommandResult> ConfigureDistroRegistrationAsync(
            string name,
            uint defaultUid,
            uint flags,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new("configure", name, [defaultUid.ToString(), flags.ToString()]));
            ConfigureCalls++;
            if (!IgnoreConfigurationWrites)
                Configuration = Configuration with { DefaultUid = defaultUid, Flags = flags };
            return Task.FromResult(Ok());
        }

        public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            Calls.Add(new("terminate", name, []));
            Distros = Distros.Select(distro => string.Equals(distro.Name, name, StringComparison.Ordinal)
                ? distro with { State = "Stopped" }
                : distro).ToArray();
            return Task.FromResult(Ok());
        }

        public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default)
        {
            BeforeUnregister?.Invoke();
            Calls.Add(new("unregister", name, []));
            UnregisterCalls++;
            Distros = Distros.Where(distro => !string.Equals(distro.Name, name, StringComparison.Ordinal)).ToArray();
            if (InstallDirectory is not null && Directory.Exists(InstallDirectory))
                Directory.Delete(InstallDirectory, recursive: true);
            return Task.FromResult(Ok());
        }

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(new("distro", name, [.. command]));
            if (!_distroResponses.TryDequeue(out var response))
                throw new InvalidOperationException($"No response queued for: {string.Join(' ', command)}");
            return response(cancellationToken);
        }
    }
}
