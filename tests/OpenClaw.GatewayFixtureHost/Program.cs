using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.GatewayFixtureHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--control-listen")
            return await ListenForControlAsync(int.Parse(args[1]), args[2]);
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("Usage: dotnet OpenClaw.GatewayFixtureHost.dll --app <built-app.exe> [--scenario multi-session-browse|chat-interactive|welcome-send-recovery|context-usage-snapshots] [--interaction <name>] [--artifacts <directory>] [--duration-seconds <1..86400>] [--browser-proxy] [--browser-proxy-foreign]");
            Console.WriteLine("Starts a synthetic Gateway and an isolated real app. Ctrl+C stops only this run.");
            return args.Length == 0 ? 2 : 0;
        }
        string? appPath = null;
        string? artifacts = null;
        int? durationSeconds = null;
        var scenarioName = GatewayScenario.BrowseName;
        string? interaction = null;
        var enableNodeBrowserProxy = false;
        var foreignBrowserControl = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--browser-proxy")
            {
                enableNodeBrowserProxy = true;
                continue;
            }
            if (args[index] == "--browser-proxy-foreign")
            {
                enableNodeBrowserProxy = true;
                foreignBrowserControl = true;
                continue;
            }
            if (index + 1 >= args.Length || args[index] is not ("--app" or "--artifacts" or "--duration-seconds" or "--scenario" or "--interaction"))
            {
                Console.Error.WriteLine($"Unknown or incomplete option: {args[index]}");
                return 2;
            }
            if (args[index] == "--app") appPath = args[++index];
            else if (args[index] == "--artifacts") artifacts = args[++index];
            else if (args[index] == "--scenario") scenarioName = args[++index];
            else if (args[index] == "--interaction") interaction = args[++index];
            else if (int.TryParse(args[++index], out var duration) && duration is >= 1 and <= 86400)
                durationSeconds = duration;
            else
            {
                Console.Error.WriteLine("--duration-seconds must be between 1 and 86400.");
                return 2;
            }
        }
        if (string.IsNullOrWhiteSpace(appPath))
        {
            Console.Error.WriteLine("--app is required. The installed app is never selected implicitly.");
            return 2;
        }
        if (scenarioName is not (GatewayScenario.BrowseName or GatewayScenario.InteractiveName or GatewayScenario.WelcomeRecoveryName or ContextUsageFixture.Name))
        {
            Console.Error.WriteLine("Unknown fixture scenario.");
            return 2;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var usage = scenarioName == ContextUsageFixture.Name ? new ContextUsageFixture() : null;
            var scenario = usage is null ? GatewayScenario.LoadBuiltin(scenarioName) : GatewayScenario.CreateContextUsage(usage);
            if (interaction is not null && (!scenario.IsInteractive || !FixturePlayback.Scenarios.Contains(interaction)))
                throw new ArgumentException("--interaction requires chat-interactive and a supported interaction name.");
            await using var run = await GatewayFixtureRun.StartAsync(
                appPath, artifacts, stop.Token,
                enableNodeBrowserProxy: enableNodeBrowserProxy,
                foreignBrowserControl: foreignBrowserControl,
                scenario: scenario);
            if (interaction is not null) run.Gateway.Interactive!.Playback.Select(interaction);
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            Console.WriteLine($"Fixture ready. App PID: {run.AppProcessId}");
            Console.WriteLine($"Gateway: {run.Gateway.Endpoint}");
            if (run.BrowserControlPort is { } controlPort)
                Console.WriteLine($"Browser control: http://127.0.0.1:{controlPort}/ owner={(run.ForeignControlCountFile is null ? "parent" : "child")}");
            if (run.ForeignControlCountFile is { } countFile)
                Console.WriteLine($"Foreign control count: {countFile}");
            Console.WriteLine($"MCP: http://127.0.0.1:{run.McpPort}/");
            Console.WriteLine($"Profile: {run.Profile.DataDirectory}");
            Console.WriteLine($"Artifacts: {run.ArtifactsDirectory}");
            Console.WriteLine(scenario.IsInteractive
                ? "Synthetic interactive Gateway. No provider or host command executes. Type help for fixture controls."
                : usage is null
                    ? "Synthetic read-only Gateway. Use Chat, Sessions, Settings and Configuration. Ctrl+C stops this run."
                : $"Synthetic usage scenario. Select '{ContextUsageFixture.Title(ContextUsageListing.Initial)}' under Fixture Main.");
            var input = new StringBuilder();
            Task? controls = null;
            if (usage is not null)
            {
                Console.WriteLine($"Send exactly '{ContextUsageFixture.FirstPrompt}', then '{ContextUsageFixture.SecondPrompt}' in the app.");
                Console.WriteLine("After those replies, enter host console commands: missing-total, lower800, lower600, delayed1200. 'stop' ends this run.");
                controls = Task.Run(() => ReadUsageControlsAsync(run, usage, stop));
            }
            if (durationSeconds is { } seconds)
                stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                while (run.IsRunning)
                {
                    run.Gateway.Interactive?.ThrowIfFaulted();
                    if (controls?.IsFaulted == true) await controls;
                    // Nonblocking console reads keep duration/Ctrl+C cleanup bounded.
                    if (scenario.IsInteractive && !Console.IsInputRedirected)
                        while (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            if (key.Key == ConsoleKey.Enter)
                            {
                                Console.WriteLine();
                                await ControlAsync(run, input.ToString(), stop.Token);
                                input.Clear();
                            }
                            else if (key.Key == ConsoleKey.Backspace && input.Length > 0)
                            {
                                input.Length--;
                                Console.Write("\b \b");
                            }
                            else if (!char.IsControl(key.KeyChar) && input.Length < 16_384)
                            {
                                input.Append(key.KeyChar);
                                Console.Write(key.KeyChar);
                            }
                        }
                    await Task.Delay(250, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Ctrl+C is the explicit interactive stop operation.
            }
            if (!stop.IsCancellationRequested && run.AppExitCode is not 0)
            {
                var failure = new InvalidOperationException($"Fixture app exited unexpectedly (code {run.AppExitCode}).");
                await run.WriteReportAsync("app-failed", failure);
                Console.Error.WriteLine(failure.Message);
                return 1;
            }
            await run.WriteReportAsync("stopped");
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task ControlAsync(GatewayFixtureRun run, string command, CancellationToken ct)
    {
        var runtime = run.Gateway.Interactive!;
        try
        {
            switch (command.Trim())
            {
                case "help":
                    Console.WriteLine($"Next response: {string.Join(", ", FixturePlayback.Scenarios)}");
                    Console.WriteLine("Controls: pause, step, resume, disconnect, questions, resolve <question.resolve JSON params>");
                    Console.WriteLine("Scenarios apply to the next accepted send, then return to streaming. Stop/answers remain available while paused.");
                    break;
                case "pause": runtime.Playback.Pause(); break;
                case "step": runtime.Playback.Step(); break;
                case "resume": runtime.Playback.Resume(); break;
                case "disconnect": await run.Gateway.CloseConnectionsAsync(ct); break;
                case "questions":
                    Console.WriteLine(JsonSerializer.Serialize(await runtime.RespondAsync("question.list", JsonSerializer.SerializeToElement(new { }), ct)));
                    break;
                case var value when value.StartsWith("resolve ", StringComparison.Ordinal):
                    using (var json = JsonDocument.Parse(value["resolve ".Length..]))
                        Console.WriteLine(JsonSerializer.Serialize(await runtime.RespondAsync("question.resolve", json.RootElement, ct)));
                    break;
                case "": break;
                default:
                    runtime.Playback.Select(command.Trim());
                    Console.WriteLine($"Next response: {command.Trim()}");
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FixtureRequestException)
        {
            Console.Error.WriteLine($"Fixture control rejected: {ex.Message}");
        }
    }

    private static async Task ReadUsageControlsAsync(GatewayFixtureRun run, ContextUsageFixture usage,
        CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested &&
                await Console.In.ReadLineAsync(stop.Token) is { } command)
            {
                if (command == "stop") { stop.Cancel(); return; }
                ContextUsageListing? listing = command switch
                {
                    "missing-total" => ContextUsageListing.MissingTotal,
                    "lower800" => ContextUsageListing.Lower800,
                    "lower600" => ContextUsageListing.Lower600,
                    "delayed1200" => ContextUsageListing.Delayed1200,
                    _ => null
                };
                if (listing is null)
                {
                    Console.WriteLine("Unknown fixture control. No state changed.");
                    continue;
                }
                usage.SelectListing(listing.Value);
                await run.Gateway.NotifySessionsChangedAsync(stop.Token);
                Console.WriteLine($"Requested {ContextUsageFixture.Title(listing.Value)}.");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private static async Task<int> ListenForControlAsync(int port, string countFile)
    {
        var requests = 0;
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            return 1;
        }

        File.WriteAllText(countFile, "requests=0\n");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                requests++;
                File.WriteAllText(countFile, $"requests={requests}\n");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
        return 0;
    }
}
