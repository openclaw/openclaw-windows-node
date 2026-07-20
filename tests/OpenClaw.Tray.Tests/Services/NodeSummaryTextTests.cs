using System;
using System.Collections.Generic;
using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public class NodeSummaryTextTests
{
    private static GatewayNodeInfo Node(string nodeId, bool online, string? displayName = null) =>
        new() { NodeId = nodeId, IsOnline = online, DisplayName = displayName ?? string.Empty };

    [Fact]
    public void Build_NoNodes_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, NodeSummaryText.Build(Array.Empty<GatewayNodeInfo>()));
    }

    [Fact]
    public void Build_OnlineNode_StartsWithOnlineState()
    {
        var line = NodeSummaryText.Build(new[] { Node("abcdef123456", online: true, "Desk PC") });
        Assert.StartsWith("online: Desk PC (", line);
    }

    [Fact]
    public void Build_OfflineNode_StartsWithOfflineState()
    {
        var line = NodeSummaryText.Build(new[] { Node("abcdef123456", online: false, "Desk PC") });
        Assert.StartsWith("offline: Desk PC (", line);
    }

    [Fact]
    public void Build_BlankDisplayName_FallsBackToShortId()
    {
        var node = Node("abcdefghijklmnopqrstuvwxyz", online: true, displayName: "   ");
        var line = NodeSummaryText.Build(new[] { node });
        Assert.StartsWith($"online: {node.ShortId} ({node.ShortId})", line);
    }

    [Fact]
    public void Build_MultipleNodes_OneLinePerNodeJoinedByNewline()
    {
        var summary = NodeSummaryText.Build(new[]
        {
            Node("node-one-1234", online: true, "One"),
            Node("node-two-5678", online: false, "Two"),
        });

        var lines = summary.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("online: One (", lines[0]);
        Assert.StartsWith("offline: Two (", lines[1]);
    }
}
