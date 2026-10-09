using System.Diagnostics;
using System.Text.Json;
using System.Security.AccessControl;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcNativeFactAttribute : FactAttribute
{
    public MxcNativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_MXC_NATIVE_PROOF") != "1")
            Skip = "Set OPENCLAW_RUN_MXC_NATIVE_PROOF=1 only for an authorized isolated native containment proof.";
        else if (!MxcAvailability.Probe().CanRunSystemRunSandbox)
            Skip = "The official SDK cannot admit native-deny BaseContainer. Native containment was not exercised.";
    }
}

/// <summary>Real product runner proof using only synthetic user roots and synthetic sensitive files.</summary>
[Collection("MxcOwnership")]
public sealed class MxcCommandRunnerIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _runId = "n-" + Guid.NewGuid().ToString("N");
    private readonly MxcRequestContext _context;
    private readonly SettingsData _settings = new() { SystemRunAllowWindowsUi = true };
    private readonly List<object> _processes = [];
    private readonly List<object> _checks = [];
    private readonly List<string> _junctions = [];
    private readonly Dictionary<string, string> _acls = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastScratch;
    private string Profile => _context.RequireFolder("Profile");
    private string Documents => _context.RequireFolder("Documents");
    private string Project => Path.Combine(Profile, "Projects");
    private string Outside => Path.Combine(_root, "outside");
    private string SettingsDirectory => _context.ProtectedPaths[1];

    public MxcCommandRunnerIntegrationTests()
    {
        var artifacts = Environment.GetEnvironmentVariable("OPENCLAW_MXC_PROOF_ROOT")
            ?? throw new InvalidOperationException("Native proof requires an explicit owned artifact root outside the repository.");
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        if (!Path.IsPathFullyQualified(artifacts) ||
            Path.GetFullPath(artifacts).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native proof scratch must be outside the source checkout.");
        foreach (var key in new[] { "OPENCLAW_TRAY_DATA_DIR", "OPENCLAW_TRAY_APPDATA_DIR", "OPENCLAW_TRAY_LOCALAPPDATA_DIR" })
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                throw new InvalidOperationException($"Native proof requires isolated {key} before process creation.");
        _root = Directory.CreateDirectory(Path.Combine(artifacts, _runId[..14])).FullName;
        _context = MxcSyntheticContext.Create(_root);
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Outside);
        var aclPaths = _context.UserFolders.Select(f => f.Path!).Concat(_context.ProtectedPaths)
            .Concat(new[] { _root, Project, Outside, _context.WindowsDirectory, _context.SystemDirectory });
        foreach (var path in aclPaths)
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && !_acls.ContainsKey(current))
                    _acls.Add(current, Acl(current));
        Write("acl-before.json", _acls);
        using var testHost = Process.GetCurrentProcess();
        Write("ownership.json", new
        {
            RunId = _runId, Worktree = repo, SourceManifest = Environment.GetEnvironmentVariable("OPENCLAW_MXC_SOURCE_MANIFEST"),
            Lane = "product runner, synthetic user folders", DataRoots = IsolationRoots(), Processes = _processes,
            TestHost = new
            {
                Pid = Environment.ProcessId, StartTimeUtc = testHost.StartTime.ToUniversalTime().ToString("O"),
                Executable = Environment.ProcessPath, CommandLine = string.Join(" ", Environment.GetCommandLineArgs()),
            },
        });
        Register("running");
    }

    private MxcCommandRunner Runner => new(() => _settings, () => SettingsDirectory,
        spawn: async request =>
        {
            Assert.DoesNotContain(request.Filesystem!.ReadonlyPaths.Concat(request.Filesystem.ReadwritePaths),
                p => Path.TrimEndingDirectorySeparator(p).Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetPathRoot(p)!), StringComparison.OrdinalIgnoreCase));
            Assert.False(request.InheritDefaultEnvironment);
            Assert.Null(request.Environment);
            _lastScratch = request.WorkingDirectory;
            var process = await MxcContainer.SpawnAsync(request,
                new SpawnOptions { Telemetry = new() { Enabled = false } }, CancellationToken.None);
            using var osProcess = Process.GetProcessById((int)process.Id);
            _processes.Add(new
            {
                Pid = process.Id, StartTimeUtc = osProcess.StartTime.ToUniversalTime().ToString("O"),
                Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                CommandLine = request.Command, DataDirectories = IsolationRoots(), RunId = _runId,
                WorkingDirectory = request.WorkingDirectory, EnvironmentSource = "MXC SDK default user-profile environment",
            });
            Write("processes.json", _processes);
            return process;
        },
        probe: request =>
        {
            var probe = MxcContainer.Probe(request);
            Write($"probe-{_processes.Count}.json", probe);
            return probe;
        },
        scratchRoot: Path.Combine(_root, "s"), contextProvider: () => _context);

    private CommandRequest Cmd(string payload) => new()
    {
        Argv =
        [
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d", "/s", "/c",
            "(for /L %i in (1,1,20000) do @set MXC_PROOF_WAIT=%i) & " + payload,
        ],
        TimeoutMs = 30_000,
    };

    [MxcNativeFact]
    public async Task NativeSdk_CapturesStdoutStderrAndNonzeroExit()
    {
        var result = await Runner.RunAsync(Cmd(
            "echo native-output & echo PROOF_TEMP=%TEMP% & echo PROOF_CWD=%CD% & echo native-error 1>&2 & exit /b 42"));
        Write("output.json", result);
        Assert.Equal(42, result.ExitCode);
        Assert.Contains("native-output", result.Stdout);
        Assert.Contains("native-error", result.Stderr);
        Assert.False(result.TimedOut);
        Assert.Contains("PROOF_TEMP=", result.Stdout);
        Assert.Contains("PROOF_CWD=" + _lastScratch, result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [MxcNativeFact]
    public async Task NativeSdk_DefaultEnvironmentProvidesTempWithoutInheritingParentProcessVariables()
    {
        var sentinel = "OPENCLAW_MXC_PARENT_ONLY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinel, "synthetic-parent-value");
        try
        {
            var result = await Runner.RunAsync(Cmd(
                "echo TEMP_STARTED & " +
                "(if exist \"%TEMP%\\.\" (echo TEMP_EXISTS) else (echo TEMP_MISSING)) & " +
                $"(if defined {sentinel} (echo PARENT_INHERITED) else (echo PARENT_ABSENT)) & " +
                "(echo TEMP_MARKER > \"%TEMP%\\ready.txt\") && type \"%TEMP%\\ready.txt\" & echo TEMP_FINISHED"));
            Write("default-environment-temp.json", result);
            var lines = result.Stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).ToArray();
            Assert.False(result.TimedOut);
            Assert.Contains("TEMP_STARTED", lines);
            Assert.Contains("TEMP_FINISHED", lines);
            Assert.Contains("TEMP_EXISTS", lines);
            Assert.Contains("TEMP_MARKER", lines);
            Assert.Contains("PARENT_ABSENT", lines);
            Assert.DoesNotContain("PARENT_INHERITED", lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinel, null);
        }
    }

    [MxcNativeFact]
    public async Task NativeSdk_UserFolderMatrixBlocksUngrantReadAndProtectedChildren()
    {
        File.WriteAllText(Path.Combine(Documents, "read.txt"), "DOCUMENT_MARKER");
        File.WriteAllText(Path.Combine(Outside, "read.txt"), "OUTSIDE_MARKER");
        var downloads = _context.RequireFolder("Downloads");
        var oneDrive = _context.RequireFolder("OneDrive");
        File.WriteAllText(Path.Combine(downloads, "read.txt"), "DOWNLOADS_MARKER");
        File.WriteAllText(Path.Combine(oneDrive, "read.txt"), "ONEDRIVE_MARKER");
        for (var i = 0; i < _context.ProtectedPaths.Count; i++)
            File.WriteAllText(Path.Combine(_context.ProtectedPaths[i], "synthetic-secret.txt"), $"PROTECTED_{i}");
        var secretAlias = Path.Combine(Documents, "secret-link");
        var outsideAlias = Path.Combine(Documents, "outside-link");
        CreateJunction(secretAlias, _context.ProtectedPaths[0]);
        CreateJunction(outsideAlias, Outside);

        foreach (var name in new[] { "strict", "balanced", "selected-write", "open" })
        {
            _settings.SystemRunFilesystemScope = name switch
            {
                "strict" => SystemRunFilesystemScope.SelectedFolders,
                "open" => SystemRunFilesystemScope.UserFilesReadWrite,
                _ => SystemRunFilesystemScope.UserFilesReadOnly,
            };
            _settings.SandboxCustomFolders = name == "selected-write"
                ? [new() { Path = Project, Access = SandboxFolderAccess.ReadWrite }] : [];
            var docWrite = Path.Combine(Documents, name + ".txt");
            var projectWrite = Path.Combine(Project, name + ".txt");
            var outsideWrite = Path.Combine(Outside, name + ".txt");
            var newChild = Path.Combine(Project, name, "child.txt");
            var commands = new[]
            {
                "echo WORKLOAD_STARTED",
                $"type \"{Path.Combine(Documents, "read.txt")}\"",
                $"type \"{Path.Combine(downloads, "read.txt")}\"",
                $"type \"{Path.Combine(oneDrive, "read.txt")}\"",
                $"echo CREATED > \"{docWrite}\"",
                $"echo CREATED > \"{projectWrite}\"",
                $"mkdir \"{Path.GetDirectoryName(newChild)}\"",
                $"echo CREATED > \"{newChild}\"",
                "echo SCRATCH_CREATED > \"%TEMP%\\t.txt\"",
                "type \"%TEMP%\\t.txt\"",
                $"type \"{Path.Combine(Outside, "read.txt")}\"",
                $"echo CREATED > \"{outsideWrite}\"",
                $"type \"{Path.Combine(secretAlias, "synthetic-secret.txt")}\"",
                $"echo ALIAS_CREATED > \"{Path.Combine(secretAlias, name + "-alias.txt")}\"",
                $"type \"{Path.Combine(outsideAlias, "read.txt")}\"",
                $"echo ALIAS_CREATED > \"{Path.Combine(outsideAlias, name + "-alias.txt")}\"",
                "echo WORKLOAD_FINISHED",
            };
            var result = await Runner.RunAsync(Cmd(string.Join(" & ", commands)));
            Write(name + "-output.json", result);
            Check(name, "workload-ran", result.Stdout.Contains("WORKLOAD_STARTED") && result.Stdout.Contains("WORKLOAD_FINISHED") && !result.TimedOut);
            Check(name, "personal-read", result.Stdout.Contains("DOCUMENT_MARKER") == (name != "strict"));
            Check(name, "redirected-downloads-read", result.Stdout.Contains("DOWNLOADS_MARKER") == (name != "strict"));
            Check(name, "redirected-onedrive-read", result.Stdout.Contains("ONEDRIVE_MARKER") == (name != "strict"));
            Check(name, "documents-write", File.Exists(docWrite) == (name == "open"));
            Check(name, "selected-project-write", File.Exists(projectWrite) == (name is "selected-write" or "open"));
            Check(name, "new-descendant-write", File.Exists(newChild) == (name is "selected-write" or "open"));
            Check(name, "sdk-temp-usable-without-initialization", result.Stdout.Contains("SCRATCH_CREATED"));
            Check(name, "ungranted-sibling-read-denied", !result.Stdout.Contains("OUTSIDE_MARKER"));
            Check(name, "ungranted-sibling-write-denied", !File.Exists(outsideWrite));
            Check(name, "protected-alias-read-denied", !result.Stdout.Contains("PROTECTED_0"));
            Check(name, "protected-alias-write-denied", !File.Exists(Path.Combine(_context.ProtectedPaths[0], name + "-alias.txt")));
            Check(name, "outside-alias-write-denied", !File.Exists(Path.Combine(Outside, name + "-alias.txt")));

            for (var i = 0; i < _context.ProtectedPaths.Count; i++)
            {
                var protectedRoot = _context.ProtectedPaths[i];
                var secret = Path.Combine(protectedRoot, "synthetic-secret.txt");
                var created = Path.Combine(protectedRoot, name + ".txt");
                var denial = await Runner.RunAsync(Cmd(
                    $"echo PROTECTED_STARTED & type \"{secret}\" & echo MUTATED > \"{secret}\" & " +
                    $"echo CREATED > \"{created}\" & echo PROTECTED_FINISHED"));
                Write($"{name}-protected-{i}-output.json", denial);
                Check(name, $"protected-{i}-workload-ran", denial.Stdout.Contains("PROTECTED_STARTED") && denial.Stdout.Contains("PROTECTED_FINISHED") && !denial.TimedOut);
                Check(name, $"protected-{i}-read-write-create-denied", !denial.Stdout.Contains("PROTECTED_" + i) &&
                    File.ReadAllText(secret) == "PROTECTED_" + i && !File.Exists(created));
            }
        }
        Write("summary.json", new { Passed = _checks.Count, Failed = 0, Scenarios = 4 });
    }

    private void Check(string scenario, string check, bool passed)
    {
        _checks.Add(new { Scenario = scenario, Check = check, Passed = passed });
        Write("checks.json", _checks);
        Assert.True(passed, $"{scenario}: {check}. Native observations, not absence alone, determine the result.");
    }

    private void CreateJunction(string path, string target)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", path, target }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        _processes.Add(new
        {
            Pid = process.Id, StartTimeUtc = process.StartTime.ToUniversalTime().ToString("O"),
            Executable = executable, CommandLine = string.Join(" ", info.ArgumentList),
            DataDirectories = IsolationRoots(), RunId = _runId,
        });
        Write("processes.json", _processes);
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
        _junctions.Add(path);
    }

    private static string?[] IsolationRoots() =>
        new[] { "OPENCLAW_TRAY_DATA_DIR", "OPENCLAW_TRAY_APPDATA_DIR", "OPENCLAW_TRAY_LOCALAPPDATA_DIR" }
            .Select(Environment.GetEnvironmentVariable).ToArray();
    private void Write(string name, object value) =>
        File.WriteAllText(Path.Combine(_root, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private void Register(string state)
    {
        var registry = Environment.GetEnvironmentVariable("OPENCLAW_MXC_LAB_REGISTRY");
        if (registry is not null)
            File.AppendAllText(registry, JsonSerializer.Serialize(new { RunId = _runId, State = state, Manifest = Path.Combine(_root, "ownership.json") }) + Environment.NewLine);
    }
    private static string Acl(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native ACL proof requires Windows.");
        return new DirectoryInfo(path).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
    }
    public void Dispose()
    {
        foreach (var record in _processes)
        {
            var identity = JsonSerializer.SerializeToElement(record);
            try
            {
                using var process = Process.GetProcessById(identity.GetProperty("Pid").GetInt32());
                if (process.StartTime.ToUniversalTime().ToString("O") == identity.GetProperty("StartTimeUtc").GetString() &&
                    !process.HasExited)
                {
                    Register("cleanup-blocked");
                    throw new InvalidOperationException("An owned process is still live. Its fixture and evidence were retained.");
                }
            }
            catch (ArgumentException) { /* The recorded process no longer exists. */ }
        }
        var after = _acls.ToDictionary(pair => pair.Key, pair => Acl(pair.Key), StringComparer.OrdinalIgnoreCase);
        Write("acl-after.json", after);
        if (_acls.Any(pair => pair.Value != after[pair.Key]))
        {
            Register("cleanup-blocked/acl-changed");
            throw new InvalidOperationException("A fixture or ancestor ACL changed. Evidence and fixtures retained.");
        }
        foreach (var junction in _junctions) if (Directory.Exists(junction)) Directory.Delete(junction);
        foreach (var directory in new[] { Profile, Outside, Path.Combine(_root, "s"),
            _context.RequireFolder("Downloads"), _context.RequireFolder("OneDrive") })
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Write("cleanup.json", new { FixturesRemoved = true, RunId = _runId });
        Register("stopped/fixtures-cleaned");
    }
}
