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
/// <item><see cref="ResolvePathDirsForReadonly"/> — grants every existing
///   <c>$PATH</c> directory as readonly so command-line tools (git, node,
///   python, ...) can be read from inside the sandbox. Drive roots skipped.</item>
/// <item>Scratch dir injection — adds the per-invocation scratch dir as
///   readwrite and forces <c>TEMP</c>/<c>TMP</c>/<c>TMPDIR</c> at it so
///   commands don't write to the user's real <c>%TEMP%</c>.</item>
/// <item>Cwd auto-grant — adds <c>request.Cwd</c> as readonly when not already
///   covered by an allow grant. AppContainer does NOT auto-grant cwd, so this
///   is required for commands to even start.</item>
/// <item>Defensive re-filter of allow lists against the deny list.</item>
/// <item>Shell command-line construction (cmd <c>/S /C</c>, powershell
///   <c>-EncodedCommand</c>).</item>
/// </list>
/// Env scrubbing happens upstream in <c>SystemCapability.HandleRunAsync</c>
/// via <c>ExecEnvSanitizer.Sanitize</c>; this class doesn't scrub env.
/// </remarks>
public static class MxcConfigBuilder
{
    /// <summary>
    /// Default per-process timeout when the caller doesn't supply one.
    /// </summary>
    public const int DefaultProcessTimeoutMs = 30_000;

    /// <summary>
    /// Build the <see cref="MxcConfig"/> for a sandboxed invocation.
    /// </summary>
    /// <param name="request">Capability invocation request.</param>
    /// <param name="scratchDir">Per-invocation scratch directory the executor created.</param>
    /// <param name="containerId">Optional explicit container id (test/diagnostic use). Random GUID when null.</param>
    /// <param name="pathEnvVar">Optional override for the PATH env-var contents (test use).</param>
    public static MxcConfig Build(
        SandboxExecutionRequest request,
        string scratchDir,
        string? containerId = null,
        string? pathEnvVar = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(scratchDir)) throw new ArgumentException("scratchDir required", nameof(scratchDir));

        var policy = request.Policy;
        var args = ParseSystemRunArgs(request.Args);

        // commandLine — shell-quoted.
        var commandLine = ShellCommandLine.Build(args.Shell, args.Command, args.Argv);

        // readonly = UI grants + every existing PATH dir (so tools like git,
        // node, python can be read inside the sandbox). Additional compatibility
        // paths are added below.
        var roFromPolicy = (policy?.Filesystem?.ReadonlyPaths ?? Array.Empty<string>()).ToList();
        var pathDirs = ResolvePathDirsForReadonly(pathEnvVar);
        foreach (var dir in pathDirs)
            if (!roFromPolicy.Contains(dir, StringComparer.OrdinalIgnoreCase))
                roFromPolicy.Add(dir);

        // readwrite = UI grants + scratch dir.
        var rwFromPolicy = (policy?.Filesystem?.ReadwritePaths ?? Array.Empty<string>()).ToList();
        if (!rwFromPolicy.Contains(scratchDir, StringComparer.OrdinalIgnoreCase))
            rwFromPolicy.Add(scratchDir);
        AddCompatibilityReadonlyPaths(roFromPolicy, roFromPolicy.Concat(rwFromPolicy).ToArray());

        // denied list from policy (settings dir, ~/.ssh, browser profiles, ...).
        var denied = (policy?.Filesystem?.DeniedPaths ?? Array.Empty<string>()).ToList();

        // cwd auto-grant — AppContainer does not auto-grant the working
        // directory. Give ungranted cwd read access so shells can start, but
        // never silently upgrade it to write access; writes require an
        // explicit readwrite folder grant.
        if (!string.IsNullOrWhiteSpace(request.Cwd)
            && !IsCoveredBy(request.Cwd, roFromPolicy)
            && !IsCoveredBy(request.Cwd, rwFromPolicy))
        {
            if (!OverlapsAny(request.Cwd, denied))
                roFromPolicy.Add(request.Cwd);
        }

        // Deny wins: strip any allow that overlaps a deny after the merges above.
        roFromPolicy = FilterOutDenied(roFromPolicy, denied);
        rwFromPolicy = FilterOutDenied(rwFromPolicy, denied);

