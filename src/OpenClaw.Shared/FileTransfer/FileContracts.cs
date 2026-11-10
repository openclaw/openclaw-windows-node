using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.FileTransfer;

public static class FileLimits
{
    public const int MessageBytes = 1024 * 1024;
    public const int DefaultUnaryBytes = 8 * MessageBytes;
    public const int UnaryBytes = 16 * MessageBytes;
    public const long DefaultCreateBytes = 50L * MessageBytes;
    public const long MaximumTransferBytes = 1024L * MessageBytes;
    public const int DefaultPageEntries = 200;
    public const int TreeEntries = 5000;
    public const int TreeDepth = 128;
    public const int ArchivePathBytes = 32_000;
    public const int DirectoryInventoryEntries = 100_000;
    public const int ExpandedArchiveBytes = 64 * MessageBytes;
    public const int MetadataBytes = 2 * MessageBytes;
    public const int RequestBytes = ((UnaryBytes + 2) / 3 * 4) + MetadataBytes;
    public const int ResultBytes = RequestBytes;
    public const long SafeInteger = 9_007_199_254_740_991;
    public const int OperationTimeoutMs = 60_000;
    public const int TransferTimeoutMs = 600_000;
    public const int RetainedDirectories = 64;
    public const long RetainedBytes = 256L * MessageBytes;
    public const int RetainedRoots = 16;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetentionRetry = TimeSpan.FromMinutes(5);
}

public enum FileOperation { Stat, Fetch, List, Archive, Write, Create, ListDirectories, Upload, ReapUploads }

public sealed class FileCommandException(string code, string message, bool invalidRequest = false) : NodeCommandException(message)
{
    public string Code { get; } = code;
    public bool InvalidRequest { get; } = invalidRequest;
}

public sealed record FileBinding(
    string Kind, string? Device = null, string? Inode = null,
    string? AnchorPath = null, string? AnchorDevice = null, string? AnchorInode = null,
    string? TargetDevice = null, string? TargetInode = null)
{
    public void Validate()
    {
        static bool Present(string? value) => !string.IsNullOrEmpty(value);
        if (Kind == "existing" && Present(Device) && Present(Inode) &&
            AnchorPath is null && AnchorDevice is null && AnchorInode is null && TargetDevice is null && TargetInode is null) return;
        if (Kind == "write" && Present(AnchorPath) && Present(AnchorDevice) && Present(AnchorInode) &&
            Device is null && Inode is null && ((TargetDevice is null && TargetInode is null) ||
                (Present(TargetDevice) && Present(TargetInode)))) return;
        throw new FileCommandException("INVALID_PARAMS", "Invalid path binding.");
    }
}

public sealed record FileCommandRequest(
    FileOperation Operation, string Path, bool PreflightOnly = false, bool FollowSymlinks = false,
    string? ExpectedCanonicalPath = null, FileBinding? ExpectedBinding = null,
    string? RootPath = null, long MaxBytes = FileLimits.DefaultUnaryBytes,
    bool Binary = false, int MaxEntries = FileLimits.DefaultPageEntries, long Offset = 0,
    string? ContentBase64 = null, bool Overwrite = false, bool CreateParents = false,
    bool RejectHardlinks = false, string? ExpectedSha256 = null, long SizeBytes = 0, string? Name = null);

/// <summary>Internal admission data. Never deserialized from node command arguments.</summary>
public sealed record FileWorkerAdmission(
    string ExecutionMode, string? Reason, long PolicyRevision, string PolicyFingerprint,
    FileDataAccess? DataAccess, string Home, string UploadRoot, UploadRootIdentity? ExpectedUploadRoot = null,
    long TransferBytes = FileLimits.DefaultCreateBytes, FileOperation? ExpectedOperation = null)
{
    public void Validate()
    {
        if (ExpectedOperation is { } operation && !Enum.IsDefined(operation))
            throw new FileCommandException("SANDBOX_UNAVAILABLE", "The admitted file operation is invalid.");
        if (TransferBytes is < 1 or > FileLimits.MaximumTransferBytes)
            throw new FileCommandException("SANDBOX_UNAVAILABLE", "The committed Sandbox transfer budget is invalid.");
        if (ExecutionMode == "sandbox" && DataAccess is not null && Reason is null) return;
        if (ExecutionMode == "host" && DataAccess is null && Reason == "windows_unsupported") return;
        throw new FileCommandException("SANDBOX_UNAVAILABLE", "The execution mode has no valid admitted policy.");
    }
}

public sealed record FileDataAccess(string[] ReadonlyPaths, string[] ReadwritePaths, string[] DeniedPaths, string[] ProtectedPaths)
{
    private enum Right { Read, Write, Deny }
    public void Demand(string path, bool write)
    {
        path = SandboxPathRules.Normalize(path);
        var boundaries = ReadwritePaths.Select(p => (p, Right.Write))
            .Concat(ReadonlyPaths.Select(p => (p, Right.Read)))
            .Concat(DeniedPaths.Select(p => (p, Right.Deny)));
        var right = SandboxPathRules.Effective(path, boundaries);
        if (ProtectedPaths.Any(p => SandboxPathRules.Within(path, SandboxPathRules.Normalize(p))) ||
            right is null or Right.Deny || (write && right != Right.Write))
            throw new FileCommandException("PERMISSION_DENIED", "The current Sandbox data policy does not allow this operation.");
    }
}

public sealed record UploadRootIdentity(string Path, string Device, string Inode);
public sealed record UploadRetentionState(UploadRootIdentity Root, int Count, long Bytes, DateTimeOffset? NextExpiry);
public sealed record FileSemanticOutcome(bool Ok, string? Code = null, string? Message = null);
public sealed record FileExecutionInfo(string ExecutionMode, string? Reason, long PolicyRevision);
public sealed record FileOperationResult(JsonElement Payload, FileSemanticOutcome Outcome,
    UploadRetentionState? Retention = null, FileExecutionInfo? Execution = null, bool InvalidRequest = false);
public sealed record FileWorkerRequest(int Version, FileWorkerAdmission Admission, FileCommandRequest Command);

public static class FileJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32
    };
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
}
