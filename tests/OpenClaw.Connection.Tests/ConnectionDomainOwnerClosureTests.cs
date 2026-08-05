namespace OpenClaw.Connection.Tests;

public sealed class ConnectionDomainOwnerClosureTests
{
    [Fact]
    public void GatewayConnectionManager_DoesNotReintroduceNodeGenerationOrTelemetryOwnership()
    {
        var manager = ReadConnectionSource("GatewayConnectionManager.cs");

        Assert.DoesNotContain("_nodeConnectionGeneration", manager);
        Assert.DoesNotContain("_nodeOperationCts", manager);
        Assert.DoesNotContain("_nodeStartSemaphore", manager);
        Assert.DoesNotContain("private bool IsCurrentNodeAttempt", manager);
        Assert.DoesNotContain("StartNodeConnectionCoreAsync", manager);
        Assert.DoesNotContain("ObserveNodeTelemetryStatus", manager);
        Assert.DoesNotContain("openclaw.connection.node.", manager);

        var declarations = Directory
            .EnumerateFiles(
                GetConnectionSourceDirectory(),
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .Count(source => source.Contains(
                "internal bool IsCurrentNodeAttempt(",
                StringComparison.Ordinal));
        Assert.Equal(1, declarations);
    }

    [Fact]
    public void GatewayConnectionManager_DoesNotReintroduceDevicePairWorkflowOwnership()
    {
        var manager = ReadConnectionSource("GatewayConnectionManager.cs");

        Assert.DoesNotContain("_devicePairReconnectAttempts", manager);
        Assert.DoesNotContain("_devicePairAutoApproveInFlight", manager);
        Assert.DoesNotContain("AutoApproveDevicePairingRequestAsync", manager);
        Assert.DoesNotContain("ReconnectAfterApprovedDevicePairAsync", manager);
        Assert.DoesNotContain("BuildDeviceAutoApprovalFailureDetail", manager);
    }

    [Fact]
    public void GatewayConnectionManager_DoesNotReintroduceBootstrapTimingOwnership()
    {
        var manager = ReadConnectionSource("GatewayConnectionManager.cs");

        Assert.DoesNotContain("_forceBootstrapForGatewayRecordId", manager);
        Assert.DoesNotContain("_activeConnectUsedBootstrapToken", manager);
        Assert.DoesNotContain("_postBootstrapOperatorReconnectScheduled", manager);
        Assert.DoesNotContain("_operatorTokenRecoveryAttemptedGatewayId", manager);
        Assert.DoesNotContain("TryClearBootstrapTokenAfterDurablePairing", manager);
        Assert.DoesNotContain("TrySchedulePostBootstrapOperatorReconnect", manager);
        Assert.DoesNotContain("Action<Exception>", manager);
    }

    [Fact]
    public void NodeConnectorEvents_HaveOneSubscriptionOwner()
    {
        var manager = ReadConnectionSource("GatewayConnectionManager.cs");
        var nodeOwner = ReadConnectionSource("NodeConnectionCoordinator.cs");
        var pairOwner = ReadConnectionSource("DevicePairApprovalCoordinator.cs");

        Assert.Equal(
            1,
            Count(manager, "_nodeConnector.StatusChanged += OnNodeStatusChanged;"));
        Assert.Equal(
            1,
            Count(
                manager,
                "_nodeConnector.PairingStatusChanged += OnNodePairingStatusChanged;"));
        Assert.Equal(
            1,
            Count(
                manager,
                "_nodeConnector.DeviceTokenReceived += OnNodeDeviceTokenReceived;"));
        Assert.Equal(
            1,
            Count(manager, "_nodeConnector.StatusChanged -= OnNodeStatusChanged;"));
        Assert.Equal(
            1,
            Count(
                manager,
                "_nodeConnector.PairingStatusChanged -= OnNodePairingStatusChanged;"));
        Assert.Equal(
            1,
            Count(
                manager,
                "_nodeConnector.DeviceTokenReceived -= OnNodeDeviceTokenReceived;"));
        Assert.DoesNotContain(".StatusChanged +=", nodeOwner);
        Assert.DoesNotContain(".PairingStatusChanged +=", nodeOwner);
        Assert.DoesNotContain(".PairingStatusChanged +=", pairOwner);
    }

    [Fact]
    public void CredentialFailureFormatting_HasOneCanonicalOwner()
    {
        var manager = ReadConnectionSource("GatewayConnectionManager.cs");
        var nodeOwner = ReadConnectionSource("NodeConnectionCoordinator.cs");
        var formatter = ReadConnectionSource(
            "CredentialResolutionFailureFormatter.cs");

        foreach (var formerOwner in new[] { manager, nodeOwner })
        {
            Assert.DoesNotContain("BuildCredentialFailureMessage", formerOwner);
            Assert.DoesNotContain("MissingNodeCredentialMessage", formerOwner);
            Assert.DoesNotContain(
                "stored device token is corrupt. Re-pair this PC",
                formerOwner);
            Assert.DoesNotContain(
                "stored device token is unreadable. Check file permissions",
                formerOwner);
            Assert.DoesNotContain(
                "Add a shared/bootstrap gateway token or re-pair this PC.",
                formerOwner);
            Assert.DoesNotContain(
                "Re-pair this PC or add a shared/bootstrap gateway token.",
                formerOwner);
        }

        foreach (var nodeOwnedConstant in new[]
                 {
                     "MissingNodeConnectorMessage",
                     "MissingActiveGatewayForNodeMessage",
                     "MissingGatewayRecordForNodeMessage"
                 })
        {
            Assert.DoesNotContain(
                $"private const string {nodeOwnedConstant}",
                manager);
            Assert.Equal(
                1,
                Count(
                    manager + nodeOwner,
                    $"private const string {nodeOwnedConstant}"));
        }
        Assert.Equal(
            1,
            Count(
                formatter,
                "internal static class CredentialResolutionFailureFormatter"));
        Assert.Equal(
            1,
            Directory
                .EnumerateFiles(
                    GetConnectionSourceDirectory(),
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText)
                .Count(source => source.Contains(
                    "private const string MissingNodeCredentialMessage",
                    StringComparison.Ordinal)));
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string ReadConnectionSource(string fileName) =>
        File.ReadAllText(Path.Combine(GetConnectionSourceDirectory(), fileName));

    private static string GetConnectionSourceDirectory()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(
            "OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(
                configuredRoot,
                "src",
                "OpenClaw.Connection");
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "OpenClaw.Connection");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src\\OpenClaw.Connection.");
    }
}
