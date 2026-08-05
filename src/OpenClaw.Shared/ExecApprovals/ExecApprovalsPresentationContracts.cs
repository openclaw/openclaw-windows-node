using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

public interface IExecApprovalsPresentationStore
{
    event EventHandler<ExecApprovalsChangedEventArgs>? Changed;

    Task<ExecApprovalsReadOnlySnapshotResult> GetSnapshotReadOnlyAsync(CancellationToken cancellationToken = default);

    ExecApprovalsWriterOrigin CreateWriterOrigin();

    Task<ExecApprovalsSnapshot?> ReplaceAsync(
        string baseHash,
        ExecApprovalsFile replacement,
        ExecApprovalsWriterOrigin? origin,
        Func<ExecApprovalsFile, ExecApprovalsFile, string?>? deltaValidator = null);
}

public sealed class ExecApprovalsWriterOrigin
{
    internal ExecApprovalsWriterOrigin()
    {
    }
}

public enum ExecApprovalsChangeKind
{
    SnapshotUpdated,
    SnapshotRecovered,
    SnapshotInvalid,
}

public enum ExecApprovalsSnapshotFailureKind
{
    LegacyMigrationRequired,
    UntrustedPath,
    UnsupportedVersion,
    MalformedJson,
    ReadFailed,
}

public sealed record ExecApprovalsSnapshotFailure(
    ExecApprovalsSnapshotFailureKind Kind,
    string Hash,
    int? Version,
    string Message);

public sealed record ExecApprovalsReadOnlySnapshotResult(
    ExecApprovalsSnapshot? Snapshot,
    ExecApprovalsSnapshotFailure? Failure,
    ExecApprovalsSnapshot? LastValidSnapshot)
{
    public bool IsSuccess => Failure is null;
}

public sealed class ExecApprovalsChangedEventArgs : EventArgs
{
    public ExecApprovalsChangedEventArgs(
        long sequence,
        ExecApprovalsChangeKind kind,
        string hash,
        int? version,
        ExecApprovalsSnapshot? snapshot,
        ExecApprovalsSnapshotFailure? failure,
        ExecApprovalsSnapshot? lastValidSnapshot,
        ExecApprovalsWriterOrigin? origin)
    {
        Sequence = sequence;
        Kind = kind;
        Hash = hash;
        Version = version;
        Snapshot = snapshot;
        Failure = failure;
        LastValidSnapshot = lastValidSnapshot;
        Origin = origin;
    }

    public long Sequence { get; }

    public ExecApprovalsChangeKind Kind { get; }

    public string Hash { get; }

    public int? Version { get; }

    public ExecApprovalsSnapshot? Snapshot { get; }

    public ExecApprovalsSnapshotFailure? Failure { get; }

    public ExecApprovalsSnapshot? LastValidSnapshot { get; }

    public ExecApprovalsWriterOrigin? Origin { get; }
}
