namespace OpenClaw.TestSupport;

/// <summary>
/// Saves and restores environment variables for the duration of a test.
/// Replaces the repeated read-original / try-finally / SetEnvironmentVariable
/// restore dance (see e.g. SetupContextTests). Restores every variable it set,
/// in reverse order, on dispose. See <c>docs/ARCHITECTURE.md</c> (ledger id
/// <c>test-env-scope</c>).
/// </summary>
/// <remarks>
/// Environment variables are process-global, so tests that use this fixture
/// must not run in parallel with other tests that read/write the same
/// variables. Keep such tests in a non-parallel xUnit collection (see the
/// existing <c>EnvironmentVariableCollection</c> pattern).
/// </remarks>
public sealed class EnvironmentScope : IDisposable
{
    private readonly List<(string Name, string? Original)> _originals = new();

    /// <summary>Creates an empty scope. Use <see cref="Set"/> to mutate variables.</summary>
    public EnvironmentScope()
    {
    }

    /// <summary>Creates a scope and immediately sets one variable.</summary>
    public EnvironmentScope(string name, string? value)
    {
        Set(name, value);
    }

    /// <summary>
    /// Sets an environment variable, remembering its prior value so it is
    /// restored on dispose. Fluent: returns this scope.
    /// </summary>
    public EnvironmentScope Set(string name, string? value)
    {
        _originals.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        for (var i = _originals.Count - 1; i >= 0; i--)
        {
            var (name, original) = _originals[i];
            Environment.SetEnvironmentVariable(name, original);
        }
        _originals.Clear();
    }
}
