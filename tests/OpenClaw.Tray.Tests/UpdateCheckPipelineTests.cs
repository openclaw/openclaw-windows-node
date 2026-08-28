using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UpdateCheckPipelineTests
{
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
        var boundary = new FakeUpdateCheckBoundary(
            checkForUpdates: () => true,
            releasesFactory: () =>
            {
                releasesEnumerated = true;
                return [Candidate("v2026.7.1-2")];
            });

        var result = await UpdateCheckPipeline.CheckAsync(boundary, "2026.7.1");

        Assert.True(result.UpdateFound);
        Assert.Null(result.ActivatedFallbackTag);
        Assert.False(releasesEnumerated);
    }

    private static UpdateReleaseCandidate Candidate(
        string tagName,
        Func<bool>? hasCompatibleAsset = null,
        Action? activate = null) =>
        new(
            tagName,
            Draft: false,
            Prerelease: false,
            Published: true,
            hasCompatibleAsset ?? (() => true),
            activate ?? (() => { }));

    private sealed class FakeUpdateCheckBoundary : IUpdateCheckBoundary
    {
        private readonly Func<bool> _checkForUpdates;
        private readonly Func<IEnumerable<UpdateReleaseCandidate>> _releasesFactory;

        public FakeUpdateCheckBoundary(
            Func<bool> checkForUpdates,
            IEnumerable<UpdateReleaseCandidate>? releases = null,
            Func<IEnumerable<UpdateReleaseCandidate>>? releasesFactory = null)
        {
            _checkForUpdates = checkForUpdates;
            _releasesFactory = releasesFactory ?? (() => releases ?? []);
        }

        public Task<bool> CheckForUpdatesAsync() => Task.FromResult(_checkForUpdates());

        public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates() =>
            _releasesFactory();
    }
}
