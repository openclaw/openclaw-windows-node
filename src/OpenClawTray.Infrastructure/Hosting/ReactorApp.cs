using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Hosting;
using OpenClawTray.Infrastructure.Hosting.Devtools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinUI = Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Configuration for ReactorApp.Run. Scoped as a single record to avoid scattered static fields.
/// </summary>
internal record ReactorAppOptions(
    Func<Component>? RootFactory = null,
    Func<RenderContext, Element>? RootRenderFunc = null,
    Action<ReactorHost>? Configure = null,
    string WindowTitle = "Reactor App",
    int WindowWidth = 1024,
    int WindowHeight = 768,
    bool FullScreen = false);

public static class ReactorApp
{
    // Application.Start blocks and creates ReactorApplication via parameterless constructor,
    // so we must communicate config through a static. Using a single record keeps this scoped.
    private static ReactorAppOptions _options = new();
    internal static ReactorAppOptions Options
    {
        get => Volatile.Read(ref _options);
        set => Volatile.Write(ref _options, value);
    }
    private static ReactorHost? _activeHost;
    public static ReactorHost? ActiveHost
    {
        get => Volatile.Read(ref _activeHost);
        internal set => Volatile.Write(ref _activeHost, value);
    }

    private static int _previewParamDeprecationWarned;

    // Session-scoped flag. True iff the process was launched with a devtools
    // subverb (--devtools app / --devtools run) AND the developer passed
    // devtools: true to Run. Frozen after startup; read by UseDevtools() and
    // by the DevtoolsMenu component to decide whether to render themselves.
    private static int _devtoolsEnabled;
    public static bool DevtoolsEnabled
    {
        get => Volatile.Read(ref _devtoolsEnabled) != 0;
        internal set => Volatile.Write(ref _devtoolsEnabled, value ? 1 : 0);
    }

