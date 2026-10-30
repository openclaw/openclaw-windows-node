using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class ConfigPatchResultTests
{
    [Theory]
    [InlineData("baseHash is stale")]
    [InlineData("invalid baseHash value")]
    [InlineData("BaseHash mismatch")]          // case-insensitive
    public void LooksLikeStaleBaseHash_IsTrueWhenErrorMentionsBaseHash(string error)
    {
        var result = new ConfigPatchResult { Ok = false, Error = error };
        Assert.True(result.LooksLikeStaleBaseHash);
    }

    [Theory]
    [InlineData("stale config detected")]
    [InlineData("STALE: config was updated by another client")]
    public void LooksLikeStaleBaseHash_IsTrueWhenErrorMentionsStale(string error)
    {
        var result = new ConfigPatchResult { Ok = false, Error = error };
        Assert.True(result.LooksLikeStaleBaseHash);
    }

    [Theory]
    [InlineData("conflict detected in hash value")]
    [InlineData("write conflict: baseHash changed")]
    public void LooksLikeStaleBaseHash_IsTrueWhenConflictAndHashCombined(string error)
    {
        var result = new ConfigPatchResult { Ok = false, Error = error };
        Assert.True(result.LooksLikeStaleBaseHash);
    }

    [Theory]
    [InlineData("property 'conflict_mode' is invalid")]  // must NOT be treated as stale hash
    [InlineData("validation conflict on field 'mode'")]
    [InlineData("conflict in schema definition")]
    public void LooksLikeStaleBaseHash_IsFalseWhenConflictAlone(string error)
    {
        var result = new ConfigPatchResult { Ok = false, Error = error };
        Assert.False(result.LooksLikeStaleBaseHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gateway rejected the patch")]
    [InlineData("permission denied")]
    public void LooksLikeStaleBaseHash_IsFalseForOtherErrors(string? error)
    {
        var result = new ConfigPatchResult { Ok = false, Error = error };
        Assert.False(result.LooksLikeStaleBaseHash);
    }

    [Fact]
    public void LooksLikeStaleBaseHash_IsFalseWhenOkTrue()
    {
        var result = new ConfigPatchResult { Ok = true };
        Assert.False(result.LooksLikeStaleBaseHash);
    }
}
