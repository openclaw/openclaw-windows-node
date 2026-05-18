using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;

namespace OpenClawTray.Services;

/// <summary>
/// Groups diagnostic clipboard operations that were spread across App.xaml.cs.
/// Each method copies a formatted diagnostic to the clipboard.
/// </summary>
internal sealed class DiagnosticsClipboardService
{
    private readonly Func<GatewayCommandCenterState> _captureState;

    public DiagnosticsClipboardService(Func<GatewayCommandCenterState> captureState)
    {
        _captureState = captureState;
    }

    public void CopyDiagnostic(string label, Func<GatewayCommandCenterState, string> format)
    {
        try
        {
            App.CopyTextToClipboard(format(_captureState()));
            Logger.Info($"Copied {label} from deep link");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy {label} from deep link: {ex.Message}");
        }
    }

    public void CopySupportContext() =>
        CopyDiagnostic("support context", CommandCenterTextHelper.BuildSupportContext);

    public void CopyDebugBundle() =>
        CopyDiagnostic("debug bundle", CommandCenterTextHelper.BuildDebugBundle);

    public void CopyBrowserSetupGuidance() =>
        CopyDiagnostic("browser setup guidance", CommandCenterTextHelper.BuildBrowserSetupGuidance);

    public void CopyPortDiagnostics() =>
        CopyDiagnostic("port diagnostics", s => CommandCenterTextHelper.BuildPortDiagnosticsSummary(s.PortDiagnostics));

    public void CopyCapabilityDiagnostics() =>
        CopyDiagnostic("capability diagnostics", CommandCenterTextHelper.BuildCapabilityDiagnosticsSummary);

    public void CopyNodeInventory() =>
        CopyDiagnostic("node inventory", s => CommandCenterTextHelper.BuildNodeInventorySummary(s.Nodes));

    public void CopyChannelSummary() =>
        CopyDiagnostic("channel summary", s => CommandCenterTextHelper.BuildChannelSummaryText(s.Channels));

    public void CopyActivitySummary() =>
        CopyDiagnostic("activity summary", s => CommandCenterTextHelper.BuildActivitySummary(s.RecentActivity));

    public void CopyExtensibilitySummary() =>
        CopyDiagnostic("extensibility summary", s => CommandCenterTextHelper.BuildExtensibilitySummary(s.Channels));
}
