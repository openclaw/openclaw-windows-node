using OpenClaw.Shared;
using OpenClaw.Connection;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UpdateCheckPipelineTests
{
    [Fact]
    public void AutomaticUpdateCheck_StartupPlanWaitsOnlyForOperatorHandshake()
    {
        Assert.Equal(
            AutomaticUpdateCheckStartupPlan.AwaitOperatorHandshakeWithDeadline,
            AutomaticUpdateCheckPolicy.PlanStartup(
                StartupGatewayConnectKind.Operator));
        Assert.Equal(
            AutomaticUpdateCheckStartupPlan.Immediate,
            AutomaticUpdateCheckPolicy.PlanStartup(
                StartupGatewayConnectKind.NodeOnly));
        Assert.Equal(
            AutomaticUpdateCheckStartupPlan.Immediate,
            AutomaticUpdateCheckPolicy.PlanStartup(
                StartupGatewayConnectKind.None));
    }

    [Theory]
    [InlineData(RoleConnectionState.PairingRequired, true)]
    [InlineData(RoleConnectionState.Error, false)]
    [InlineData(RoleConnectionState.Connecting, false)]
    public void AutomaticUpdateCheck_FallsBackOnlyForTerminalGatewayState(
        RoleConnectionState state,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutomaticUpdateCheckPolicy.IsGatewayStatusTerminallyUnavailable(
                state));
    }

    [Fact]
    public async Task CheckAsync_WhenOrdinaryCheckDeclinesCorrection_ActivatesCompatibleFallback()
    {
        var events = new List<string>();
        var incompatibleActivated = false;
        var correctionActivated = false;
        var boundary = new FakeUpdateCheckBoundary(
            checkForUpdates: () =>
            {
                events.Add("ordinary-check-declined");
                return false;
            },
            releases:
            [
                Candidate(
                    "v2026.7.1-3",
                    hasCompatibleAsset: () =>
                    {
                        events.Add("incompatible-asset-rejected");
                        return false;
                    },
                    activate: () => incompatibleActivated = true),
                Candidate(
                    "v2026.7.1-2",
                    hasCompatibleAsset: () =>
                    {
                        events.Add("compatible-asset-selected");
                        return true;
                    },
                    activate: () =>
                    {
                        events.Add("correction-activated");
                        correctionActivated = true;
                    })
            ]);

        var result = await UpdateCheckPipeline.CheckAsync(boundary, "2026.7.1");

        Assert.True(result.UpdateFound);
        Assert.Equal("v2026.7.1-2", result.ActivatedFallbackTag);
        Assert.False(result.IsSecurityCritical);
        Assert.False(incompatibleActivated);
        Assert.True(correctionActivated);
        Assert.Equal(
            [
                "ordinary-check-declined",
                "incompatible-asset-rejected",
                "compatible-asset-selected",
                "correction-activated"
            ],
            events);
    }

    [Fact]
    public async Task CheckAsync_WhenOrdinaryCheckFindsUpdate_InspectsEligibleReleasesForSecurity()
    {
        var releasesEnumerated = false;
        var boundary = new FakeUpdateCheckBoundary(
            checkForUpdates: () => true,
            releasesFactory: () =>
            {
                releasesEnumerated = true;
                return
                [
                    Candidate("v2026.8.2", body: "Ordinary maintenance update"),
                    Candidate("v2026.8.1", body: "Fixes CVE-2026-1234")
                ];
            });

        var result = await UpdateCheckPipeline.CheckAsync(boundary, "2026.8.0");

        Assert.True(result.UpdateFound);
        Assert.Null(result.ActivatedFallbackTag);
        Assert.True(result.IsSecurityCritical);
        Assert.True(releasesEnumerated);
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            result));
    }

    [Fact]
    public async Task CheckAsync_WhenSecurityReleaseIsIneligible_AllowsOrdinarySuppression()
    {
        var boundary = new FakeUpdateCheckBoundary(
            checkForUpdates: () => true,
            releases:
            [
                Candidate(
                    "v2026.8.1",
                    body: "Fixes GHSA-abcd-1234-5678",
                    hasCompatibleAsset: () => false)
            ]);

        var result = await UpdateCheckPipeline.CheckAsync(boundary, "2026.8.0");

        Assert.True(result.UpdateFound);
        Assert.False(result.IsSecurityCritical);
        Assert.True(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            result));
    }

    [Fact]
    public async Task CheckAsync_WhenReleaseHistoryIsTruncated_FailsSafe()
    {
        var boundary = new FakeUpdateCheckBoundary(
            checkForUpdates: () => true,
            releases:
            [
                Candidate("v2026.8.2", body: "Ordinary maintenance update")
            ],
            releaseHistoryComplete: false);

        var result = await UpdateCheckPipeline.CheckAsync(boundary, "2026.8.0");

        Assert.True(result.IsSecurityCritical);
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            result));
    }

    [Fact]
    public async Task CheckAsync_FallbackScanPreservesInterveningSecurityRelease()
    {
        var ordinaryActivated = false;
        var boundary = new FakeUpdateCheckBoundary(
            checkForUpdates: () => false,
            releases:
            [
                Candidate(
                    "v2026.8.2",
                    body: "Ordinary maintenance update",
                    activate: () => ordinaryActivated = true),
                Candidate("v2026.8.1", body: "Security update")
            ]);

        var result = await UpdateCheckPipeline.CheckAsync(boundary, "2026.8.0");

        Assert.True(ordinaryActivated);
        Assert.Equal("v2026.8.2", result.ActivatedFallbackTag);
        Assert.True(result.IsSecurityCritical);
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            result));
    }

    [Theory]
    [InlineData("Security update", null)]
    [InlineData(null, "Fixes CVE-2026-1234")]
    [InlineData(null, "Advisory GHSA-abcd-1234-5678")]
    [InlineData(null, "<!-- openclaw-update: security-critical -->")]
    public void IsSecurityCritical_RecognizesReleaseSafetySignals(
        string? name,
        string? body)
    {
        Assert.True(UpdateReleasePolicy.IsSecurityCritical(
            Candidate("v2026.8.1", name: name, body: body)));
    }

    [Fact]
    public void IsSecurityCritical_TreatsUnavailableReleaseMetadataAsFailSafe()
    {
        Assert.True(UpdateReleasePolicy.IsSecurityCritical(null));
    }

    [Fact]
    public void ShouldSuppress_OnlyExtendedStableOrdinaryUpdates()
    {
        var ordinary = new UpdateCheckOutcome(
            UpdateFound: true,
            SelectedRelease: Candidate("v2026.8.1"));
        var security = new UpdateCheckOutcome(
            UpdateFound: true,
            SelectedRelease: Candidate(
                "v2026.8.1",
                body: "Security fix for GHSA-abcd-1234-5678"));

        Assert.True(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "stable" },
            ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(null, ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = null },
            ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended_stable" },
            ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            security));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            new UpdateCheckOutcome(UpdateFound: true, SelectedRelease: null)));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(TimeoutException))]
    public async Task GatewayStatusLookup_FailsSafeForUnavailableStatus(
        Type exceptionType)
    {
        var observed = new List<Exception>();

        var result = await GatewayUpdateStatusLookup.TryGetAsync(
            () => Task.FromException<GatewayUpdateStatus?>(
                (Exception)Activator.CreateInstance(exceptionType, "unavailable")!),
            observed.Add);

        Assert.Null(result);
        Assert.IsType(exceptionType, Assert.Single(observed));
    }

    private static UpdateReleaseCandidate Candidate(
        string tagName,
        string? name = null,
        string? body = null,
        Func<bool>? hasCompatibleAsset = null,
        Action? activate = null) =>
        new(
            tagName,
            name,
            body,
            Draft: false,
            Prerelease: false,
            Published: true,
            hasCompatibleAsset ?? (() => true),
            activate ?? (() => { }));

    private sealed class FakeUpdateCheckBoundary : IUpdateCheckBoundary
    {
        private readonly Func<bool> _checkForUpdates;
        private readonly Func<IEnumerable<UpdateReleaseCandidate>> _releasesFactory;
        private readonly Func<UpdateReleaseCandidate?> _selectedRelease;
        private readonly bool _releaseHistoryComplete;

        public FakeUpdateCheckBoundary(
            Func<bool> checkForUpdates,
            IEnumerable<UpdateReleaseCandidate>? releases = null,
            Func<IEnumerable<UpdateReleaseCandidate>>? releasesFactory = null,
            Func<UpdateReleaseCandidate?>? selectedRelease = null,
            bool releaseHistoryComplete = true)
        {
            _checkForUpdates = checkForUpdates;
            _releasesFactory = releasesFactory ?? (() => releases ?? []);
            _selectedRelease = selectedRelease ??
                (() => Candidate("v2026.8.1"));
            _releaseHistoryComplete = releaseHistoryComplete;
        }

        public Task<bool> CheckForUpdatesAsync() => Task.FromResult(_checkForUpdates());

        public UpdateReleaseCandidate? GetSelectedRelease() => _selectedRelease();

        public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates() =>
            _releasesFactory();

        public bool IsReleaseHistoryComplete(string currentVersion) =>
            _releaseHistoryComplete;
    }
}
