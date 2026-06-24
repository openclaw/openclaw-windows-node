using OpenClaw.E2ETests;
using Xunit;

namespace OpenClaw.E2ETests.Setup;

[CollectionDefinition("E2E MXC Setup", DisableParallelization = true)]
public sealed class MxcE2ESetupCollection : ICollectionFixture<MxcE2ESetupFixture> { }

/// <summary>
/// Gates the expensive setup fixture before xUnit initializes WSL/tray state for
/// MXC-only proofs. Method-level skips are too late for collection fixtures.
/// </summary>
public sealed class MxcE2ESetupFixture : IAsyncLifetime
{
    private E2ESetupFixture? _inner;

    public E2ESetupFixture Inner => _inner
        ?? throw new InvalidOperationException($"MXC E2E fixture was not initialized: {MxcE2ETestGate.SkipReason ?? "unknown reason"}");

    public async Task InitializeAsync()
    {
        if (MxcE2ETestGate.SkipReason is not null)
            return;

        _inner = new E2ESetupFixture();
        await _inner.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_inner is not null)
            await _inner.DisposeAsync();
    }
}
