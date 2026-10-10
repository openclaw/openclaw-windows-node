using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UpdateCheckPipelineTests
{
    [Fact]
    public void Classify_TrustedOrdinaryMarker_DoesNotScanReleaseProse()
    {
        var release = Candidate(
            "v2026.9.1",
            body:
                $"{UpdateReleasePolicy.OrdinaryMarker}\n" +
                "Security team acknowledgements include CVE-2026-1234 and GHSA-abcd-1234-5678.");

        var classification = UpdateReleasePolicy.Classify(release);

        Assert.Equal(ReleaseSecurityClassification.Ordinary, classification);
    }

    [Fact]
    public void Classify_TrustedSecurityMarker_IsSecurity()
    {
        var release = Candidate(
            "v2026.9.1",
            body: UpdateReleasePolicy.SecurityMarker);

        var classification = UpdateReleasePolicy.Classify(release);

        Assert.Equal(ReleaseSecurityClassification.Security, classification);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("<!-- openclaw-update: routine -->", true)]
    [InlineData("<!-- openclaw-update: ordinary -->", false)]
    [InlineData(
        "<!-- openclaw-update: ordinary --><!-- openclaw-update: ordinary -->",
        true)]
    [InlineData(
        "<!-- openclaw-update: ordinary --><!-- openclaw-update: security-critical -->",
        true)]
    [InlineData(
        "<!-- openclaw-update: security-critical --><!-- openclaw-update: security-critical -->",
        true)]
    [InlineData(
        "<!-- openclaw-update: ordinary --><!-- openclaw-update: security-critical --><!-- openclaw-update: security-critical -->",
        true)]
    [InlineData(
        "<!-- openclaw-update: ordinary --><!-- openclaw-update: security-critica -->",
        true)]
    [InlineData(
        "<!-- openclaw-update: ordinary --><!-- openclaw-update:security-critical -->",
        true)]
    [InlineData(
        "<!--openclaw-update: ordinary--><!-- openclaw-update: unknown -->",
        true)]
    public void Classify_IncompleteMalformedOrUntrustedMetadata_IsUnverified(
        string? body,
        bool trusted)
    {
        var release = Candidate(
            "v2026.9.1",
            body: body,
            hasTrustedMetadata: trusted);

        var classification = UpdateReleasePolicy.Classify(release);

        Assert.Equal(ReleaseSecurityClassification.Unverified, classification);
    }

    [Fact]
    public void ShouldSuppress_OnlyTrustedOrdinaryExtendedStableUpdate()
    {
        var ordinary = Outcome(UpdateReleasePolicy.OrdinaryMarker);
        var security = Outcome(UpdateReleasePolicy.SecurityMarker);
        var unverified = Outcome(body: null);

        Assert.True(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            ordinary));
        Assert.True(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "EXTENDED-STABLE" },
            ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "stable" },
            ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable " },
            ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(null, ordinary));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            security));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            unverified));
        Assert.False(CompanionUpdateSuppressionPolicy.ShouldSuppress(
            new GatewayUpdateStatus { EffectiveChannel = "extended-stable" },
            ordinary with { UpdateFound = false }));
    }

    [Theory]
    [InlineData(true, true, "operator.admin", true)]
    [InlineData(false, true, "operator.admin", false)]
    [InlineData(true, false, "operator.admin", false)]
    [InlineData(true, true, "operator.read", false)]
    [InlineData(true, true, "", false)]
    public void GatewayStatusLookup_RequiresAuthenticatedAdminClient(
        bool hasHandshake,
        bool connected,
        string scope,
        bool expected)
    {
        var scopes = string.IsNullOrEmpty(scope) ? [] : new[] { scope };

        Assert.Equal(
            expected,
            GatewayUpdateStatusLookup.CanQuery(
                hasHandshake,
                connected,
                scopes));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("gateway")]
    public async Task GatewayStatusLookup_ErrorsRemainUnavailable(string failureKind)
    {
        Exception error = failureKind == "transport"
            ? new IOException("connection closed")
            : new InvalidOperationException("unauthorized update.status");
        Exception? observed = null;

        var status = await GatewayUpdateStatusLookup.TryGetAsync(
            () => Task.FromException<GatewayUpdateStatus?>(error),
            ex => observed = ex);

        Assert.Null(status);
        Assert.Same(error, observed);
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
    public async Task CheckAsync_WhenOrdinaryCheckFindsUpdate_DoesNotInspectFallbacks()
    {
        var releasesEnumerated = false;
        var selectedRelease = Candidate(
            "v2026.7.1-2",
            body: UpdateReleasePolicy.OrdinaryMarker);
        var boundary = new FakeUpdateCheckBoundary(
            checkForUpdates: () => true,
            selectedRelease: selectedRelease,
            releasesFactory: () =>
            {
                releasesEnumerated = true;
                return [Candidate("v2026.7.1-2")];
            });

        var result = await UpdateCheckPipeline.CheckAsync(boundary, "2026.7.1");

        Assert.True(result.UpdateFound);
        Assert.Same(selectedRelease, result.SelectedRelease);
        Assert.Null(result.ActivatedFallbackTag);
        Assert.False(releasesEnumerated);
    }

    private static UpdateCheckOutcome Outcome(string? body) =>
        new(
            UpdateFound: true,
            SelectedRelease: Candidate("v2026.9.1", body: body));

    private static UpdateReleaseCandidate Candidate(
        string tagName,
        string? body = null,
        bool hasTrustedMetadata = true,
        Func<bool>? hasCompatibleAsset = null,
        Action? activate = null) =>
        new(
            tagName,
            body,
            hasTrustedMetadata,
            Draft: false,
            Prerelease: false,
            Published: true,
            hasCompatibleAsset ?? (() => true),
            activate ?? (() => { }));

    private sealed class FakeUpdateCheckBoundary : IUpdateCheckBoundary
    {
        private readonly Func<bool> _checkForUpdates;
        private readonly Func<IEnumerable<UpdateReleaseCandidate>> _releasesFactory;
        private readonly UpdateReleaseCandidate? _selectedRelease;

        public FakeUpdateCheckBoundary(
            Func<bool> checkForUpdates,
            UpdateReleaseCandidate? selectedRelease = null,
            IEnumerable<UpdateReleaseCandidate>? releases = null,
            Func<IEnumerable<UpdateReleaseCandidate>>? releasesFactory = null)
        {
            _checkForUpdates = checkForUpdates;
            _selectedRelease = selectedRelease;
            _releasesFactory = releasesFactory ?? (() => releases ?? []);
        }

        public Task<bool> CheckForUpdatesAsync() => Task.FromResult(_checkForUpdates());

        public UpdateReleaseCandidate? GetSelectedRelease() => _selectedRelease;

        public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates() =>
            _releasesFactory();
    }
}
