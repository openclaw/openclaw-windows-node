using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared.FileTransfer;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Projects one executor-owned profile upload boundary into an admitted operation.
/// ResolvePath never consumes a caller target or the legacy upload-location setting.
/// </summary>
internal static class ManagedUploadPolicy
{
    internal static string ResolvePath(string scratchRoot, string settingsDirectory)
    {
        var parent = Path.GetDirectoryName(MxcFolderAdmission.NormalizeIdentity(scratchRoot))
            ?? throw new NotSupportedException("Managed upload storage has no runtime parent.");
        var profile = MxcFolderAdmission.NormalizeIdentity(settingsDirectory).ToUpperInvariant();
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile)));
        var root = Path.Combine(parent, "Uploads-" + identity);
        if (MxcRequestBuilder.Within(root, profile) || MxcRequestBuilder.Within(profile, root))
            throw new NotSupportedException("Managed upload storage cannot overlap its settings profile.");
        return root;
    }

    internal static void ValidateBoundary(NodeSandboxDataAccess data, string root)
    {
        if (data.ProtectedPaths.Concat(data.ExecutorPrivatePaths).Any(path =>
            MxcRequestBuilder.Within(root, path) || MxcRequestBuilder.Within(path, root)))
            throw new NotSupportedException("Managed upload storage conflicts with a protected or executor-private folder.");
    }

    internal static NodeSandboxResources Apply(NodeSandboxResources resources, string root, bool owned, bool mutate)
    {
        var data = resources.DataAccess;
        ValidateBoundary(data, root);
        var savedAccess = data.EffectiveAccess(root);
        if (mutate && savedAccess is SandboxFolderAccess.ReadOnly or SandboxFolderAccess.NoAccess)
            throw new FileCommandException("PERMISSION_DENIED", "The current Sandbox policy restricts managed upload storage.");
        var read = owned && savedAccess != SandboxFolderAccess.NoAccess;
        var ro = data.ReadonlyPaths.ToList();
        var rw = data.ReadwritePaths.ToList();
        var denied = data.DeniedPaths.ToList();
        if (mutate)
            rw.Add(root);
        else
        {
            // An explicit descendant RW must not pierce the managed read-only boundary.
            // Keep all explicit denies, and never turn an unowned name into data authority.
            var descendants = rw.Where(path => MxcRequestBuilder.Within(path, root)).ToArray();
            rw.RemoveAll(path => MxcRequestBuilder.Within(path, root));
            if (read)
            {
                ro.AddRange(descendants);
                ro.Add(root);
            }
            else
            {
                ro.RemoveAll(path => MxcRequestBuilder.Within(path, root));
                denied.Add(root);
            }
        }
        return new(new(ro, rw, denied, data.ProtectedPaths, data.ExecutorPrivatePaths), resources.InfrastructureAccess);
    }
}

// Finite trusted metadata, not a caller-supplied grant list or execution-mode selector.
internal sealed record FixedFileInvocation(FileOperation Operation,
    string? RecoveryRoot = null, UploadRootIdentity? ExpectedRoot = null, long UploadBytes = 0);
internal sealed record AdmittedFileInvocation(FileOperation Operation,
    string UploadRoot, UploadRootIdentity? ExpectedRoot);
