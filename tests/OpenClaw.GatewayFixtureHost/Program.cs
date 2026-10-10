using System.Net;
using System.Net.Sockets;

namespace OpenClaw.GatewayFixtureHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--control-listen")
            return await ListenForControlAsync(int.Parse(args[1]), args[2]);
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("Usage: dotnet OpenClaw.GatewayFixtureHost.dll --app <built-app.exe> [--artifacts <directory>] [--duration-seconds <1..86400>] [--browser-proxy] [--browser-proxy-foreign]");
            Console.WriteLine("Starts the multi-session-browse fixture and an isolated real app. Ctrl+C stops only this run.");
            return args.Length == 0 ? 2 : 0;
        }
        string? appPath = null;
        string? artifacts = null;
        int? durationSeconds = null;
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
            if (index + 1 >= args.Length || args[index] is not ("--app" or "--artifacts" or "--duration-seconds"))
            {
                Console.Error.WriteLine($"Unknown or incomplete option: {args[index]}");
                return 2;
            }
            if (args[index] == "--app") appPath = args[++index];
            else if (args[index] == "--artifacts") artifacts = args[++index];
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
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var run = await GatewayFixtureRun.StartAsync(
                appPath, artifacts, stop.Token,
                enableNodeBrowserProxy: enableNodeBrowserProxy,
                foreignBrowserControl: foreignBrowserControl);
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
            Console.WriteLine("Synthetic read-only Gateway. Use Chat, Sessions, Settings and Configuration. Ctrl+C stops this run.");
            if (durationSeconds is { } seconds)
                stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                while (run.IsRunning)
                    await Task.Delay(250, stop.Token);
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
