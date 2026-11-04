namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class NativeGatewayCliTimeoutTests
{
    [Fact]
    public void Timeout_DefaultCoversObservedSlowPackagedCli()
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, null);
            // An isolated-session `openclaw config validate --json` was observed at ~131 s on ARM64.
            Assert.True(NativeGatewayCliTimeout.ResolveTimeout() > TimeSpan.FromSeconds(131));
            Assert.Equal(NativeGatewayCliTimeout.ResolveTimeout(),
                NativeGatewaySetupService.ConfigurationValidationTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData(" 90 ", 90)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("not-a-number", null)]
    [InlineData("2147484", null)]
    public void Timeout_EnvironmentOverride_IsUsedOnlyForValidValues(string value, int? expectedSeconds)
    {
        var previous = Environment.GetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, value);
            var expected = expectedSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : NativeGatewayCliTimeout.DefaultTimeout;
            Assert.Equal(expected, NativeGatewayCliTimeout.ResolveTimeout());
            Assert.Equal(expected, NativeGatewaySetupService.ConfigurationValidationTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NativeGatewayCliTimeout.TimeoutEnvironmentVariable, previous);
        }
    }
}
