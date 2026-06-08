using System.Text.Json;
using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

/// <summary>
/// Tests for <see cref="MxcConfigBuilder"/>. Includes:
/// <list type="bullet">
/// <item>Golden tests vs SDK output captured by <c>tools/mxc/dump-sdk-config.cjs</c>.</item>
/// <item>Per-Sandbox-UI-setting audit tests.</item>
/// <item>Round-trip JSON shape (camelCase, no orphan fields).</item>
/// <item>Env scrub case-insensitivity.</item>
/// <item><c>ResolveToolDirsFromPath</c> via synthetic PATH.</item>
/// </list>
/// </summary>
public class MxcConfigBuilderTests
{
    private const string GoldenContainerId = "golden-test";

    // The harness used these placeholder paths; mirror them here so the C#
    // builder's filesystem grants match the golden fixtures.
    private static class P
    {
        public const string Documents = "C:\\Golden\\Documents";
        public const string Downloads = "C:\\Golden\\Downloads";
        public const string Desktop   = "C:\\Golden\\Desktop";
        public const string Custom    = "C:\\Golden\\Custom";
        public const string Settings  = "C:\\Golden\\Settings";
        public const string Ssh       = "C:\\Golden\\.ssh";
        public const string Chrome    = "C:\\Golden\\Chrome";
        public const string Edge      = "C:\\Golden\\Edge";
        public const string Brave     = "C:\\Golden\\Brave";
        public const string Firefox   = "C:\\Golden\\Firefox";
        public const string PsRead    = "C:\\Golden\\PSReadLine";
        public const string Scratch   = "C:\\Golden\\Scratch";
    }

    private static readonly string[] AlwaysDenied =
    {
        P.Settings, P.Ssh, P.Chrome, P.Edge, P.Brave, P.Firefox, P.PsRead,
    };

