using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcEnvironmentTests
{
    [Fact]
    public void IsInsideAppContainer_NormalTestProcess_ReturnsFalse()
    {
        // Standard xUnit test runner is NOT an AppContainer process.
        // (If this test ever fails it likely means the test runner moved into
        // an AppContainer, which is a meaningful signal worth investigating.)
        Assert.False(MxcEnvironment.IsInsideAppContainer());
    }
}
