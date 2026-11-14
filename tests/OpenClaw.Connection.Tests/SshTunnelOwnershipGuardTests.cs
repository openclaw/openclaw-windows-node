using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class SshTunnelOwnershipGuardTests
{
    [Fact]
    public async Task ScopeTracksOwnMutationsAcrossAwait()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        using var scope = service.BeginOwnershipScope(service.OwnershipGeneration);
        service.Stop();
        Assert.Equal(service.OwnershipGeneration, scope.Generation);
        await Task.Yield();
        service.Stop();
        Assert.Equal(service.OwnershipGeneration, scope.Generation);
    }

    [Fact]
    public async Task IndependentMutationInvalidatesScopeWithoutStoppingNewerOwner()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        using (var scope = service.BeginOwnershipScope(service.OwnershipGeneration))
        {
            Task independent;
            using (ExecutionContext.SuppressFlow())
                independent = Task.Run(service.Stop);
            await independent;
            var newerGeneration = service.OwnershipGeneration;

            Assert.Throws<InvalidOperationException>(service.Stop);
            Assert.Throws<InvalidOperationException>(() =>
                service.EnsureStarted("proof", "proof.invalid", 18789, 45670));
            Assert.Equal(newerGeneration, service.OwnershipGeneration);
            Assert.NotEqual(newerGeneration, scope.Generation);
        }
    }

    [Fact]
    public void CheckpointCannotAdoptAWriterBetweenCleanupAndRestoration()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        long checkpoint;
        using (var cleanup = service.BeginOwnershipScope(service.OwnershipGeneration))
        {
            service.Stop();
            checkpoint = cleanup.Generation;
        }
        service.Stop();
        var newerGeneration = service.OwnershipGeneration;

        using (service.BeginOwnershipScope(checkpoint))
            Assert.Throws<InvalidOperationException>(service.Stop);

        Assert.Equal(newerGeneration, service.OwnershipGeneration);
    }
}
