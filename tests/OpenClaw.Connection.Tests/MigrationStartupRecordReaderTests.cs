using System.Runtime.Versioning;
using System.Security.Principal;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class MigrationStartupRecordReaderTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MissingRecords_DoesNotCreateAnyDirectory()
    {
        using var fixture = new Fixture();

        Assert.Equal(MigrationStartupRecordStatus.None, fixture.Read().Status);
        Assert.False(Directory.Exists(fixture.Binding.RoamingDirectory));
    }

    [Theory]
    [InlineData("intent", MigrationStartupRecordStatus.Intent)]
    [InlineData("completed", MigrationStartupRecordStatus.Completed)]
    public void ValidRecord_IsReadWithoutMutation(string kind, MigrationStartupRecordStatus expected)
    {
        using var fixture = new Fixture();
        var path = fixture.Write(kind);
        var before = File.ReadAllBytes(path);
        var timestamp = File.GetLastWriteTimeUtc(path);

        var result = fixture.Read();

        Assert.Equal(expected, result.Status);
        Assert.Equal(kind, result.Record!.Kind);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void ExpiredIntent_RemainsPendingWithoutRenewingConsent()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("intent", Now.AddDays(-31));
        var before = File.ReadAllBytes(path);

        var result = fixture.Read();

        Assert.Equal(MigrationStartupRecordStatus.Intent, result.Status);
        Assert.Equal(Now.AddDays(-1), result.Record!.ExpiresUtc);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Completion_TakesPrecedenceOverOldOrCorruptIntent()
    {
        using var fixture = new Fixture();
        fixture.Write("completed");
        File.WriteAllBytes(Path.Combine(fixture.Directory, MigrationRecordCodec.IntentFileName), [1, 2, 3]);

        Assert.Equal(MigrationStartupRecordStatus.Completed, fixture.Read().Status);
    }

    [Fact]
    public void CorruptCompletion_DoesNotFallBackToValidIntent()
    {
        using var fixture = new Fixture();
        fixture.Write("intent");
        File.WriteAllBytes(Path.Combine(fixture.Directory, MigrationRecordCodec.CompletionFileName), [1, 2, 3]);

        Assert.Equal(MigrationStartupRecordStatus.Invalid, fixture.Read().Status);
    }

    [Theory]
    [InlineData("intent", "completed.dpapi")]
    [InlineData("completed", "intent.dpapi")]
    public void WrongRecordKind_RequiresRecovery(string kind, string destination)
    {
        using var fixture = new Fixture();
        var original = fixture.Write(kind);
        File.Move(original, Path.Combine(fixture.Directory, destination));

        Assert.Equal(MigrationStartupRecordStatus.Invalid, fixture.Read().Status);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("architecture")]
    [InlineData("install")]
    public void WrongBinding_RequiresRecovery(string field)
    {
        using var fixture = new Fixture();
        fixture.Write("completed");
        switch (field)
        {
            case "user": fixture.Binding.UserSid = "S-1-5-18"; break;
            case "architecture": fixture.Binding.Architecture = "arm64"; break;
            case "install": fixture.Binding.InstallDirectory += "-different"; break;
        }

        Assert.Equal(MigrationStartupRecordStatus.Invalid, fixture.Read().Status);
    }

    [Fact]
    public void LockedRecord_IsUnavailableNotAbsent()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("completed");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(MigrationStartupRecordStatus.Unavailable, fixture.Read().Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MigrationRecordCodec.MaximumRecordBytes + 1)]
    public void InvalidSize_RequiresRecovery(int size)
    {
        using var fixture = new Fixture();
        var path = fixture.Write("intent");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            stream.SetLength(size);

        Assert.Equal(MigrationStartupRecordStatus.Invalid, fixture.Read().Status);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public MigrationBinding Binding { get; }
        public string Directory => Path.Combine(Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);

        public Fixture()
        {
            Binding = new MigrationBinding
            {
                InstallDirectory = _temp.Combine("install"),
                RoamingDirectory = _temp.Combine("roaming"),
                LocalDirectory = _temp.Combine("local"),
                Architecture = "x64",
                UserSid = WindowsIdentity.GetCurrent().User!.Value
            };
        }

        public string Write(string kind, DateTime? created = null)
        {
            var time = created ?? Now.AddMinutes(-1);
            var record = new MigrationRecord
            {
                Kind = kind, MigrationId = Guid.NewGuid().ToString("D"),
                SourceVersion = "2026.9.1", TargetVersion = kind == "completed" ? "2026.9.2" : "",
                CreatedUtc = time,
                ExpiresUtc = kind == "completed" ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : time.AddDays(30),
                Fingerprint = new string('a', 64), InventoryJson = kind == "intent" ? "{}" : "",
                Binding = Binding
            };
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, kind == "intent" ? MigrationRecordCodec.IntentFileName : MigrationRecordCodec.CompletionFileName);
            File.WriteAllBytes(path, MigrationRecordCodec.Encode(record, time));
            return path;
        }

        public MigrationStartupRecord Read() =>
            new MigrationStartupRecordReader(Binding, NullLogger.Instance, new Clock()).Read();

        public void Dispose() => _temp.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
