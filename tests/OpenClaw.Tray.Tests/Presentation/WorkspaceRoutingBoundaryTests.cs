using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mcp;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class WorkspaceRoutingBoundaryTests
{
    [Theory]
    [InlineData("workspace:unknown", "Unknown Workspace route")]
    [InlineData("workspace:agents:unknown", "Unknown Workspace route")]
    [InlineData("workspace:", "Unknown Workspace route")]
    [InlineData("agentevents", "Unknown companion Settings route")]
    [InlineData("agent:main:agentevents", "Unknown companion Settings route")]
    [InlineData("unknown", "Unknown companion Settings route")]
    public async Task UnknownRoute_ReturnsMcpErrorBeforeCreatingOrForwardingAnyWindow(
        string route,
        string expectedError)
    {
        var workspaceCalls = 0;
        var companionCalls = 0;
        var capability = new AppCapability(NullLogger.Instance)
        {
            NavigateHandler = page =>
            {
                try
                {
                    WorkspaceNavigation.Dispatch(page, _ => workspaceCalls++, _ => companionCalls++, _ => { });
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
        Assert.Contains(expectedError, result.GetProperty("content")[0].GetProperty("text").GetString());
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
    [InlineData("general", false)]
    [InlineData("command-center", false)]
    [InlineData("settings", false)]
    [InlineData("sessions", false)]
    [InlineData("cron", false)]
    [InlineData("agent:main:workspace", false)]
    public void KnownRoutes_DispatchToExactlyOneWindowBoundary(string? route, bool workspace)
    {
        var workspaces = new List<WorkspaceDestination>();
        var companions = new List<string>();
        var dashboards = new List<string>();
        WorkspaceNavigation.Dispatch(route, workspaces.Add, companions.Add, dashboards.Add);
        Assert.Equal(workspace ? 1 : 0, workspaces.Count);
        Assert.Equal(workspace ? 0 : 1, companions.Count);
        Assert.Empty(dashboards);
        if (!workspace)
            Assert.Equal(route, Assert.Single(companions));
    }

    [Theory]
    [InlineData("channels")]
    [InlineData("activity")]
    public void RetiredChannelRoutes_OpenDashboardInsteadOfAnEmptySettingsPage(string route)
    {
        var workspaces = new List<WorkspaceDestination>();
        var companions = new List<string>();
        var dashboards = new List<string>();

        WorkspaceNavigation.Dispatch(route, workspaces.Add, companions.Add, dashboards.Add);

        Assert.Empty(workspaces);
        Assert.Empty(companions);
        Assert.Equal("channels", Assert.Single(dashboards));
    }

    [Fact]
    public async Task AboutAdmission_KeepsItsOwnIdentityAndPropagatesTheHostResult()
    {
        string? section = null;
        var host = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = WorkspaceNavigation.DispatchAsync("about",
            _ => throw new Xunit.Sdk.XunitException("About reached Workspace."),
            tag => { section = WorkspaceNavigation.NormalizeSettingsTag(tag, "main"); return host.Task; },
            _ => throw new Xunit.Sdk.XunitException("About reached Dashboard."));

        Assert.Equal("about", section);
        Assert.Equal(HubPageKind.About, HubPageRegistry.ResolvePage(section));
        Assert.False(admission.IsCompleted);
        host.SetResult(true);
        Assert.True(await admission);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("workspace:unknown")]
    public void UnknownAdmissionRoute_DoesNotInvokeAnyHost(string route)
    {
        var calls = 0;
        Assert.Throws<ArgumentException>(() =>
        {
            _ = WorkspaceNavigation.DispatchAsync(route,
                _ => { calls++; return Task.FromResult(true); },
                _ => { calls++; return Task.FromResult(true); },
                _ => { calls++; return Task.FromResult(true); });
        });
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NativeEntryPoints_UseTheValidatedBoundaryBeforeWindowSideEffects()
    {
        // retirement_condition: replace with native window tests when these adapters can run in Tray.Tests.
        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var manager = File.ReadAllText(Path.Combine(root, "Services", "WindowManager.cs"));
        var workspace = File.ReadAllText(Path.Combine(root, "Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("WorkspaceNavigation.DispatchAsync(navigateTo, destination =>", manager);
        Assert.Matches(@"tag => Task.FromResult\(ShowWorkspace\(\s*WorkspaceDestination.Settings\(""settings""\), activate, preserveCurrent: false, settingsTag: tag\)\)", manager);
        Assert.Contains("path => ((IAppCommands)Application.Current).TryOpenDashboardAsync(path)", manager);
        Assert.DoesNotContain("ShowCompanion", manager);
        Assert.Contains("WorkspaceNavigation.Dispatch(tag, _ => WorkspaceRouteRequested?.Invoke(tag), NavigateCompanion,",
            File.ReadAllText(Path.Combine(root, "Controls", "SettingsShellView.xaml.cs")));
        Assert.Contains("WorkspaceNavigation.Dispatch(", workspace);
    }
}
