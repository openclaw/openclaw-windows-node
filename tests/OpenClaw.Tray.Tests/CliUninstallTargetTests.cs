using OpenClaw.Connection;
using OpenClawTray;

namespace OpenClaw.Tray.Tests;

public sealed class CliUninstallTargetTests
{
    private static GatewayRecord SavedTarget => new()
    {
        Id = "selected-gateway",
        Url = "ws://127.0.0.1:18789",
        IsLocal = true,
        SetupManagedDistroName = "SelectedGateway",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryParse_UntargetedUninstallPreservesFullProfileMode(bool dryRun)
    {
        string[] args = ["--uninstall", dryRun ? "--dry-run" : "--confirm-destructive",
            "--json-output", @"C:\proof\uninstall.json"];

        Assert.True(CliUninstallTarget.TryParse(args, out var target, out var error));

        Assert.Null(target);
        Assert.Null(error);
    }

    public static TheoryData<string[]> InvalidTargetArguments => new()
    {
        new[] { "--gateway-binding", "binding-only" },
        new[] { "--GATEWAY-BINDING=binding-only" },
        new[] { "--gateway-id", "selected-gateway" },
        new[] { "--Gateway-Id=selected-gateway" },
        new[] { "--gateway-id" },
        new[] { "--gateway-binding" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding" },
        new[] { "--gateway-binding", "binding", "--gateway-id" },
        new[] { "--GATEWAY-BINDING", "--confirm-destructive" },
        new[] { "--Gateway-Id", "selected-gateway", "--GATEWAY-BINDING", "--dry-run" },
        new[] { "--gateway-id", "--gateway-binding", "binding" },
        new[] { "--gateway-id", "", "--gateway-binding", "binding" },
        new[] { "--gateway-id", " \t ", "--gateway-binding", "binding" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding", "" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding", " \t " },
        new[] { "--gateway-id=", "--gateway-binding=binding" },
        new[] { "--gateway-id= \t ", "--gateway-binding=binding" },
        new[] { "--gateway-id=selected-gateway", "--gateway-binding=" },
        new[] { "--gateway-id=selected-gateway", "--gateway-binding= \t " },
        new[] { "--Gateway-Id=selected-gateway", "--GATEWAY-BINDING= \t " },
        new[] { "--gateway-id=--dry-run", "--gateway-binding=binding" },
        new[] { "--gateway-id=selected-gateway", "--gateway-binding=--confirm-destructive" },
        new[] { "--gateway-id", " --dry-run", "--gateway-binding", "binding" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding", " --dry-run" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding", "-x" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding", "binding", "--gateway-id", "selected-gateway" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding", "binding", "--gateway-binding", "binding" },
        new[] { "--gateway-id", "selected-gateway", "--gateway-binding", "binding", "--GATEWAY-ID=selected-gateway" },
        new[] { "--gateway-id=selected-gateway", "--gateway-binding=binding", "--Gateway-Binding", "binding" },
    };

    [Theory]
    [MemberData(nameof(InvalidTargetArguments))]
    public void TryParse_InvalidTargetRequestFailsClosed(string[] targetArgs)
    {
        string[] args = ["--uninstall", "--confirm-destructive", .. targetArgs];

        Assert.False(CliUninstallTarget.TryParse(args, out var target, out var error));

        Assert.Null(target);
        Assert.NotNull(error);
        Assert.Contains("No uninstall was started.", error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TryGetSetupArguments_ValidTargetForwardsCanonicalPair(bool inlineValues, bool mixedCase)
    {
        var saved = SavedTarget;
        var binding = LocalGatewaySettings.CaptureRemovalAuthority(saved);
        var idFlag = mixedCase ? "--Gateway-Id" : "--gateway-id";
        var bindingFlag = mixedCase ? "--GATEWAY-BINDING" : "--gateway-binding";
        string[] targetArgs = inlineValues
            ? [$"{idFlag}={saved.Id}", $"{bindingFlag}={binding}"]
            : [bindingFlag, binding, idFlag, saved.Id];
        string[] args = ["--uninstall", "--dry-run", .. targetArgs];

        Assert.True(CliUninstallTarget.TryParse(args, out var target, out var error));
        Assert.Null(error);
        Assert.NotNull(target);
        Assert.True(target.TryGetSetupArguments(saved, out var forwarded));
        Assert.Equal(["--gateway-id", saved.Id, "--gateway-binding", binding], forwarded);
    }

    [Fact]
    public void TryGetSetupArguments_MixedEqualsAndSeparateValuesForwardCanonicalPair()
    {
        var saved = SavedTarget;
        var binding = LocalGatewaySettings.CaptureRemovalAuthority(saved);
        string[] args = ["--uninstall", "--confirm-destructive",
            $"--gateway-binding={binding}", "--gateway-id", saved.Id];

        Assert.True(CliUninstallTarget.TryParse(args, out var target, out _));
        Assert.NotNull(target);
        Assert.True(target.TryGetSetupArguments(saved, out var forwarded));
        Assert.Equal(["--gateway-id", saved.Id, "--gateway-binding", binding], forwarded);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different-id")]
    [InlineData("different-url")]
    [InlineData("different-distro")]
    [InlineData("remote")]
    [InlineData("native")]
    [InlineData("ssh")]
    [InlineData("shared-token")]
    [InlineData("bootstrap-token")]
    [InlineData("signature")]
    [InlineData("browser-port")]
    [InlineData("label")]
    public void TryGetSetupArguments_ChangedOrNonWslTargetDoesNotForward(string change)
    {
        var saved = SavedTarget;
        string[] args = ["--gateway-id", saved.Id, "--gateway-binding", LocalGatewaySettings.CaptureRemovalAuthority(saved)];
        GatewayRecord? current = change switch
        {
            "missing" => null,
            "different-id" => saved with { Id = "other-gateway" },
            "different-url" => saved with { Url = "ws://127.0.0.1:18790" },
            "different-distro" => saved with { SetupManagedDistroName = "OtherGateway" },
            "remote" => saved with { IsLocal = false, SetupManagedDistroName = null, Url = "wss://remote.example.test" },
            "native" => saved with { NativePackageFamilyName = "OpenClawGateway_test", SetupManagedDistroName = null },
            "ssh" => saved with { SshTunnel = new("user", "remote.example.test", 18789, 18789) },
            "shared-token" => saved with { SharedGatewayToken = "changed-fixture-token" },
            "bootstrap-token" => saved with { BootstrapToken = "changed-fixture-bootstrap" },
            "signature" => saved with { RequiresV2Signature = true },
            "browser-port" => saved with { BrowserControlPort = 19999 },
            "label" => saved with { FriendlyName = "Changed" },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Assert.True(CliUninstallTarget.TryParse(args, out var target, out _));
        Assert.NotNull(target);
        Assert.False(target.TryGetSetupArguments(current, out var forwarded));
        Assert.Empty(forwarded);
    }

    [Theory]
    [InlineData("selected-gateway", "stale-binding")]
    [InlineData("other-gateway", null)]
    public void TryGetSetupArguments_MismatchedRequestDoesNotForward(string gatewayId, string? binding)
    {
        var saved = SavedTarget;
        string[] args = ["--gateway-id", gatewayId,
            "--gateway-binding", binding ?? LocalGatewaySettings.CaptureRemovalAuthority(saved)];

        Assert.True(CliUninstallTarget.TryParse(args, out var target, out _));
        Assert.NotNull(target);
        Assert.False(target.TryGetSetupArguments(saved, out var forwarded));
        Assert.Empty(forwarded);
    }

    [Fact]
    public void RemovalConsentIgnoresOnlyConnectionTimestampAndNeverForwardsCredentials()
    {
        var saved = SavedTarget with { SharedGatewayToken = "private-fixture-token", BootstrapToken = "private-fixture-bootstrap" };
        var binding = LocalGatewaySettings.CaptureRemovalAuthority(saved);
        var target = new CliUninstallTarget(saved.Id, binding);

        Assert.True(target.TryGetSetupArguments(saved with { LastConnected = DateTime.UtcNow }, out var forwarded));
        Assert.Equal(64, binding.Length);
        Assert.DoesNotContain(saved.SharedGatewayToken, string.Join(" ", forwarded));
        Assert.DoesNotContain(saved.BootstrapToken, string.Join(" ", forwarded));
        Assert.False(new CliUninstallTarget(saved.Id, GatewayDashboardBinding.Capture(saved))
            .TryGetSetupArguments(saved, out _));
    }
}
