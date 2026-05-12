namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Cross-platform sandbox policy expressing what a contained payload can access.
/// Mirrors the <c>SandboxPolicy</c> shape from <c>@microsoft/mxc-sdk</c>'s
/// TypeScript types (see <c>microsoft/mxc/sdk/src/types.ts</c>). C# representation
/// so we can build policy without going through the Node bridge.
/// </summary>
public sealed record SandboxPolicy(
    string Version,
    FilesystemPolicy? Filesystem = null,
    NetworkPolicy? Network = null,
    UiPolicy? Ui = null,
    int? TimeoutMs = null);

public sealed record FilesystemPolicy(
    IReadOnlyList<string>? ReadwritePaths = null,
    IReadOnlyList<string>? ReadonlyPaths = null,
    IReadOnlyList<string>? DeniedPaths = null,
    bool? ClearPolicyOnExit = null);

public sealed record NetworkPolicy(
    bool AllowOutbound = false,
    bool AllowLocalNetwork = false,
    IReadOnlyList<string>? AllowedHosts = null,
    IReadOnlyList<string>? BlockedHosts = null);

public sealed record UiPolicy(
    bool AllowWindows = false,
    ClipboardPolicy Clipboard = ClipboardPolicy.None,
    bool AllowInputInjection = false);

public enum ClipboardPolicy
{
    None,
    Read,
    Write,
    All,
}

/// <summary>
/// When <see cref="SettingsData.SystemRunSandboxEnabled"/> is <c>true</c>, system.run
/// is contained via MXC AppContainer. When MXC is unavailable on the host, the call
/// is denied (no fallback). When the toggle is <c>false</c>, system.run runs on the
/// host as before.
/// </summary>
public enum SandboxMode
{
    /// <summary>Sandbox required; fail-closed if unavailable.</summary>
    Enabled,

    /// <summary>Bypass MXC entirely.</summary>
    Disabled,
}
