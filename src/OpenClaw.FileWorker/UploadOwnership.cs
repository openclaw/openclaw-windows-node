using System.Runtime.Versioning;
using System.Text.Json;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.FileWorker;

/// <summary>One atomic native EA, attached during FILE_CREATE before an object can be observed unmarked.</summary>
[SupportedOSPlatform("windows")]
internal sealed record UploadOwnership(
    int Version, string Kind, string ParentDevice, string ParentInode, string Name,
    string? Device = null, string? Inode = null, string? FileName = null,
    string? FileDevice = null, string? FileInode = null, DateTimeOffset? Expires = null,
    string State = "creating", string? LegacyMarkerDevice = null, string? LegacyMarkerInode = null)
{
    internal static UploadOwnership Creating(NativeEntry parent, string name, string kind, string? fileName = null) =>
        new(1, kind, parent.Device, parent.Inode, name, FileName: fileName);

    internal UploadOwnership Bind(NativeEntry entry) => this with { Device = entry.Device, Inode = entry.Inode };
    internal byte[] Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, FileJson.Options);
    internal void Save(NativeEntry entry) => Native.WriteOwnership(entry.Handle, Bytes());

    internal static UploadOwnership? Read(NativeEntry entry)
    {
        var bytes = Native.ReadOwnership(entry.Handle);
        if (bytes is null) return null;
        try { return JsonSerializer.Deserialize<UploadOwnership>(bytes, FileJson.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw Blocked("Upload ownership metadata is invalid."); }
    }

    internal void Match(NativeEntry parent, NativeEntry entry, string kind)
    {
        if (Version != 1 || Kind != kind || ParentDevice != parent.Device || ParentInode != parent.Inode ||
            Name != Path.GetFileName(entry.Path) || (Device is null) != (Inode is null) ||
            (Device is not null && (Device != entry.Device || Inode != entry.Inode)) ||
            (Device is null && State != "creating") || State is not ("creating" or "retained" or "deleting"))
            throw Blocked("An upload ownership record no longer matches its native object.");
    }

    internal static FileCommandException Blocked(string message) => new("UPLOAD_CLEANUP_BLOCKED", message);
}
