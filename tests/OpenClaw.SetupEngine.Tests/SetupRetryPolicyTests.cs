namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupRetryPolicyTests
{
    [Fact]
    public void PrepareWslInstallationRetry_EnablesCleanupWhenInitialSetupDidNot()
    {
        var config = new SetupConfig { CleanBeforeRun = false };

        SetupRetryPolicy.PrepareWslInstallationRetry(config);

        Assert.True(config.CleanBeforeRun);
    }

    [Fact]
    public void PrepareWslInstallationRetry_MakesStaleDistroCleanupEligible()
    {
        var config = new SetupConfig { CleanBeforeRun = false };
        using var logger = new SetupLogger(filePath: null);
        using var journal = new TransactionJournal(filePath: null, logger);
        var context = new SetupContext(
            config,
            logger,
            journal,
            new CommandRunner(logger),
            CancellationToken.None);
        var cleanup = new CleanupStaleDistroStep();

        SetupRetryPolicy.PrepareWslInstallationRetry(config);

        Assert.False(cleanup.CanSkip(context));
    }
}
