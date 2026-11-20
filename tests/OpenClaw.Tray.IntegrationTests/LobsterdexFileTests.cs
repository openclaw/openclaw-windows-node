using System.Text.Json;
using OpenClawTray.Chat.LobsterVisits;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class LobsterdexFileTests
{
    [Fact]
    public async Task ReopeningPreservesFirstNameAndDate_AndRecordsOnlyTheFirstShiny()
    {
        using var profile = new TempDirectory();
        using var otherProfile = new TempDirectory();
        var store = LobsterdexFile.ForProfile(profile.Path);
        var firstDate = new DateTimeOffset(2026, 8, 2, 23, 55, 0, TimeSpan.FromHours(-7));
        var firstShiny = firstDate.AddDays(1);
        var updates = 0;
        store.Changed += () => Interlocked.Increment(ref updates);

        await store.RecordAsync("crimson", "Pinchy", false, firstDate);
        var shiny = store.RecordAsync("crimson", "Scuttles", true, firstShiny);
        var blue = store.RecordAsync("blue", "Blueberry", false, firstDate.AddDays(2));
        // Shutdown must drain already admitted writes even before their callers await them.
        await LobsterdexFile.DrainProfileAsync(profile.Path);
        Assert.Equal(2, (await new LobsterdexFile(Path.Combine(profile.Path, "lobsterdex.json")).ReadAsync()).Count);
        await Task.WhenAll(shiny, blue);
        await store.RecordAsync("crimson", "Bubbles", true, firstDate.AddDays(3));
        Assert.Equal(3, updates);

        var reopened = await new LobsterdexFile(Path.Combine(profile.Path, "lobsterdex.json")).ReadAsync();
        Assert.Equal(2, reopened.Count);
        Assert.Equal(new("Pinchy", firstDate, firstShiny), reopened["crimson"]);
        Assert.Equal(firstDate.Offset, reopened["crimson"].FirstSeenAt.Offset);
        Assert.Null(reopened["blue"].ShinySeenAt);
        Assert.Empty(await LobsterdexFile.ForProfile(otherProfile.Path).ReadAsync());
    }

    [Fact]
    public async Task UnreadableCollectionDoesNotGetReplacedByANewVisit()
    {
        using var profile = new TempDirectory();
        const string broken = """{"crimson":{"name":"Pinchy","firstSeenAt":"not a date"}}""";
        var path = Path.Combine(profile.Path, "lobsterdex.json");
        await File.WriteAllTextAsync(path, broken);

        var store = LobsterdexFile.ForProfile(profile.Path);
        await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync());
        await Assert.ThrowsAsync<JsonException>(() => store.RecordAsync("blue", "Blueberry", false, DateTimeOffset.UnixEpoch));
        Assert.Equal(broken, await File.ReadAllTextAsync(path));
    }
}
