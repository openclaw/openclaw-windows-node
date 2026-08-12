using System.Reflection;
using System.Runtime.CompilerServices;

namespace OpenClaw.Shared.Tests;

public sealed class CodexCatalogPolicySurfaceTests
{
    [Fact]
    public void RawCodexAppServerSurfaces_AreInternalToThePermissionOwner()
    {
        var assembly = typeof(SettingsData).Assembly;
        var resolver = RequiredType(assembly, "OpenClaw.Shared.Codex.CodexExecutableResolver");
        var client = RequiredType(assembly, "OpenClaw.Shared.Codex.CodexAppServerClient");
        var catalog = RequiredType(assembly, "OpenClaw.Shared.Codex.CodexSessionCatalogService");
        var capability = RequiredType(assembly, "OpenClaw.Shared.Capabilities.CodexSessionCapability");
        var mcpBridge = RequiredType(assembly, "OpenClaw.Shared.Mcp.McpToolBridge");

        Assert.DoesNotContain(
            assembly.ExportedTypes,
            type => type == resolver || type == client || type == catalog || type == capability);
        Assert.DoesNotContain(
            client.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            method => method.Name.StartsWith("Connect", StringComparison.Ordinal) && method.IsPublic);
        Assert.DoesNotContain(
            client.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            method => method.Name is "ListThreadsAsync" or "ListThreadTurnsAsync" && method.IsPublic);
        Assert.DoesNotContain(
            mcpBridge.GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == "HandleRequestAsync");

        var friends = assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0])
            .ToArray();
        Assert.Equal(
            [
                "OpenClaw.Shared.Tests",
                "OpenClaw.Tray.Tests",
                "OpenClaw.Tray.WinUI",
                "OpenClaw.WinNode.Cli.Tests",
            ],
            friends.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void HistoryCatalog_RemainsAnInternalCapabilityOperation()
    {
        var assembly = typeof(SettingsData).Assembly;
        var capability = RequiredType(assembly, "OpenClaw.Shared.Capabilities.CodexSessionCapability");
        var catalog = RequiredType(assembly, "OpenClaw.Shared.Codex.CodexSessionCatalogService");

        Assert.NotNull(capability.GetField(
            "ThreadsHistoryListCommand",
            BindingFlags.Public | BindingFlags.Static));
        Assert.DoesNotContain(
            catalog.GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == "ListThreadHistoryAsync");
    }

    private static Type RequiredType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)!;
}
