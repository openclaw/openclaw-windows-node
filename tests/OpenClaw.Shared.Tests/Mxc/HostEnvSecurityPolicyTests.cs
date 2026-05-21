using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

/// <summary>
/// Tests for <see cref="HostEnvSecurityPolicy"/>. The policy is loaded from
/// the embedded canonical JSON copied from
/// <c>openclaw/openclaw:src/infra/host-env-security-policy.json</c>.
/// </summary>
public class HostEnvSecurityPolicyTests
{
    [Theory]
    // Common credential vars (in blockedEverywhereKeys or blockedOverrideOnlyKeys).
    [InlineData("GITHUB_TOKEN")]
    [InlineData("AWS_ACCESS_KEY_ID")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AZURE_CLIENT_SECRET")]
    [InlineData("NPM_TOKEN")]
    [InlineData("GH_TOKEN")]
    // Code-injection vectors.
    [InlineData("NODE_OPTIONS")]
    [InlineData("NODE_PATH")]
    [InlineData("PYTHONPATH")]
    [InlineData("PYTHONSTARTUP")]
    [InlineData("RUBYOPT")]
    [InlineData("PERL5OPT")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    // Git command-overrides.
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("GIT_EXTERNAL_DIFF")]
    [InlineData("GIT_ASKPASS")]
    public void IsBlocked_True_ForCanonicalListedVars(string name)
    {
        Assert.True(HostEnvSecurityPolicy.Default.IsBlocked(name),
            $"Expected {name} to be in the canonical openclaw blocklist.");
    }

    [Theory]
    // Prefix-based vectors (case-insensitive).
    [InlineData("LD_PRELOAD")]
    [InlineData("LD_LIBRARY_PATH")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("BASH_FUNC_foo%%")]
    [InlineData("ld_preload")] // lowercase should still match
    public void IsBlocked_True_ForBlockedPrefixes(string name)
    {
        Assert.True(HostEnvSecurityPolicy.Default.IsBlocked(name));
    }

    [Theory]
    // Malformed names — must not allow smuggling KEY=VAL pairs in.
    [InlineData("")]
    [InlineData("FOO=BAR")]
    [InlineData("FOO\0BAR")]
    [InlineData("FOO\nBAR")]
    [InlineData("FOO\rBAR")]
    public void IsBlocked_True_ForMalformedNames(string name)
    {
        Assert.True(HostEnvSecurityPolicy.Default.IsBlocked(name));
    }

    [Theory]
    // Names that should NOT be blocked — passed through to the sandbox.
    [InlineData("FOO_BAR")]
    [InlineData("MY_APP_CONFIG")]
    [InlineData("BUILD_NUMBER")]
    public void IsBlocked_False_ForBenignNames(string name)
    {
        Assert.False(HostEnvSecurityPolicy.Default.IsBlocked(name));
    }

    [Fact]
    public void Default_LoadsAllPolicyCategories()
    {
        // The canonical JSON has 90+ blockedEverywhereKeys and 144+ blockedOverrideOnlyKeys;
        // expect at minimum ~200 entries combined and at least 3 blocked prefixes.
        var policy = HostEnvSecurityPolicy.Default;
        Assert.True(policy.BlockedKeys.Count >= 200,
            $"Expected at least 200 blocked keys, got {policy.BlockedKeys.Count}");
        Assert.True(policy.BlockedPrefixes.Count >= 3,
            $"Expected at least 3 blocked prefixes, got {policy.BlockedPrefixes.Count}");
    }
}
