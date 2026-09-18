using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayFixtureIsolationTests
{
    [Fact]
    public void Get_ValidAbsoluteRoots_EnablesWithoutReadingOrCreatingProfileState()
    {
        using var temp = new TempDirectory();
        var sentinel = temp.Combine("installed-state-sentinel.json");
        File.WriteAllText(sentinel, """{"synthetic":"unchanged"}""");
        var environment = ValidEnvironment(temp);
        var before = File.ReadAllBytes(sentinel);

        var context = GatewayFixtureIsolation.Get(environment.GetValueOrDefault);

        Assert.True(context.IsEnabled);
        Assert.Equal(temp.Combine("profile"), context.DataDirectory);
        Assert.Equal(temp.Combine("setup-local"), context.LocalDataDirectory);
        Assert.False(Directory.Exists(context.DataDirectory));
        Assert.False(Directory.Exists(context.LocalDataDirectory));
        Assert.Equal(before, File.ReadAllBytes(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public void Get_WithoutExactOptIn_DoesNotResolveOrValidatePaths(string? mode)
    {
        var reads = new List<string>();
        var context = GatewayFixtureIsolation.Get(name =>
        {
            reads.Add(name);
            Assert.Equal(GatewayFixtureIsolation.ModeEnvironmentVariable, name);
            return mode;
        });

        Assert.False(context.IsEnabled);
        Assert.Null(context.DataDirectory);
        Assert.Null(context.LocalDataDirectory);
        Assert.Single(reads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("profile")]
    [InlineData("../profile")]
    [InlineData("C:profile")]
    [InlineData("\\profile")]
    public void Get_MissingOrRelativeRoot_ThrowsWithoutFallback(string? root)
    {
        using var temp = new TempDirectory();
        foreach (var variable in new[]
        {
            GatewayFixtureIsolation.DataDirectoryEnvironmentVariable,
            GatewayFixtureIsolation.LocalDataDirectoryEnvironmentVariable,
        })
        {
            var environment = ValidEnvironment(temp);
            environment[variable] = root;

            var error = Assert.Throws<InvalidOperationException>(
                () => GatewayFixtureIsolation.Get(environment.GetValueOrDefault));

            Assert.Contains(variable, error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        }
    }

    [Fact]
    public void Get_InvalidAbsoluteRoot_ThrowsWithoutFallback()
    {
        using var temp = new TempDirectory();
        foreach (var variable in new[]
        {
            GatewayFixtureIsolation.DataDirectoryEnvironmentVariable,
            GatewayFixtureIsolation.LocalDataDirectoryEnvironmentVariable,
        })
        {
            var environment = ValidEnvironment(temp);
            environment[variable] = temp.Combine("invalid\0directory");

            var error = Assert.Throws<InvalidOperationException>(
                () => GatewayFixtureIsolation.Get(environment.GetValueOrDefault));

            Assert.Contains(variable, error.Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        }
    }

    [Theory]
    [InlineData("invalid*directory")]
    [InlineData("invalid?directory")]
    [InlineData("invalid:directory")]
    public void Get_InvalidWindowsDirectoryCharacters_AreRejected(string leaf)
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        var environment = ValidEnvironment(temp);
        environment[GatewayFixtureIsolation.DataDirectoryEnvironmentVariable] = temp.Combine(leaf);

        Assert.Throws<InvalidOperationException>(
            () => GatewayFixtureIsolation.Get(environment.GetValueOrDefault));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData("legacy-root")]
    [InlineData(" ")]
    public void Get_ShadowingLocalAppDataOverride_IsRejected(string overrideValue)
    {
        using var temp = new TempDirectory();
        var environment = ValidEnvironment(temp);
        environment[GatewayFixtureIsolation.LocalAppDataDirectoryEnvironmentVariable] = overrideValue;

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewayFixtureIsolation.Get(environment.GetValueOrDefault));

        Assert.Contains(GatewayFixtureIsolation.LocalAppDataDirectoryEnvironmentVariable, error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Get_ReturnsCanonicalRootsWithoutCreatingThem()
    {
        using var temp = new TempDirectory();
        var environment = ValidEnvironment(temp);
        environment[GatewayFixtureIsolation.DataDirectoryEnvironmentVariable] =
            temp.Combine("unused", "..", "profile");

        var context = GatewayFixtureIsolation.Get(environment.GetValueOrDefault);

        Assert.Equal(temp.Combine("profile"), context.DataDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    private static Dictionary<string, string?> ValidEnvironment(TempDirectory temp) => new()
    {
        [GatewayFixtureIsolation.ModeEnvironmentVariable] = "1",
        [GatewayFixtureIsolation.DataDirectoryEnvironmentVariable] = temp.Combine("profile"),
        [GatewayFixtureIsolation.LocalDataDirectoryEnvironmentVariable] = temp.Combine("setup-local"),
    };
}
