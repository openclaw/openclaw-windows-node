using System.Text;

namespace OpenClaw.TestSupport;

/// <summary>
/// Captures stdout/stderr and provides an environment lookup for CLI tests,
/// replacing the repeated <c>(StringWriter Out, StringWriter Err)</c> tuples
/// plus ad hoc env dictionaries in the CLI test projects. Optionally exposes a
/// shared <see cref="FakeMcpServer"/> for command/list-tools round trips.
/// See <c>docs/ARCHITECTURE.md</c> (ledger id <c>test-cli-harness</c>).
/// </summary>
public sealed class CliHarness : IDisposable
{
    private readonly Dictionary<string, string?> _env = new(StringComparer.OrdinalIgnoreCase);
    private FakeMcpServer? _server;

    /// <summary>Captured standard output writer to pass to the CLI runner.</summary>
    public StringWriter Out { get; } = new(new StringBuilder());

    /// <summary>Captured standard error writer to pass to the CLI runner.</summary>
    public StringWriter Err { get; } = new(new StringBuilder());

    /// <summary>Lazily created loopback MCP server (created on first access).</summary>
    public FakeMcpServer Server => _server ??= new FakeMcpServer();

    /// <summary>Text written to stdout so far.</summary>
    public string StdOut => Out.ToString();

    /// <summary>Text written to stderr so far.</summary>
    public string StdErr => Err.ToString();

    /// <summary>Registers an environment value visible via <see cref="EnvLookup"/>.</summary>
    public CliHarness WithEnv(string name, string? value)
    {
        _env[name] = value;
        return this;
    }

    /// <summary>
    /// Environment lookup delegate for CLI runners that accept one. Returns null
    /// for names that were not registered via <see cref="WithEnv"/>.
    /// </summary>
    public Func<string, string?> EnvLookup
        => name => _env.TryGetValue(name, out var value) ? value : null;

    public void Dispose()
    {
        _server?.Dispose();
        Out.Dispose();
        Err.Dispose();
    }
}
