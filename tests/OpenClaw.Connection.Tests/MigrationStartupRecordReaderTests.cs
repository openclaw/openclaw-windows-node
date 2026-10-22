using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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

    [Fact]
    public void CorruptCompletion_StillReportsTheReceiptAsPresent()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        File.WriteAllBytes(Path.Combine(fixture.Directory, MigrationRecordCodec.CompletionFileName), [1, 2, 3]);

        var result = fixture.Read();

        // Undecodable, but the file proves data already moved. Admission must still protect it.
        Assert.Equal(MigrationStartupRecordStatus.Invalid, result.Status);
        Assert.True(result.CompletionPresent);
    }

    [Fact]
    public void CorruptIntentAlone_DoesNotClaimAReceipt()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        File.WriteAllBytes(Path.Combine(fixture.Directory, MigrationRecordCodec.IntentFileName), [1, 2, 3]);

        var result = fixture.Read();

        Assert.Equal(MigrationStartupRecordStatus.Invalid, result.Status);
        Assert.False(result.CompletionPresent);
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

        var result = fixture.Read();

        Assert.Equal(MigrationStartupRecordStatus.Unavailable, result.Status);
        // A lock hides the contents, not the fact that a receipt exists.
        Assert.True(result.CompletionPresent);
    }

    /// <summary>
    /// BinaryReader reports a malformed string length as a plain IOException. Reporting that as
    /// an inspection failure sends the record to InspectionFailed, and a receipt there blocks
    /// startup from a screen that offers no way out. Recovery is the only screen with a discard.
    /// </summary>
    [Fact]
    public void ARecordThatDecryptsButDoesNotParse_RequiresRecovery()
    {
        using var fixture = new Fixture();
        System.IO.Directory.CreateDirectory(fixture.Directory);
        // A negative 7-bit-encoded string length, which is what BinaryReader rejects.
        var plain = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x41, 0x42 };
        var protectedBytes = ProtectedData.Protect(
            plain,
            Encoding.UTF8.GetBytes("OpenClaw.InnoToStore.Migration.v1"),
            DataProtectionScope.CurrentUser);
        // The record must genuinely reach the parser, or this proves nothing.
        Assert.Throws<IOException>(
            () => MigrationRecordCodec.DecodeForRenewedConsent(protectedBytes, fixture.Binding, Now));
        File.WriteAllBytes(
            Path.Combine(fixture.Directory, MigrationRecordCodec.CompletionFileName), protectedBytes);

        var result = fixture.Read();

        Assert.Equal(MigrationStartupRecordStatus.Invalid, result.Status);
        Assert.True(result.CompletionPresent);
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

    [Fact]
    public void ReparsePointAncestor_IsUnavailableNotRecovery()
    {
        using var fixture = new Fixture();
        using var target = new TempDirectory();
        var targetPath = target.Combine("roaming");
        System.IO.Directory.CreateDirectory(targetPath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(fixture.Binding.RoamingDirectory)!);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{fixture.Binding.RoamingDirectory}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        try
        {
            // Recovery cannot repair a junction, so this must not be reported as a corrupt record.
            Assert.Equal(MigrationStartupRecordStatus.Unavailable, fixture.Read().Status);
        }
        finally
        {
            System.IO.Directory.Delete(fixture.Binding.RoamingDirectory);
        }
    }

    [Fact]
    public void CompletionDirectory_IsTreatedAsAReceipt()
    {
        using var fixture = new Fixture();
        System.IO.Directory.CreateDirectory(
            Path.Combine(fixture.Directory, MigrationRecordCodec.CompletionFileName));

        var result = fixture.Read();

        // File.Exists answers false for a directory, so an existence check would have failed
        // open here and allowed normal startup. Only a definite not-found proves nothing moved.
        Assert.Equal(MigrationStartupRecordStatus.Unavailable, result.Status);
        Assert.True(result.CompletionPresent);
    }

    [Fact]
    public void NoRecordsAtAll_DoesNotClaimAReceipt()
    {
        using var fixture = new Fixture();

        var result = fixture.Read();

        Assert.Equal(MigrationStartupRecordStatus.None, result.Status);
        Assert.False(result.CompletionPresent);
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
