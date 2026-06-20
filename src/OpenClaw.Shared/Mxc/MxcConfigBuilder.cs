using System.Text;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Pure function: <see cref="SandboxExecutionRequest"/> + scratch directory →
/// <see cref="MxcConfig"/> for direct invocation of <c>wxc-exec.exe</c>.
/// </summary>
/// <remarks>
/// What this class does:
/// <list type="bullet">
/// <item>Translates <see cref="SandboxPolicy"/> (from the Sandbox page) and the
///   agent's request into the JSON shape wxc-exec consumes.</item>
/// <item><see cref="ResolvePathDirsForShellPath"/> — reconstructs <c>PATH</c>
///   inside the launched shell and grants backend-safe PATH directories as
///   readonly, so user-level tools can be resolved and executed without asking
///   MXC's DACL fallback to prepare protected system directories.</item>
/// <item>Scratch dir injection — adds the per-invocation scratch dir as
///   readwrite and bootstraps <c>TEMP</c>/<c>TMP</c>/<c>TMPDIR</c> inside the
///   launched shell. Explicit <c>process.env</c> injection is intentionally
///   disabled for the current Windows MXC 0.7 processcontainer backend because
///   non-empty env entries fail process creation.</item>
/// <item>Cwd auto-grant — adds <c>request.Cwd</c> as readonly when not already
///   covered by an allow grant. AppContainer does NOT auto-grant cwd, so this
///   is required for commands to even start.</item>
/// <item>Defensive re-filter of allow lists against the deny list.</item>
/// <item>Shell command-line construction (cmd <c>/S /C</c>, powershell
///   <c>-EncodedCommand</c>).</item>
/// </list>
/// Env scrubbing happens upstream in <c>SystemCapability.HandleRunAsync</c>
/// via <c>ExecEnvSanitizer.Sanitize</c>; this class rejects explicit env until
/// the backend accepts it.
/// </remarks>
public static class MxcConfigBuilder
{
    // MXC processcontainer defaults to cmd because it starts inside the
    // AppContainer while preserving the default UI-deny boundary. PowerShell
    // remains available when explicitly requested, but it must not silently
    // relax UI containment.
    private const string DefaultShell = "cmd";

    /// <summary>
    /// Default per-process timeout when the caller doesn't supply one.
    /// </summary>
    public const int DefaultProcessTimeoutMs = 30_000;

    /// <summary>
    /// Build the <see cref="MxcConfig"/> for a sandboxed invocation.
    /// </summary>
    /// <param name="request">Capability invocation request.</param>
    /// <param name="scratchDir">Per-invocation scratch directory the executor created.</param>
    public static MxcConfig Build(
        SandboxExecutionRequest request,
        string scratchDir) =>
        Build(request, scratchDir, MxcConfigBuildContext.Default);

