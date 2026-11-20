using System.Text.Json.Nodes;
using OpenClaw.Connection.LocalAi;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiPrefixCacheStoreTests
{
    private static string NameFor(int index) => $"openclaw-prefix-{index:x40}.bin";

    private static string Write(string directory, string name, int day)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "kv");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    [Fact]
    public void Key_IsStableAndChangesWithEveryInputThatInvalidatesTheSavedState()
    {
        var prefix = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "rules" });
        var tools = new JsonArray(new JsonObject { ["type"] = "function" });

        string key = LocalAiPrefixCacheKey.Compute("profile", "model", prefix, tools);

        Assert.Matches("^[0-9a-f]{40}$", key);
        Assert.Equal(key, LocalAiPrefixCacheKey.Compute("profile", "model", JsonNode.Parse(prefix.ToJsonString())!.AsArray(), tools));
        Assert.NotEqual(key, LocalAiPrefixCacheKey.Compute("profile2", "model", prefix, tools));
        Assert.NotEqual(key, LocalAiPrefixCacheKey.Compute("profile", "model2", prefix, tools));
        Assert.NotEqual(key, LocalAiPrefixCacheKey.Compute("profile", "model", prefix, null));
        Assert.NotEqual(key, LocalAiPrefixCacheKey.Compute(
            "profile", "model", new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "rules!" }), tools));
    }

    [Fact]
    public void FileName_UsesTheKeyAndTheGeneratedPattern()
    {
        string key = LocalAiPrefixCacheKey.Compute("p", "m", new JsonArray(), null);

        Assert.Equal($"openclaw-prefix-{key}.bin", LocalAiPrefixCacheStore.FileNameFor(key));
    }

    [Fact]
    public void Prune_KeepsTheFourMostRecentPrefixFilesAndNeverTouchesOtherFiles()
    {
        using var temp = new TempDirectory("prefix-cache-");
        var store = new LocalAiPrefixCacheStore(temp.Path);
        for (int i = 1; i <= 6; i++)
            Write(temp.Path, NameFor(i), i);
        File.WriteAllText(Path.Combine(temp.Path, "other.bin"), "x");
        File.WriteAllText(Path.Combine(temp.Path, "openclaw-prefix-short.bin"), "x");

        IReadOnlyList<string> removed = store.Prune();

        Assert.Equal([NameFor(1), NameFor(2)], removed.Order().ToArray());
        Assert.Equal(
            new[] { NameFor(3), NameFor(4), NameFor(5), NameFor(6), "openclaw-prefix-short.bin", "other.bin" }.Order(),
            Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Prune_RemovesNothingAtTheLimitAndToleratesAMissingDirectory()
    {
        using var temp = new TempDirectory("prefix-cache-");
        for (int i = 1; i <= 4; i++)
            Write(temp.Path, NameFor(i), i);

        Assert.Empty(new LocalAiPrefixCacheStore(temp.Path).Prune());
        Assert.Empty(new LocalAiPrefixCacheStore(Path.Combine(temp.Path, "missing")).Prune());
    }

    [Fact]
    public void Touch_CountsAsUseSoARestoredPrefixOutlivesOlderUnusedOnes()
    {
        using var temp = new TempDirectory("prefix-cache-");
        var store = new LocalAiPrefixCacheStore(temp.Path);
        for (int i = 1; i <= 5; i++)
            Write(temp.Path, NameFor(i), i);

        store.Touch(NameFor(1));
        IReadOnlyList<string> removed = store.Prune();

        Assert.Equal([NameFor(2)], removed);
        Assert.True(store.Exists(NameFor(1)));
    }

    [Fact]
    public void Exists_RejectsNamesThatAreNotGeneratedFileNames()
    {
        using var temp = new TempDirectory("prefix-cache-");
        File.WriteAllText(Path.Combine(temp.Path, "other.bin"), "x");

        Assert.False(new LocalAiPrefixCacheStore(temp.Path).Exists("other.bin"));
        Assert.False(new LocalAiPrefixCacheStore(temp.Path).Exists(@"..\" + NameFor(1)));
    }

    [Fact]
    public void Newest_ReturnsTheMostRecentlyUsedGeneratedFileAndItsKey()
    {
        using var temp = new TempDirectory("prefix-cache-");
        var store = new LocalAiPrefixCacheStore(temp.Path);
        Assert.Null(store.Newest());
        Write(temp.Path, NameFor(1), 1);
        Write(temp.Path, NameFor(3), 3);
        Write(temp.Path, NameFor(2), 2);
        File.WriteAllText(Path.Combine(temp.Path, "other.bin"), "x");

        Assert.Equal(NameFor(3), store.Newest());
        Assert.Equal($"{3:x40}", LocalAiPrefixCacheStore.KeyFromFileName(NameFor(3)));
        Assert.Null(new LocalAiPrefixCacheStore(Path.Combine(temp.Path, "missing")).Newest());
    }
}
