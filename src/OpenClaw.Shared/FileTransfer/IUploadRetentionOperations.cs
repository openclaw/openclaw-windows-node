namespace OpenClaw.Shared.FileTransfer;

/// <summary>Scheduling boundary. Implementations execute cleanup through the shared Sandbox owner.</summary>
public interface IUploadRetentionOperations
{
    event Action<UploadRetentionState>? RetentionChanged;
    /// <summary>Raised after an admitted upload worker has exited, including interrupted uploads.</summary>
    event Action<string>? UploadSettled;
    Func<string, UploadRootIdentity?>? UploadAdmission { get; set; }
    Task<FileOperationResult> RecoverUploadsAsync(string rootPath, UploadRootIdentity? identity, CancellationToken stop);
}
