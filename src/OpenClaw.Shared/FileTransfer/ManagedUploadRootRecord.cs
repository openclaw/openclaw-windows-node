using System.Text.Json;

namespace OpenClaw.Shared.FileTransfer;

/// <summary>One root EA format shared by trusted bootstrap and the worker's defensive root checks.</summary>
internal sealed record ManagedUploadRootRecord(int Version, string Kind, string ParentDevice, string ParentInode,
    string Name, string? Device = null, string? Inode = null, string State = "creating")
{
    internal byte[] Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, FileJson.Options);
}
