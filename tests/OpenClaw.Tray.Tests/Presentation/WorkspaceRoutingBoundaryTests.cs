using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mcp;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceRoutingBoundaryTests
{
    [Theory]
    [InlineData("workspace:unknown")]
    [InlineData("workspace:agents:unknown")]
    [InlineData("workspace:")]
    public async Task UnknownRoute_ReturnsMcpErrorBeforeCreatingOrForwardingAnyWindow(string route)
    {
        var workspaceCalls = 0;
        var companionCalls = 0;
        var capability = new AppCapability(NullLogger.Instance)
        {
            NavigateHandler = page =>
            {
                try
                {
                    WorkspaceNavigation.Dispatch(page, _ => workspaceCalls++, _ => companionCalls++);
                    return Task.FromResult<object?>(new { navigated = true, page });
                }
                catch (ArgumentException ex)
                {
                    return Task.FromResult<object?>(new { navigated = false, error = ex.Message });
                }
            }
        };
        var bridge = new McpToolBridge(() => [capability], NullLogger.Instance);
        var response = await bridge.HandleRequestAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "app.navigate", arguments = new { page = route } }
        }));

        using var json = JsonDocument.Parse(response!);
        var result = json.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("Unknown Workspace route", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, workspaceCalls);
        Assert.Equal(0, companionCalls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("hub", true)]
    [InlineData("chat", true)]
    [InlineData("home", true)]
    [InlineData("workspace", true)]
    [InlineData("workspace:home", true)]
    [InlineData("workspace:notifications", true)]
    [InlineData("workspace:agents", true)]
    [InlineData("workspace:sessions", true)]
    [InlineData("settings", false)]
    [InlineData("sessions", false)]
    [InlineData("cron", false)]
    [InlineData("agent:main:workspace", false)]
    public void KnownRoutes_DispatchToExactlyOneWindowBoundary(string? route, bool workspace)
    {
        var workspaces = new List<WorkspaceDestination>();
        var companions = new List<string>();
        WorkspaceNavigation.Dispatch(route, workspaces.Add, companions.Add);
        Assert.Equal(workspace ? 1 : 0, workspaces.Count);
        Assert.Equal(workspace ? 0 : 1, companions.Count);
        if (!workspace)
            Assert.Equal(route, Assert.Single(companions));
    }

    [Fact]
    public void NativeEntryPoints_UseTheValidatedBoundaryBeforeWindowSideEffects()
    {
        // retirement_condition: replace with native window tests when these adapters can run in Tray.Tests.
        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var manager = File.ReadAllText(Path.Combine(root, "Services", "WindowManager.cs"));
        var hub = File.ReadAllText(Path.Combine(root, "Windows", "HubWindow.xaml.cs"));
        Assert.Contains("WorkspaceNavigation.Dispatch(navigateTo, destination =>", manager);
        Assert.Contains("tag => ShowCompanion(tag, activate)", manager);
        Assert.Contains("WorkspaceNavigation.Dispatch(tag, _ => CurrentApp.ShowHub(tag), NavigateCompanion)", hub);
    }
}
