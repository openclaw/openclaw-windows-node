using Microsoft.Extensions.DependencyInjection;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// The WinUI-free core of the App composition root. Registers the presentation-layer
/// infrastructure and the App-owned singletons that view models depend on.
/// </summary>
/// <remarks>
/// Ownership rules encoded here:
/// <list type="bullet">
/// <item>App-owned singletons (<see cref="IUiDispatcher"/>, <see cref="IAppCommands"/>,
/// <see cref="SettingsManager"/>) are registered as <b>pre-built instances</b>, so the
/// container never disposes them — App remains their sole owner (no double-dispose).</item>
/// <item><see cref="NavigationScopeManager"/> is a container-created singleton, so the
/// container disposes it when the root provider is disposed.</item>
/// <item>Page view models are transient and are resolved from a per-navigation scope,
/// so they are disposed when navigation moves away.</item>
/// </list>
/// WinUI-bound registrations (the dispatcher/page-activator/navigation adapters) are
/// added separately by App so this method stays unit-testable in a pure net10 project.
/// </remarks>
internal static class AppServiceRegistration
{
    public static IServiceCollection AddOpenClawTrayCore(this IServiceCollection services, AppServiceContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        // App-owned singletons: pre-built instances → container does not dispose them.
        services.AddSingleton(context.Dispatcher);
        services.AddSingleton(context.AppCommands);
        services.AddSingleton(context.Settings);

        // Settings facade over the App-owned SettingsManager. Constructed eagerly from the
        // already-owned singletons so presentation code depends on ISettingsStore, never the
        // concrete manager. It subscribes to the manager (which App owns) and is not disposed
        // by the container.
        services.AddSingleton<ISettingsStore>(new SettingsStore(context.Settings, context.Dispatcher));

        // Container-owned navigation lifetime manager (disposed with the root provider).
        services.AddSingleton<NavigationScopeManager>();

        // Transient page view models resolved per navigation scope.
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<PermissionsPageViewModel>();

        return services;
    }
}
