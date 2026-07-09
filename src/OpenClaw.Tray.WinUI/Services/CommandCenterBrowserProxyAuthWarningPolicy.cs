using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

internal static class CommandCenterBrowserProxyAuthWarningPolicy
{
    internal static bool ShouldShow(
        bool nodeBrowserProxyEnabled,
        bool activeGatewayHasSharedToken,
        IReadOnlyList<NodeCapabilityHealthInfo> nodes)
    {
        if (!nodeBrowserProxyEnabled || activeGatewayHasSharedToken)
            return false;

        return nodes.Any(node =>
            node.BrowserApprovedCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase) ||
            node.UnverifiedDeclaredCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase) ||
            node.LocalDeclaredCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase));
    }
}