    internal static MxcConfig Build(
        SandboxExecutionRequest request,
        string scratchDir,
        MxcConfigBuildContext context)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(scratchDir)) throw new ArgumentException("scratchDir required", nameof(scratchDir));
        if (context is null) throw new ArgumentNullException(nameof(context));
        var deniedPathExists = context.DeniedPathExists ?? PathExists;

        var policy = request.Policy;
        var args = ParseSystemRunArgs(request.Args);

        if (request.Env is { Count: > 0 })
        {
            throw new NotSupportedException(
                "Explicit environment variables are not supported by the Windows MXC 0.7 processcontainer backend.");
        }

        // readonly = UI grants. Additional compatibility paths are added below.
        // PATH itself is bootstrapped inside the shell, and backend-safe PATH
        // directories are also granted readonly so PATH-resolved user tools can
        // actually be read/executed from inside AppContainer.
        var roFromPolicy = (policy?.Filesystem?.ReadonlyPaths ?? Array.Empty<string>()).ToList();
        var pathDirs = ResolvePathDirsForShellPath(context.PathEnvVar);
        foreach (var dir in pathDirs)
        {
            if (!IsBackendSafeReadonlyGrant(dir)) continue;
            if (!roFromPolicy.Contains(dir, StringComparer.OrdinalIgnoreCase))
                roFromPolicy.Add(dir);
        }

        // commandLine — shell-quoted, with PATH/TEMP/TMP/TMPDIR bootstrapped
        // inside the shell because MXC 0.7 rejects non-empty process.env.
        var commandLine = ShellCommandLine.Build(args.Shell, args.Command, args.Argv, scratchDir, pathDirs);
        var allowWindows = policy?.Ui?.AllowWindows == true;

        // readwrite = UI grants + scratch dir.
        var rwFromPolicy = (policy?.Filesystem?.ReadwritePaths ?? Array.Empty<string>()).ToList();
        if (!rwFromPolicy.Contains(scratchDir, StringComparer.OrdinalIgnoreCase))
            rwFromPolicy.Add(scratchDir);

        // denied list from policy (settings dir, ~/.ssh, browser profiles, ...).
        // Use the full list for local allow-list filtering, but do not emit
        // known host profile roots to the MXC DACL fallback: those paths often
        // cannot be prepared and make the sandbox fail before command launch.
        var deniedForFiltering = (policy?.Filesystem?.DeniedPaths ?? Array.Empty<string>()).ToList();
        var deniedForBackend = deniedForFiltering
            .Where(path => ShouldEmitDeniedPathToBackend(path, deniedPathExists))
            .ToList();

        // cwd auto-grant — AppContainer does not auto-grant the working
        // directory. Give ungranted cwd read access so shells can start, but
        // never silently upgrade it to write access; writes require an
        // explicit readwrite folder grant.
        if (!string.IsNullOrWhiteSpace(request.Cwd)
            && !IsCoveredBy(request.Cwd, roFromPolicy)
            && !IsCoveredBy(request.Cwd, rwFromPolicy))
        {
            if (!OverlapsAny(request.Cwd, deniedForFiltering))
                roFromPolicy.Add(request.Cwd);
        }

        // Deny wins: strip any allow that overlaps a deny after the merges above.
        roFromPolicy = FilterOutDenied(roFromPolicy, deniedForFiltering);
        rwFromPolicy = FilterOutDenied(rwFromPolicy, deniedForFiltering);

        // process.env — intentionally empty. MXC 0.7 processcontainer currently
        // fails process creation when a non-empty process.env array is supplied,
        // so shell-level bootstrap above carries PATH/scratch temp instead.
        var env = BuildEnv(request.Env);

        // timeout — caller-supplied or default.
        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : DefaultProcessTimeoutMs;

        // capabilities — only network for now.
        var capabilities = new List<string>();
        if (policy?.Network?.AllowOutbound == true)
            capabilities.Add("internetClient");

        var network = new MxcNetwork
        {
            DefaultPolicy = policy?.Network?.AllowOutbound == true ? "allow" : "block",
            EnforcementMode = "capabilities",
        };

        var topLevelUi = new MxcUi
        {
            Disable = !allowWindows,
            Clipboard = MapClipboard(policy?.Ui?.Clipboard ?? ClipboardPolicy.None),
            Injection = false,
        };

        var processContainerUi = new MxcBaseProcessUi
        {
            Isolation = allowWindows ? "desktop" : "container",
            DesktopSystemControl = false,
            SystemSettings = "none",
            Ime = false,
        };

        return new MxcConfig
        {
            Version = MxcPolicyBuilder.SupportedPolicyVersion,
            ContainerId = context.ContainerId ?? Guid.NewGuid().ToString("N"),
            // Top-level "containment" is intentionally omitted; the SDK doesn't
            // emit it either. Isolation lives in processContainer.ui.isolation.
            Process = new MxcProcess
            {
                CommandLine = commandLine,
                Cwd = string.IsNullOrWhiteSpace(request.Cwd) ? null : request.Cwd,
                Env = env,
                TimeoutMs = timeoutMs,
            },
            ProcessContainer = new MxcProcessContainer
            {
                LeastPrivilege = false,
                Capabilities = capabilities.ToArray(),
                Ui = processContainerUi,
            },
            Filesystem = new MxcFilesystem
            {
                ReadonlyPaths = roFromPolicy.ToArray(),
                ReadwritePaths = rwFromPolicy.ToArray(),
                DeniedPaths = deniedForBackend.ToArray(),
                // SDK output didn't include clearPolicyOnExit even when the
                // input policy had it set, so we omit it here too.
                ClearPolicyOnExit = null,
            },
            Network = network,
            Ui = topLevelUi,
            Lifecycle = new MxcLifecycle
            {
                DestroyOnExit = true,
                PreservePolicy = false,
            },
        };
    }

    /// <summary>
    /// Walk PATH and return each existing directory for shell-level PATH
    /// bootstrap. Drive roots (e.g. <c>C:\</c>) are skipped so a misconfigured
    /// PATH entry cannot make the payload search an entire drive root.
    /// </summary>
    public static List<string> ResolvePathDirsForShellPath(string? pathEnvVar = null)
    {
        var path = pathEnvVar ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathDirs = path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim().Trim('"'))
            .Where(d => d.Length > 0)
            .ToList();

        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in pathDirs)
        {
            if (IsDriveRoot(dir)) continue;
            try
            {
                if (!Directory.Exists(dir)) continue;
            }
            catch
            {
                continue;
            }
            if (seen.Add(dir)) dirs.Add(dir);
        }

        return dirs;
    }

    private static bool IsDriveRoot(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root)) return false;
            var trimmedDir = Path.TrimEndingDirectorySeparator(dir);
            var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
            return string.Equals(trimmedDir, trimmedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBackendSafeReadonlyGrant(string dir)
    {
        if (IsDriveRoot(dir)) return false;
        if (IsProtectedSystemPath(dir)) return false;
        return true;
    }

    private static bool IsProtectedSystemPath(string dir)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var normalized = NormalizePath(dir);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (var root in ProtectedSystemRoots())
        {
            var protectedRoot = NormalizePath(root);
            if (!string.IsNullOrWhiteSpace(protectedRoot) &&
                IsSameOrNested(normalized, protectedRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ProtectedSystemRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Environment.GetEnvironmentVariable("SystemRoot") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("windir") ?? string.Empty;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
    }

    /// <summary>
    /// Build the explicit process.env array for the wxc-exec sandbox.
    /// </summary>
    /// <remarks>
    /// Current MXC 0.7 Windows processcontainer accepts an empty env array but
    /// rejects non-empty entries at <c>CreateProcessW</c>. Emit an explicit
    /// empty array for normal requests so the config does not rely on implicit
    /// host-environment inheritance semantics. PATH and scratch temp variables
    /// are set by the shell command line bootstrap instead.
    /// </remarks>
    public static IReadOnlyList<string>? BuildEnv(IReadOnlyDictionary<string, string>? requestEnv)
    {
        if (requestEnv is null || requestEnv.Count == 0)
            return Array.Empty<string>();

        throw new NotSupportedException(
            "Explicit environment variables are not supported by the Windows MXC 0.7 processcontainer backend.");
    }

    private static List<string> FilterOutDenied(List<string> allowed, List<string> denied)
    {
        if (allowed.Count == 0 || denied.Count == 0) return allowed;
        var normalizedDenied = denied
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        return allowed
            .Where(a =>
            {
                var na = NormalizePath(a);
                if (string.IsNullOrEmpty(na)) return false;
                foreach (var d in normalizedDenied)
                    if (PathsOverlap(na, d)) return false;
                return true;
            })
            .ToList();
    }

    private static bool ShouldEmitDeniedPathToBackend(string path, Func<string, bool> pathExists)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        foreach (var hostProfileRoot in HostProfileDenyRoots())
        {
            var root = NormalizePath(hostProfileRoot);
            if (!string.IsNullOrWhiteSpace(root) && IsSameOrNested(normalized, root))
                return false;
        }

        try
        {
            if (!pathExists(normalized))
                return false;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    private static IEnumerable<string> HostProfileDenyRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".ssh");
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Google", "Chrome", "User Data");
            yield return Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
            yield return Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data");
        }

        if (!string.IsNullOrWhiteSpace(appData))
        {
            yield return Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
            yield return Path.Combine(appData, "Microsoft", "Windows", "PowerShell", "PSReadLine");
        }
    }

    private static bool IsCoveredBy(string candidate, IEnumerable<string> ancestors)
    {
        var nc = NormalizePath(candidate);
        if (string.IsNullOrEmpty(nc)) return false;
        foreach (var a in ancestors)
        {
            var na = NormalizePath(a);
            if (string.IsNullOrEmpty(na)) continue;
            if (IsSameOrNested(nc, na)) return true;
        }
        return false;
    }

    private static bool OverlapsAny(string candidate, IEnumerable<string> paths)
    {
        var nc = NormalizePath(candidate);
        if (string.IsNullOrEmpty(nc)) return false;
        foreach (var path in paths)
        {
            var np = NormalizePath(path);
            if (string.IsNullOrEmpty(np)) continue;
            if (PathsOverlap(nc, np)) return true;
        }
        return false;
    }

    private static bool PathsOverlap(string left, string right) =>
        IsSameOrNested(left, right) || IsSameOrNested(right, left);

    private static bool IsSameOrNested(string path, string candidateParent)
    {
        if (string.Equals(path, candidateParent, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(candidateParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(candidateParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    private static string MapClipboard(ClipboardPolicy mode) => mode switch
    {
        ClipboardPolicy.Read => "read",
        ClipboardPolicy.Write => "write",
        ClipboardPolicy.All => "all",
        _ => "none",
    };

    /// <summary>
    /// Capability args envelope for system.run. Other capability shapes can add
    /// their own parser here as they're rehosted.
    /// </summary>
    private static SystemRunArgs ParseSystemRunArgs(System.Text.Json.JsonElement args)
    {
        if (args.ValueKind != System.Text.Json.JsonValueKind.Object)
            return new SystemRunArgs("", DefaultShell, Array.Empty<string>());

        string command = args.TryGetProperty("command", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            ? (c.GetString() ?? "") : "";
        string shell = args.TryGetProperty("shell", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
            ? (s.GetString() ?? DefaultShell) : DefaultShell;
        string[] argv = Array.Empty<string>();
        if (args.TryGetProperty("args", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            argv = a.EnumerateArray()
                .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .ToArray();
        }
        return new SystemRunArgs(command, shell, argv);
    }

    private sealed record SystemRunArgs(string Command, string Shell, IReadOnlyList<string> Argv);
}

internal sealed record MxcConfigBuildContext(
    string? ContainerId = null,
    string? PathEnvVar = null,
    Func<string, bool>? DeniedPathExists = null)
{
    public static MxcConfigBuildContext Default { get; } = new();
}

/// <summary>
/// Shell command-line construction for the sandboxed payload — wraps the
/// agent's command in <c>cmd.exe /S /C "..."</c> or
/// <c>powershell.exe -EncodedCommand &lt;utf16le-base64&gt;</c> so it can be
/// passed verbatim to <c>CreateProcessW</c> inside the AppContainer.
/// </summary>
internal static class ShellCommandLine
{
    private static readonly string[] CmdBootstrapTempEnvNames = ["TEMP", "TMP", "TMPDIR"];

    public static string Build(
        string shell,
        string command,
        IReadOnlyList<string> argv,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        var normalized = (shell ?? "cmd").Trim().ToLowerInvariant();
        return normalized switch
        {
            "cmd" => BuildCmd(command, argv, scratchDir, pathDirs),
            "pwsh" or "powershell" => BuildPowershell(
                normalized == "pwsh" ? ResolvePwshExe(pathDirs) : ResolveWindowsPowerShellExe(),
                command,
                argv,
                scratchDir,
                pathDirs),
            _ => BuildPowershell(ResolveWindowsPowerShellExe(), command, argv, scratchDir, pathDirs),
        };
    }

    private static string BuildCmd(
        string command,
        IReadOnlyList<string> argv,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        // cmd /S /C "<command> [args]" — /S strips outer quotes so cmd treats
        // everything after /C as the command line verbatim. If the payload
        // references env vars we bootstrap in this same /C line, rewrite just
        // those refs to delayed expansion; otherwise cmd expands %TEMP% before
        // the preceding set command runs.
        var rewrittenCommand = RewriteCmdBootstrapEnvRefs(command, pathDirs, out var needsDelayedExpansion);
        var rewrittenArgv = new List<string>(argv.Count);
        foreach (var arg in argv)
        {
            rewrittenArgv.Add(RewriteCmdBootstrapEnvRefs(arg, pathDirs, out var argNeedsDelayedExpansion));
            needsDelayedExpansion |= argNeedsDelayedExpansion;
        }

        var sb = new StringBuilder(QuoteProcessPath(ResolveCmdExe()));
        if (needsDelayedExpansion)
            sb.Append(" /V:ON");
        sb.Append(" /S /C \"");
        AppendCmdEnvironmentBootstrap(sb, scratchDir, pathDirs);
        sb.Append(rewrittenCommand);
        foreach (var a in rewrittenArgv)
        {
            sb.Append(' ');
            sb.Append(QuoteForCmd(a));
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string RewriteCmdBootstrapEnvRefs(
        string value,
        IReadOnlyList<string> pathDirs,
        out bool rewritten)
    {
        rewritten = false;
        var result = value;
        foreach (var name in CmdBootstrapTempEnvNames)
        {
            result = ReplaceOrdinalIgnoreCase(
                result,
                $"%{name}%",
                $"!{name}!",
                ref rewritten);
        }

        if (pathDirs.Count > 0)
        {
            result = ReplaceOrdinalIgnoreCase(
                result,
                "%PATH%",
                "!PATH!",
                ref rewritten);
        }

        return result;
    }

    private static string ReplaceOrdinalIgnoreCase(
        string value,
        string search,
        string replacement,
        ref bool replaced)
    {
        var index = value.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return value;

        var sb = new StringBuilder(value.Length);
        var cursor = 0;
        while (index >= 0)
        {
            sb.Append(value, cursor, index - cursor);
            sb.Append(replacement);
            cursor = index + search.Length;
            index = value.IndexOf(search, cursor, StringComparison.OrdinalIgnoreCase);
            replaced = true;
        }

        sb.Append(value, cursor, value.Length - cursor);
        return sb.ToString();
    }

    private static string BuildPowershell(
        string exe,
        string command,
        IReadOnlyList<string> argv,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        // -EncodedCommand <UTF-16LE-base64> avoids quoting pitfalls entirely.
        // We concatenate command + argv with spaces and let powershell parse it.
        var sb = new StringBuilder();
        AppendPowershellEnvironmentBootstrap(sb, scratchDir, pathDirs);
        sb.Append(command);
        foreach (var a in argv)
        {
            sb.Append(' ');
            sb.Append(QuoteForPowershell(a));
        }
        var script = sb.ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"{QuoteProcessPath(exe)} -NoProfile -NonInteractive -EncodedCommand {encoded}";
    }

    private static void AppendCmdEnvironmentBootstrap(
        StringBuilder sb,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        AppendCmdSet(sb, "TEMP", scratchDir);
        AppendCmdSet(sb, "TMP", scratchDir);
        AppendCmdSet(sb, "TMPDIR", scratchDir);
        if (pathDirs.Count > 0)
            AppendCmdSet(sb, "PATH", string.Join(Path.PathSeparator, pathDirs));
    }

    private static void AppendCmdSet(StringBuilder sb, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("set \"")
            .Append(name)
            .Append('=')
            .Append(value.Replace("\"", ""))
            .Append("\" && ");
    }

    private static void AppendPowershellEnvironmentBootstrap(
        StringBuilder sb,
        string scratchDir,
        IReadOnlyList<string> pathDirs)
    {
        AppendPowershellSet(sb, "TEMP", scratchDir);
        AppendPowershellSet(sb, "TMP", scratchDir);
        AppendPowershellSet(sb, "TMPDIR", scratchDir);
        if (pathDirs.Count > 0)
            AppendPowershellSet(sb, "PATH", string.Join(Path.PathSeparator, pathDirs));
    }

    private static void AppendPowershellSet(StringBuilder sb, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append("$env:")
            .Append(name)
            .Append(" = ")
            .Append(QuoteEnvironmentValueForPowershell(value))
            .Append("; ");
    }

    private static string ResolveCmdExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "cmd.exe"
            : Path.Combine(systemRoot, "System32", "cmd.exe");
    }

    private static string ResolveWindowsPowerShellExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "powershell.exe"
            : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string ResolvePwshExe(IReadOnlyList<string> pathDirs)
    {
        const string executableName = "pwsh.exe";
        foreach (var dir in pathDirs)
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Keep PATH resolution best-effort; launch will fail closed if
                // pwsh is not resolvable on the host.
            }
        }

        return executableName;
    }

    private static string QuoteProcessPath(string path)
    {
        if (path.Length > 0 && path.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return path;

        return "\"" + path.Replace("\"", "\\\"") + "\"";
    }

    private static string QuoteForCmd(string arg)
    {
        // Note: `%VAR%` env-var expansion inside `cmd /S /C "..."` cannot be
        // reliably suppressed via quoting (cmd parses % before applying quote
        // rules). Bootstrap env refs are rewritten before quoting; callers
        // wanting fully verbatim arguments should use powershell
        // (-EncodedCommand) which has no cmd env-expansion ambiguity.
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%' }) < 0)
            return arg;
        return "\"" + arg.Replace("\"", "\"\"") + "\"";
    }

    private static string QuoteForPowershell(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\'', '"', '`', '$' }) < 0)
            return arg;
        return "'" + arg.Replace("'", "''") + "'";
    }

    private static string QuoteEnvironmentValueForPowershell(string value) =>
        "'" + value.Replace("'", "''") + "'";
}
