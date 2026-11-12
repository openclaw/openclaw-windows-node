using System.Reflection;

// An independently supplied build manifest is the process fixture's product
// input, not a bypass of resolver hashing, native ownership or deletion.
internal sealed class CacheManifestAssembly(byte[] bytes) : Assembly
{
    public override Stream GetManifestResourceStream(string name) => new MemoryStream(bytes, writable: false);
}
