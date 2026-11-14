using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.Shared.Tests.FileTransfer;

public sealed class GatewayFileAccessPolicyTests
{
    private const string Node = "abc123";
    private const string AllFileCommands = """["file.stat","file.fetch","dir.list","dir.fetch","file.write","file.create","fs.listDir","terminal.upload"]""";

    private static JsonElement Config(string nodes, bool versioned = true, string? gateway = null)
    {
        var version = versioned ? "\"policyVersion\":2," : "";
        var gatewaySection = gateway is null ? "" : "\"gateway\":" + gateway + ",";
        using var document = JsonDocument.Parse(
            "{" + gatewaySection + "\"plugins\":{\"entries\":{\"file-transfer\":{\"config\":{" + version + "\"nodes\":" + nodes + "}}}}}");
        return document.RootElement.Clone();
    }

    private static JsonObject NodePatch(GatewayFileAccessPlan plan) =>
        plan.Patch!["plugins"]!["entries"]!["file-transfer"]!["config"]!["nodes"]![Node]!.AsObject();

    [Fact]
    public void Plan_FirstWriteAllowsEveryPathAndVersionsThePolicy()
    {
        var plan = GatewayFileAccessPolicy.Plan(Config("{}", versioned: false), Node);

        Assert.Equal(GatewayFileAccessPlanKind.Patch, plan.Kind);
        Assert.Equal(2, plan.Patch!["plugins"]!["entries"]!["file-transfer"]!["config"]!["policyVersion"]!.GetValue<int>());
        var node = NodePatch(plan);
        Assert.Equal("""["**"]""", node["allowReadPaths"]!.ToJsonString());
        Assert.Equal("""["**"]""", node["allowWritePaths"]!.ToJsonString());
        Assert.Null(node["denyPaths"]);
    }

    [Fact]
    public void Plan_NewEntryStartsFromTheEffectiveDisplayNameEntryRatherThanTheWildcard()
    {
        var config = Config("""{"*":{"ask":"off","denyPaths":["/star/**"]},"Win PC":{"ask":"always","maxBytes":5,"denyPaths":["/secret/**"]}}""");

        Assert.True(GatewayFileAccessPolicy.NeedsDisplayName(config, Node));
        var node = NodePatch(GatewayFileAccessPolicy.Plan(config, Node, "Win PC"));

        Assert.Equal("always", node["ask"]!.GetValue<string>());
        Assert.Equal(5, node["maxBytes"]!.GetValue<int>());
        Assert.Equal("""["/secret/**"]""", node["denyPaths"]!.ToJsonString());
    }

    [Fact]
    public void Plan_ExistingEntryOnlyGainsAllowAllAndKeepsEveryExistingRule()
    {
        var config = Config("""{"*":{"denyPaths":["/star/**"]},"abc123":{"ask":"always","allowReadPaths":["/x/**"],"allowWritePaths":[],"denyPaths":["/admin/**"]}}""");

        Assert.False(GatewayFileAccessPolicy.NeedsDisplayName(config, Node));
        var node = NodePatch(GatewayFileAccessPolicy.Plan(config, Node));

        // Removing entries can trip the Gateway's shrinking-write guard, so existing rules are restated.
        Assert.Equal("""["/x/**","**"]""", node["allowReadPaths"]!.ToJsonString());
        Assert.Equal("""["**"]""", node["allowWritePaths"]!.ToJsonString());
        // The wildcard never applies to a node with its own entry, and its own controls and denies are untouched.
        Assert.Null(node["denyPaths"]);
        Assert.Null(node["ask"]);
    }

    [Fact]
    public void Plan_AllowAllEntryNeedsNoWrite()
    {
        var plan = GatewayFileAccessPolicy.Plan(
            Config("""{"abc123":{"allowReadPaths":["/x/**","**"],"allowWritePaths":["**"],"denyPaths":["/admin/**"]}}""",
                gateway: """{"nodes":{"commands":{"allow":""" + AllFileCommands + "}}}"), Node);

        Assert.Equal(GatewayFileAccessPlanKind.NoChange, plan.Kind);
    }

    [Fact]
    public void Plan_AppendsMissingFileCommandsToTheAllowlistExceptDeniedOnes()
    {
        // Setup writes the allowlist once, so Gateways set up before the file commands shipped lack them.
        var config = Config("{}", gateway: """{"nodes":{"commands":{"allow":["camera.snap","file.stat"],"deny":["file.write"]}}}""");

        var plan = GatewayFileAccessPolicy.Plan(config, Node);

        Assert.Equal(GatewayFileAccessPlanKind.Patch, plan.Kind);
        Assert.Equal(
            """["camera.snap","file.stat","file.fetch","dir.list","dir.fetch","file.create","fs.listDir","terminal.upload"]""",
            plan.Patch!["gateway"]!["nodes"]!["commands"]!["allow"]!.ToJsonString());
    }

    [Fact]
    public void Plan_LeavesTheAllowlistAloneOnceThisNodeAllowsAll()
    {
        // The allowlist is Gateway-wide; re-adding on every connection would undo an administrator's removal.
        var config = Config("""{"abc123":{"allowReadPaths":["**"],"allowWritePaths":["**"]}}""",
            gateway: """{"nodes":{"commands":{"allow":["file.stat"]}}}""");

        Assert.Equal(GatewayFileAccessPlanKind.NoChange, GatewayFileAccessPolicy.Plan(config, Node).Kind);
    }

    [Fact]
    public void Plan_ExtendsTheLegacyAllowlistKeyWhenOnlyItExists()
    {
        var config = Config("{}", gateway: """{"nodes":{"allowCommands":["system.run"]}}""");

        var nodes = GatewayFileAccessPolicy.Plan(config, Node).Patch!["gateway"]!["nodes"]!;

        Assert.Null(nodes["commands"]);
        Assert.Equal("system.run", nodes["allowCommands"]![0]!.GetValue<string>());
        Assert.Equal(9, nodes["allowCommands"]!.AsArray().Count);
    }

    [Fact]
    public void Plan_LeavesANewerPolicyVersionUnchanged()
    {
        using var document = JsonDocument.Parse("""{"plugins":{"entries":{"file-transfer":{"config":{"policyVersion":3,"nodes":{}}}}}}""");

        Assert.Equal(GatewayFileAccessPlanKind.UnsupportedPolicyVersion,
            GatewayFileAccessPolicy.Plan(document.RootElement, Node).Kind);
    }

    [Fact]
    public void Plan_UnreviewedLegacyRulesBlockTheWrite() =>
        Assert.Equal(GatewayFileAccessPlanKind.MigrationRequired,
            GatewayFileAccessPolicy.Plan(Config("""{"other":{"allowReadPaths":["/x/**"]}}""", versioned: false), Node).Kind);

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    public void Plan_RejectsNodeIdsThatAreNotSinglePathSegments(string nodeId) =>
        Assert.Equal(GatewayFileAccessPlanKind.InvalidNode, GatewayFileAccessPolicy.Plan(Config("{}"), nodeId).Kind);
}
