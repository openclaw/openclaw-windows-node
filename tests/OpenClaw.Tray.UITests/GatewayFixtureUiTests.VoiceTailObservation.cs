// Bounded, content-free observations for the historical voice fact. Reuse its
// existing history response and native predicate reads; never probe UIA after failure.
using System.Text.Json;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const int VoiceTailExpectedEntryCount = 30;

    private sealed record VoiceTailHistoryObservation(DateTimeOffset ObservedAtUtc, bool ReadSucceeded,
        bool? ComposeTargetMatches = null, bool? HistoryLoaded = null, int? ReturnedEntryCount = null,
        int? ExpectedContentMatchCount = null, bool? FinalEntryMatches = null);

    private sealed class VoiceTailNativeObservation
    {
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public bool? ComposerPresent { get; set; }
        public bool? SelectedThreadMatches { get; set; }
        public bool? RenderConsumedHistory { get; set; }
        public bool? TimelinePresent { get; set; }
        public bool? FinalElementPresent { get; set; }
        public bool? FinalOffscreen { get; set; }
        public bool? FinalHasPositiveBounds { get; set; }
        public bool? FinalWithinVerticalViewport { get; set; }
        public bool? FinalOverlapsHorizontalViewport { get; set; }
        public bool? FinalVisible { get; set; }
    }

    // One latest history projection and one latest poll, not a growing trace.
    // BeginPoll resets unknowns so a short circuit never reports stale visibility.
    private sealed class VoiceTailObservation
    {
        public VoiceTailHistoryObservation? History { get; private set; }
        public VoiceTailNativeObservation? LastPoll { get; private set; }
        public int PollCount { get; private set; }
        public bool WaitSucceeded { get; set; }

        public void ObserveHistory(JsonElement snapshot) => History = ProjectVoiceHistory(snapshot);

        public VoiceTailNativeObservation BeginPoll()
        {
            PollCount++;
            return LastPoll = new VoiceTailNativeObservation();
        }

        public async Task WriteAsync(string artifactsDirectory)
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(artifactsDirectory, "fractional-voice-tail-observation.json"),
                    JsonSerializer.Serialize(new
                    {
                        History, PollCount, LastPoll, WaitSucceeded, WaitFinishedAtUtc = DateTimeOffset.UtcNow
                    }));
            }
            catch (Exception)
            {
                // Best effort, like the local production warning. Do not replace
                // the original wait failure or skip any post-success assertion.
            }
        }
    }

    private static VoiceTailHistoryObservation ProjectVoiceHistory(JsonElement snapshot)
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            var timeline = snapshot.GetProperty("selectedTimeline");
            var entries = timeline.GetProperty("entries");
            var count = entries.GetArrayLength();
            var matches = 0;
            // app.chat.snapshot returns at most the last 30 entries. This is a
            // response observation, not proof of the full model or mounted rows.
            for (var index = 0; index < Math.Min(count, VoiceTailExpectedEntryCount); index++)
            {
                var expected = index == VoiceTailExpectedEntryCount - 1 ? GatewayScenario.VoiceFinalSentinel : $"Fixture voice history message {index + 1:D2}.";
                if (entries[index].GetProperty("Text").GetString() == expected) matches++;
            }
            return new(at, true,
                snapshot.GetProperty("composeTarget").GetProperty("sessionKey").GetString() == GatewayScenario.VoiceSessionKey,
                timeline.GetProperty("historyLoaded").GetBoolean(), count, matches,
                count > 0 && entries[count - 1].GetProperty("Text").GetString() == GatewayScenario.VoiceFinalSentinel);
        }
        catch (Exception)
        {
            return new(at, false);
        }
    }

    [Theory]
    [InlineData(true, 30, true)]
    [InlineData(false, 29, true)]
    [InlineData(true, 29, false)]
    public void VoiceTailObservation_ProjectsCountsWithoutRetainingContent(bool firstMatches, int expectedMatches, bool finalMatches)
    {
        var entries = Enumerable.Range(1, 30).Select(index => new
        {
            Text = index == 1 && !firstMatches || index == 30 && !finalMatches ? "private-message-marker"
                : index == 30 ? GatewayScenario.VoiceFinalSentinel : $"Fixture voice history message {index:D2}."
        }).ToArray();
        var snapshot = JsonSerializer.SerializeToElement(new
        {
            composeTarget = new { sessionKey = GatewayScenario.VoiceSessionKey },
            selectedTimeline = new { historyLoaded = true, entries },
            unrelated = "private-field-marker"
        });
        var observation = ProjectVoiceHistory(snapshot);
        Assert.True(observation.ReadSucceeded);
        Assert.True(observation.ComposeTargetMatches);
        Assert.True(observation.HistoryLoaded);
        Assert.Equal(30, observation.ReturnedEntryCount);
        Assert.Equal(expectedMatches, observation.ExpectedContentMatchCount);
        Assert.Equal(finalMatches, observation.FinalEntryMatches);
        var json = JsonSerializer.Serialize(observation);
        Assert.DoesNotContain("private-", json);
        Assert.DoesNotContain(GatewayScenario.VoiceSessionKey, json);
        Assert.DoesNotContain(GatewayScenario.VoiceFinalSentinel, json);
        Assert.DoesNotContain("Fixture voice history message", json);
    }

    [Fact]
    public void VoiceTailObservation_MissingDataAndSkippedNativePredicatesStayUnknown()
    {
        var history = ProjectVoiceHistory(JsonSerializer.SerializeToElement(new { selectedTimeline = (object?)null }));
        Assert.False(history.ReadSucceeded);
        Assert.Null(history.ReturnedEntryCount);
        Assert.Null(history.FinalEntryMatches);
        var observation = new VoiceTailObservation();
        var previous = observation.BeginPoll();
        previous.FinalVisible = true;
        var current = observation.BeginPoll();
        current.RenderConsumedHistory = false;
        Assert.Equal(2, observation.PollCount);
        Assert.Same(current, observation.LastPoll);
        Assert.Null(current.FinalVisible);
        Assert.Null(current.TimelinePresent);
        Assert.Null(current.FinalOffscreen);
    }
}
