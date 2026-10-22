using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal sealed record ActivationConfirmation(string ActionDisplayName, string RedactedInput);

internal enum LaunchActivationKind
{
    Launch, Protocol, StartupTask, Other
}

internal sealed record LaunchActivationInput(
    string? ProtocolUri,
    IReadOnlyList<string> CommandLineArguments,
    string? PostSetupLaunch,
    bool SetupShownDuringStartup,
    LaunchActivationKind Kind = LaunchActivationKind.Launch);

internal abstract record ActivationPlan
{
    internal sealed record Ignore : ActivationPlan;
    internal sealed record Dispatch(ActivationRoute Route) : ActivationPlan;
    internal sealed record Confirm(ActivationRoute Route, ActivationConfirmation Prompt) : ActivationPlan;

    private ActivationPlan()
    {
    }
}

internal interface IActivationPlanSink
{
    Task DispatchAsync(ActivationRoute route, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(ActivationConfirmation confirmation, CancellationToken cancellationToken);
}