    // Unpackaged WinUI apps (WindowsPackageType=None) don't inherit DPI awareness from an
    // MSIX manifest, so the process defaults to DPI-unaware and Windows applies blurry bitmap
    // scaling. Setting PerMonitorV2 awareness before any window is created tells the OS the
    // app will handle DPI itself, producing crisp rendering at any scale factor.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    /// <summary>
    /// Launches the app. Set <c>devtools: true</c> in DEBUG builds to enable the
    /// <c>mur devtools</c> / <c>--devtools</c> surface: component switching via VS Code,
    /// MCP agent tools (Phase 2+), and component listing.
    /// </summary>
    /// <remarks>
    /// The <c>preview</c> parameter is deprecated and is kept for one release. When both are
    /// passed, <c>devtools</c> wins.
    /// </remarks>
    public static void Run<TRoot>(
        string title = "Reactor App",
        int width = 1024,
        int height = 768,
        bool fullScreen = false,
        bool devtools = false,
        // DEPRECATED: use 'devtools:'. Kept for one release. The runtime emits a
        // one-shot stderr warning when this is set without 'devtools:'.
        bool preview = false,
        Action<ReactorHost>? configure = null)
        where TRoot : Component, new()
    {
        var effectiveDevtools = ResolveDevtoolsParam(devtools, preview);
        if (effectiveDevtools && TryRunDevtools(title, width, height, configure, hostRoot: typeof(TRoot))) return;

        RunOnSta(() =>
        {
            InitProcess();
            Options = new ReactorAppOptions(
                RootFactory: () => new TRoot(),
                Configure: configure,
                WindowTitle: title,
                WindowWidth: width,
                WindowHeight: height,
                FullScreen: fullScreen);

            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new ReactorApplication();
            });
        });
    }

    /// <summary>
    /// Launches the app with a render function instead of a Component subclass.
    /// See the generic overload for <c>devtools</c> semantics.
    /// </summary>
    public static void Run(
        string title,
        Func<RenderContext, Element> rootRender,
        int width = 1024,
        int height = 768,
        bool fullScreen = false,
        bool devtools = false,
        // DEPRECATED: use 'devtools:'. Kept for one release. The runtime emits a
        // one-shot stderr warning when this is set without 'devtools:'.
        bool preview = false,
        Action<ReactorHost>? configure = null)
    {
        var effectiveDevtools = ResolveDevtoolsParam(devtools, preview);
        if (effectiveDevtools && TryRunDevtools(title, width, height, configure)) return;

        RunOnSta(() =>
        {
            InitProcess();
            Options = new ReactorAppOptions(
                RootRenderFunc: rootRender,
                Configure: configure,
                WindowTitle: title,
                WindowWidth: width,
                WindowHeight: height,
                FullScreen: fullScreen);

            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new ReactorApplication();
            });
        });
    }

    /// <summary>
    /// Reconciles the deprecated <c>preview:</c> parameter with the new <c>devtools:</c>.
    /// If only <c>preview</c> is set, emit a one-time deprecation warning to stderr.
    /// </summary>
    internal static bool ResolveDevtoolsParam(bool devtools, bool preview)
    {
        if (preview && !devtools && Interlocked.Exchange(ref _previewParamDeprecationWarned, 1) == 0)
        {
            Console.Error.WriteLine("[reactor] 'preview:' is deprecated; use 'devtools:'.");
        }
        return devtools || preview;
    }

    /// <summary>
    /// Checks the process command-line for <c>--devtools</c> or the deprecated <c>--preview</c>.
    /// If a devtools subverb is selected, launches the corresponding flow (list, run, etc.).
    /// With <c>--vscode</c>, starts the capture server for the VS Code preview panel. Only
    /// active when the caller passes <c>devtools: true</c>.
    /// </summary>
    private static bool TryRunDevtools(string title, int width, int height, Action<ReactorHost>? configure, Type? hostRoot = null)
    {
        var args = Environment.GetCommandLineArgs();
        var options = DevtoolsCliParser.Parse(args);

        if (options.PreviewAndDevtoolsConflict)
        {
            Console.Error.WriteLine("[devtools] Error: pass either --devtools or --preview, not both.");
            return true;
        }

        if (options.Subverb is null) return false;

        // Install log capture as the very first side-effect after we know
        // devtools is active. Runs before component reflection, before any
        // Application.Start, so startup Debug/Trace/Console output is caught
        // even when the agent attaches late. Skipped when `--devtools-logs off`
        // is set. In stdio transport we must NOT forward Console.Out (that's
        // the JSON-RPC frame) — writes still land in the buffer, just not
        // passed through to the parent process.
        if (options.Subverb == DevtoolsSubverb.Run && !options.LogsDisabled)
        {
            var capBytes = options.LogsCapacityMb is { } mb
                ? (long)mb * 1024 * 1024
                : LogCaptureBuffer.DefaultCapacityBytes;
            var forwardOut = options.Transport != McpTransport.Stdio;
            LogCaptureInstall.Install(capBytes, forwardConsole: forwardOut);
        }

        if (options.UsedDeprecatedPreview)
            Console.Error.WriteLine("[reactor] '--preview' is deprecated; use '--devtools run'.");

        switch (options.Subverb)
        {
            case DevtoolsSubverb.List:
                return RunListSubverb(options);
            case DevtoolsSubverb.Run:
                DevtoolsEnabled = true;
                return RunRunSubverb(options, title, width, height, configure, hostRoot);
            case DevtoolsSubverb.Screenshot:
                return RunScreenshotSubverb(options, width, height, configure, hostRoot);
            case DevtoolsSubverb.Tree:
                Console.Error.WriteLine($"[devtools] '--devtools tree' (headless) is not implemented yet.");
                return true;
            case DevtoolsSubverb.App:
                // Pass-through mode: enable the in-app dev UI flag and let the
                // caller's normal run loop proceed (returning false skips the
                // short-circuit in Run<TRoot>).
                DevtoolsEnabled = true;
                return false;
            default:
                return false;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Activator.CreateInstance for component types resolved by reflection.")]
    private static bool RunScreenshotSubverb(DevtoolsCliOptions options, int width, int height, Action<ReactorHost>? configure, Type? hostRoot = null)
    {
        if (string.IsNullOrEmpty(options.ScreenshotOutputPath))
        {
            Console.Error.WriteLine("[devtools] '--devtools screenshot' requires --out <path.png>.");
            return true;
        }

        var componentName = options.ComponentName ?? hostRoot?.Name ?? FindAllComponentNames().FirstOrDefault();
        if (componentName == null)
        {
            Console.Error.WriteLine("[devtools] No Component subclasses found.");
            return true;
        }
        var type = FindComponentType(componentName);
        if (type == null)
        {
            Console.Error.WriteLine($"[devtools] Component '{componentName}' not found.");
            return true;
        }

        string outPath = options.ScreenshotOutputPath!;

        RunOnSta(() =>
        {
            InitProcess();

            Options = new ReactorAppOptions(
                RootFactory: () => (Core.Component)Activator.CreateInstance(type)!,
                Configure: host =>
                {
                    configure?.Invoke(host);
                    // Capture once after first render, then exit. UpdateLayout flushes
                    // pending measure/arrange so the first frame is stable.
                    host.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            if (host.Window.Content is FrameworkElement fe) fe.UpdateLayout();
                            var capture = ScreenshotCapture.CaptureWindow(host.Window, includeChrome: false);
                            File.WriteAllBytes(outPath, capture.Png);
                            Console.WriteLine($"[devtools] Wrote {capture.Width}x{capture.Height} PNG to {outPath}");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[devtools] Screenshot failed: {ex.Message}");
                        }
                        finally
                        {
                            Environment.Exit(0);
                        }
                    });
                },
                WindowTitle: $"Screenshot — {componentName}",
                WindowWidth: width,
                WindowHeight: height);

            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new ReactorApplication();
            });
        });

        return true;
    }

    private static bool RunListSubverb(DevtoolsCliOptions options)
    {
        var names = FindAllComponentNames().ToList();
        foreach (var name in names)
            Console.WriteLine(name);
        Console.Out.Flush();
        if (!string.IsNullOrEmpty(options.ListOutputPath))
            File.WriteAllLines(options.ListOutputPath, names);
        return true;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Activator.CreateInstance for component types resolved by reflection.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Generic type parameter flows through for component instantiation.")]
    private static bool RunRunSubverb(DevtoolsCliOptions options, string title, int width, int height, Action<ReactorHost>? configure, Type? hostRoot = null)
    {
        _ = title;

        // Resolve the initial component type. Precedence:
        //   1. Explicit --component on the command line — the user asked.
        //   2. The TRoot type that the host passed to Run<TRoot> — matches their
        //      intent and avoids "first-alphabetical" surprises where a nested
        //      helper component wins over the real app root.
        //   3. Fallback to the first component the reflection scan finds.
        string? componentName = options.ComponentName;
        Type? componentType = null;
        if (componentName != null)
        {
            componentType = FindComponentType(componentName);
            if (componentType == null)
            {
                Console.Error.WriteLine($"[devtools] Component '{componentName}' not found.");
                Console.Error.WriteLine($"[devtools] Available components: {string.Join(", ", FindAllComponentNames())}");
                return true;
            }
        }
        else if (hostRoot != null && typeof(Core.Component).IsAssignableFrom(hostRoot) && !hostRoot.IsAbstract)
        {
            componentType = hostRoot;
            componentName = hostRoot.Name;
        }
        else
        {
            var firstName = FindAllComponentNames().FirstOrDefault();
            if (firstName == null)
            {
                Console.Error.WriteLine("[devtools] No Component subclasses found.");
                return true;
            }
            componentType = FindComponentType(firstName)!;
            componentName = firstName;
            Console.Error.WriteLine(
                $"[devtools] No --component passed and Run<T> not detected; defaulting to '{firstName}' (alphabetical). " +
                $"Pass --component to pick another.");
        }

        bool vscodeMode = options.VsCodeMode;
        int captureFps = options.Fps;

        Console.WriteLine($"[devtools] Previewing {componentType.FullName}");
        Console.WriteLine($"[devtools] Hot reload active — edit and save to see changes instantly");
        if (vscodeMode) Console.WriteLine($"[devtools] VS Code mode enabled (capture @ {captureFps} fps)");

        var initialComponentType = componentType;
        var initialComponentName = componentName;

        RunOnSta(() =>
        {
            InitProcess();

            Action<ReactorHost> combinedConfigure = host =>
            {
                configure?.Invoke(host);

                // Shared switch-component callback — reused by both the VS Code
                // capture server and the MCP devtools server so they agree on
                // the active component.
                bool SwitchComponentCore(string name)
                {
                    var type = FindComponentType(name);
                    if (type == null) return false;

                    host.Window.DispatcherQueue.TryEnqueue(() =>
                    {
                        var instance = (Core.Component)Activator.CreateInstance(type)!;
                        host.Mount(instance);
                        host.Window.Title = $"Preview — {name}";
                    });

                    initialComponentName = name;
                    Console.WriteLine($"[devtools] Switched to {type.FullName}");
                    return true;
                }

                if (vscodeMode)
                {
                    var server = new PreviewCaptureServer(
                        host.Window.DispatcherQueue,
                        host.Window,
                        captureFps);

                    server.GetComponents = () => FindAllComponentNames().ToList();
                    server.GetCurrentComponent = () => initialComponentName;
                    server.SwitchComponent = SwitchComponentCore;

                    server.Start();
                    host.Window.Closed += (_, _) => server.Dispose();
                }

                // MCP devtools server — always on when --devtools run is active.
                // Port pinned by --mcp-port for the supervisor reload loop.
                // Log level pinned by --devtools-log-level (default: call).
                var logger = new DevtoolsLogger(
                    DevtoolsLogger.DefaultDirectory(),
                    global::System.Diagnostics.Process.GetCurrentProcess().Id,
                    DevtoolsLogger.ParseLevel(options.LogLevel));
                var projectId = options.ProjectIdentifier ?? DeriveProjectIdentifier();
                if (projectId is not null && DevtoolsMcpServer.IsAnotherSessionActive(projectId, out var existing))
                {
                    Console.Error.WriteLine(
                        $"[devtools] another session for this project is active at {existing!.Endpoint} (pid {existing.Pid}); stop it first");
                    Environment.Exit(3);
                    return;
                }

                var mcp = new DevtoolsMcpServer(
                    host.Window.DispatcherQueue,
                    host.Window,
                    preferredPort: options.McpPort,
                    logger: logger,
                    transport: options.Transport,
                    projectIdentifier: projectId);

                var windows = new WindowRegistry(mcp.BuildTag);
                var nodes = new NodeRegistry();
                // Pin the primary devtools window to "main" so the handle
                // doesn't drift when switchComponent updates the title.
                windows.Attach(host.Window, isMain: true, stableId: "main");

                DevtoolsTools.RegisterCore(mcp, new DevtoolsTools.ToolHostContext
                {
                    GetComponents = () => FindAllComponentNames().ToList(),
                    GetComponentsDetailed = () => FindAllComponentsDetailed().ToList(),
                    GetCurrentComponent = () => initialComponentName,
                    SwitchComponent = SwitchComponentCore,
                    RequestReload = () => RequestDevtoolsReload(mcp, host),
                    RequestShutdown = () => RequestDevtoolsShutdown(mcp, host),
                    Windows = windows,
                    Nodes = nodes,
                });
                DevtoolsUiaTools.RegisterUiaTools(mcp, nodes, windows);
                DevtoolsFireTool.Register(mcp, () => host.RootComponent);
                DevtoolsStateTool.Register(mcp, () => host.RootComponent);
                DevtoolsLogsTool.Register(mcp, () => LogCaptureInstall.Shared);

                mcp.Start();
                // Ready line fires after the first render — subscribe once to the host.
                bool announced = false;
                host.Window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (announced) return;
                    announced = true;
                    mcp.AnnounceReady();
                });
                host.Window.Closed += (_, _) => mcp.Dispose();
            };

            Options = new ReactorAppOptions(
                RootFactory: () => (Core.Component)Activator.CreateInstance(initialComponentType)!,
                Configure: combinedConfigure,
                WindowTitle: $"Preview — {initialComponentName}",
                WindowWidth: width,
                WindowHeight: height);

            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new ReactorApplication();
            });
        });

        return true;
    }

    /// <summary>
    /// Finds a Component type by name across all loaded assemblies (case-insensitive).
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly.GetTypes for devtools component discovery.")]
    internal static Type? FindComponentType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (global::System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            catch { continue; }

            var match = types.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) &&
                typeof(Core.Component).IsAssignableFrom(t) &&
                !t.IsAbstract);
            if (match != null) return match;
        }
        return null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly.GetTypes for devtools component enumeration.")]
    internal static IEnumerable<string> FindAllComponentNames()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch (global::System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; } catch { return []; } })
            .Where(t => typeof(Core.Component).IsAssignableFrom(t!) && !t!.IsAbstract && !t.FullName!.StartsWith("OpenClawTray.Infrastructure."))
            .Select(t => t!.Name)
            .Distinct()
            .OrderBy(n => n);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Assembly.GetTypes for devtools detailed component listing.")]
    internal static IEnumerable<Hosting.Devtools.ComponentInfo> FindAllComponentsDetailed()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch (global::System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; } catch { return []; } })
            .Where(t => typeof(Core.Component).IsAssignableFrom(t!) && !t!.IsAbstract && !t.FullName!.StartsWith("OpenClawTray.Infrastructure."))
            .Select(t => new Hosting.Devtools.ComponentInfo(
                Name: t!.Name,
                FullName: t.FullName ?? t.Name,
                IsNested: t.IsNested,
                IsPublic: t.IsPublic || t.IsNestedPublic,
                Namespace: t.Namespace))
            .GroupBy(c => c.Name)
            .Select(g => g.First());
    }

    /// <summary>
    /// Identifier used to hash this session's lockfile path when the supervisor
    /// didn't pass <c>--devtools-project</c>. Falls back to the entry assembly
    /// location — stable per build output, sufficient for single-instance.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3000", Justification = "Assembly.Location used for diagnostic project identifier.")]
    private static string? DeriveProjectIdentifier()
    {
        try
        {
            var asm = global::System.Reflection.Assembly.GetEntryAssembly();
            var loc = asm?.Location;
            if (!string.IsNullOrEmpty(loc)) return loc;
        }
        catch { }
        return null;
    }

    internal static void ResetDeprecationWarningForTests()
    {
        Interlocked.Exchange(ref _previewParamDeprecationWarned, 0);
    }

    internal static void ResetDevtoolsEnabledForTests()
    {
        Interlocked.Exchange(ref _devtoolsEnabled, 0);
    }

    /// <summary>
    /// Sentinel exit code consumed by the `mur devtools` supervisor to mean
    /// "rebuild and respawn". Any other exit code propagates.
    /// </summary>
    internal const int DevtoolsReloadExitCode = 42;

    private static void RequestDevtoolsReload(DevtoolsMcpServer mcp, ReactorHost host)
    {
        // Response flush happens before shutdown — the tool returns first, then the
        // UI thread disposes the listener and closes the window. Exit 42 tells the
        // supervisor to rebuild and relaunch with the same pinned MCP port.
        _ = Task.Run(async () =>
        {
            await Task.Delay(100); // Let the HTTP response flush.
            try { mcp.Dispose(); } catch { }
            host.Window.DispatcherQueue.TryEnqueue(() =>
            {
                try { host.Window.Close(); } catch { }
                Environment.Exit(DevtoolsReloadExitCode);
            });
        });
    }

    /// <summary>
    /// Same shape as the reload path, but exits with code 0 so the `mur devtools`
    /// supervisor returns cleanly without rebuilding.
    /// </summary>
    private static void RequestDevtoolsShutdown(DevtoolsMcpServer mcp, ReactorHost host)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(100); // Let the HTTP response flush.
            try { mcp.Dispose(); } catch { }
            host.Window.DispatcherQueue.TryEnqueue(() =>
            {
                try { host.Window.Close(); } catch { }
                Environment.Exit(0);
            });
        });
    }

    private static void InitProcess()
    {
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            global::System.Diagnostics.Debug.WriteLine($"SetProcessDpiAwarenessContext failed: {Marshal.GetLastWin32Error()}");
        WinRT.ComWrappersSupport.InitializeComWrappers();
    }

    /// <summary>
    /// Ensures the action runs on an STA thread. WinUI 3's DesktopChildSiteBridge requires
    /// STA for UI Automation (screen readers, test tools) to traverse into the XAML island.
    /// Top-level statements and async Main produce MTA threads where [STAThread] cannot be
    /// applied, so we re-launch on a dedicated STA thread when needed.
    /// </summary>
    private static void RunOnSta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

        // Current thread is MTA — spawn a new STA thread and run there.
        Exception? caught = null;
        var staThread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();
        if (caught is not null)
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caught).Throw();
    }
}

