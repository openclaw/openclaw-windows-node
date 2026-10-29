using System.Diagnostics.CodeAnalysis;

namespace OpenClawTray;

/// <summary>
/// Selects the single destructive operation requested on the tray command line.
/// Only the selected flag is forwarded to SetupEngine, so SetupEngine never sees a
/// conflicting pair; the tray must reject the combination itself.
/// </summary>
internal static class DestructiveCliOperation
{
    public const string UninstallFlag = "--uninstall";
    public const string ResetLocalAiFlag = OpenClawTray.Services.LocalAiResetService.ResetLocalAiFlag;

    public static bool IsRequested(IReadOnlyCollection<string> args) =>
        args.Contains(UninstallFlag, StringComparer.OrdinalIgnoreCase) ||
        args.Contains(ResetLocalAiFlag, StringComparer.OrdinalIgnoreCase);

    public static bool TrySelect(
        IReadOnlyCollection<string> args,
        [NotNullWhen(true)] out string? operationFlag,
        [NotNullWhen(false)] out string? error)
    {
        bool uninstall = args.Contains(UninstallFlag, StringComparer.OrdinalIgnoreCase);
        bool resetLocalAi = args.Contains(ResetLocalAiFlag, StringComparer.OrdinalIgnoreCase);
        operationFlag = null;
        error = null;
        if (uninstall && resetLocalAi)
        {
            error = $"ERROR: {UninstallFlag} and {ResetLocalAiFlag} cannot be used together.";
            return false;
        }
        if (!uninstall && !resetLocalAi)
        {
            error = "ERROR: no destructive operation was requested.";
            return false;
        }
        operationFlag = uninstall ? UninstallFlag : ResetLocalAiFlag;
        return true;
    }
}