        // env — agent-supplied vars (already scrubbed upstream by
        // ExecEnvSanitizer in SystemCapability) plus TEMP/TMP/TMPDIR forced
        // to scratch.
        var env = BuildEnv(request.Env, scratchDir, pathDirs);

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
            Disable = true,
            Clipboard = MapClipboard(policy?.Ui?.Clipboard ?? ClipboardPolicy.None),
            Injection = false,
        };

        var processContainerUi = new MxcBaseProcessUi
        {
            Isolation = "container",
            DesktopSystemControl = false,
            SystemSettings = "none",
            Ime = false,
        };

        return new MxcConfig
        {
            Version = MxcPolicyBuilder.SupportedPolicyVersion,
            ContainerId = containerId ?? Guid.NewGuid().ToString("N"),
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
                DeniedPaths = denied.ToArray(),
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
    /// Walk PATH and return each existing directory as a readonly grant
    /// candidate. Drive roots (e.g. <c>C:\</c>) are skipped so a misconfigured
    /// PATH entry can't grant the entire system drive.
    /// </summary>
    public static List<string> ResolvePathDirsForReadonly(string? pathEnvVar = null)
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

    private static void AddCompatibilityReadonlyPaths(List<string> readonlyPaths, IEnumerable<string> grantedPaths)
    {
        foreach (var path in grantedPaths)
            AddCompatibilityReadonlyPath(readonlyPaths, path);

        AddCompatibilityReadonlyPath(readonlyPaths, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddCompatibilityReadonlyPath(readonlyPaths, Environment.GetEnvironmentVariable("SystemDrive") ?? string.Empty);
    }

    private static void AddCompatibilityReadonlyPath(List<string> readonlyPaths, string path)
    {
        string? root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
        catch { return; }

        if (string.IsNullOrWhiteSpace(root))
            return;

        if (!readonlyPaths.Contains(root, StringComparer.OrdinalIgnoreCase))
            readonlyPaths.Add(root);
    }

    /// <summary>
    /// Build the env array (KEY=VALUE strings) the wxc-exec sandbox will inherit.
    /// </summary>
    /// <remarks>
    /// Env from the agent has already been scrubbed upstream in
    /// <c>SystemCapability.HandleRunAsync</c> via
    /// <c>ExecEnvSanitizer.Sanitize</c> (which rejects the whole command if
    /// anything dangerous is present). We pass the surviving entries through
    /// and force <c>TEMP</c>/<c>TMP</c>/<c>TMPDIR</c> to <paramref name="scratchDir"/>
    /// so tools inside the sandbox don't write into the user's real <c>%TEMP%</c>.
    /// </remarks>
    public static IReadOnlyList<string> BuildEnv(
        IReadOnlyDictionary<string, string>? requestEnv,
        string scratchDir,
        IReadOnlyList<string>? pathDirs = null)
    {
        // Windows env vars are case-insensitive — use OrdinalIgnoreCase so
        // duplicate-case agent entries don't end up as separate strings.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (requestEnv is not null)
        {
            foreach (var (name, value) in requestEnv)
            {
                if (string.IsNullOrEmpty(name) || value is null) continue;
                // Reject names with NUL/CR/LF/'=' so an agent can't smuggle
                // a second KEY=VALUE pair into a single name field.
                bool malformed = false;
                foreach (var ch in name)
                {
                    if (ch == '=' || ch == '\0' || ch == '\r' || ch == '\n')
                    {
                        malformed = true;
                        break;
                    }
                }
                if (malformed) continue;
                env[name] = value;
            }
        }

        env["TEMP"] = scratchDir;
        env["TMP"] = scratchDir;
        env["TMPDIR"] = scratchDir;
        if (pathDirs is { Count: > 0 })
            env["PATH"] = string.Join(Path.PathSeparator, pathDirs);

        return env.Select(kvp => $"{kvp.Key}={kvp.Value}").ToList();
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
            return new SystemRunArgs("", "powershell", Array.Empty<string>());

        string command = args.TryGetProperty("command", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
            ? (c.GetString() ?? "") : "";
        string shell = args.TryGetProperty("shell", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
            ? (s.GetString() ?? "powershell") : "powershell";
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

/// <summary>
/// Shell command-line construction for the sandboxed payload — wraps the
/// agent's command in <c>cmd.exe /S /C "..."</c> or
/// <c>powershell.exe -EncodedCommand &lt;utf16le-base64&gt;</c> so it can be
/// passed verbatim to <c>CreateProcessW</c> inside the AppContainer.
/// </summary>
internal static class ShellCommandLine
{
    public static string Build(string shell, string command, IReadOnlyList<string> argv)
    {
        var normalized = (shell ?? "powershell").Trim().ToLowerInvariant();
        return normalized switch
        {
            "cmd" => BuildCmd(command, argv),
            "pwsh" or "powershell" => BuildPowershell(normalized == "pwsh" ? "pwsh.exe" : "powershell.exe", command, argv),
            _ => BuildPowershell("powershell.exe", command, argv),
        };
    }

    private static string BuildCmd(string command, IReadOnlyList<string> argv)
    {
        // cmd /S /C "<command> [args]" — /S strips outer quotes so cmd treats
        // everything after /C as the command line verbatim.
        var sb = new StringBuilder("cmd.exe /S /C \"");
        sb.Append(command);
        foreach (var a in argv)
        {
            sb.Append(' ');
            sb.Append(QuoteForCmd(a));
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string BuildPowershell(string exe, string command, IReadOnlyList<string> argv)
    {
        // -EncodedCommand <UTF-16LE-base64> avoids quoting pitfalls entirely.
        // We concatenate command + argv with spaces and let powershell parse it.
        var sb = new StringBuilder(command);
        foreach (var a in argv)
        {
            sb.Append(' ');
            sb.Append(QuoteForPowershell(a));
        }
        var script = sb.ToString();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"{exe} -NoProfile -NonInteractive -EncodedCommand {encoded}";
    }

    private static string QuoteForCmd(string arg)
    {
        // Note: `%VAR%` env-var expansion inside `cmd /S /C "..."` cannot be
        // reliably suppressed via quoting (cmd parses % before applying quote
        // rules). The cmd shell route is opt-in and runs inside the AppContainer
        // with a controlled env, so the expansion target is sandbox-side, not
        // host-side. Callers wanting verbatim arguments should use powershell
        // (-EncodedCommand) which has no env-expansion ambiguity.
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
}