    private static SandboxPolicy LockedDownPolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: Array.Empty<string>(),
            ReadonlyPaths: Array.Empty<string>(),
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: false, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.None, AllowInputInjection: false),
        TimeoutMs: 30_000);

    private static SandboxPolicy BalancedPolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: Array.Empty<string>(),
            ReadonlyPaths: new[] { P.Documents, P.Downloads, P.Desktop },
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: true, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
        TimeoutMs: 60_000);

    private static SandboxPolicy PermissivePolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: new[] { P.Documents, P.Downloads, P.Desktop },
            ReadonlyPaths: Array.Empty<string>(),
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: true, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.All, AllowInputInjection: false),
        TimeoutMs: 300_000);

    private static SandboxPolicy CustomPolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: new[] { P.Custom },
            ReadonlyPaths: new[] { P.Documents },
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: true, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.All, AllowInputInjection: false),
        TimeoutMs: 60_000);

    private static SandboxExecutionRequest RequestFor(SandboxPolicy policy) => new(
        CapabilityCommand: "system.run",
        Args: JsonDocument.Parse("{}").RootElement,
        Policy: policy,
        TimeoutMs: 0); // explicitly zero so timeout in builder uses request.TimeoutMs=0 → default 30s

    [Theory]
    [InlineData("locked-down", "LockedDown")]
    [InlineData("balanced", "Balanced")]
    [InlineData("permissive", "Permissive")]
    [InlineData("custom", "Custom")]
    public void BuiltConfig_MatchesSdkGolden(string preset, string presetMethod)
    {
        SandboxPolicy policy = presetMethod switch
        {
            "LockedDown" => LockedDownPolicy(),
            "Balanced"   => BalancedPolicy(),
            "Permissive" => PermissivePolicy(),
            _            => CustomPolicy(),
        };

        // The golden test reproduces the exact harness recipe: no commandLine,
        // no env, no cwd, no PATH-resolved tool dirs. We pass an empty PATH and
        // an empty agent env. The harness stripped process.* (commandLine, cwd,
        // env, timeout) too; do the same on the C# side before comparing.
        var request = RequestFor(policy);
        var config = MxcConfigBuilder.Build(
            request,
            scratchDir: P.Scratch,
            containerId: GoldenContainerId,
            pathEnvVar: "");

        // Strip the process bag the harness dropped, plus normalize containerId.
        var stripped = config with
        {
            ContainerId = $"golden-{preset}",
            Process = config.Process with { CommandLine = "", Cwd = null, Env = null, TimeoutMs = null },
            Filesystem = config.Filesystem is null ? null : config.Filesystem with
            {
                ReadonlyPaths = config.Filesystem.ReadonlyPaths?
                    .Where(p => !IsDriveRoot(p))
                    .ToArray(),
            },
        };

        var actualJson = JsonSerializer.Serialize(stripped, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        // Drop "commandLine":"" because the SDK output has the process bag completely empty.
        using var actualDoc = JsonDocument.Parse(actualJson);
        var actualObj = JsonObjectNode.FromJsonElement(actualDoc.RootElement);
        if (actualObj.Contains("process") && actualObj["process"] is JsonObjectNode procNode &&
            procNode.TryGetString("commandLine", out var cmd) && cmd == string.Empty)
        {
            procNode.Remove("commandLine");
        }

        var goldenPath = ResolveGoldenPath(preset);
        var expectedJson = File.ReadAllText(goldenPath);
        using var expectedDoc = JsonDocument.Parse(expectedJson);
        var expectedObj = JsonObjectNode.FromJsonElement(expectedDoc.RootElement);

        // Deep structural equality, tolerant of property order.
        AssertJsonEqual(expectedObj, actualObj, path: "$");
    }

    private static string ResolveGoldenPath(string preset)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Mxc", "Golden", $"sdk-config-{preset}.json");
        if (File.Exists(local)) return local;
        // Fallback: walk up from the assembly dir to find the source path.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var probe = Path.Combine(dir, "tests", "OpenClaw.Shared.Tests", "Mxc", "Golden", $"sdk-config-{preset}.json");
            if (File.Exists(probe)) return probe;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException($"Golden not found for preset {preset}");
    }

    [Fact]
    public void Build_OutboundOn_AddsInternetClientCapability()
    {
        var policy = BalancedPolicy();
        var config = MxcConfigBuilder.Build(RequestFor(policy), P.Scratch, pathEnvVar: "");
        Assert.Contains("internetClient", config.AppContainer!.Capabilities!);
        Assert.Equal("allow", config.Network!.DefaultPolicy);
    }

    [Fact]
    public void Build_OutboundOff_OmitsInternetClient_AndNetworkBlocks()
    {
        var policy = LockedDownPolicy();
        var config = MxcConfigBuilder.Build(RequestFor(policy), P.Scratch, pathEnvVar: "");
        Assert.DoesNotContain("internetClient", config.AppContainer!.Capabilities!);
        Assert.Equal("block", config.Network!.DefaultPolicy);
    }

    [Theory]
    [InlineData(ClipboardPolicy.None,  "none")]
    [InlineData(ClipboardPolicy.Read,  "read")]
    [InlineData(ClipboardPolicy.Write, "write")]
    [InlineData(ClipboardPolicy.All,   "all")]
    public void Build_ClipboardMode_RoundTripsToWxcExecString(ClipboardPolicy mode, string expected)
    {
        var policy = LockedDownPolicy() with { Ui = new UiPolicy(false, mode, false) };
        var config = MxcConfigBuilder.Build(RequestFor(policy), P.Scratch, pathEnvVar: "");
        Assert.Equal(expected, config.Ui!.Clipboard);
    }

    [Fact]
    public void Build_AddsScratchDirToReadwritePaths()
    {
        var config = MxcConfigBuilder.Build(RequestFor(BalancedPolicy()), P.Scratch, pathEnvVar: "");
        Assert.Contains(P.Scratch, config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void Build_OverridesTempEnvVarsToScratch()
    {
        var request = RequestFor(BalancedPolicy()) with
        {
            Env = new Dictionary<string, string>
            {
                ["TEMP"] = "C:\\real-temp",
                ["TMP"] = "C:\\real-tmp",
                ["TMPDIR"] = "C:\\real-tmpdir",
            },
        };
        var config = MxcConfigBuilder.Build(request, P.Scratch, pathEnvVar: "");
        var env = config.Process.Env!;
        Assert.Contains($"TEMP={P.Scratch}", env);
        Assert.Contains($"TMP={P.Scratch}", env);
        Assert.Contains($"TMPDIR={P.Scratch}", env);
    }

    [Fact]
    public void Build_AutoGrantsCwdAsReadonly_WhenNotAlreadyCovered()
    {
        var request = RequestFor(BalancedPolicy()) with { Cwd = "C:\\unrelated\\workdir" };
        var config = MxcConfigBuilder.Build(request, P.Scratch, pathEnvVar: "");
        Assert.Contains("C:\\unrelated\\workdir", config.Filesystem!.ReadonlyPaths!);
        Assert.DoesNotContain("C:\\unrelated\\workdir", config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void Build_DoesNotDowngradeCwd_WhenAlreadyCoveredByReadwrite()
    {
        var policy = PermissivePolicy(); // Documents already in readwrite
        var request = RequestFor(policy) with { Cwd = Path.Combine(P.Documents, "subfolder") };
        var config = MxcConfigBuilder.Build(request, P.Scratch, pathEnvVar: "");

        Assert.DoesNotContain(Path.Combine(P.Documents, "subfolder"), config.Filesystem!.ReadonlyPaths!);
        Assert.DoesNotContain(Path.Combine(P.Documents, "subfolder"), config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void Build_DoesNotAutoGrantCwd_WhenAlreadyCovered()
    {
        var policy = BalancedPolicy(); // Documents already in readonly
        var request = RequestFor(policy) with { Cwd = Path.Combine(P.Documents, "subfolder") };
        var config = MxcConfigBuilder.Build(request, P.Scratch, pathEnvVar: "");
        // Should not have added the subfolder explicitly (parent already grants).
        Assert.DoesNotContain(Path.Combine(P.Documents, "subfolder"), config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void Build_DoesNotAutoGrantCwd_WhenOverlapsDenied()
    {
        var policy = BalancedPolicy();
        var request = RequestFor(policy) with { Cwd = Path.Combine(P.Ssh, "keys") };
        var config = MxcConfigBuilder.Build(request, P.Scratch, pathEnvVar: "");
        Assert.DoesNotContain(Path.Combine(P.Ssh, "keys"), config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void ResolvePathDirsForReadonly_ReturnsExistingPathDirs()
    {
        // Synthesize an existing dir on PATH; ensure it shows up as a readonly
        // grant candidate. No tool-name filter — every existing PATH dir
        // counts (mirrors the SDK's getAvailableToolsPolicy behavior).
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-tool-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var dirs = MxcConfigBuilder.ResolvePathDirsForReadonly(pathEnvVar: tempDir);
            Assert.Contains(tempDir, dirs);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Build_SynthesizesPathEnvFromGrantedPathDirs()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-path-env-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var config = MxcConfigBuilder.Build(RequestFor(BalancedPolicy()), P.Scratch, pathEnvVar: tempDir);
            Assert.Contains($"PATH={tempDir}", config.Process.Env!);
            Assert.Contains(tempDir, config.Filesystem!.ReadonlyPaths!);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Build_AddsDriveRootReadonlyForGrantedFolderTraversal()
    {
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { "C:\\workspace\\out" },
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: AlwaysDenied,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var config = MxcConfigBuilder.Build(RequestFor(policy), P.Scratch, pathEnvVar: "");
        Assert.Contains("C:\\", config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void Build_DoesNotTreatReadwriteChildAsCoveringParentCwd()
    {
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { "C:\\workspace\\out" },
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: AlwaysDenied,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var config = MxcConfigBuilder.Build(RequestFor(policy) with { Cwd = "C:\\workspace" }, P.Scratch, pathEnvVar: "");
        Assert.Contains("C:\\workspace", config.Filesystem!.ReadonlyPaths!);
        Assert.DoesNotContain("C:\\workspace", config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void ResolvePathDirsForReadonly_SkipsNonExistentDirs()
    {
        var fake = Path.Combine(Path.GetTempPath(), "definitely-not-real-xyzqq-" + Guid.NewGuid().ToString("N"));
        var dirs = MxcConfigBuilder.ResolvePathDirsForReadonly(pathEnvVar: fake);
        Assert.Empty(dirs);
    }

    [Fact]
    public void ResolvePathDirsForReadonly_SkipsDriveRoots()
    {
        var dirs = MxcConfigBuilder.ResolvePathDirsForReadonly(pathEnvVar: "C:\\");
        Assert.Empty(dirs);
    }

    [Fact]
    public void Build_DefensiveFilterStripsAllowEntriesOverlappingDenied()
    {
        // Caller (somehow) provides a custom RW folder pointing inside ~/.ssh.
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { Path.Combine(P.Ssh, "keys") },
                ReadonlyPaths: new[] { Path.Combine(P.Chrome, "Profile 1") },
                DeniedPaths: AlwaysDenied,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);
        var config = MxcConfigBuilder.Build(RequestFor(policy), P.Scratch, pathEnvVar: "");
        Assert.DoesNotContain(Path.Combine(P.Ssh, "keys"), config.Filesystem!.ReadwritePaths!);
        Assert.DoesNotContain(Path.Combine(P.Chrome, "Profile 1"), config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void Build_TimeoutDefaultsTo30sWhenRequestZero()
    {
        var config = MxcConfigBuilder.Build(RequestFor(BalancedPolicy()), P.Scratch, pathEnvVar: "");
        Assert.Equal(30_000, config.Process.TimeoutMs);
    }

    [Fact]
    public void Build_TimeoutHonorsRequestValue()
    {
        var req = RequestFor(BalancedPolicy()) with { TimeoutMs = 12_345 };
        var config = MxcConfigBuilder.Build(req, P.Scratch, pathEnvVar: "");
        Assert.Equal(12_345, config.Process.TimeoutMs);
    }

    // ---- helpers for tolerant JSON comparison ----

    private static void AssertJsonEqual(JsonObjectNode expected, JsonObjectNode actual, string path)
    {
        foreach (var key in expected.Keys)
        {
            Assert.True(actual.Contains(key), $"Missing key {path}.{key} in actual; actual keys=[{string.Join(",", actual.Keys)}]");
            var ev = expected[key];
            var av = actual[key];
            AssertNodeEqual(ev, av, $"{path}.{key}");
        }

        // Symmetric check: any extra keys on `actual` that aren't in `expected`
        // are reported, except for the small allow-list of fields our C# emits
        // that the SDK doesn't (and that we explicitly want to surface) and the
        // per-invocation random fields we already strip from the golden.
        foreach (var key in actual.Keys)
        {
            if (expected.Contains(key)) continue;
            if (IsToleratedExtraKey($"{path}.{key}")) continue;
            Assert.Fail($"Unexpected extra key in actual at {path}.{key}; expected keys=[{string.Join(",", expected.Keys)}]");
        }
    }

    private static bool IsDriveRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsToleratedExtraKey(string fullPath)
    {
        // commandLine/cwd/env/timeoutMs live under "process" — the SDK leaves
        // process empty when called with createConfigFromPolicy and we add
        // these ourselves. appContainer.name is a per-invocation random hex
        // that we stripped from the goldens.
        return fullPath is
            "$.process.commandLine" or
            "$.process.cwd" or
            "$.process.env" or
            "$.process.timeoutMs" or
            "$.appContainer.name";
    }

    private static void AssertNodeEqual(object? expected, object? actual, string path)
    {
        switch (expected)
        {
            case JsonObjectNode eo when actual is JsonObjectNode ao:
                AssertJsonEqual(eo, ao, path); break;
            case List<object?> el when actual is List<object?> al:
                Assert.True(el.Count == al.Count, $"{path}: expected length {el.Count}, got {al.Count}");
                for (int i = 0; i < el.Count; i++) AssertNodeEqual(el[i], al[i], $"{path}[{i}]");
                break;
            default:
                Assert.True(Equals(expected, actual) || string.Equals(expected?.ToString(), actual?.ToString(), StringComparison.Ordinal),
                    $"{path}: expected {expected} ({expected?.GetType().Name ?? "null"}), got {actual} ({actual?.GetType().Name ?? "null"})");
                break;
        }
    }

    /// <summary>Trivial ordered-key JSON object/array tree we can compare and mutate.</summary>
    private sealed class JsonObjectNode
    {
        private readonly Dictionary<string, object?> _map = new(StringComparer.Ordinal);
        private readonly List<string> _order = new();
        public IEnumerable<string> Keys => _order;
        public bool Contains(string key) => _map.ContainsKey(key);
        public object? this[string key] { get => _map[key]; set { if (!_map.ContainsKey(key)) _order.Add(key); _map[key] = value; } }
        public bool TryGetString(string key, out string value)
        {
            if (_map.TryGetValue(key, out var v) && v is string s) { value = s; return true; }
            value = string.Empty; return false;
        }
        public void Remove(string key) { _map.Remove(key); _order.Remove(key); }

        public static JsonObjectNode FromJsonElement(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Expected object root");
            var node = new JsonObjectNode();
            foreach (var prop in root.EnumerateObject()) node[prop.Name] = Convert(prop.Value);
            return node;
        }

        private static object? Convert(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.Object => FromJsonElement(el),
            JsonValueKind.Array => el.EnumerateArray().Select(Convert).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var i) ? (object)i : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
