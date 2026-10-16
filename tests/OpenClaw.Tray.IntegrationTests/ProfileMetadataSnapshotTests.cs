using OpenClaw.TestSupport.Ui;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class ProfileMetadataSnapshotTests
{
    [Fact]
    public void DiffReportsAddedRemovedAndModifiedEntriesWithoutReadingContents()
    {
        var old = new ProfileFileMetadata(10, DateTime.UnixEpoch);
        var current = new ProfileFileMetadata(11, DateTime.UnixEpoch.AddSeconds(1));
        var before = new Dictionary<string, ProfileFileMetadata>
        {
            ["profile\\unchanged"] = old, ["profile\\removed"] = old, ["profile\\modified"] = old
        };
        var after = new Dictionary<string, ProfileFileMetadata>
        {
            ["profile\\unchanged"] = old, ["profile\\added"] = current, ["profile\\modified"] = current
        };
        var changes = ProfileMetadataSnapshot.Compare(before, after);
        Assert.Equal(new[] { "added", "modified", "removed" }, changes.Select(change => change.Kind));
        Assert.Null(changes[0].Before);
        Assert.Equal(current, changes[0].After);
        Assert.Equal(old, changes[1].Before);
        Assert.Equal(current, changes[1].After);
        Assert.Null(changes[2].After);
    }

    [Fact]
    public void TimestampOnlyChangesAreStillIsolationChanges()
    {
        var before = new Dictionary<string, ProfileFileMetadata> { ["profile\\log"] = new(10, DateTime.UnixEpoch) };
        var after = new Dictionary<string, ProfileFileMetadata> { ["profile\\log"] = new(10, DateTime.UnixEpoch.AddSeconds(1)) };
        Assert.Single(ProfileMetadataSnapshot.Compare(before, after));
    }

    [Fact]
    public void AbsentRootIsDifferentFromPresentEmptyRoot()
    {
        using var root = new TempDirectory();
        var path = root.Combine("profile");
        var roots = new Dictionary<string, string> { ["test"] = path };
        var before = ProfileMetadataSnapshot.Capture(roots);
        Directory.CreateDirectory(path);
        var after = ProfileMetadataSnapshot.Capture(roots);
        var change = Assert.Single(ProfileMetadataSnapshot.Compare(before, after));
        Assert.Equal("test\\<root>", change.Path);
        Assert.Equal("added", change.Kind);
    }

    [Fact]
    public void SnapshotSerializesMetadataNotFileContent()
    {
        using var root = new TempDirectory();
        File.WriteAllText(root.Combine("synthetic.txt"), "do-not-export-this-synthetic-content");
        var snapshot = ProfileMetadataSnapshot.Capture(new Dictionary<string, string> { ["test"] = root.Path });
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("do-not-export", json);
        Assert.DoesNotContain(root.Path, json);
        Assert.Equal(new FileInfo(root.Combine("synthetic.txt")).Length, snapshot["test\\synthetic.txt"].Size);
        Assert.Empty(ProfileMetadataSnapshot.Compare(snapshot, ProfileMetadataSnapshot.Capture(
            new Dictionary<string, string> { ["test"] = root.Path })));
    }
}
