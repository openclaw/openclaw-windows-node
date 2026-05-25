using OpenClaw.Connection;

namespace OpenClaw.Tray.Tests;

public class OperatorScopeHelperTests
{
    [Fact]
    public void CanReadConfig_AllowsAdminOrReadScope()
    {
        Assert.True(OperatorScopeHelper.CanReadConfig(["operator.admin"]));
        Assert.True(OperatorScopeHelper.CanReadConfig(["operator.read"]));
        Assert.False(OperatorScopeHelper.CanReadConfig(["operator.write"]));
        Assert.False(OperatorScopeHelper.CanReadConfig([]));
    }

    [Fact]
    public void CanWriteConfig_AllowsAdminOrWriteScope()
    {
        Assert.True(OperatorScopeHelper.CanWriteConfig(["operator.admin"]));
        Assert.True(OperatorScopeHelper.CanWriteConfig(["operator.write"]));
        Assert.False(OperatorScopeHelper.CanWriteConfig(["operator.read"]));
        Assert.False(OperatorScopeHelper.CanWriteConfig([]));
    }
}
