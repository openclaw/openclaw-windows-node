namespace OpenClaw.TestSupport;

/// <summary>
/// Creates a unique temporary directory for a test and best-effort deletes it
/// on dispose. Replaces the repeated
/// <c>Path.Combine(Path.GetTempPath(), "openclaw-test-" + Guid...)</c> plus
/// <c>Directory.Delete(..., true)</c> boilerplate scattered across the suite.
/// See <c>docs/ARCHITECTURE.md</c> (ledger id <c>test-temp-dir</c>).
/// </summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Absolute path to the created directory.</summary>
    public string Path { get; }

    public TempDirectory(string prefix = "openclaw-test-")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    /// <summary>Combines relative parts against the temp directory root.</summary>
    public string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Path;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    public override string ToString() => Path;

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
