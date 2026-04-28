using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum UrlNavigationApprovalDecisionKind
{
    Deny,
    AllowOnce,
    AllowHost,
}

internal sealed record UrlNavigationApprovalDecision(UrlNavigationApprovalDecisionKind Kind, string? Reason = null)
{
    public static UrlNavigationApprovalDecision Deny(string? reason = null) => new(UrlNavigationApprovalDecisionKind.Deny, reason);
    public static UrlNavigationApprovalDecision AllowOnce() => new(UrlNavigationApprovalDecisionKind.AllowOnce);
    public static UrlNavigationApprovalDecision AllowHost() => new(UrlNavigationApprovalDecisionKind.AllowHost);
}

/// <summary>
/// Confirmation prompt for high-risk navigations. Backed by Win32 MessageBoxW
/// rather than a WinUI ContentDialog: the tray's only live XAML window is the
/// off-screen keep-alive used to anchor the WinUI runtime, so a ContentDialog
/// rendered against its XamlRoot would never be visible. Spinning up a fresh
/// visible Window for the prompt crashed with ExecutionEngineException when
/// invoked from the MCP handler's nested dispatcher callback. Win32 MessageBox
/// has no WinUI dependencies, can be called from any thread, and pumps its own
/// modal message loop — so it's reliable from the MCP request path regardless
/// of which (if any) tray windows are open.
/// </summary>
internal sealed class UrlNavigationApprovalService
{
    private readonly IOpenClawLogger _logger;

    public UrlNavigationApprovalService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    public Task<UrlNavigationApprovalDecision> RequestAsync(
        HttpUrlRiskProfile risk,
        string agentIdentity,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(UrlNavigationApprovalDecision.Deny("Navigation prompt was cancelled"));

        var caption = "OpenClaw — Approve URL";
        var reasonsBlock = risk.Reasons.Count == 0
            ? "(no specific reasons captured)"
            : string.Join(Environment.NewLine, risk.Reasons.Select(r => "• " + r));
        var text =
            "An agent wants to open a high-risk destination in your browser." + Environment.NewLine +
            Environment.NewLine +
            risk.CanonicalOrigin + Environment.NewLine +
            Environment.NewLine +
            $"Zone: {risk.Zone}" + Environment.NewLine +
            $"Agent: {agentIdentity}" + Environment.NewLine +
            $"Host: {risk.HostKey}" + Environment.NewLine +
            Environment.NewLine +
            "Reasons:" + Environment.NewLine + reasonsBlock + Environment.NewLine +
            Environment.NewLine +
            "Yes — open this URL." + Environment.NewLine +
            "No — block it.";

        // Run MessageBoxW on a worker thread so the calling MCP handler's
        // continuation doesn't pin while the user thinks. MessageBoxW pumps its
        // own modal loop, so it's safe to call from a non-UI thread.
        return Task.Run(() =>
        {
            try
            {
                var flags = MB_YESNO | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND | MB_DEFBUTTON2;
                var result = MessageBoxW(IntPtr.Zero, text, caption, flags);
                var decision = result == IDYES
                    ? UrlNavigationApprovalDecision.AllowOnce()
                    : UrlNavigationApprovalDecision.Deny();
                _logger.Info($"[NavigationApproval] Prompt decision: {decision.Kind} for {risk.CanonicalOrigin}");
                return decision;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[NavigationApproval] Prompt failed: {ex.Message}");
                return UrlNavigationApprovalDecision.Deny($"Prompt failed: {ex.Message}");
            }
        }, cancellationToken);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const int IDYES = 6;
}
