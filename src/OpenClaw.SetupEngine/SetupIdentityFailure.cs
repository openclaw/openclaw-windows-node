using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

internal static class SetupIdentityFailure
{
    public static StepResult Terminal(
        SetupContext context,
        string operation,
        DeviceIdentityLoadException exception)
    {
        var cause = exception.InnerException;
        var detail = cause == null
            ? exception.GetType().Name
            : $"{cause.GetType().Name}: {cause.Message}";
        context.Logger.Error($"Saved device identity load failed during {operation}: {detail}");
        return StepResult.Terminal(DeviceIdentityLoadException.RecoveryMessage, exception);
    }
}
