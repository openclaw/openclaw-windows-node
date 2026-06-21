using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal sealed class AssistantBridgeService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private readonly IOpenClawLogger _logger;
    private readonly string _userId;
    private readonly Func<OpenClawCliLauncher?> _launcherResolver;

    public AssistantBridgeService(IOpenClawLogger logger, string userId = "owner")
        : this(logger, userId, ResolveOpenClawCli)
    {
    }

    internal AssistantBridgeService(
        IOpenClawLogger logger,
        string userId,
        Func<OpenClawCliLauncher?> launcherResolver)
    {
        _logger = logger;
        _userId = string.IsNullOrWhiteSpace(userId) ? "owner" : userId;
        _launcherResolver = launcherResolver;
    }

    public async Task<AssistantBridgeSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunOpenClawAsync(["dashboard", "bridge", "status", "--user", _userId, "--json"], cancellationToken);
        if (!result.Success)
            return AssistantBridgeSnapshot.Unavailable(result.ErrorMessage);

        try
        {
            return ParseStatus(result.Stdout);
        }
        catch (JsonException ex)
        {
            _logger.Warn($"Assistant bridge status JSON could not be parsed: {ex.Message}");
            return AssistantBridgeSnapshot.Unavailable("OpenClaw returned status JSON the Companion could not read.");
        }
    }

    public Task<AssistantCommandResult> StartListenServiceAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(
            ["assistant", "listen-service", "start", "--user", _userId, "--allow-cloud", "--store-turn", "--json"],
            cancellationToken);

    public Task<AssistantCommandResult> StopListenServiceAsync(CancellationToken cancellationToken = default) =>
        RunCommandAsync(
            ["assistant", "listen-service", "stop", "--user", _userId, "--json"],
            cancellationToken);

    private async Task<AssistantCommandResult> RunCommandAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await RunOpenClawAsync(args, cancellationToken);
        return new AssistantCommandResult(result.Success, result.ErrorMessage);
    }

    private async Task<AssistantProcessResult> RunOpenClawAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var launcher = _launcherResolver();
        if (launcher == null)
            return AssistantProcessResult.Failed("OpenClaw backend checkout was not found. Set OPENCLAW_BACKEND_ROOT or keep it at D:\\Projects\\OpenClaw.");

        var psi = new ProcessStartInfo
        {
            FileName = launcher.ExecutablePath,
            WorkingDirectory = launcher.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in launcher.PrefixArgs)
            psi.ArgumentList.Add(arg);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return AssistantProcessResult.Failed("Could not start the OpenClaw backend command.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? "OpenClaw backend command failed." : stderr.Trim();
                return AssistantProcessResult.Failed(detail);
            }

            return new AssistantProcessResult(true, stdout, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AssistantProcessResult.Failed("OpenClaw backend command timed out.");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Assistant bridge command failed to start: {ex.Message}");
            return AssistantProcessResult.Failed("OpenClaw backend command could not be started.");
        }
    }

    internal static AssistantBridgeSnapshot ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var generatedAt = ReadString(root, "generated_at");
        var assistant = ReadObject(root, "assistant");
        var voice = ReadObject(root, "voice");

        var listen = AssistantListenServiceSnapshot.Empty;
        if (assistant.HasValue && assistant.Value.TryGetProperty("listen_service", out var listenElement))
        {
            listen = new AssistantListenServiceSnapshot(
                ReadString(listenElement, "status"),
                ReadBool(listenElement, "configured"),
                ReadNullableInt(listenElement, "pid"),
                ReadBool(listenElement, "stop_requested"),
                ReadBool(listenElement, "allow_cloud"),
                ReadBool(listenElement, "speak_aloud"),
                ReadString(listenElement, "transcriber"),
                ReadString(listenElement, "input_mode"),
                ReadString(listenElement, "log_file"));
        }

        var turns = new List<AssistantTurnSnapshot>();
        if (assistant.HasValue && assistant.Value.TryGetProperty("recent_turns", out var recentTurns) &&
            recentTurns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in recentTurns.EnumerateArray())
            {
                turns.Add(new AssistantTurnSnapshot(
                    ReadString(turn, "created_at"),
                    ReadString(turn, "source"),
                    ReadString(turn, "input_text"),
                    ReadString(turn, "display_response_text"),
                    ReadString(turn, "provider"),
                    ReadString(turn, "model_profile"),
                    ReadString(turn, "stage"),
                    ReadNullableInt(turn, "total_ms")));
            }
        }

        return new AssistantBridgeSnapshot(
            true,
            "",
            generatedAt,
            ReadString(root, "user_id"),
            ReadString(voice, "preferred_input_device"),
            ReadString(voice, "preferred_output_device"),
            listen,
            turns);
    }

    internal static OpenClawCliLauncher? ResolveOpenClawCli()
    {
        foreach (var root in CandidateBackendRoots())
        {
            var normalizedRoot = Environment.ExpandEnvironmentVariables(root);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
                continue;

            var openclawExe = Path.Combine(normalizedRoot, ".venv", "Scripts", "openclaw.exe");
            if (File.Exists(openclawExe))
                return new OpenClawCliLauncher(openclawExe, normalizedRoot, []);

            var pythonExe = Path.Combine(normalizedRoot, ".venv", "Scripts", "python.exe");
            if (File.Exists(pythonExe))
                return new OpenClawCliLauncher(pythonExe, normalizedRoot, ["-m", "openclaw.cli"]);
        }

        return null;
    }

    internal static IReadOnlyList<string> CandidateBackendRoots()
    {
        var candidates = new List<string>();
        var overrideRoot = Environment.GetEnvironmentVariable("OPENCLAW_BACKEND_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            candidates.Add(overrideRoot);

        candidates.Add(@"D:\Projects\OpenClaw");
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projects",
            "OpenClaw"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "source",
            "repos",
            "OpenClaw"));

        return candidates;
    }

    private static JsonElement? ReadObject(JsonElement? parent, string name)
    {
        if (parent is not { } element || element.ValueKind != JsonValueKind.Object)
            return null;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string ReadString(JsonElement? parent, string name)
    {
        if (parent is not { } element || element.ValueKind != JsonValueKind.Object)
            return "";
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static bool ReadBool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? ReadNullableInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private sealed record AssistantProcessResult(bool Success, string Stdout, string ErrorMessage)
    {
        public static AssistantProcessResult Failed(string errorMessage) => new(false, "", errorMessage);
    }
}

internal sealed record OpenClawCliLauncher(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> PrefixArgs);

internal sealed record AssistantCommandResult(bool Success, string ErrorMessage);

internal sealed record AssistantBridgeSnapshot(
    bool IsAvailable,
    string ErrorMessage,
    string GeneratedAt,
    string UserId,
    string PreferredInputDevice,
    string PreferredOutputDevice,
    AssistantListenServiceSnapshot ListenService,
    IReadOnlyList<AssistantTurnSnapshot> RecentTurns)
{
    public static AssistantBridgeSnapshot Unavailable(string errorMessage) =>
        new(false, errorMessage, "", "", "", "", AssistantListenServiceSnapshot.Empty, []);
}

internal sealed record AssistantListenServiceSnapshot(
    string Status,
    bool Configured,
    int? Pid,
    bool StopRequested,
    bool AllowCloud,
    bool SpeakAloud,
    string Transcriber,
    string InputMode,
    string LogFile)
{
    public static AssistantListenServiceSnapshot Empty { get; } =
        new("", false, null, false, false, false, "", "", "");

    public bool IsRunning => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);

    public bool IsStopped =>
        string.Equals(Status, "stopped", StringComparison.OrdinalIgnoreCase) ||
        (StopRequested && string.Equals(Status, "stop-requested", StringComparison.OrdinalIgnoreCase));
}

internal sealed record AssistantTurnSnapshot(
    string CreatedAt,
    string Source,
    string InputText,
    string ResponseText,
    string Provider,
    string ModelProfile,
    string Stage,
    int? TotalMs);
