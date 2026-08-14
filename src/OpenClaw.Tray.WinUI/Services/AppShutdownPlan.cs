namespace OpenClawTray.Services;

internal sealed record AppShutdownStep(string Name, Func<ValueTask> Execute);

internal sealed record AppShutdownPlan(
    Action BeginShutdown,
    IReadOnlyList<AppShutdownStep> Steps,
    Action ExitApplication);
