using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ClawHubInstallCoordinatorTests
{
    [Fact]
    public async Task Cancel_NeverCallsInstall()
    {
        var (client, proxy) = Gateway();
        var interaction = new RecordingInteraction { ConfirmResult = false };
        var coordinator = new ClawHubInstallCoordinator(() => client, interaction);

        await coordinator.ExecuteAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(1, proxy.InspectCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Equal(1, interaction.ConfirmCalls);
        Assert.Empty(interaction.Successes);
    }

    [Fact]
    public async Task DisconnectedGateway_ShowsErrorWithoutInspectOrInstall()
    {
        var (client, proxy) = Gateway();
        proxy.Connected = false;
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(0, proxy.InspectCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Contains(interaction.Errors, error => error.Title == "Gateway connection required");
    }

    [Fact]
    public async Task MissingAdministratorScope_ShowsErrorWithoutInspectOrInstall()
    {
        var (client, proxy) = Gateway();
        proxy.Scopes = ["operator.read"];
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(0, proxy.InspectCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Contains(interaction.Errors, error => error.Title == "Administrator scope required");
    }

    [Theory]
    [InlineData("Plugin not found")]
    [InlineData("Network unavailable")]
    public async Task InspectFailure_IsVisibleAndCannotBeApproved(string message)
    {
        var (client, proxy) = Gateway();
        proxy.Inspect = () => Task.FromException<PluginInspectionInfo>(
            new InvalidOperationException(message));
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(1, proxy.InspectCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Equal(0, interaction.ConfirmCalls);
        Assert.Contains(interaction.Errors, error =>
            error.Title == "Could not inspect plugin" && error.Message.Contains(message));
    }

    [Fact]
    public async Task MissingManagedPlugin_UsesClawHubPackageInstallContract()
    {
        var (client, proxy) = Gateway();
        proxy.Inspect = () => Task.FromException<PluginInspectionInfo>(
            new GatewayRequestException("Plugin \"expedia-openclaw\" not found."));
        var interaction = new RecordingInteraction();
        var request = new ClawHubInstallRequest(
            ClawHubListingKind.Plugin,
            "expedia-openclaw",
            "@expediagroup/expedia-openclaw",
            null);

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(1, interaction.PackageConfirmCalls);
        Assert.Equal(
            ("@expediagroup/expedia-openclaw", "expedia-openclaw", null, false),
            proxy.InstallRequest);
        Assert.Single(interaction.PackageSuccesses);
        Assert.Empty(interaction.Errors);
    }

    [Fact]
    public async Task MissingManagedPluginCancel_NeverCallsInstall()
    {
        var (client, proxy) = Gateway();
        proxy.Inspect = () => Task.FromException<PluginInspectionInfo>(
            new GatewayRequestException("Plugin \"expedia-openclaw\" not found."));
        var interaction = new RecordingInteraction { ConfirmResult = false };

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(CommunityRequest(), CancellationToken.None);

        Assert.Equal(1, interaction.PackageConfirmCalls);
        Assert.Equal(0, proxy.InstallCalls);
    }

    [Fact]
    public async Task CommunityPluginPolicyWarning_RequiresExplicitRetryApproval()
    {
        var (client, proxy) = Gateway();
        proxy.Inspect = () => Task.FromException<PluginInspectionInfo>(
            new GatewayRequestException("Plugin \"expedia-openclaw\" not found."));
        proxy.Install = call => call == 1
            ? Task.FromException<PluginInstallResult>(PolicyWarningException())
            : Task.FromResult(new PluginInstallResult(true, []));
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(CommunityRequest(), CancellationToken.None);

        Assert.Equal(2, proxy.InstallCalls);
        Assert.Equal(
            ("@expediagroup/expedia-openclaw", "expedia-openclaw", null, true),
            proxy.InstallRequest);
        Assert.Equal(1, interaction.PolicyWarningConfirmCalls);
        Assert.Equal(2, interaction.PackageProgressShown);
        Assert.Equal(2, interaction.ProgressHidden);
        Assert.Single(interaction.PackageSuccesses);
    }

    [Fact]
    public async Task CommunityPluginPolicyWarningCancel_NeverAcknowledgesWarning()
    {
        var (client, proxy) = Gateway();
        proxy.Inspect = () => Task.FromException<PluginInspectionInfo>(
            new GatewayRequestException("Plugin \"expedia-openclaw\" not found."));
        proxy.Install = _ => Task.FromException<PluginInstallResult>(PolicyWarningException());
        var interaction = new RecordingInteraction { PolicyWarningConfirmResult = false };

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(CommunityRequest(), CancellationToken.None);

        Assert.Equal(1, proxy.InstallCalls);
        Assert.False(proxy.InstallRequest?.AcknowledgePolicy);
        Assert.Equal(1, interaction.PolicyWarningConfirmCalls);
        Assert.Empty(interaction.PackageSuccesses);
    }

    [Fact]
    public async Task NonMissingInspectFailure_DoesNotFallBackToPackageInstall()
    {
        var (client, proxy) = Gateway();
        proxy.Inspect = () => Task.FromException<PluginInspectionInfo>(
            new GatewayRequestException("Network unavailable"));
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(CommunityRequest(), CancellationToken.None);

        Assert.Equal(0, interaction.PackageConfirmCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Contains(interaction.Errors, error => error.Title == "Could not inspect plugin");
    }

    [Fact]
    public async Task InstallFailureAfterApproval_IsVisibleAndNeverReportsSuccess()
    {
        var (client, proxy) = Gateway();
        proxy.Install = _ => Task.FromException<PluginInstallResult>(
            new TimeoutException("Gateway connection dropped"));
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(1, proxy.InstallCalls);
        Assert.Equal(1, interaction.ProgressShown);
        Assert.Equal(1, interaction.ProgressHidden);
        Assert.Empty(interaction.Successes);
        Assert.Contains(interaction.Errors, error =>
            error.Title == "Plugin install failed" &&
            error.Message.Contains("Gateway connection dropped"));
    }

    [Fact]
    public async Task Approval_UsesInspectedPackageIdAndReviewToken()
    {
        var (client, proxy) = Gateway();
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(
            ("@openclaw/diagnostics-otel", "diagnostics-otel", "review-token", false),
            proxy.InstallRequest);
        Assert.Single(interaction.Successes);
        Assert.Empty(interaction.Errors);
    }

    [Fact]
    public async Task ManagedPluginPolicyWarning_RequiresExplicitRetryApproval()
    {
        var (client, proxy) = Gateway();
        proxy.Install = call => call == 1
            ? Task.FromException<PluginInstallResult>(PolicyWarningException())
            : Task.FromResult(new PluginInstallResult(true, []));
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(ValidRequest(), CancellationToken.None);

        Assert.Equal(2, proxy.InstallCalls);
        Assert.Equal(
            ("@openclaw/diagnostics-otel", "diagnostics-otel", "review-token", true),
            proxy.InstallRequest);
        Assert.Equal(1, interaction.PolicyWarningConfirmCalls);
        Assert.Equal(2, interaction.ProgressShown);
        Assert.Equal(2, interaction.ProgressHidden);
        Assert.Single(interaction.Successes);
    }

    [Fact]
    public async Task SkillApproval_UsesClawHubSkillInstallContract()
    {
        var (client, proxy) = Gateway();
        var interaction = new RecordingInteraction();
        var request = new ClawHubInstallRequest(
            ClawHubListingKind.Skill,
            "@alipay/alipay-aipay",
            null);

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(0, proxy.InspectCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Equal("@alipay/alipay-aipay", proxy.SkillInstallRequest);
        Assert.Equal(1, interaction.SkillConfirmCalls);
        Assert.Equal(1, interaction.SkillProgressShown);
        Assert.Single(interaction.SkillSuccesses);
        Assert.Empty(interaction.Errors);
    }

    [Fact]
    public async Task SkillsShApproval_UsesExactExternalInstallReference()
    {
        var (client, proxy) = Gateway();
        var interaction = new RecordingInteraction();
        var request = new ClawHubInstallRequest(
            ClawHubListingKind.Skill,
            "skills-sh:vercel-labs/skills/find-skills",
            null);

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("skills-sh:vercel-labs/skills/find-skills", proxy.SkillInstallRequest);
        Assert.Equal(1, interaction.SkillConfirmCalls);
        Assert.Single(interaction.SkillSuccesses);
        Assert.Empty(interaction.Errors);
    }

    [Fact]
    public async Task SkillCancel_NeverCallsGatewayInstall()
    {
        var (client, proxy) = Gateway();
        var interaction = new RecordingInteraction { ConfirmResult = false };
        var request = new ClawHubInstallRequest(
            ClawHubListingKind.Skill,
            "@alipay/alipay-aipay",
            null);

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(request, CancellationToken.None);

        Assert.Null(proxy.SkillInstallRequest);
        Assert.Equal(1, interaction.SkillConfirmCalls);
        Assert.Equal(0, interaction.SkillProgressShown);
    }

    [Fact]
    public async Task InvalidLink_ShowsErrorBeforeGatewayAccess()
    {
        var (client, proxy) = Gateway();
        var interaction = new RecordingInteraction();

        await new ClawHubInstallCoordinator(() => client, interaction)
            .ExecuteAsync(new ClawHubInstallRequest(null, "Missing id"), CancellationToken.None);

        Assert.Equal(0, proxy.InspectCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Contains(interaction.Errors, error => error.Title == "Invalid ClawHub install link");
    }

    [Fact]
    public async Task ConcurrentRequest_IsRejectedWithoutSecondInspect()
    {
        var (client, proxy) = Gateway();
        var interaction = new RecordingInteraction
        {
            ConfirmStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
            ConfirmRelease = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var coordinator = new ClawHubInstallCoordinator(() => client, interaction);

        var first = coordinator.ExecuteAsync(ValidRequest(), CancellationToken.None);
        await interaction.ConfirmStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.ExecuteAsync(
            new ClawHubInstallRequest("second-plugin", null),
            CancellationToken.None);
        interaction.ConfirmRelease.SetResult(false);
        await first;

        Assert.Equal(1, proxy.InspectCalls);
        Assert.Equal(0, proxy.InstallCalls);
        Assert.Contains(interaction.Errors, error =>
            error.Title == "ClawHub install already in progress");
    }

    private static ClawHubInstallRequest ValidRequest() =>
        new("diagnostics-otel", null);

    private static ClawHubInstallRequest CommunityRequest() =>
        new(
            ClawHubListingKind.Plugin,
            "expedia-openclaw",
            "@expediagroup/expedia-openclaw",
            null);

    private static GatewayRequestException PolicyWarningException()
    {
        using var details = System.Text.Json.JsonDocument.Parse(
            """
            {
              "installPolicyCode": "install_policy_warning_acknowledgement_required",
              "targetName": "@expediagroup/expedia-openclaw",
              "targetType": "plugin",
              "requestMode": "install",
              "reason": "Static analysis found behavior that requires review.",
              "findings": [
                {
                  "ruleId": "network-access",
                  "severity": "warn",
                  "message": "Plugin declares outbound network access."
                }
              ]
            }
            """);
        return new GatewayRequestException(
            "Install policy acknowledgement required.",
            details.RootElement.Clone());
    }

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    public class GatewayProxy : DispatchProxy
    {
        public bool Connected = true;
        public IReadOnlyList<string> Scopes = ["operator.admin"];
        public int InspectCalls;
        public int InstallCalls;
        public (string PackageName, string PluginId, string? ReviewToken, bool AcknowledgePolicy)?
            InstallRequest;
        public string? SkillInstallRequest;
        public Func<Task<PluginInspectionInfo>> Inspect = () => Task.FromResult(Inspection());
        public Func<int, Task<PluginInstallResult>> Install = _ =>
            Task.FromResult(new PluginInstallResult(false, []));

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_GrantedOperatorScopes" => Scopes,
            "InspectPluginAsync" => InspectPlugin(),
            "InstallClawHubPluginAsync" => InstallPlugin(args!),
            "InstallClawHubSkillAsync" => InstallSkill(args!),
            _ => throw new NotSupportedException(method?.Name)
        };

        private Task<PluginInspectionInfo> InspectPlugin()
        {
            InspectCalls++;
            return Inspect();
        }

        private Task<PluginInstallResult> InstallPlugin(object?[] args)
        {
            InstallCalls++;
            InstallRequest = (
                (string)args[0]!,
                (string)args[1]!,
                (string?)args[2],
                (bool)args[3]!);
            return Install(InstallCalls);
        }

        private Task<ClawHubSkillInstallResult> InstallSkill(object?[] args)
        {
            SkillInstallRequest = (string)args[0]!;
            return Task.FromResult(new ClawHubSkillInstallResult(
                SkillInstallRequest,
                "1.0.0",
                null));
        }

        private static PluginInspectionInfo Inspection() => new(
            "diagnostics-otel",
            "Diagnostics OpenTelemetry",
            "Exports telemetry.",
            "@openclaw/diagnostics-otel",
            "review-token",
            [new PluginCapabilityGroup("tools", ["diagnostics.export"])],
            [],
            "clean",
            []);
    }

    private sealed class RecordingInteraction : IClawHubInstallInteraction
    {
        public bool ConfirmResult { get; set; } = true;
        public int ConfirmCalls { get; private set; }
        public int SkillConfirmCalls { get; private set; }
        public int PackageConfirmCalls { get; private set; }
        public int PolicyWarningConfirmCalls { get; private set; }
        public int ProgressShown { get; private set; }
        public int PackageProgressShown { get; private set; }
        public int SkillProgressShown { get; private set; }
        public int ProgressHidden { get; private set; }
        public TaskCompletionSource? ConfirmStarted { get; init; }
        public TaskCompletionSource<bool>? ConfirmRelease { get; init; }
        public bool PolicyWarningConfirmResult { get; set; } = true;
        public List<(string Title, string Message)> Errors { get; } = [];
        public List<(PluginInspectionInfo Inspection, PluginInstallResult Result)> Successes { get; } = [];
        public List<(string PackageName, PluginInstallResult Result)> PackageSuccesses { get; } = [];
        public List<(string SkillId, ClawHubSkillInstallResult Result)> SkillSuccesses { get; } = [];

        public Task ShowErrorAsync(
            string title,
            string message,
            CancellationToken cancellationToken)
        {
            Errors.Add((title, message));
            return Task.CompletedTask;
        }

        public async Task<bool> ConfirmAsync(
            PluginInspectionInfo inspection,
            CancellationToken cancellationToken)
        {
            ConfirmCalls++;
            ConfirmStarted?.TrySetResult();
            return ConfirmRelease is null
                ? ConfirmResult
                : await ConfirmRelease.Task.WaitAsync(cancellationToken);
        }

        public Task<bool> ConfirmSkillAsync(
            string skillId,
            CancellationToken cancellationToken)
        {
            SkillConfirmCalls++;
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool> ConfirmPackageAsync(
            string listingId,
            string packageName,
            CancellationToken cancellationToken)
        {
            PackageConfirmCalls++;
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool> ConfirmInstallPolicyWarningAsync(
            PluginInstallPolicyWarning warning,
            CancellationToken cancellationToken)
        {
            PolicyWarningConfirmCalls++;
            return Task.FromResult(PolicyWarningConfirmResult);
        }

        public Task ShowInstallProgressAsync(
            PluginInspectionInfo inspection,
            CancellationToken cancellationToken)
        {
            ProgressShown++;
            return Task.CompletedTask;
        }

        public Task ShowSkillInstallProgressAsync(
            string skillId,
            CancellationToken cancellationToken)
        {
            SkillProgressShown++;
            return Task.CompletedTask;
        }

        public Task ShowPackageInstallProgressAsync(
            string packageName,
            CancellationToken cancellationToken)
        {
            PackageProgressShown++;
            return Task.CompletedTask;
        }

        public Task HideInstallProgressAsync()
        {
            ProgressHidden++;
            return Task.CompletedTask;
        }

        public Task ShowSuccessAsync(
            PluginInspectionInfo inspection,
            PluginInstallResult result,
            CancellationToken cancellationToken)
        {
            Successes.Add((inspection, result));
            return Task.CompletedTask;
        }

        public Task ShowSkillSuccessAsync(
            string skillId,
            ClawHubSkillInstallResult result,
            CancellationToken cancellationToken)
        {
            SkillSuccesses.Add((skillId, result));
            return Task.CompletedTask;
        }

        public Task ShowPackageSuccessAsync(
            string packageName,
            PluginInstallResult result,
            CancellationToken cancellationToken)
        {
            PackageSuccesses.Add((packageName, result));
            return Task.CompletedTask;
        }
    }
}
