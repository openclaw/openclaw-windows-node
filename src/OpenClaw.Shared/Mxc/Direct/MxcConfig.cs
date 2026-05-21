// Shared with the OrcaCore project; keep namespace stable.
// Original schema (Version/ContainerId/Containment/Process/AppContainer/Filesystem) is unchanged.
// All additions are init-only, nullable, [JsonIgnore(WhenWritingNull)] so callers that don't
// set new fields produce byte-identical JSON.
using System.Text.Json.Serialization;

namespace OrcaCore.Models;

public sealed record MxcConfig
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "0.4.0-alpha";

    [JsonPropertyName("containerId")]
    public required string ContainerId { get; init; }

    [JsonPropertyName("containment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Containment { get; init; } = "appcontainer";

    [JsonPropertyName("process")]
    public required MxcProcess Process { get; init; }

    [JsonPropertyName("appContainer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcAppContainer? AppContainer { get; init; }

    [JsonPropertyName("filesystem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcFilesystem? Filesystem { get; init; }

    // Additive (OpenClaw): network policy. Null = wxc-exec defaults.
    [JsonPropertyName("network")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcNetwork? Network { get; init; }

    // Additive (OpenClaw): top-level UI policy (clipboard, injection, disable).
    [JsonPropertyName("ui")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcUi? Ui { get; init; }

    // Additive (OpenClaw): lifecycle controls. Set only when golden capture proves SDK does.
    [JsonPropertyName("lifecycle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MxcLifecycle? Lifecycle { get; init; }
}

public sealed record MxcProcess
{
    [JsonPropertyName("commandLine")]
    public required string CommandLine { get; init; }

    // Additive (OpenClaw): explicit cwd inside the container.
    [JsonPropertyName("cwd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cwd { get; init; }

    // Additive (OpenClaw): environment as KEY=VALUE strings.
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

    // Additive (OpenClaw): mirror SDK fields when golden capture shows them set.
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("leastPrivilege")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LeastPrivilege { get; init; }

    // Additive (OpenClaw): per-process BaseProcess UI block. wxc-exec accepts either
    // appContainer.ui or top-level ui depending on its mode; we serialize whichever
    // the golden capture confirms.
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

    // Additive (OpenClaw): explicit deny list (wins over allow).
    [JsonPropertyName("deniedPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? DeniedPaths { get; init; }

    // Additive (OpenClaw): tear down policy on container exit.
    [JsonPropertyName("clearPolicyOnExit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ClearPolicyOnExit { get; init; }

    [JsonPropertyName("executablePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutablePath { get; init; }
}

// Additive (OpenClaw): network policy block.
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

// Additive (OpenClaw): top-level UI policy.
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

// Additive (OpenClaw): lifecycle block.
public sealed record MxcLifecycle
{
    [JsonPropertyName("destroyOnExit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DestroyOnExit { get; init; }

    [JsonPropertyName("preservePolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PreservePolicy { get; init; }
}

public sealed record MxcResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }

    // Additive (OpenClaw): true if WaitForExit cancelled via host-side timeout/cancellation.
    public bool TimedOut { get; init; }

    // Additive (OpenClaw): wall-clock duration of the wxc-exec invocation.
    public long DurationMs { get; init; }
}
