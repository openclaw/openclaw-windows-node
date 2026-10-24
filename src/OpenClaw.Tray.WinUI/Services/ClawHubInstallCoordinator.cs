using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum ClawHubListingKind
{
    Plugin,
    Skill
}

internal sealed record ClawHubInstallRequest(
    ClawHubListingKind Kind,
    string? ListingId,
    string? PackageName,
    string? ValidationError)
{
    private const int MaxListingIdLength = 200;
    private const int MaxSkillReferenceLength = 512;
    private const int MaxPackageNameLength = 214;

    public ClawHubInstallRequest(string? listingId, string? validationError)
        : this(ClawHubListingKind.Plugin, listingId, null, validationError)
    {
    }

    public ClawHubInstallRequest(
        ClawHubListingKind kind,
        string? listingId,
        string? validationError)
        : this(kind, listingId, null, validationError)
    {
    }

    public bool IsValid => ValidationError is null && ListingId is not null;

    public static ClawHubInstallRequest FromDeepLink(DeepLinkResult result)
    {
        if (!string.Equals(result.Path?.TrimEnd('/'), "clawhub/install", StringComparison.OrdinalIgnoreCase))
            return new(ClawHubListingKind.Plugin, null, null, "This ClawHub link does not target the install route.");

        var queryParts = result.Query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (queryParts.Length is < 1 or > 3 ||
            result.Parameters.Count != queryParts.Length ||
            result.Parameters.Keys.Any(static key =>
                !string.Equals(key, "id", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "kind", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "package", StringComparison.OrdinalIgnoreCase)) ||
            !result.Parameters.TryGetValue("id", out var listingId))
        {
            return new(
                ClawHubListingKind.Plugin,
                null,
                null,
                "The ClawHub install link must contain a listing id and optional listing kind.");
        }

        var kind = ClawHubListingKind.Plugin;
        if (result.Parameters.TryGetValue("kind", out var kindValue))
        {
            kind = kindValue.ToLowerInvariant() switch
            {
                "plugin" => ClawHubListingKind.Plugin,
                "skill" => ClawHubListingKind.Skill,
                _ => (ClawHubListingKind)(-1)
            };
            if (!Enum.IsDefined(kind))
                return new(ClawHubListingKind.Plugin, null, null, "The ClawHub listing kind is unsupported.");
        }

        listingId = listingId.Trim();
        if (listingId.Length == 0)
            return new(kind, null, null, "The ClawHub install link is missing its listing id.");
        var maxListingIdLength = kind == ClawHubListingKind.Skill
            ? MaxSkillReferenceLength
            : MaxListingIdLength;
        if (listingId.Length > maxListingIdLength)
            return new(kind, null, null, "The ClawHub listing id is too long.");
        if (!IsSupportedListingId(kind, listingId))
            return new(kind, null, null, "The ClawHub listing id contains unsupported characters.");

        string? packageName = null;
        if (result.Parameters.TryGetValue("package", out var packageValue))
        {
            if (kind != ClawHubListingKind.Plugin)
                return new(kind, null, null, "A ClawHub skill install link cannot contain a plugin package.");

            packageName = packageValue.Trim();
            if (packageName.Length == 0)
                return new(kind, null, null, "The ClawHub plugin package name is empty.");
            if (packageName.Length > MaxPackageNameLength)
                return new(kind, null, null, "The ClawHub plugin package name is too long.");
            if (!HasSupportedIdentifierCharacters(packageName))
                return new(kind, null, null, "The ClawHub plugin package name contains unsupported characters.");
        }

        return new(kind, listingId, packageName, null);
    }

    private static bool HasSupportedIdentifierCharacters(string value) =>
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or '@' or '/');

    private static bool IsSupportedListingId(ClawHubListingKind kind, string value)
    {
        const string skillsShPrefix = "skills-sh:";
        if (kind != ClawHubListingKind.Skill ||
            !value.StartsWith(skillsShPrefix, StringComparison.Ordinal))
        {
            return HasSupportedIdentifierCharacters(value);
        }

        var segments = value[skillsShPrefix.Length..].Split('/');
        return segments.Length == 3 &&
               segments.All(static segment =>
                   segment.Length > 0 &&
                   segment.All(static character =>
                       char.IsAsciiLetterOrDigit(character) ||
                       character is '-' or '_' or '.'));
    }
}

