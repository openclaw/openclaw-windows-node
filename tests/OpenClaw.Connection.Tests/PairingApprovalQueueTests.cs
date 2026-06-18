using System;
using System.Collections.Generic;
using System.Linq;
using OpenClaw.Connection;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Connection.Tests;

public class PendingApprovalTests
{
    [Fact]
    public void FromDevice_MapsAllFields_AndNormalizesIp()
    {
        var req = new DevicePairingRequest
        {
            RequestId = "req-1",
            DeviceId = "dev-1",
            DisplayName = "Bedroom iPad",
            Platform = "iPadOS",
            Role = "operator",
            Scopes = new[] { "operator.read", "", "operator.admin" },
            RemoteIp = "::ffff:192.168.1.50",
            IsRepair = true,
            Ts = 1234,
        };

        var a = PendingApproval.FromDevice(req);

        Assert.Equal(PairingApprovalKind.Device, a.Kind);
        Assert.Equal("req-1", a.RequestId);
        Assert.Equal("dev-1", a.DeviceId);
        Assert.Equal("Bedroom iPad", a.DisplayName);
        Assert.Equal("iPadOS", a.Platform);
        Assert.Equal("operator", a.Role);
        Assert.Equal(new[] { "operator.read", "operator.admin" }, a.Scopes);
        Assert.Equal("192.168.1.50", a.RemoteIp);
        Assert.True(a.IsRepair);
        Assert.Equal(1234, a.Ts);
    }

    [Fact]
    public void FromDevice_DefaultsRoleToOperator_WhenMissing()
    {
        var a = PendingApproval.FromDevice(new DevicePairingRequest { RequestId = "r", DeviceId = "d" });
        Assert.Equal("operator", a.Role);
    }

    [Fact]
    public void FromNode_MapsFields_AndHasNoScopes()
    {
        var req = new PairingRequest
        {
            RequestId = "req-2",
            NodeId = "node-9",
            DisplayName = "Studio PC",
            Platform = "windows",
            Version = "1.2.3",
            RemoteIp = "10.0.0.4",
            Ts = 99,
        };

        var a = PendingApproval.FromNode(req);

        Assert.Equal(PairingApprovalKind.Node, a.Kind);
        Assert.Equal("node-9", a.DeviceId);
        Assert.Equal("node", a.Role);
        Assert.Empty(a.Scopes);
        Assert.Equal("1.2.3", a.Version);
    }

    [Fact]
    public void DecisionId_PrefersRequestId_FallsBackToDeviceId()
    {
        Assert.Equal("req", PendingApproval.FromDevice(new DevicePairingRequest { RequestId = "req", DeviceId = "dev" }).DecisionId);
        Assert.Equal("dev", PendingApproval.FromDevice(new DevicePairingRequest { RequestId = "", DeviceId = "dev" }).DecisionId);
    }

    [Fact]
    public void Key_IsKindScoped()
    {
        var device = PendingApproval.FromDevice(new DevicePairingRequest { RequestId = "same", DeviceId = "x" });
        var node = PendingApproval.FromNode(new PairingRequest { RequestId = "same", NodeId = "y" });
        Assert.NotEqual(device.Key, node.Key);
    }

    [Fact]
    public void IsActionable_FalseWhenNoIds()
    {
        Assert.False(PendingApproval.FromNode(new PairingRequest { RequestId = "", NodeId = "" }).IsActionable);
        Assert.True(PendingApproval.FromDevice(new DevicePairingRequest { RequestId = "", DeviceId = "d" }).IsActionable);
    }
}

public class PairingApprovalQueueTests
{
    private static DevicePairingListInfo Devices(params DevicePairingRequest[] reqs) =>
        new() { Pending = reqs.ToList() };

    private static PairingListInfo Nodes(params PairingRequest[] reqs) =>
        new() { Pending = reqs.ToList() };

    private static DevicePairingRequest Device(string id, double ts = 0, string[]? scopes = null) =>
        new() { RequestId = id, DeviceId = $"d-{id}", DisplayName = id, Ts = ts, Scopes = scopes };

    private static PairingRequest Node(string id, string? nodeId = null, double ts = 0) =>
        new() { RequestId = id, NodeId = nodeId ?? $"n-{id}", DisplayName = id, Ts = ts };