/// <summary>
/// Application subclass that implements IXamlMetadataProvider so the native XAML
/// schema context can resolve managed types from XBF theme resources.
/// No App.xaml needed — XamlControlsResources are loaded programmatically.
/// The IXamlMetadataProvider implementation delegates to the WinUI controls'
/// built-in provider so that custom control types (TextCommandBarFlyout, etc.)
/// can be instantiated from XBF theme resources.
/// </summary>
public partial class ReactorApplication : Application, IXamlMetadataProvider
{
    // The Reactor library's XAML build pipeline generates
    // OpenClawTray.Infrastructure.Reactor_XamlTypeInfo.XamlMetaDataProvider — a full provider
    // that covers ReactorDefaultResources, XamlControlsResources, ResourceDictionary,
    // system primitives, and chains to XamlControlsXamlMetaDataProvider for control
    // types. That generated provider is the right primary delegate: it's AOT-safe,
    // preserves type registration via compile-time code rather than runtime reflection,
    // and correctly handles the schema-only lookups WinUI performs during Application
    // startup when theme dictionaries load.
    //
    // We resolve the generated type at runtime because referencing the generated name
    // directly would make the C# pre-compile (run by the XAML compiler itself) fail with
    // CS0246 — the generated class doesn't exist yet when that check runs. The
    // DynamicDependency keeps the type alive under AOT trimming.
    private IXamlMetadataProvider? _reactorProvider;

