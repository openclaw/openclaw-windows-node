using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenClaw.Shared.Commands;

/// <summary>
/// Single owner of the canonical <c>cmd.exe /d /s /c &lt;command&gt;</c> carrier shape.
///
/// The Windows node originates this argv when it forwards a shell command, and the
/// gateway validates it against the approval record's rawCommand. Both the exec
/// approvals binder and the MXC command-line builder must agree on exactly which
/// argv shapes are that carrier and what command text it carries, otherwise one
/// layer can authorize a shape the other refuses to run (or vice versa).
/// </summary>
internal static class CanonicalCmdCarrier
{
    /// <summary>
    /// True when the token names cmd by basename, so both bare <c>cmd</c>/<c>cmd.exe</c>
    /// and a fully-qualified <c>C:\Windows\System32\cmd.exe</c> are recognized.
    ///
    /// This is the SERIALIZATION predicate. cmd parses its own raw command line
    /// instead of going through CommandLineToArgvW, so any argv that cmd will
    /// receive must be built with the cmd-aware serializer no matter where the
    /// image lives. Being permissive here fails safe: the worst case is correct
    /// quoting for an untrusted image. Do not use it to decide trust.
    /// </summary>
    internal static bool IsCmdExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        var fileName = Path.GetFileName(executable.Trim());
        return string.Equals(fileName, "cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "cmd.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only for a carrier we are willing to look THROUGH when deciding
    /// approval identity.
    ///
    /// Looking through a carrier means the operator is shown, and may durably
    /// approve, the inner executable while the outer image is what actually runs
    /// for a one-time allow. That is only sound when the outer image is the real
    /// system cmd. A copy of cmd.exe in a writable directory can ignore its
    /// arguments entirely and run arbitrary code, so it must never be looked
    /// through: an unrecognized carrier falls through to the indirect-host
    /// rejection and stays unbindable.
    ///
    /// The canonical gateway carrier uses the bare name. A bare name is only a token
    /// check; <see cref="ResolveTrustedCarrierPath"/> is what resolves it to a real
    /// system-directory image and is what durable binding requires. A fully-qualified
    /// token is accepted here only when it already points into one of those directories.
    /// </summary>
    internal static bool IsTrustedCarrierExecutable(string? executable)
    {
        if (!IsCmdExecutable(executable))
            return false;

        var trimmed = executable!.Trim();
        var directory = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(directory))
            return true;

        if (!Path.IsPathFullyQualified(trimmed))
            return false;

        foreach (var systemDirectory in SystemDirectories())
        {
            if (string.IsNullOrEmpty(systemDirectory))
                continue;
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(systemDirectory)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SystemDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
    }

    /// <summary>
    /// The absolute path a trusted carrier token must launch, or null when the token
    /// is not a trusted carrier.
    ///
    /// The canonical carrier arrives as a bare name, but a launched executable has to
    /// be fully qualified: leaving argv[0] relative would let Windows re-resolve it
    /// against PATH or the working directory at launch time, which is exactly the
    /// hijack the resolved-path rule exists to prevent. Pinning argv[0] here is not a
    /// rewrite of the command, it is the same identity spelled unambiguously.
    /// </summary>
    internal static string? ResolveTrustedCarrierPath(string? executable)
    {
        if (!IsTrustedCarrierExecutable(executable))
            return null;

        var trimmed = executable!.Trim();
        if (Path.IsPathFullyQualified(trimmed))
            return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;

        foreach (var systemDirectory in SystemDirectories())
        {
            if (string.IsNullOrEmpty(systemDirectory))
                continue;
            var candidate = Path.Combine(systemDirectory, "cmd.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Recognizes <c>cmd[.exe] /d /s /c &lt;tail...&gt;</c> and reconstructs the single
    /// command-line string cmd.exe receives.
    ///
    /// The live gateway uses one pre-joined tail element. Low-level callers and
    /// upstream approval fixtures may provide several already-tokenized elements.
    /// A multi-element tail is reconstructible only when every element is free of
    /// whitespace and quotes, because otherwise the process-creation quoting is not
    /// recoverable by a plain space join. Non-reconstructible tails return false so
    /// callers fail closed rather than guessing at a different command than the one
    /// that will run.
    /// </summary>
    internal static bool TryGetCanonicalPayload(IReadOnlyList<string> argv, out string payload) =>
        TryGetCanonicalPayload(argv, requireTrustedCarrier: false, out payload);

    /// <summary>
    /// Trust-side variant: only looks through a carrier that
    /// <see cref="IsTrustedCarrierExecutable"/> accepts.
    /// </summary>
    internal static bool TryGetTrustedCanonicalPayload(IReadOnlyList<string> argv, out string payload) =>
        TryGetCanonicalPayload(argv, requireTrustedCarrier: true, out payload);

    private static bool TryGetCanonicalPayload(
        IReadOnlyList<string> argv,
        bool requireTrustedCarrier,
        out string payload)
    {
        payload = "";
        if (argv is null
            || argv.Count < 5
            || !(requireTrustedCarrier
                ? IsTrustedCarrierExecutable(argv[0])
                : IsCmdExecutable(argv[0]))
            || !IsSwitch(argv[1], "/d")
            || !IsSwitch(argv[2], "/s")
            || !IsSwitch(argv[3], "/c"))
        {
            return false;
        }

        if (argv.Count == 5)
        {
            payload = argv[4];
            return true;
        }

        var builder = new StringBuilder();
        for (var i = 4; i < argv.Count; i++)
        {
            var token = argv[i];
            if (string.IsNullOrEmpty(token) || !IsSpaceJoinable(token))
                return false;
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(token);
        }

        payload = builder.ToString();
        return true;
    }

    private static bool IsSwitch(string token, string expected) =>
        string.Equals(token, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsSpaceJoinable(string token)
    {
        foreach (var ch in token)
        {
            if (char.IsWhiteSpace(ch) || ch == '"' || ch == '\'')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Builds the carrier that will actually run, with the payload's executable token
    /// pinned to <paramref name="pinnedExecutable"/> (the binder-resolved absolute
    /// path) and everything else preserved.
    ///
    /// Preserving the carrier keeps the in-band PATH and TEMP bootstrap that MXC
    /// requires, but a preserved carrier also means cmd.exe resolves the payload
    /// executable itself, at launch, searching the working directory before PATH. That
    /// is a different resolver running at a different time than the one that authorized
    /// the command, so the two can disagree and an attacker who can write to the
    /// working directory decides which one wins. Pinning removes the second resolution
    /// entirely: cmd is handed a fully qualified path and has nothing left to search.
    ///
    /// argv[0] is pinned to the resolved system cmd.exe for the same reason.
    ///
    /// Everything else is byte-preserved. The tail arity is preserved too: a
    /// pre-joined tail stays one element and a tokenized tail keeps its elements, so
    /// no new process-creation quoting is introduced. Returns false when the pinned
    /// path cannot be represented in the payload without changing how cmd parses it,
    /// which fails closed to prompt-only.
    /// </summary>
    internal static bool TryBuildPinnedCarrier(
        IReadOnlyList<string> argv,
        string carrierPath,
        string pinnedExecutable,
        out IReadOnlyList<string> pinnedArgv)
    {
        pinnedArgv = [];
        if (!TryGetTrustedCanonicalPayload(argv, out var payload))
            return false;
        if (string.IsNullOrWhiteSpace(carrierPath) || !Path.IsPathFullyQualified(carrierPath))
            return false;
        if (!Path.IsPathFullyQualified(pinnedExecutable))
            return false;
        if (!CmdPayloadTokenizer.TryPinExecutable(payload, pinnedExecutable, out var pinnedPayload))
            return false;

        var rebuilt = new string[argv.Count];
        rebuilt[0] = carrierPath;
        rebuilt[1] = argv[1];
        rebuilt[2] = argv[2];
        rebuilt[3] = argv[3];

        if (argv.Count == 5)
        {
            rebuilt[4] = pinnedPayload;
        }
        else
        {
            // A tokenized tail keeps one payload token per element, so the executable
            // is exactly element 4 and the rest are copied untouched.
            rebuilt[4] = pinnedExecutable;
            for (var i = 5; i < argv.Count; i++)
                rebuilt[i] = argv[i];
        }

        // Differential check: the carrier we are about to run must still parse as the
        // canonical shape and must yield exactly the payload we intended. This catches
        // any arity or joining mistake above rather than trusting the construction.
        if (!TryGetTrustedCanonicalPayload(rebuilt, out var roundTripped)
            || !string.Equals(roundTripped, pinnedPayload, StringComparison.Ordinal))
        {
            return false;
        }

        pinnedArgv = rebuilt;
        return true;
    }

    /// <summary>
    /// Verifies that a carrier about to be executed differs from the validated request
    /// only in the two ways pinning is allowed to change it: argv[0] resolved to the
    /// system cmd.exe, and the payload's executable token replaced by a fully qualified
    /// path to the same file name. Every other token, and all interior spacing, must be
    /// byte-identical.
    ///
    /// The payload check is done by reconstruction rather than by comparison. We take
    /// the request's own payload, apply the single edit pinning is permitted to make,
    /// and require the result to equal the payload actually being executed byte for
    /// byte. Comparing token values instead would accept a payload whose tokens happen
    /// to match while its interior spacing was rewritten, which is drift this function
    /// exists to reject.
    ///
    /// This is checked again at execution time rather than trusted from bind time,
    /// because a rewritten command line is the one place metacharacter drift could be
    /// introduced between approval and launch.
    /// </summary>
    internal static bool PinnedCarrierMatchesRequest(
        IReadOnlyList<string> executionArgv,
        IReadOnlyList<string> requestArgv)
    {
        if (executionArgv.Count != requestArgv.Count || executionArgv.Count < 5)
            return false;

        var carrierPath = ResolveTrustedCarrierPath(requestArgv[0]);
        if (carrierPath is null
            || !string.Equals(executionArgv[0], carrierPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 1; i <= 3; i++)
        {
            if (!string.Equals(executionArgv[i], requestArgv[i], StringComparison.Ordinal))
                return false;
        }

        if (!TryGetCanonicalPayload(requestArgv, out var requestPayload)
            || !TryGetTrustedCanonicalPayload(executionArgv, out var executionPayload))
        {
            return false;
        }

        if (!CmdPayloadTokenizer.TryTokenize(requestPayload, out var requestTokens, out _)
            || !CmdPayloadTokenizer.TryTokenize(executionPayload, out var executionTokens, out _))
        {
            return false;
        }

        if (requestTokens.Count == 0 || executionTokens.Count == 0)
            return false;

        var pinned = executionTokens[0];
        if (!Path.IsPathFullyQualified(pinned)
            || !CmdPayloadTokenizer.IsSafelyRepresentableToken(pinned))
        {
            return false;
        }

        // Reconstruct the only payload this request is allowed to have become. Equality
        // here proves argument values, argument count, and every byte of interior
        // spacing survived unchanged, because the sole edit applied was the executable
        // token span.
        if (!CmdPayloadTokenizer.TryPinExecutable(requestPayload, pinned, out var expectedPayload)
            || !string.Equals(expectedPayload, executionPayload, StringComparison.Ordinal))
        {
            return false;
        }

        // The pinned path must still name the same program the request named, so
        // pinning can sharpen an identity but never swap it for a different one. A
        // bare name legitimately gains its PATHEXT extension when it is resolved, so
        // "tool" pinned to "...\tool.exe" is the same identity spelled completely.
        //
        // The equivalence is "pinned name is the request name plus one appended
        // extension", not "the request name looks extension-less". Path.HasExtension
        // is true for any dotted name, so testing it here would reject legitimately
        // versioned tools ("python3.11", "clang-15.0") whose whole name is the stem
        // that PATHEXT resolution appended ".exe" to. Those bind successfully, so
        // rejecting them only here would fail the run with an internal error instead
        // of the prompt-only fallback the binder decided on. Stripping exactly one
        // trailing extension cannot swap the stem, so this stays identity-preserving.
        var requestName = Path.GetFileName(requestTokens[0].Replace('/', '\\'));
        var pinnedName = Path.GetFileName(pinned);
        if (string.Equals(pinnedName, requestName, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(
            Path.GetFileNameWithoutExtension(pinnedName),
            requestName,
            StringComparison.OrdinalIgnoreCase);
    }
}
