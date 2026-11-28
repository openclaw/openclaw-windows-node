// ClaudeCliAgentBridge.cs
//
// An isolated native Gateway runs under its own agent account, with its own profile and PATH. Claude Code
// installed and signed in for the Windows user is invisible there, so the Gateway's "Anthropic Claude CLI"
// setup choice fails with "claude was not found on PATH" even though the option promises to keep using the
// existing Claude Code login on this host.
//
// ClaudeCliAgentBridge closes that gap when the user explicitly picks a Claude CLI choice. It copies the
// user's claude.exe and .credentials.json into a one-time folder under the agent's shared folder, asks
// `clawctl pwsh` to place them in the agent profile and on the agent's PATH, and removes the one-time folder
// again. It never runs for other providers and shares nothing when the user has no Claude Code sign-in.
//
// Usage:
//   if (ClaudeCliAgentBridge.Applies(choice.Kind, choice.Id))
//   {
//       var bridge = new ClaudeCliAgentBridge((arguments, ct) => RunClawCtlAsync(arguments, ct));
//       if (await bridge.ShareAsync(ct) == ClaudeCliShareResult.SharedRestartRequired)
//           await RestartGatewayAsync(ct); // a running Gateway keeps the PATH it started with
//   }

using System.Globalization;
using System.Text.Json;

namespace OpenClaw.SetupEngine;

/// <summary>Outcome of sharing the signed-in user's Claude Code with an isolated Gateway agent.</summary>
public enum ClaudeCliShareResult
{
    /// <summary>The user has no native Claude Code install with a saved sign-in. Nothing was shared.</summary>
    NotAvailable,
    /// <summary>The agent has the user's Claude Code and sign-in, and its PATH already reached it.</summary>
    Shared,
    /// <summary>Shared, and the agent's PATH changed: the Gateway must restart to see Claude Code.</summary>
    SharedRestartRequired,
}