internal interface IClawHubInstallInteraction
{
    Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(PluginInspectionInfo inspection, CancellationToken cancellationToken);
    Task<bool> ConfirmPackageAsync(
        string listingId,
        string packageName,
        CancellationToken cancellationToken);
    Task<bool> ConfirmInstallPolicyWarningAsync(
        PluginInstallPolicyWarning warning,
        CancellationToken cancellationToken);
    Task<bool> ConfirmSkillAsync(string skillId, CancellationToken cancellationToken);
    Task ShowInstallProgressAsync(PluginInspectionInfo inspection, CancellationToken cancellationToken);
    Task ShowPackageInstallProgressAsync(string packageName, CancellationToken cancellationToken);
    Task ShowSkillInstallProgressAsync(string skillId, CancellationToken cancellationToken);
    Task HideInstallProgressAsync();
    Task ShowSuccessAsync(
        PluginInspectionInfo inspection,
        PluginInstallResult result,
        CancellationToken cancellationToken);
    Task ShowPackageSuccessAsync(
        string packageName,
        PluginInstallResult result,
        CancellationToken cancellationToken);
    Task ShowSkillSuccessAsync(
        string skillId,
        ClawHubSkillInstallResult result,
        CancellationToken cancellationToken);
}

internal sealed class ClawHubInstallCoordinator(
    Func<IOperatorGatewayClient?> getClient,
    IClawHubInstallInteraction interaction)
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public async Task ExecuteAsync(ClawHubInstallRequest request, CancellationToken cancellationToken)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
        {
            await interaction.ShowErrorAsync(
                "ClawHub install already in progress",
                "Finish or cancel the current install review before opening another ClawHub install link.",
                cancellationToken);
            return;
        }

        try
        {
            if (!request.IsValid)
            {
                await interaction.ShowErrorAsync(
                    "Invalid ClawHub install link",
                    request.ValidationError ?? "The ClawHub install link is invalid.",
                    cancellationToken);
                return;
            }

            var client = getClient();
            if (client is null || !client.IsConnectedToGateway)
            {
                await interaction.ShowErrorAsync(
                    "Gateway connection required",
                    "Connect Windows Hub to a Gateway before installing a ClawHub plugin.",
                    cancellationToken);
                return;
            }

            if (!client.GrantedOperatorScopes.Contains("operator.admin", StringComparer.Ordinal))
            {
                await interaction.ShowErrorAsync(
                    "Administrator scope required",
                    "The connected Gateway session does not grant operator.admin, which is required to install ClawHub plugins and skills.",
                    cancellationToken);
                return;
            }

            if (request.Kind == ClawHubListingKind.Skill)
            {
                await ExecuteSkillInstallAsync(client, request.ListingId!, cancellationToken);
                return;
            }

            PluginInspectionInfo inspection;
            try
            {
                inspection = await client.InspectPluginAsync(
                    request.ListingId!,
                    timeoutMs: 15000).WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (request.PackageName is not null && IsMissingManagedPlugin(exception))
                {
                    await ExecutePackageInstallAsync(
                        client,
                        request.ListingId!,
                        request.PackageName,
                        cancellationToken);
                    return;
                }

                await interaction.ShowErrorAsync(
                    "Could not inspect plugin",
                    $"Windows Hub could not review this ClawHub listing. {exception.Message}",
                    cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(inspection.PackageName))
            {
                await interaction.ShowErrorAsync(
                    "Plugin is not installable from ClawHub",
                    "The Gateway inspection did not provide a verified ClawHub package name.",
                    cancellationToken);
                return;
            }

            if (!await interaction.ConfirmAsync(inspection, cancellationToken))
                return;

            await interaction.ShowInstallProgressAsync(inspection, cancellationToken);
            try
            {
                var result = await client.InstallClawHubPluginAsync(
                    inspection.PackageName,
                    inspection.PluginId,
                    inspection.ReviewToken,
                    acknowledgeInstallPolicyWarning: false,
                    timeoutMs: 120000).WaitAsync(cancellationToken);
                await interaction.HideInstallProgressAsync();
                await interaction.ShowSuccessAsync(inspection, result, cancellationToken);
            }
            catch (GatewayRequestException exception)
                when (PluginManagementParser.TryParseInstallPolicyWarning(exception, out var warning))
            {
                await interaction.HideInstallProgressAsync();
                if (!await interaction.ConfirmInstallPolicyWarningAsync(warning!, cancellationToken))
                    return;

                await interaction.ShowInstallProgressAsync(inspection, cancellationToken);
                try
                {
                    var result = await client.InstallClawHubPluginAsync(
                        inspection.PackageName,
                        inspection.PluginId,
                        inspection.ReviewToken,
                        acknowledgeInstallPolicyWarning: true,
                        timeoutMs: 120000).WaitAsync(cancellationToken);
                    await interaction.HideInstallProgressAsync();
                    await interaction.ShowSuccessAsync(inspection, result, cancellationToken);
                }
                catch (Exception retryException) when (retryException is not OperationCanceledException)
                {
                    await interaction.HideInstallProgressAsync();
                    await interaction.ShowErrorAsync(
                        "Plugin install failed",
                        $"The Gateway did not install {inspection.Name}. {retryException.Message}",
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await interaction.HideInstallProgressAsync();
                    throw;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await interaction.HideInstallProgressAsync();
                await interaction.ShowErrorAsync(
                    "Plugin install failed",
                    $"The Gateway did not install {inspection.Name}. {exception.Message}",
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await interaction.HideInstallProgressAsync();
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task ExecutePackageInstallAsync(
        IOperatorGatewayClient client,
        string listingId,
        string packageName,
        CancellationToken cancellationToken)
    {
        if (!await interaction.ConfirmPackageAsync(listingId, packageName, cancellationToken))
            return;

        await interaction.ShowPackageInstallProgressAsync(packageName, cancellationToken);
        try
        {
            var result = await client.InstallClawHubPluginAsync(
                packageName,
                listingId,
                reviewToken: null,
                acknowledgeInstallPolicyWarning: false,
                timeoutMs: 120000).WaitAsync(cancellationToken);
            await interaction.HideInstallProgressAsync();
            await interaction.ShowPackageSuccessAsync(packageName, result, cancellationToken);
        }
        catch (GatewayRequestException exception)
            when (PluginManagementParser.TryParseInstallPolicyWarning(exception, out var warning))
        {
            await interaction.HideInstallProgressAsync();
            if (!await interaction.ConfirmInstallPolicyWarningAsync(warning!, cancellationToken))
                return;

            await interaction.ShowPackageInstallProgressAsync(packageName, cancellationToken);
            try
            {
                var result = await client.InstallClawHubPluginAsync(
                    packageName,
                    listingId,
                    reviewToken: null,
                    acknowledgeInstallPolicyWarning: true,
                    timeoutMs: 120000).WaitAsync(cancellationToken);
                await interaction.HideInstallProgressAsync();
                await interaction.ShowPackageSuccessAsync(packageName, result, cancellationToken);
            }
            catch (Exception retryException) when (retryException is not OperationCanceledException)
            {
                await interaction.HideInstallProgressAsync();
                await ShowPackageInstallErrorAsync(packageName, retryException, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await interaction.HideInstallProgressAsync();
                throw;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await interaction.HideInstallProgressAsync();
            await ShowPackageInstallErrorAsync(packageName, exception, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await interaction.HideInstallProgressAsync();
            throw;
        }
    }

    private Task ShowPackageInstallErrorAsync(
        string packageName,
        Exception exception,
        CancellationToken cancellationToken) =>
        interaction.ShowErrorAsync(
            "Plugin install failed",
            $"The Gateway did not install {packageName}. {exception.Message}",
            cancellationToken);

    private static bool IsMissingManagedPlugin(Exception exception) =>
        exception.Message.Contains("plugin", StringComparison.OrdinalIgnoreCase) &&
        exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private async Task ExecuteSkillInstallAsync(
        IOperatorGatewayClient client,
        string skillId,
        CancellationToken cancellationToken)
    {
        if (!await interaction.ConfirmSkillAsync(skillId, cancellationToken))
            return;

        await interaction.ShowSkillInstallProgressAsync(skillId, cancellationToken);
        try
        {
            var result = await client.InstallClawHubSkillAsync(
                skillId,
                timeoutMs: 120000).WaitAsync(cancellationToken);
            await interaction.HideInstallProgressAsync();
            await interaction.ShowSkillSuccessAsync(skillId, result, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await interaction.HideInstallProgressAsync();
            await interaction.ShowErrorAsync(
                "Skill install failed",
                $"The Gateway did not install {skillId}. {exception.Message}",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await interaction.HideInstallProgressAsync();
            throw;
        }
    }
}