    [Fact]
    public void Reconcile_SurfacesNewDeviceAndNodeRequests()
    {
        var q = new PairingApprovalQueue();
        var delta = q.Reconcile(Devices(Device("a")), Nodes(Node("b")));

        Assert.Equal(2, delta.Added.Count);
        Assert.Empty(delta.ResolvedKeys);
        Assert.Equal(2, delta.Current.Count);
    }

    [Fact]
    public void Reconcile_DoesNotReAddKnownRequest()
    {
        var q = new PairingApprovalQueue();
        q.Reconcile(Devices(Device("a")), null);
        var second = q.Reconcile(Devices(Device("a")), null);

        Assert.Empty(second.Added);
        Assert.Single(second.Current);
    }

    [Fact]
    public void Reconcile_ReportsResolvedWhenRequestLeaves()
    {
        var q = new PairingApprovalQueue();
        q.Reconcile(Devices(Device("a")), null);
        var delta = q.Reconcile(Devices(), null);

        Assert.Single(delta.ResolvedKeys);
        Assert.Equal("Device:a", delta.ResolvedKeys[0]);
        Assert.Empty(delta.Current);
    }

    [Fact]
    public void Reconcile_FiltersOwnNodeRequest()
    {
        var q = new PairingApprovalQueue();
        var delta = q.Reconcile(null, Nodes(Node("self", nodeId: "MY-NODE"), Node("other", nodeId: "OTHER")), ownNodeDeviceId: "my-node");

        Assert.Single(delta.Added);
        Assert.Equal("other", delta.Added[0].RequestId);
    }

    [Fact]
    public void Reconcile_DropsUnactionableEntries()
    {
        var q = new PairingApprovalQueue();
        var delta = q.Reconcile(null, Nodes(new PairingRequest { RequestId = "", NodeId = "" }));
        Assert.Empty(delta.Added);
    }

    [Fact]
    public void Reconcile_DedupsDuplicateIds()
    {
        var q = new PairingApprovalQueue();
        var delta = q.Reconcile(Devices(Device("a"), Device("a")), null);
        Assert.Single(delta.Added);
    }

    [Fact]
    public void Reconcile_OrdersCurrentByTimestamp()
    {
        var q = new PairingApprovalQueue();
        var delta = q.Reconcile(Devices(Device("late", ts: 200), Device("early", ts: 100)), null);
        Assert.Equal("early", delta.Current[0].RequestId);
        Assert.Equal("late", delta.Current[1].RequestId);
    }

    [Fact]
    public void MarkSubmitted_SuppressesFromActionableSet()
    {
        var q = new PairingApprovalQueue();
        var first = q.Reconcile(Devices(Device("a")), null);
        q.MarkSubmitted(first.Added[0], approved: true, nowMs: 1000);

        var second = q.Reconcile(Devices(Device("a")), null, nowMs: 1100); // gateway still echoes it
        Assert.Empty(second.Added);
        Assert.Empty(second.Current); // submitted => not actionable
        Assert.Empty(second.ConfirmedDecisions); // not yet resolved
    }

    [Fact]
    public void MarkSubmitted_ConfirmsDecisionWhenRequestLeavesList()
    {
        var q = new PairingApprovalQueue();
        var first = q.Reconcile(Devices(Device("a")), null);
        q.MarkSubmitted(first.Added[0], approved: true, nowMs: 1000);

        // Gateway accepted -> request drops from the pending list -> confirmed.
        var delta = q.Reconcile(Devices(), null, nowMs: 1200);
        Assert.Single(delta.ConfirmedDecisions);
        Assert.True(delta.ConfirmedDecisions[0].Approved);
        Assert.Equal("a", delta.ConfirmedDecisions[0].Approval.RequestId);
        Assert.True(delta.HasChanges);
    }

    [Fact]
    public void MarkSubmitted_ConfirmsRejectionWithCorrectFlag()
    {
        var q = new PairingApprovalQueue();
        var first = q.Reconcile(Devices(Device("a")), null);
        q.MarkSubmitted(first.Added[0], approved: false, nowMs: 0);

        var delta = q.Reconcile(Devices(), null, nowMs: 100);
        Assert.Single(delta.ConfirmedDecisions);
        Assert.False(delta.ConfirmedDecisions[0].Approved);
    }

