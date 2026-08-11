using System;
using System.Collections.Generic;
using System.IO;
using OpenClaw.Shared.Commands;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Derives the single identity that is eligible for durable allowlist authorization.
///
/// Two properties matter here and are easy to conflate. The identity is what the
/// operator sees and what durable policy describes; the transport is what actually
/// runs. For a canonical cmd carrier those differ, and the binder is what keeps them
/// consistent: it looks through the carrier for identity while preserving the carrier
/// for execution.
/// </summary>
internal static class ExecReusableCommandBinder
{
    private static readonly HashSet<string> s_cmdBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy",
        "date", "del", "dir", "echo", "endlocal", "erase", "exit", "for",
        "ftype", "goto", "if", "md", "mkdir", "mklink", "move", "path",
        "pause", "popd", "prompt", "pushd", "rd", "rem", "ren", "rename",
        "rmdir", "set", "setlocal", "shift", "start", "time", "title", "type",
        "ver", "verify", "vol",
    };

    /// <summary>
    /// Why a command could not be bound to a durable identity. The command may still
    /// be approved as a one-time operation; this only explains why no reusable rule
    /// is offered, so the reason can be surfaced instead of a silent null.
    /// </summary>
    internal enum BindFailure
    {
        None = 0,
        EmptyCommand,
        NonCanonicalCmdCarrier,
        UntrustedCarrierImage,
        CarrierPayloadNotStatic,
        CarrierPayloadIsBuiltin,
        ShellWrapper,
        EnvWrapperHasModifiers,
        ExecutableNotResolved,
        ExecutableNotFound,
        ExecutableNotBindable,
        ExecutableOnNetworkPath,
        ArgumentContainsNul,
        CarrierPayloadNotPinnable,
    }

    internal static ExecReusableCommand? TryBind(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env)
        => TryBind(command, cwd, env, out _);

    internal static ExecReusableCommand? TryBind(
        IReadOnlyList<string> command,
        string? cwd,
        IReadOnlyDictionary<string, string>? env,
        out BindFailure failure)
    {
        failure = BindFailure.None;
        if (command.Count == 0)
        {
            failure = BindFailure.EmptyCommand;
            return null;
        }

        // Checked before any carrier parsing so it covers every path that can reach a
        // persisted binding. NUL is the argument separator inside a persisted argPattern,
        // so an argument containing one is ambiguous: "a\0b" renders identically to the
        // two arguments "a","b" and would let a stored rule match a differently segmented
        // argv. It is also not representable in a Windows command line, so rejecting is
        // fail-closed.
        if (ContainsNul(command))
        {
            failure = BindFailure.ArgumentContainsNul;
            return null;
        }

        if (CanonicalCmdCarrier.IsCmdExecutable(command[0]))
        {
            if (!CanonicalCmdCarrier.TryGetTrustedCanonicalPayload(command, out var payload))
            {
                // Distinguish the two ways a cmd-shaped argv can fail so the operator
                // and the logs can tell "we do not understand this shape" apart from
                // "this cmd image is not the system one".
                failure = CanonicalCmdCarrier.TryGetCanonicalPayload(command, out _)
                    ? BindFailure.UntrustedCarrierImage
                    : BindFailure.NonCanonicalCmdCarrier;
                return null;
            }

            if (!TryTokenizeStaticCmdPayload(payload, out var payloadArgv))
            {
                failure = BindFailure.CarrierPayloadNotStatic;
                return null;
            }
            if (payloadArgv.Count == 0
                || ExecCommandToken.IsEnv(payloadArgv[0])
                || s_cmdBuiltins.Contains(ExecCommandToken.NormalizedBasename(payloadArgv[0])))
            {
                failure = BindFailure.CarrierPayloadIsBuiltin;
                return null;
            }

            // The identity looks through the carrier to the inner executable, and the
            // carrier is preserved for transport so its in-band PATH/TEMP bootstrap
            // survives. Bind the identity first, because pinning needs the resolved
            // absolute path that binding produces.
            var bound = BindDirect(payloadArgv, cwd, env, executionArgv: null, out failure);
            if (bound is null)
                return null;

            var carrierPath = CanonicalCmdCarrier.ResolveTrustedCarrierPath(command[0]);
            if (carrierPath is null)
            {
                failure = BindFailure.UntrustedCarrierImage;
                return null;
            }

            // Pin both resolutions the carrier would otherwise perform at launch:
            // argv[0] so Windows cannot re-resolve a bare "cmd.exe" against PATH, and
            // the payload's executable token so cmd cannot resolve it against the
            // working directory. Without the second pin the command is authorized by
            // one resolver and run by another, and anything able to drop a file in the
            // working directory after approval picks the winner.
            //
            // Pinning is refused, not approximated. A path that cmd would not read back
            // byte for byte (spaces, quotes, %, !, ^, redirection or grouping
            // characters) fails closed here and the command stays prompt-only.
            if (!CanonicalCmdCarrier.TryBuildPinnedCarrier(
                    command, carrierPath, bound.Argv[0], out var executionArgv))
            {
                failure = BindFailure.CarrierPayloadNotPinnable;
                return null;
            }

            return new ExecReusableCommand(bound.Argv, bound.Resolution, executionArgv);
        }

        if (ExecShellWrapperNormalizer.Extract(command).IsWrapper)
        {
            failure = BindFailure.ShellWrapper;
            return null;
        }

        return BindDirect(command, cwd, env, executionArgv: null, out failure);
    }

    private static ExecReusableCommand? BindDirect(
        IReadOnlyList<string> argv,
        string? cwd,
        IReadOnlyDictionary<string, string>? env,
        IReadOnlyList<string>? executionArgv,
        out BindFailure failure)
    {
        failure = BindFailure.None;
        if (argv.Count == 0)
        {
            failure = BindFailure.EmptyCommand;
            return null;
        }

        // NUL is the argument separator in the persisted argPattern. Re-checked here
        // because the carrier branch tokenizes a payload string into a fresh argv, so a
        // token produced by that split has not passed the check at the top of TryBind.
        if (ContainsNul(argv))
        {
            failure = BindFailure.ArgumentContainsNul;
            return null;
        }

        if (ExecEnvInvocationUnwrapper.AnyWrapperHasModifiers(argv))
        {
            failure = BindFailure.EnvWrapperHasModifiers;
            return null;
        }
        var effectiveArgv = ExecEnvInvocationUnwrapper.UnwrapForResolution(argv);
        if (effectiveArgv.Count == 0
            || ExecCommandToken.IsEnv(effectiveArgv[0]))
        {
            failure = BindFailure.ExecutableNotResolved;
            return null;
        }

        var resolution = ExecCommandResolver.Resolve(effectiveArgv, cwd, env);
        var resolvedPath = resolution?.ResolvedPath;
        if (resolution is null
            || string.IsNullOrWhiteSpace(resolvedPath)
            || !Path.IsPathFullyQualified(resolvedPath))
        {
            failure = BindFailure.ExecutableNotResolved;
            return null;
        }
        if (IsNetworkPath(resolvedPath))
        {
            failure = BindFailure.ExecutableOnNetworkPath;
            return null;
        }
        if (!File.Exists(resolvedPath))
        {
            failure = BindFailure.ExecutableNotFound;
            return null;
        }
        if (!IsBindableExecutable(resolvedPath))
        {
            failure = BindFailure.ExecutableNotBindable;
            return null;
        }

        var boundArgv = new string[effectiveArgv.Count];
        boundArgv[0] = resolvedPath;
        for (var i = 1; i < effectiveArgv.Count; i++)
            boundArgv[i] = effectiveArgv[i];

        return new ExecReusableCommand(boundArgv, resolution.Value, executionArgv);
    }

    /// <summary>
    /// True for a UNC path or a path on a network-mapped drive.
    ///
    /// A durable rule records a path, and the content behind a network path is
    /// controlled by whoever serves the share rather than by the local machine, so a
    /// remote executable is never eligible for reuse. Allow-once still works.
    /// </summary>
    internal static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            return false;

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            // An unavailable drive cannot be shown to be local, so refuse durable reuse.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Delegates to <see cref="CmdPayloadTokenizer"/>, which is the single owner of cmd
    /// payload parsing so the binder and the carrier reconstruction cannot disagree
    /// about what a payload's tokens are.
    /// </summary>
    internal static bool TryTokenizeStaticCmdPayload(
        string payload,
        out IReadOnlyList<string> argv)
        => CmdPayloadTokenizer.TryTokenize(payload, out argv);

    /// <summary>
    /// Durable binding is restricted to images the loader executes directly.
    ///
    /// PATH resolution probes every PATHEXT entry, which by default also includes
    /// .COM, .VBS, .VBE, .JS, .JSE, .WSF, .WSH, and .MSC. Those targets are all
    /// interpreted content whose meaning can change without any change to the path
    /// that was approved, so an allowlist of extensions is used here rather than a
    /// denylist of the two batch extensions.
    ///
    /// This allowlist is deliberately .EXE only. Native .com images are executed by
    /// the loader too, but widening durable authorization to another image format is
    /// a separate decision with its own review, not a detail of carrier binding, so
    /// it is not made here. A .com target is still runnable; it is prompt-only, which
    /// is the fail-closed side.
    /// </summary>
    internal static bool IsBindableExecutable(string path)
        => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNul(IReadOnlyList<string> argv)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i].IndexOf('\0') >= 0)
                return true;
        }
        return false;
    }

    /// <summary>Stable diagnostic token for logs and prompts.</summary>
    internal static string DescribeFailure(BindFailure failure) => failure switch
    {
        BindFailure.None => "bound",
        BindFailure.EmptyCommand => "empty-command",
        BindFailure.NonCanonicalCmdCarrier => "non-canonical-cmd-carrier",
        BindFailure.UntrustedCarrierImage => "untrusted-cmd-carrier-image",
        BindFailure.CarrierPayloadNotStatic => "carrier-payload-not-static",
        BindFailure.CarrierPayloadIsBuiltin => "carrier-payload-is-shell-builtin",
        BindFailure.ShellWrapper => "shell-wrapper",
        BindFailure.EnvWrapperHasModifiers => "env-wrapper-has-modifiers",
        BindFailure.ExecutableNotResolved => "executable-not-resolved",
        BindFailure.ExecutableNotFound => "executable-not-found",
        BindFailure.ExecutableNotBindable => "executable-not-bindable",
        BindFailure.ExecutableOnNetworkPath => "executable-on-network-path",
        BindFailure.ArgumentContainsNul => "argument-contains-nul",
        BindFailure.CarrierPayloadNotPinnable => "carrier-payload-not-pinnable",
        _ => "unknown",
    };
}
