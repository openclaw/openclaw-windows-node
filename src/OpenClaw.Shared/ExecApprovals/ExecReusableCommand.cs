using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// A single executable whose resolved identity, reusable policy pattern, durable
/// argument binding, and execution transport are derived together and cannot drift
/// apart.
///
/// Approval identity and execution transport are deliberately separate.
/// <see cref="Argv"/> is the identity: the resolved executable plus the arguments it
/// will actually receive, and it is what <see cref="Pattern"/> and
/// <see cref="ArgPattern"/> describe. <see cref="ExecutionArgv"/> is the transport:
/// the argv the node hands to the sandbox.
///
/// For a directly invoked executable the two are the same list. For a strictly
/// recognized canonical cmd carrier they differ: the identity looks through the
/// carrier to the inner executable so the operator approves what really runs, while
/// the transport stays the original validated carrier. Substituting the bound direct
/// argv for the carrier would drop the carrier's in-band PATH/TEMP bootstrap, which
/// is the only environment contract MXC currently accepts (MxcConfigBuilder rejects
/// a non-empty process.env), so the transport must be preserved verbatim.
/// </summary>
public sealed class ExecReusableCommand
{
    /// <summary>
    /// Approval identity argv: the resolved executable followed by its arguments.
    /// Never the carrier. This is what the operator is shown and what durable policy
    /// describes.
    /// </summary>
    public IReadOnlyList<string> Argv { get; }

    /// <summary>
    /// Execution transport argv: exactly what the node runs. Identical to
    /// <see cref="Argv"/> for a direct invocation; the original, unmodified request
    /// argv when the identity was derived by looking through a canonical carrier.
    /// </summary>
    public IReadOnlyList<string> ExecutionArgv { get; }

    public ExecCommandResolution Resolution { get; }

    /// <summary>Durable executable-path pattern (the resolved fully-qualified path).</summary>
    public string Pattern { get; }

    /// <summary>
    /// Durable argument binding for <see cref="Argv"/>. Always present: a rule this
    /// node generates describes one operation, not one program. It is derived here
    /// rather than supplied so it can never describe different arguments than the
    /// identity it is stored beside.
    /// </summary>
    public string ArgPattern { get; }

    /// <summary>
    /// True when the identity was obtained by looking through a canonical carrier,
    /// so <see cref="ExecutionArgv"/> is the carrier rather than <see cref="Argv"/>.
    /// </summary>
    public bool IsCarrierTransport { get; }

    internal ExecReusableCommand(
        IReadOnlyList<string> argv,
        ExecCommandResolution resolution,
        IReadOnlyList<string>? executionArgv = null)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var resolvedPath = resolution.ResolvedPath;
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(resolvedPath))
            throw new ArgumentException("Reusable command requires a resolved executable.", nameof(argv));
        if (!Path.IsPathFullyQualified(resolvedPath))
            throw new ArgumentException("Reusable command executable must be fully qualified.", nameof(resolution));
        if (!string.Equals(argv[0], resolvedPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Reusable command argv must start with its resolved executable.", nameof(argv));
        if (executionArgv is { Count: 0 })
            throw new ArgumentException("Execution argv cannot be empty.", nameof(executionArgv));

        Argv = new ReadOnlyCollection<string>(argv.ToArray());
        ExecutionArgv = executionArgv is null
            ? Argv
            : new ReadOnlyCollection<string>(executionArgv.ToArray());
        IsCarrierTransport = executionArgv is not null;
        Resolution = resolution;
        Pattern = resolvedPath;
        ArgPattern = ExecArgPattern.BuildArgPattern(Argv);
    }
}