    [Fact]
    public void MarkSubmitted_ReSurfacesAfterTimeoutWhenGatewayNeverActs()
    {
        var q = new PairingApprovalQueue();
        var first = q.Reconcile(Devices(Device("a")), null, nowMs: 0);
        q.MarkSubmitted(first.Added[0], approved: true, nowMs: 0);

        // Still echoed, before timeout: suppressed, not re-added.
        var before = q.Reconcile(Devices(Device("a")), null, nowMs: PairingApprovalQueue.SubmissionResolveTimeoutMs - 1);
        Assert.Empty(before.Added);
        Assert.Empty(before.Current);
        Assert.Empty(before.ConfirmedDecisions);

        // Past timeout, still echoed: re-surfaced for retry, NOT confirmed.
        var after = q.Reconcile(Devices(Device("a")), null, nowMs: PairingApprovalQueue.SubmissionResolveTimeoutMs + 1);
        Assert.Single(after.Added);
        Assert.Single(after.Current);
        Assert.Empty(after.ConfirmedDecisions);
    }

    [Fact]
    public void Reconcile_DefersNodeRequestsWhenAsked()
    {
        var q = new PairingApprovalQueue();
        var deferred = q.Reconcile(Devices(Device("dev")), Nodes(Node("n1")), deferNodeRequests: true);
        Assert.Single(deferred.Added); // only the device
        Assert.Equal("dev", deferred.Added[0].RequestId);

        // Once no longer deferred, the node request surfaces.
        var resumed = q.Reconcile(Devices(Device("dev")), Nodes(Node("n1")), deferNodeRequests: false);
        Assert.Single(resumed.Added);
        Assert.Equal(PairingApprovalKind.Node, resumed.Added[0].Kind);
    }

    [Fact]
    public void Find_ReturnsTrackedButNotSubmitted()
    {
        var q = new PairingApprovalQueue();
        var delta = q.Reconcile(Devices(Device("a")), null);
        var approval = delta.Added[0];

        Assert.NotNull(q.Find(approval.Key));
        q.MarkSubmitted(approval, approved: true, nowMs: 0);
        Assert.Null(q.Find(approval.Key));
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var q = new PairingApprovalQueue();
        q.Reconcile(Devices(Device("a")), null);
        q.Reset();
        Assert.True(q.IsEmpty);

        // After reset the same request surfaces again.
        var delta = q.Reconcile(Devices(Device("a")), null);
        Assert.Single(delta.Added);
    }
}

public class PairingScopeDescriptionsTests
{
    [Theory]
    [InlineData("operator.admin", "Admin access")]
    [InlineData("operator.read", "Read OpenClaw data")]
    [InlineData("operator.write", "Send messages and make changes")]
    [InlineData("operator.approvals", "Manage approvals")]
    [InlineData("operator.pairing", "Pair and repair devices")]
    [InlineData("operator.talk.secrets", "Use Talk credentials")]
    [InlineData("OPERATOR.ADMIN", "Admin access")]
    public void Describe_ReturnsFriendlyLabel_ForKnownScope(string scope, string expected)
    {
        Assert.Equal(expected, PairingScopeDescriptions.Describe(scope));
    }

    [Fact]
    public void Describe_ReturnsRawScope_ForUnknown()
    {
        Assert.Equal("custom.scope", PairingScopeDescriptions.Describe(" custom.scope "));
    }

    [Fact]
    public void Describe_ReturnsEmpty_ForBlank()
    {
        Assert.Equal(string.Empty, PairingScopeDescriptions.Describe("   "));
    }

    [Fact]
    public void DescribeAll_DropsBlanksAndDeduplicates_PreservingOrder()
    {
        var result = PairingScopeDescriptions.DescribeAll(
            new[] { "operator.admin", "", "operator.admin", "operator.read" });
        Assert.Equal(new[] { "Admin access", "Read OpenClaw data" }, result);
    }

    [Fact]
    public void DescribeAll_ReturnsEmpty_ForNull()
    {
        Assert.Empty(PairingScopeDescriptions.DescribeAll(null));
    }

    [Theory]
    [InlineData("operator.admin", true)]
    [InlineData("unknown.scope", false)]
    [InlineData("", false)]
    public void IsKnown_ReflectsMapMembership(string scope, bool expected)
    {
        Assert.Equal(expected, PairingScopeDescriptions.IsKnown(scope));
    }
}
