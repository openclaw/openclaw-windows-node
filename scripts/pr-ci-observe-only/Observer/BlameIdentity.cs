using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OpenClaw.ObserveOnly;

internal enum BlameIdentityState { Unobserved, Absent, ExpectedSingle, ExpectedEquivalentCopies, ConflictingIdentity, QueryFailed }
internal enum BlameLoadContext { Default, LoadFile, Unsupported }
internal sealed record BlameAssemblyFact(Assembly Assembly, BlameLoadContext Context, bool Dynamic, string Location,
    string LoadedFullName, string FileFullName, Guid LoadedMvid, Guid FileMvid, string Sha256, string Version);
internal sealed record BlameSelection(BlameIdentityState State, BlameAssemblyFact? Identity = null)
{
    public bool Accepted => State is BlameIdentityState.ExpectedSingle or BlameIdentityState.ExpectedEquivalentCopies;
}

// Identity of an already-loaded default-context binary, not proof of an active collector instance.
internal static class BlameIdentity
{
    public const string SimpleName = "Microsoft.TestPlatform.Extensions.BlameDataCollector";
    // Bound reflection work before inspecting names/files. Framework snapshots are normally far smaller.
    // Eight permits the documented default + LoadFile copies without accepting an unbounded copy set.
    internal const int MaximumSnapshotAssemblies = 512;
    internal const int MaximumMatchingAssemblies = 8;
    internal const long MaximumBinaryBytes = 16 * 1024 * 1024; // Narrow SDK extension, not arbitrary binary scanning.
    public static string Wire(BlameIdentityState state) => state switch {
        BlameIdentityState.Unobserved => "unobserved", BlameIdentityState.Absent => "absent",
        BlameIdentityState.ExpectedSingle => "expected-single", BlameIdentityState.ExpectedEquivalentCopies => "expected-equivalent-copies",
        BlameIdentityState.ConflictingIdentity => "conflicting-identity", _ => "query-failed" };

    public static BlameSelection InspectLoaded(string expectedHash)
    {
        try { return Inspect(AppDomain.CurrentDomain.GetAssemblies(), expectedHash); } // One snapshot; no assembly is loaded here.
        catch { return new(BlameIdentityState.QueryFailed); }
    }
    internal static BlameSelection Inspect(Assembly[]? snapshot, string expectedHash)
    {
        try
        {
            if (snapshot is null || snapshot.Length > MaximumSnapshotAssemblies) return new(BlameIdentityState.QueryFailed);
            var matches = new List<Assembly>();
            foreach (var assembly in snapshot)
            {
                if (assembly is null) return new(BlameIdentityState.QueryFailed);
                if (assembly.GetName().Name != SimpleName) continue;
                if (matches.Count == MaximumMatchingAssemblies) return new(BlameIdentityState.QueryFailed);
                matches.Add(assembly);
            }
            return Evaluate(matches.Select(ReadFact).ToArray(), expectedHash);
        }
        catch { return new(BlameIdentityState.QueryFailed); } // No exception text, paths or context names escape.
    }
    private static BlameAssemblyFact ReadFact(Assembly assembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        var kind = context == AssemblyLoadContext.Default ? BlameLoadContext.Default
            : context is not null && !context.IsCollectible
                && context.GetType().Assembly == typeof(AssemblyLoadContext).Assembly
                && context.GetType().FullName == "System.Runtime.Loader.IndividualAssemblyLoadContext"
                ? BlameLoadContext.LoadFile : BlameLoadContext.Unsupported;
        if (assembly.IsDynamic || kind == BlameLoadContext.Unsupported)
            return new(assembly, kind, assembly.IsDynamic, "", "", "", Guid.Empty, Guid.Empty, "", "");
        var path = ReceiptCustody.Canonical(assembly.Location);
        var name = assembly.GetName().FullName ?? "";
        var mvid = assembly.ManifestModule.ModuleVersionId;
        // A retained read handle excludes replacement/writing while disk identity and hash are compared.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length == 0 || file.Length > MaximumBinaryBytes) throw new InvalidDataException();
        var digest = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        file.Position = 0;
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        var metadata = pe.GetMetadataReader();
        var fileMvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        var fileName = AssemblyName.GetAssemblyName(path).FullName ?? ""; // Metadata read only, not Assembly.Load*.
        var version = FileVersionInfo.GetVersionInfo(path);
        return new(assembly, kind, false, path, name, fileName, mvid, fileMvid, digest,
            $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}");
    }
    // Pure comparison over privately observed facts. Synthetic negatives use this seam, not invented loader outcomes.
    internal static BlameSelection Evaluate(IReadOnlyList<BlameAssemblyFact>? facts, string expectedHash)
    {
        if (facts is null || facts.Count > MaximumMatchingAssemblies) return new(BlameIdentityState.QueryFailed);
        if (!Regex.IsMatch(expectedHash ?? "", "\\A[a-f0-9]{64}\\z")) return new(BlameIdentityState.ConflictingIdentity);
        if (facts.Count == 0) return new(BlameIdentityState.Absent);
        BlameAssemblyFact? execution = null;
        foreach (var fact in facts)
        {
            if (fact is null || fact.Assembly is null || fact.Dynamic || fact.Context is not (BlameLoadContext.Default or BlameLoadContext.LoadFile)
                || string.IsNullOrEmpty(fact.Location) || !Path.IsPathFullyQualified(fact.Location)
                || string.IsNullOrEmpty(fact.LoadedFullName) || fact.LoadedFullName != fact.FileFullName
                || fact.LoadedMvid == Guid.Empty || fact.LoadedMvid != fact.FileMvid || fact.Sha256 != expectedHash
                || !Regex.IsMatch(fact.Version ?? "", "\\A[0-9]{1,10}(\\.[0-9]{1,10}){3}\\z"))
                return new(BlameIdentityState.ConflictingIdentity);
            try { if (new AssemblyName(fact.LoadedFullName).Name != SimpleName) return new(BlameIdentityState.ConflictingIdentity); }
            catch { return new(BlameIdentityState.ConflictingIdentity); }
            if (fact.Context == BlameLoadContext.Default)
            {
                if (execution is not null) return new(BlameIdentityState.ConflictingIdentity);
                execution = fact;
            }
        }
        if (execution is null) return new(BlameIdentityState.ConflictingIdentity);
        foreach (var fact in facts)
            if (!string.Equals(fact.Location, execution.Location, StringComparison.OrdinalIgnoreCase)
                || fact.LoadedFullName != execution.LoadedFullName || fact.LoadedMvid != execution.LoadedMvid
                || fact.Sha256 != execution.Sha256 || fact.Version != execution.Version)
                return new(BlameIdentityState.ConflictingIdentity);
        return new(facts.Count == 1 ? BlameIdentityState.ExpectedSingle : BlameIdentityState.ExpectedEquivalentCopies, execution);
    }
}
