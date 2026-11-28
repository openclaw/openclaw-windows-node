using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

// Environment is process-global. The existing collection excludes parallel readers.
[Collection(OpenClawTrayDataDirEnvironmentCollection.Name)]
public sealed class Issue1642SelectionTests
{
    public static IEnumerable<object[]> Cases() => Issue1642Fixtures.Load().Cases
        .Select(item => new object[] { item.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PipelineOnly_DesiredSelection(string name)
    {
        var input = Issue1642Fixtures.Load().Cases.Single(item => item.Name == name);
        using var environment = new EnvironmentScope("OPENCLAW_UPDATE_CHANNEL", input.Channel);
        var boundary = new RecordedReleaseBoundary(input);

        // This is the REAL production selector, not a copied version comparator.
        // At the pinned base this owner has NO channel input and ignores the env var.
        // These are desired-contract probes, NOT caller/backend opt-in proof.
        var outcome = await UpdateCheckPipeline.CheckAsync(boundary, input.Current);

        Assert.Equal(input.ExpectedSelectedTag is not null, outcome.UpdateFound);
        Assert.Equal(input.ExpectedSelectedTag, outcome.SelectedRelease?.TagName);
        if (input.ExpectedSelectedTag is null)
        {
            Assert.Empty(boundary.Activated);
            return;
        }

        var expected = boundary.Candidates.Single(item => item.TagName == input.ExpectedSelectedTag);
        Assert.Same(expected, outcome.SelectedRelease);
        if (input.BackendFound && input.BackendSelectedTag == input.ExpectedSelectedTag)
        {
            Assert.Empty(boundary.Activated);
        }
        else
        {
            Assert.Equal(input.ExpectedSelectedTag, outcome.ActivatedFallbackTag);
            Assert.Equal(new[] { input.ExpectedSelectedTag }, boundary.Activated);
            Assert.Equal(input.Releases.Single(item => item.Tag == input.ExpectedSelectedTag).Asset.Id,
                boundary.ActivatedAssetId);
        }
        if (input.ExpectedClassification is { } classification)
            Assert.Equal(classification, UpdateReleasePolicy.Classify(outcome.SelectedRelease).ToString());
        if (input.ExpectedSuppressed is { } suppressed)
        {
            Assert.Equal(suppressed, CompanionUpdateSuppressionPolicy.ShouldSuppress(
                new GatewayUpdateStatus { EffectiveChannel = input.GatewayChannel }, outcome));
        }
    }

    [Fact]
    public async Task Stable_BackendSuccessRetainsFastPath()
    {
        using var environment = new EnvironmentScope("OPENCLAW_UPDATE_CHANNEL", "stable");
        var selected = new UpdateReleaseCandidate("v2026.9.8-1", "ordinary", true,
            false, false, true, () => true, () => throw new InvalidOperationException("Already selected"));
        var boundary = new StableBoundary(selected);
        var outcome = await UpdateCheckPipeline.CheckAsync(boundary, "2026.9.5");
        Assert.True(outcome.UpdateFound);
        Assert.Same(selected, outcome.SelectedRelease);
        Assert.Null(outcome.ActivatedFallbackTag);
    }

    private sealed class StableBoundary(UpdateReleaseCandidate selected) : IUpdateCheckBoundary
    {
        public Task<bool> CheckForUpdatesAsync() => Task.FromResult(true);
        public UpdateReleaseCandidate GetSelectedRelease() => selected;
        public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates() =>
            throw new InvalidOperationException("Stable backend success must not enumerate fallback candidates.");
    }

    private sealed class RecordedReleaseBoundary : IUpdateCheckBoundary
    {
        private readonly Issue1642Case _input;
        public UpdateReleaseCandidate[] Candidates { get; }
        public List<string> Activated { get; } = [];
        public string? ActivatedAssetId { get; private set; }

        public RecordedReleaseBoundary(Issue1642Case input)
        {
            _input = input;
            // Map external metadata only. No sorting, version comparison or eligibility logic.
            Candidates = input.Releases.Select(release => new UpdateReleaseCandidate(
                release.Tag, release.Body, release.TrustedMetadata, release.Draft,
                release.Prerelease, release.Published, () => release.Asset.Compatible,
                () => { Activated.Add(release.Tag); ActivatedAssetId = release.Asset.Id; })).ToArray();
        }

        public Task<bool> CheckForUpdatesAsync() => Task.FromResult(_input.BackendFound);
        public UpdateReleaseCandidate? GetSelectedRelease() => Candidates.SingleOrDefault(
            release => release.TagName == _input.BackendSelectedTag);
        public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates() => Candidates;
    }
}

internal static class Issue1642Fixtures
{
    public static Issue1642Corpus Load() => JsonSerializer.Deserialize<Issue1642Corpus>(
        File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "tests",
            "OpenClaw.Tray.Tests", "Fixtures", "issue1642.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Missing #1642 corpus");
}

public sealed class Issue1642Corpus
{
    public Issue1642Case[] Cases { get; init; } = [];
}
public sealed class Issue1642Case
{
    public string Name { get; init; } = "";
    public string Current { get; init; } = "";
    public string? Channel { get; init; }
    public Issue1642Release[] Releases { get; init; } = [];
    public string? ExpectedSelectedTag { get; init; }
    public bool BackendFound { get; init; }
    public string? BackendSelectedTag { get; init; }
    public string? GatewayChannel { get; init; }
    public bool? ExpectedSuppressed { get; init; }
    public string? ExpectedClassification { get; init; }
}
public sealed class Issue1642Release
{
    public string Tag { get; init; } = "";
    public string Body { get; init; } = "";
    public bool Prerelease { get; init; }
    public bool Draft { get; init; }
    public bool Published { get; init; }
    public bool TrustedMetadata { get; init; }
    public Issue1642Asset Asset { get; init; } = new();
}
public sealed class Issue1642Asset
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Compatible { get; init; }
    public string Url { get; init; } = "";
}
