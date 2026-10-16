using System.Text.Json;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Ui;

namespace OpenClaw.Tray.UITests.Journeys;

[Collection("Gateway fixture UI")]
public sealed class ContractJourneyPreflightTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "ContractPreflight")]
    public async Task SharedProfileMustBeQuietBeforeAnyAppLaunch()
    {
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_CONTRACT_REPORTS_DIR")
            ?? throw new InvalidOperationException("The contract runner must supply an evidence directory.");
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("The contract evidence directory must be absolute.");
        var roots = new Dictionary<string, string>
        {
            ["ApplicationData"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray"),
            ["LocalApplicationData"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenClawTray")
        };
        IReadOnlyList<ProfileMetadataChange> changes = [];
        bool? unchanged = null;
        try
        {
            var before = ProfileMetadataSnapshot.Capture(roots);
            // A defined observation window, not a delay used to synchronize a UI action.
            await Task.Delay(TimeSpan.FromSeconds(10));
            changes = ProfileMetadataSnapshot.Compare(before, ProfileMetadataSnapshot.Capture(roots));
            unchanged = changes.Count == 0;
            await File.WriteAllTextAsync(Path.Combine(directory, "preflight.json"),
                JsonSerializer.Serialize(new { ObservationSeconds = 10, IsolationUnchanged = unchanged, Changes = changes },
                    new JsonSerializerOptions { WriteIndented = true }));
            if (unchanged != true)
                throw new InvalidOperationException(
                    "Preflight blocked: shared-profile metadata changed before any fixture app was launched. " +
                    "Pause the shared writers before qualification; no app behavior was tested.");
        }
        catch (Exception failure)
        {
            foreach (var caseId in new[]
            {
                "C01.NavigationPreservesPreferences", "C04.PreferenceOffToOnSurvivesRestart",
                "C04.PreferenceOnToOffSurvivesRestart", "C01.McpOnlyStartsWithoutGatewayCredential",
                "C01.UnavailableGatewayStillOpensWorkspace", "C03.SendStreamsIntoSelectedConversation",
                "C03.CancelStopsOutputAndAllowsNextSend",
                "C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer"
            })
            {
                var report = new ContractJourneyReport(caseId, null, null, unchanged, null, failure.Message, changes,
                    IsolationAuditAttempted: true);
                await File.WriteAllTextAsync(Path.Combine(directory, $"{caseId}.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            throw;
        }
    }
}
