using System.Globalization;
using System.Text;
using OpenClaw.Shared;

internal sealed class CliOptions
{
    public string SettingsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenClawTray",
        "settings.json");

    public string? GatewayUrlOverride { get; set; }
    public string? TokenOverride { get; set; }
    public string Message { get; set; } = "openclaw-cli validation ping";
    public int Repeat { get; set; } = 1;
    public int DelayMs { get; set; } = 500;
    public int ConnectTimeoutMs { get; set; } = 10000;
    public bool ProbeReadApis { get; set; }
    public bool Verbose { get; set; }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            PrintUsage();
            return 0;
        }

        CliOptions options;
        try
        {
            options = ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            PrintUsage();
            return 2;
        }

        var (gatewayUrl, token, loaded) = LoadConnectionFromSettings(options);
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            Console.Error.WriteLine("Gateway URL is missing. Set it in tray settings or pass --url.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("Token is missing. Set it in tray settings or pass --token.");
            return 2;
        }

        Console.WriteLine($"Settings file: {options.SettingsPath}");
        Console.WriteLine($"Gateway URL: {GatewayUrlHelper.SanitizeForDisplay(gatewayUrl)}");
        Console.WriteLine($"Token source: {(options.TokenOverride is null ? "settings" : "--token override")}");
        if (loaded is not null)
        {
            Console.WriteLine($"Node mode in settings: {loaded.EnableNodeMode}");
            Console.WriteLine($"SSH tunnel in settings: {loaded.UseSshTunnel} (local port {loaded.SshTunnelLocalPort})");
        }

        IOpenClawLogger logger = options.Verbose ? new ConsoleLogger() : NullLogger.Instance;
        using var client = new OpenClawGatewayClient(gatewayUrl, token, logger);

        var lastStatus = ConnectionStatus.Disconnected;
        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.StatusChanged += (_, status) =>
        {
            lastStatus = status;
            Console.WriteLine($"Status: {status}");
            if (status == ConnectionStatus.Connected)
            {
                connectedTcs.TrySetResult(true);
            }
            else if (status == ConnectionStatus.Error)
            {
                errorTcs.TrySetResult(true);
            }
        };

        client.SessionsUpdated += (_, sessions) => Console.WriteLine($"sessions.list -> {sessions.Length} session(s)");
        client.UsageUpdated += (_, usage) => Console.WriteLine($"usage -> tokens {usage.TotalTokens}, requests {usage.RequestCount}, cost ${usage.CostUsd:F4}");
        client.NodesUpdated += (_, nodes) => Console.WriteLine($"node.list -> {nodes.Length} node(s)");

        Console.WriteLine("Connecting...");
        await client.ConnectAsync();

        var connected = await WaitForConnectedAsync(connectedTcs.Task, errorTcs.Task, options.ConnectTimeoutMs);
        if (!connected)
        {
            Console.Error.WriteLine($"Connection did not reach Connected within {options.ConnectTimeoutMs}ms (last status: {lastStatus}).");
            return 1;
        }

        Console.WriteLine($"Connected. Device ID: {client.OperatorDeviceId ?? "(unknown)"}");
        Console.WriteLine($"Granted scopes: {string.Join(", ", client.GrantedOperatorScopes)}");

        if (options.ProbeReadApis)
        {
            Console.WriteLine("Probing read APIs (sessions/usage/nodes)...");
            await client.RequestSessionsAsync();
            await client.RequestUsageAsync();
            await client.RequestNodesAsync();
            await Task.Delay(1200);
        }

        var failures = 0;
        for (var i = 1; i <= options.Repeat; i++)
        {
            var message = options.Repeat == 1
                ? options.Message
                : $"{options.Message} [attempt {i}/{options.Repeat}]";

            try
            {
                Console.WriteLine($"chat.send #{i} -> \"{message}\"");
                await client.SendChatMessageAsync(message);
                Console.WriteLine($"chat.send #{i} OK");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"chat.send #{i} FAILED: {ex.Message}");
            }

            if (i < options.Repeat)
            {
                await Task.Delay(options.DelayMs);
            }
        }

        if (failures > 0)
        {
            Console.Error.WriteLine($"Completed with {failures} failed send(s).");
            return 1;
        }

        Console.WriteLine("All sends succeeded.");
        return 0;
    }

    private static async Task<bool> WaitForConnectedAsync(Task connected, Task error, int timeoutMs)
    {
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        var completed = await Task.WhenAny(connected, error, timeoutTask);
        if (completed == connected)
        {
            return true;
        }

        return false;
    }

    private static (string GatewayUrl, string Token, SettingsData? Loaded) LoadConnectionFromSettings(CliOptions options)
    {
        var loaded = LoadSettings(options.SettingsPath);

        var gatewayUrl = options.GatewayUrlOverride;
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            gatewayUrl = BuildEffectiveGatewayUrl(loaded);
        }

        var token = options.TokenOverride;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = loaded?.Token;
        }

        return (gatewayUrl ?? string.Empty, token ?? string.Empty, loaded);
    }

    private static SettingsData? LoadSettings(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Settings file not found", path);
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        var settings = SettingsData.FromJson(json);
        if (settings is null)
        {
            throw new InvalidOperationException("Settings JSON could not be parsed");
        }

        return settings;
    }

    private static string? BuildEffectiveGatewayUrl(SettingsData? settings)
    {
        if (settings is null)
        {
            return null;
        }

        if (!settings.UseSshTunnel)
        {
            return settings.GatewayUrl;
        }

        var port = settings.SshTunnelLocalPort <= 0 ? 18789 : settings.SshTunnelLocalPort;
        return $"ws://127.0.0.1:{port}";
    }

    private static CliOptions ParseArgs(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--settings":
                    options.SettingsPath = RequireValue(args, ref i, arg);
                    break;
                case "--url":
                    options.GatewayUrlOverride = RequireValue(args, ref i, arg);
                    break;
                case "--token":
                    options.TokenOverride = RequireValue(args, ref i, arg);
                    break;
                case "--message":
                    options.Message = RequireValue(args, ref i, arg);
                    break;
                case "--repeat":
                    options.Repeat = ParseInt(RequireValue(args, ref i, arg), min: 1, name: arg);
                    break;
                case "--delay-ms":
                    options.DelayMs = ParseInt(RequireValue(args, ref i, arg), min: 0, name: arg);
                    break;
                case "--connect-timeout-ms":
                    options.ConnectTimeoutMs = ParseInt(RequireValue(args, ref i, arg), min: 1000, name: arg);
                    break;
                case "--probe-read":
                    options.ProbeReadApis = true;
                    break;
                case "--verbose":
                    options.Verbose = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {name}");
        }

        index++;
        return args[index];
    }

    private static int ParseInt(string value, int min, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < min)
        {
            throw new ArgumentException($"{name} must be an integer >= {min}");
        }

        return parsed;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("OpenClaw CLI WebSocket validator");
        Console.WriteLine();
        Console.WriteLine("Reads the same tray settings file and runs chat.send checks over gateway WebSocket.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project src/OpenClaw.Cli -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --settings <path>            Settings file (default: %APPDATA%\\OpenClawTray\\settings.json)");
        Console.WriteLine("  --url <ws://...>             Override gateway URL");
        Console.WriteLine("  --token <token>              Override token");
        Console.WriteLine("  --message <text>             Message to send");
        Console.WriteLine("  --repeat <n>                 Number of sends (default: 1)");
        Console.WriteLine("  --delay-ms <n>               Delay between sends (default: 500)");
        Console.WriteLine("  --connect-timeout-ms <n>     Wait for Connected state (default: 10000)");
        Console.WriteLine("  --probe-read                 Request sessions/usage/nodes once");
        Console.WriteLine("  --verbose                    Enable shared client console logs");
        Console.WriteLine("  --help, -h                   Show this help");
    }
}
