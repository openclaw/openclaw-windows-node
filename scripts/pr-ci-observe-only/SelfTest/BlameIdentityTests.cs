using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using OpenClaw.ObserveOnly;

internal static class BlameIdentityTests
{
    // Managed loader/filesystem controls inside the existing SelfTest process. No native child or real Blame is loaded.
    public static int Run()
    {
        int cases = 0;
        void Require(bool value) { if (!value) throw new InvalidOperationException("blame-identity-selftest"); }
        void Case(Action action) { action(); cases++; }
        var fixture = Path.Combine(AppContext.BaseDirectory, "Microsoft.TestPlatform.Extensions.BlameDataCollector.dll");
        // Independent expected digest: raw fixture bytes, not the selector's candidate/hash reader.
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant();
        var fixtureFullName = AssemblyName.GetAssemblyName(fixture).FullName;
        Case(() => Require(BlameIdentity.InspectLoaded(expectedHash).State == BlameIdentityState.Absent));
        var execution = Assembly.Load(new AssemblyName("Microsoft.TestPlatform.Extensions.BlameDataCollector"));
        var single = BlameIdentity.InspectLoaded(expectedHash);
        Case(() => Require(single.Accepted && single.State == BlameIdentityState.ExpectedSingle
            && ReferenceEquals(single.Identity!.Assembly, execution) && single.Identity.Sha256 == expectedHash
            && single.Identity.LoadedFullName == fixtureFullName
            && ReferenceEquals(AssemblyLoadContext.GetLoadContext(execution), AssemblyLoadContext.Default)));
        var copy = Assembly.LoadFile(Path.GetFullPath(fixture)); // Exact VSTest metadata discovery behavior.
        var snapshot = AppDomain.CurrentDomain.GetAssemblies();
        var matching = snapshot.Where(a => a.GetName().Name == "Microsoft.TestPlatform.Extensions.BlameDataCollector").ToArray();
        Case(() => {
            Require(!ReferenceEquals(execution, copy) && matching.Length == 2);
            Require(matching.Length != 1); // Independent old predicate must reject the legitimate duplicate-object state.
            var result = BlameIdentity.Inspect(snapshot, expectedHash);
            Require(result.Accepted && result.State == BlameIdentityState.ExpectedEquivalentCopies
                && ReferenceEquals(result.Identity!.Assembly, execution) && result.Identity.Sha256 == expectedHash);
        });
        Case(() => Require(BlameIdentity.Inspect([copy], expectedHash).State == BlameIdentityState.ConflictingIdentity));
        Case(() => Require(BlameIdentity.Inspect(snapshot, new string('0', 64)).State == BlameIdentityState.ConflictingIdentity));
        Case(() => Require(BlameIdentity.Inspect([new UnreadableAssembly()], expectedHash).State == BlameIdentityState.QueryFailed));
        Case(() => Require(BlameIdentity.Inspect(null, expectedHash).State == BlameIdentityState.QueryFailed));
        Case(() => Require(BlameIdentity.Inspect(Enumerable.Repeat(execution, 513).ToArray(), expectedHash).State == BlameIdentityState.QueryFailed));
        Case(() => Require(BlameIdentity.Inspect(Enumerable.Repeat(execution, 9).ToArray(), expectedHash).State == BlameIdentityState.QueryFailed));
        Case(() => Require(BlameIdentity.Inspect([typeof(string).Assembly], expectedHash).State == BlameIdentityState.Absent));
        // Synthetic fact mutations cover otherwise impractical corrupt metadata, without claiming runtime observation.
        var good = single.Identity!;
        void Bad(BlameAssemblyFact value) => Require(!BlameIdentity.Evaluate([value], expectedHash).Accepted
            && BlameIdentity.Evaluate([value], expectedHash).State == BlameIdentityState.ConflictingIdentity);
        void BadExtra(BlameAssemblyFact value) => Require(BlameIdentity.Evaluate([good, value], expectedHash).State == BlameIdentityState.ConflictingIdentity);
        var equivalent = good with { Assembly = copy, Context = BlameLoadContext.LoadFile };
        Case(() => Require(BlameIdentity.Evaluate([good, equivalent], expectedHash).State == BlameIdentityState.ExpectedEquivalentCopies));
        Case(() => Require(BlameIdentity.Evaluate([good, good], expectedHash).State == BlameIdentityState.ConflictingIdentity));
        Case(() => Bad(good with { Context = BlameLoadContext.Unsupported }));
        Case(() => Bad(good with { Dynamic = true }));
        Case(() => Bad(good with { Location = "" }));
        Case(() => Bad(good with { Location = "relative.dll" }));
        Case(() => Bad(good with { FileFullName = "Untrusted, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" }));
        Case(() => Bad(good with { LoadedFullName = "Untrusted, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", FileFullName = "Untrusted, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" }));
        Case(() => Bad(good with { LoadedMvid = Guid.Empty }));
        Case(() => Bad(good with { FileMvid = Guid.Parse("11111111-1111-1111-1111-111111111111") }));
        Case(() => Bad(good with { Sha256 = "PRIVATE" }));
        Case(() => Bad(good with { Version = "PRIVATE" }));
        Case(() => BadExtra(equivalent with { Location = Path.Combine(Path.GetDirectoryName(fixture)!, "other.dll") }));
        var otherName = new AssemblyName(fixtureFullName!) { Version = new Version(9, 8, 7, 6) }.FullName;
        Case(() => BadExtra(equivalent with { LoadedFullName = otherName!, FileFullName = otherName! }));
        var otherMvid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Case(() => BadExtra(equivalent with { LoadedMvid = otherMvid, FileMvid = otherMvid }));
        Case(() => BadExtra(equivalent with { Sha256 = new string('f',64) }));
        Case(() => Require(BlameIdentity.Evaluate([good], "PRIVATE").State == BlameIdentityState.ConflictingIdentity));
        Case(() => Require(BlameIdentity.Evaluate(null, expectedHash).State == BlameIdentityState.QueryFailed));
        Case(() => Require(BlameIdentity.Evaluate([], expectedHash).State == BlameIdentityState.Absent));
        Case(() => Require(BlameIdentity.Wire(BlameIdentityState.ExpectedEquivalentCopies) == "expected-equivalent-copies"
            && BlameIdentity.Wire((BlameIdentityState)999) == "query-failed"));
        return cases;
    }
    private sealed class UnreadableAssembly : Assembly
    {
        public override AssemblyName GetName() => throw new IOException("PRIVATE synthetic metadata read failure");
    }
}
