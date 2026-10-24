namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    public async Task<PluginInspectionInfo> InspectPluginAsync(
        string pluginId,
        int timeoutMs = 15000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var payload = await SendWizardRequestAsync(
            "plugins.inspect",
            new { pluginId },
            timeoutMs).ConfigureAwait(false);
        return PluginManagementParser.ParseInspection(payload);
    }

    public async Task<PluginInstallResult> InstallClawHubPluginAsync(
        string packageName,
        string expectedPluginId,
        string? reviewToken,
        bool acknowledgeInstallPolicyWarning = true,
        int timeoutMs = 120000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPluginId);

        object parameters = reviewToken switch
        {
            not null when acknowledgeInstallPolicyWarning => new
            {
                source = "clawhub",
                packageName,
                expectedPluginId,
                mode = "install",
                acknowledgeInstallPolicyWarning = true,
                acknowledgeCapabilities = new { reviewToken }
            },
            not null => new
            {
                source = "clawhub",
                packageName,
                expectedPluginId,
                mode = "install",
                acknowledgeCapabilities = new { reviewToken }
            },
            null when acknowledgeInstallPolicyWarning => new
            {
                source = "clawhub",
                packageName,
                expectedPluginId,
                mode = "install",
                acknowledgeInstallPolicyWarning = true
            },
            _ => new
            {
                source = "clawhub",
                packageName,
                expectedPluginId,
                mode = "install"
            }
        };
        var payload = await SendWizardRequestAsync(
            "plugins.install",
            parameters,
            timeoutMs).ConfigureAwait(false);
        return PluginManagementParser.ParseInstall(payload);
    }

    public async Task<ClawHubSkillInstallResult> InstallClawHubSkillAsync(
        string slug,
        int timeoutMs = 120000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var payload = await SendWizardRequestAsync(
            "skills.install",
            new
            {
                source = "clawhub",
                slug
            },
            timeoutMs).ConfigureAwait(false);
        return ClawHubSkillManagementParser.ParseInstall(payload);
    }
}
