namespace OpenClaw.Tray.Tests;

public sealed class ChatPermissionRenderingContractTests
{
    [Fact]
    public void PendingPermission_RendersGatewayProvidedActionsIncludingAllowAlways()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatTimeline.cs");

        Assert.Contains("ChatPermissionActionKeys.NormalizeActions(entry.PermissionActions)", timeline);
        Assert.Contains("ChatPermissionActionKeys.AllowAlways => allowAlwaysLabel", timeline);
        Assert.Contains("onResponse?.Invoke(requestId, action)", timeline);
    }

    [Fact]
    public void DecidedPermission_RendersAllowedAlwaysAsAlwaysAllowed()
    {
        var timeline = Read("src", "OpenClaw.Tray.WinUI", "Chat", "ChatTimeline.cs");

        Assert.Contains("ChatPermissionDecision.AllowedAlways", timeline);
        Assert.Contains("Chat_Permission_DecisionAlwaysAllowed", timeline);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));
}
