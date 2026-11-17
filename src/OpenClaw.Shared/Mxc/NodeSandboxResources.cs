namespace OpenClaw.Shared.Mxc;

/// <summary>Caller-data rights, excluding loader and operation scratch rights.</summary>
public sealed class NodeSandboxDataAccess
{
    public IReadOnlyList<string> ReadonlyPaths { get; }
    public IReadOnlyList<string> ReadwritePaths { get; }
    public IReadOnlyList<string> DeniedPaths { get; }
    public IReadOnlyList<string> ProtectedPaths { get; }
    public IReadOnlyList<string> ExecutorPrivatePaths { get; }

    internal NodeSandboxDataAccess(IEnumerable<string> ro, IEnumerable<string> rw,
        IEnumerable<string> denied, IEnumerable<string> protectedPaths, IEnumerable<string>? executorPrivatePaths = null)
    {
        ReadonlyPaths = Freeze(ro);
        ReadwritePaths = Freeze(rw);
        DeniedPaths = Freeze(denied);
        ProtectedPaths = Freeze(protectedPaths);
        ExecutorPrivatePaths = Freeze(executorPrivatePaths ?? []);
    }

    public SandboxFolderAccess? EffectiveAccess(string path)
    {
        var normalized = MxcFolderAdmission.NormalizeIdentity(path);
        return ProtectedPaths.Concat(ExecutorPrivatePaths).Any(p => MxcRequestBuilder.Within(normalized, p))
            ? SandboxFolderAccess.NoAccess
            : MxcFolderPermissions.EffectiveAccess(normalized, ReadonlyPaths, ReadwritePaths, DeniedPaths);
    }

    internal static IReadOnlyList<string> Freeze(IEnumerable<string> paths) =>
        Array.AsReadOnly(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
}

/// <summary>Trusted load/scratch rights. These never authorize a caller-selected file target.</summary>
public sealed class NodeSandboxInfrastructureAccess
{
    public IReadOnlyList<string> ReadonlyPaths { get; }
    public IReadOnlyList<string> ReadwritePaths { get; }

    internal NodeSandboxInfrastructureAccess(IEnumerable<string> ro, IEnumerable<string> rw)
    {
        ReadonlyPaths = NodeSandboxDataAccess.Freeze(ro);
        ReadwritePaths = NodeSandboxDataAccess.Freeze(rw);
    }
}

public sealed record NodeSandboxResources(
    NodeSandboxDataAccess DataAccess, NodeSandboxInfrastructureAccess InfrastructureAccess)
{
    internal IEnumerable<string> ReadonlyPaths => DataAccess.ReadonlyPaths.Concat(InfrastructureAccess.ReadonlyPaths);
    internal IEnumerable<string> ReadwritePaths => DataAccess.ReadwritePaths.Concat(InfrastructureAccess.ReadwritePaths);
    internal IEnumerable<string> DeniedPaths => DataAccess.ProtectedPaths.Concat(DataAccess.DeniedPaths)
        .Concat(DataAccess.ExecutorPrivatePaths);
}