/// <summary>
/// Shares the Windows user's Claude Code install and sign-in with an isolated Gateway's agent account.
/// <paramref name="clawCtl"/> runs the Gateway package's <c>clawctl</c> with the given arguments and returns
/// its standard output, throwing when the command fails.
/// </summary>
public sealed class ClaudeCliAgentBridge(
    Func<string[], CancellationToken, Task<string>> clawCtl,
    string? userProfile = null,
    Func<string, string?>? readEnvironment = null)
{
    /// <summary>Gateway sign-in choice that reuses the host's Claude Code login.</summary>
    public const string AuthChoiceId = "anthropic-cli";
    /// <summary>Gateway runtime id, also the detected candidate kind and the deprecated sign-in choice id.</summary>
    public const string RuntimeId = "claude-cli";

    private const string ExecutableName = "claude.exe";
    private const string CredentialsName = ".credentials.json";
    private const string StagingPrefix = ".openclaw-companion-claude-";
    private const string SizeMarker = "openclaw-claude-size=";
    private const string RestartMarker = "openclaw-claude-restart=";
    private const string RestartRequired = "yes";
    // PowerShell ends a single-quoted string at any of these characters; each is escaped by doubling.
    private const string PowerShellSingleQuotes = "'‘’‚‛";

    // Single quotes only: the text travels as one process argument into PowerShell inside the agent.
    // A running Gateway keeps the PATH it started with, so the program goes into the per-user folder that
    // Windows puts on every account's PATH. This assumes the Gateway was started with the same PATH as
    // this agent shell. Only an agent without that entry gets Claude Code's own install folder added to
    // its PATH, which the Gateway sees after a restart.
    internal const string TargetScript =
        "$b = Join-Path $env:LOCALAPPDATA 'Microsoft\\WindowsApps'; " +
        "$onPath = ($env:Path -split ';') -contains $b; " +
        "if (-not $onPath) { $b = Join-Path $env:USERPROFILE '.local\\bin' }; ";

    internal const string ProbeScript = TargetScript +
        "$f = Join-Path $b '" + ExecutableName + "'; " +
        "'" + SizeMarker + "' + $(if (Test-Path -LiteralPath $f) { (Get-Item -LiteralPath $f).Length } else { 0 })";

    private readonly string _userProfile =
        userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly Func<string, string?> _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;

    /// <summary>Whether a setup choice is one of the Gateway's Claude CLI routes.</summary>
    public static bool Applies(GatewayAiSetupChoiceKind kind, string id) => kind switch
    {
        GatewayAiSetupChoiceKind.Auth => id is AuthChoiceId or RuntimeId,
        GatewayAiSetupChoiceKind.Candidate => id == RuntimeId,
        _ => false,
    };

    /// <summary>
    /// Copies the user's Claude Code and sign-in into the agent account. The one-time folder under the
    /// agent's shared folder is removed before this returns, including when the agent step fails.
    /// </summary>
    public async Task<ClaudeCliShareResult> ShareAsync(CancellationToken cancellationToken)
    {
        // File probes can touch slow or remote PATH entries, so they stay off the caller's thread.
        if (await Task.Run(FindUserInstall, cancellationToken) is not { } install)
            return ClaudeCliShareResult.NotAvailable;
        var sharedFolder = ReadSharedFolder(await clawCtl(["status", "--json"], cancellationToken));
        var agentSize = ReadMarker(await RunInAgentAsync(ProbeScript, cancellationToken), SizeMarker);
        var staging = Path.Combine(sharedFolder, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() =>
            {
                // An earlier share that was killed mid-way must not leave a sign-in behind.
                RemoveStagingFolders(sharedFolder);
                Directory.CreateDirectory(staging);
                // The program is hundreds of megabytes: copy it only when the agent's copy differs in
                // size, and before the sign-in, so the sign-in spends the least time in the shared folder.
                if (agentSize != new FileInfo(install.Executable).Length.ToString(CultureInfo.InvariantCulture))
                    File.Copy(install.Executable, Path.Combine(staging, ExecutableName));
                File.Copy(install.Credentials, Path.Combine(staging, CredentialsName));
            }, cancellationToken);
            var applied = await RunInAgentAsync(ApplyScript(staging), cancellationToken);
            return ReadMarker(applied, RestartMarker) == RestartRequired
                ? ClaudeCliShareResult.SharedRestartRequired
                : ClaudeCliShareResult.Shared;
        }
        finally
        {
            // The sign-in must not stay readable in the shared folder, whatever happened above.
            await Task.Run(() => RemoveStagingFolders(sharedFolder), CancellationToken.None);
        }
    }

    private Task<string> RunInAgentAsync(string script, CancellationToken cancellationToken) =>
        clawCtl(["pwsh", "--no-color", "--command", script], cancellationToken);

    /// <summary>Removes every one-time share folder. A folder that is still held loses its sign-in first.</summary>
    private static void RemoveStagingFolders(string sharedFolder)
    {
        foreach (var folder in Directory.EnumerateDirectories(sharedFolder, StagingPrefix + "*"))
        {
            try
            {
                File.Delete(Path.Combine(folder, CredentialsName));
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Still held by the agent step. The next share removes what is left.
            }
        }
    }

    internal static string QuotePowerShellLiteral(string value)
    {
        var quoted = new System.Text.StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            quoted.Append(character);
            if (PowerShellSingleQuotes.Contains(character))
                quoted.Append(character);
        }
        return quoted.Append('\'').ToString();
    }

    /// <summary>
    /// Agent-side step. The program is copied beside its target first and then moved over it, so a
    /// program that is in use keeps its complete old file. The fallback folder is reported as needing a
    /// restart every time, because nothing here can tell which PATH the running Gateway started with.
    /// </summary>
    internal static string ApplyScript(string staging) =>
        "$ErrorActionPreference = 'Stop'; $s = " + QuotePowerShellLiteral(staging) + "; " +
        TargetScript + "$c = Join-Path $env:USERPROFILE '.claude'; " +
        "New-Item -ItemType Directory -Force -Path $b, $c | Out-Null; " +
        "$e = Join-Path $s '" + ExecutableName + "'; " +
        "if (Test-Path -LiteralPath $e) { $t = Join-Path $b '" + ExecutableName + ".new'; " +
        "try { Copy-Item -LiteralPath $e -Destination $t -Force; " +
        "Move-Item -LiteralPath $t -Destination (Join-Path $b '" + ExecutableName + "') -Force } " +
        "finally { Remove-Item -LiteralPath $t -Force -ErrorAction SilentlyContinue } }; " +
        "Copy-Item -LiteralPath (Join-Path $s '" + CredentialsName + "') -Destination (Join-Path $c '" + CredentialsName + "') -Force; " +
        "if ($onPath) { '" + RestartMarker + "no' } else { " +
        "$p = [string][Environment]::GetEnvironmentVariable('Path', 'User'); " +
        "if (($p -split ';') -notcontains $b) { " +
        "[Environment]::SetEnvironmentVariable('Path', ($p.TrimEnd(';') + ';' + $b).TrimStart(';'), 'User') }; '" +
        RestartMarker + RestartRequired + "' }";

    private (string Executable, string Credentials)? FindUserInstall()
    {
        var configured = _readEnvironment("CLAUDE_CONFIG_DIR");
        var credentials = Path.Combine(
            string.IsNullOrWhiteSpace(configured) ? Path.Combine(_userProfile, ".claude") : configured, CredentialsName);
        if (!File.Exists(credentials))
            return null;
        var directories = new[] { Path.Combine(_userProfile, ".local", "bin") }.Concat(
            (_readEnvironment("PATH") ?? "").Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var directory in directories)
        {
            try
            {
                var executable = Path.Combine(directory, ExecutableName);
                if (File.Exists(executable))
                    return (executable, credentials);
            }
            catch (ArgumentException) { /* a malformed PATH entry cannot hold the program */ }
        }
        return null;
    }

    private static string ReadSharedFolder(string statusJson)
    {
        try
        {
            using var status = JsonDocument.Parse(statusJson);
            if (status.RootElement.TryGetProperty("session", out var session) &&
                session.TryGetProperty("sharedFolder", out var folder) &&
                folder.GetString() is { Length: > 0 } path && Path.IsPathFullyQualified(path) && Directory.Exists(path))
                return path;
        }
        catch (JsonException) { /* reported below */ }
        throw new InvalidOperationException("The isolated Gateway did not report a shared folder for its agent.");
    }

    private static string ReadMarker(string output, string marker)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                return trimmed[marker.Length..];
        }
        throw new InvalidOperationException("The isolated Gateway agent did not confirm the Claude Code share.");
    }
}