    private IXamlMetadataProvider ReactorProvider => _reactorProvider ??= CreateReactorProvider();

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors,
        "OpenClawTray.Infrastructure.Reactor_XamlTypeInfo.XamlMetaDataProvider", "OpenClawTray.Infrastructure")]
    private static IXamlMetadataProvider CreateReactorProvider()
    {
        var t = global::System.Type.GetType("OpenClawTray.Infrastructure.Reactor_XamlTypeInfo.XamlMetaDataProvider, OpenClawTray.Infrastructure", throwOnError: false);
        return t is null
            ? new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider()
            : (IXamlMetadataProvider)global::System.Activator.CreateInstance(t)!;
    }

    // Fallback provider covering types WinUI may look up by-string that are not in the
    // generated library provider (e.g. user-defined types in the consuming project
    // referenced by ResourceDictionary keys). Additive safety net — in the normal path
    // the Reactor provider already satisfies queries.
    private IXamlMetadataProvider? _coreProvider;
    private IXamlMetadataProvider CoreProvider => _coreProvider ??= new Hosting.ReactorCoreXamlMetaDataProvider();

    /// <summary>
    /// Optional callback for unhandled exceptions. If set, called before deciding whether to handle.
    /// Return true to mark the exception as handled; return false (or leave null) to let it crash.
    /// </summary>
    public static Func<Exception, bool>? OnUnhandledException { get; set; }

    private readonly ILogger _logger = NullLogger.Instance;

    public ReactorApplication()
    {
        // When used as a library (consumed via ReactorHostControl), XAML compilation
        // is excluded — the host app provides its own Application with XamlControlsResources.
        // InitializeComponent() is only available when ReactorApplication.xaml is compiled.

        UnhandledException += (_, e) =>
        {
            _logger.LogError(e.Exception, "UnhandledException: {ExceptionType}: {ExceptionMessage}", e.Exception.GetType().Name, e.Exception.Message);
            if (OnUnhandledException is not null)
                e.Handled = OnUnhandledException(e.Exception);
            // Don't set e.Handled = true for unknown exceptions — let the app crash
            // with a useful error rather than silently running in a corrupt state.
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {

        var opts = ReactorApp.Options;
        var window = new Window { Title = opts.WindowTitle };
        if (opts.FullScreen)
            window.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
        else
            window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(opts.WindowWidth, opts.WindowHeight));

        var host = new ReactorHost(window);

        opts.Configure?.Invoke(host);

        if (opts.RootFactory is not null)
        {
            host.Mount(opts.RootFactory());
        }
        else if (opts.RootRenderFunc is not null)
        {
            host.Mount(opts.RootRenderFunc);
        }

        window.Activate();
    }

    // IXamlMetadataProvider — delegate to the library's generated provider (which already
    // chains to XamlControlsXamlMetaDataProvider internally) and fall back to the core
    // provider for any schema-only types the generated one doesn't carry. Returning null
    // here is the WinUI convention for "unknown type" even though the WinRT interface
    // types it as non-nullable.
    public IXamlType GetXamlType(Type type)
        => (ReactorProvider.GetXamlType(type) ?? CoreProvider.GetXamlType(type))!;
    public IXamlType GetXamlType(string fullName)
        => (ReactorProvider.GetXamlType(fullName) ?? CoreProvider.GetXamlType(fullName))!;
    public XmlnsDefinition[] GetXmlnsDefinitions() => ReactorProvider.GetXmlnsDefinitions();
}
