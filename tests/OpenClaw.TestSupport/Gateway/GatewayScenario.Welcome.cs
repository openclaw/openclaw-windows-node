using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

public sealed partial class GatewayScenario
{
    public const string WelcomeRecoveryName = "welcome-send-recovery";
    public const string WelcomeRecoverySessionKey = "agent:research:fixture-welcome";
    public const string WelcomeRecoveryTitle = "Fixture: welcome recovery";

    public static GatewayScenario CreateWelcomeRecovery()
    {
        var browse = CreateBrowse();
        // Keep the host's populated-catalog preflight, but give Research only the
        // empty target so recent chats do not replace its welcome suggestions.
        var sessions = browse._sessions.Where(session => session.AgentId == "main")
            .Append(CreateSession(WelcomeRecoverySessionKey, WelcomeRecoveryTitle,
                "research", "research", 3, []))
            .Append(CreateSession("agent:main:fixture-comparison", "Fixture: comparison",
                "main", "browse", 4, ["Synthetic comparison transcript."]))
            .ToArray();
        var reads = new Dictionary<string, JsonElement>(browse._reads, StringComparer.Ordinal)
        {
            ["controlUi.welcome.get"] = JsonSerializer.SerializeToElement(new
            {
                version = 1,
                requestedLocale = "en-US",
                resolvedLocale = "en",
                hint = "Synthetic welcome recovery proof. No real provider is contacted.",
                recentChatsLabel = "Recent chats",
                suggestions = new[]
                {
                    new { id = "fixture-first", displayText = "Try the first suggestion", sendText = "Fixture welcome: first suggestion" },
                    new { id = "fixture-second", displayText = "Try a different suggestion", sendText = "Fixture welcome: second suggestion" },
                }
            }),
        };
        return new(sessions, reads, interactive: true, scenarioName: WelcomeRecoveryName);
    }
}
