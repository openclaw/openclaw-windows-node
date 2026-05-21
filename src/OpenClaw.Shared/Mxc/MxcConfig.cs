using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// POCO contract for the JSON config wxc-exec.exe consumes via
/// <c>--config-base64</c> or <c>--config &lt;file&gt;</c>. Shape mirrors the
/// SDK's ContainerConfig (captured in tests/.../Mxc/Golden/*.json).
/// </summary>
public sealed record MxcConfig
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "0.4.0-alpha";

    [JsonPropertyName("containerId")]
    public required string ContainerId { get; init; }

    [JsonPropertyName("containment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Containment { get; init; }

    [JsonPropertyName("process")]
    public required MxcProcess Process { get; init; }

    [JsonPropertyName("appContainer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcAppContainer? AppContainer { get; init; }

    [JsonPropertyName("filesystem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcFilesystem? Filesystem { get; init; }

    [JsonPropertyName("network")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcNetwork? Network { get; init; }

    [JsonPropertyName("ui")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcUi? Ui { get; init; }

    [JsonPropertyName("lifecycle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcLifecycle? Lifecycle { get; init; }
}

public sealed record MxcProcess
{
    [JsonPropertyName("commandLine")]
    public required string CommandLine { get; init; }

    [JsonPropertyName("cwd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cwd { get; init; }

    [JsonPropertyName("env")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Env { get; init; }

    [JsonPropertyName("timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutMs { get; init; }
}

public sealed record MxcAppContainer
{
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Capabilities { get; init; }

    [JsonPropertyName("leastPrivilege")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LeastPrivilege { get; init; }

    [JsonPropertyName("ui")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcBaseProcessUi? Ui { get; init; }
}

public sealed record MxcBaseProcessUi
{
    [JsonPropertyName("isolation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Isolation { get; init; }

    [JsonPropertyName("desktopSystemControl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DesktopSystemControl { get; init; }

    [JsonPropertyName("systemSettings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemSettings { get; init; }

    [JsonPropertyName("ime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Ime { get; init; }
}

public sealed record MxcFilesystem
{
    [JsonPropertyName("readonlyPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ReadonlyPaths { get; init; }

    [JsonPropertyName("readwritePaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? ReadwritePaths { get; init; }

    [JsonPropertyName("deniedPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? DeniedPaths { get; init; }

    [JsonPropertyName("clearPolicyOnExit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ClearPolicyOnExit { get; init; }
}

public sealed record MxcNetwork
{
    [JsonPropertyName("enforcementMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnforcementMode { get; init; }

    [JsonPropertyName("defaultPolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultPolicy { get; init; }

    [JsonPropertyName("allowedHosts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? AllowedHosts { get; init; }

    [JsonPropertyName("blockedHosts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? BlockedHosts { get; init; }
}

public sealed record MxcUi
{
    [JsonPropertyName("disable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Disable { get; init; }

    [JsonPropertyName("clipboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Clipboard { get; init; }

    [JsonPropertyName("injection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Injection { get; init; }
}

public sealed record MxcLifecycle
{
    [JsonPropertyName("destroyOnExit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DestroyOnExit { get; init; }

    [JsonPropertyName("preservePolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PreservePolicy { get; init; }
}

/// <summary>Result returned by <see cref="MxcExecutor"/> after running wxc-exec.</summary>
public sealed record MxcResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public bool TimedOut { get; init; }
    public long DurationMs { get; init; }
}
