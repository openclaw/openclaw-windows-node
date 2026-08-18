namespace OpenClaw.SetupEngine;

public static class SetupRetryPolicy
{
    public static void PrepareWslInstallationRetry(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.CleanBeforeRun = true;
    }
}
