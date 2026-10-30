using OpenClaw.Connection;

namespace OpenClawTray;

internal sealed record CliUninstallTarget(string GatewayId, string Binding)
{
    internal static bool TryParse(
        string[] args,
        out CliUninstallTarget? target,
        out string? error)
    {
        target = null;
        error = null;
        string? gatewayId = null;
        string? binding = null;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            var equalsIndex = token.IndexOf('=');
            var name = equalsIndex >= 0 ? token[..equalsIndex] : token;
            var isGatewayId = string.Equals(name, "--gateway-id", StringComparison.OrdinalIgnoreCase);
            var isBinding = string.Equals(name, "--gateway-binding", StringComparison.OrdinalIgnoreCase);
            if (!isGatewayId && !isBinding)
                continue;

            if ((isGatewayId ? gatewayId : binding) is not null)
            {
                error = $"{name} may only be specified once. No uninstall was started.";
                return false;
            }

            var value = equalsIndex >= 0
                ? token[(equalsIndex + 1)..]
                : i < args.Length - 1 ? args[i + 1] : null;
            if (string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith('-'))
            {
                error = $"{name} requires a value. No uninstall was started.";
                return false;
            }

            if (isGatewayId)
                gatewayId = value;
            else
                binding = value;
            if (equalsIndex < 0)
                i++;
        }

        // Either flag is a targeted request. Never turn an incomplete pair into full-profile removal.
        if ((gatewayId is null) != (binding is null))
        {
            error = "Selected Gateway removal requires --gateway-id and --gateway-binding. No uninstall was started.";
            return false;
        }

        if (gatewayId is not null)
            target = new(gatewayId, binding!);
        return true;
    }

    internal bool TryGetSetupArguments(GatewayRecord? current, out string[] arguments)
    {
        arguments = [];
        if (current is null || current.Id != GatewayId ||
            LocalGatewaySettings.Classify(current) != LocalGatewayKind.Wsl ||
            LocalGatewaySettings.CaptureRemovalAuthority(current) != Binding)
        {
            return false;
        }

        arguments = ["--gateway-id", GatewayId, "--gateway-binding", Binding];
        return true;
    }
}
