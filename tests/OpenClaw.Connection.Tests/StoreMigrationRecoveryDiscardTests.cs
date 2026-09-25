using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class StoreMigrationRecoveryDiscardTests : IDisposable
{
    // The classifier uses the system clock, so records must be dated against it.
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly TempDirectory _temp = new();
    private readonly MigrationBinding _binding;
    private readonly Detector _detector = new();

    public StoreMigrationRecoveryDiscardTests()
    {
        _binding = new MigrationBinding
        {
            InstallDirectory = _temp.Combine("install"),
            RoamingDirectory = _temp.Combine("roaming"),
            LocalDirectory = _temp.Combine("local"),
            Architecture = "x64",
            UserSid = WindowsIdentity.GetCurrent().User!.Value
        };
    }

    private string Directory => Path.Combine(_binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
    private string Completion => Path.Combine(Directory, MigrationRecordCodec.CompletionFileName);
    private string Intent => Path.Combine(Directory, MigrationRecordCodec.IntentFileName);
    private string Consent => Path.Combine(Directory, MigrationRecordCodec.ConsentFileName);

    [Fact]
    public void UnreadableRecords_AreRemovedSoRecoveryCanBeCleared()
    {
        Seed();
        File.WriteAllBytes(Completion, [1, 2, 3]);
        File.WriteAllBytes(Intent, [4, 5, 6]);
        File.WriteAllBytes(Consent, [7, 8, 9]);
        File.WriteAllBytes(Path.Combine(Directory, InnoMigrationConsentStore.WriterLockFileName), []);

        Assert.True(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.Discarded, Discarder().Discard());

        Assert.False(File.Exists(Completion));
        Assert.False(File.Exists(Intent));
        Assert.False(File.Exists(Consent));
        Assert.False(File.Exists(Path.Combine(Directory, InnoMigrationConsentStore.WriterLockFileName)));
        Assert.Equal(MigrationStartupRecordStatus.None, Read().Status);
    }

    /// <summary>
    /// The aggregate startup status stops at the first unreadable file, so a corrupt receipt hides
    /// a perfectly good intent behind <see cref="MigrationStartupRecordStatus.Invalid"/>. Planning
    /// per file is what keeps the intent.
    /// </summary>
    [Fact]
    public void ACorruptReceiptBesideAValidIntent_CostsOnlyTheReceipt()
    {
        Seed();
        File.WriteAllBytes(Completion, [1, 2, 3]);
        var intent = WriteRecord("intent");
        var before = File.ReadAllBytes(intent);
        Assert.Equal(MigrationStartupRecordStatus.Invalid, Read().Status);

        Assert.Equal(StoreMigrationDiscardState.Discarded, Discarder().Discard());

        Assert.False(File.Exists(Completion));
        Assert.Equal(before, File.ReadAllBytes(intent));
        Assert.Equal(MigrationStartupRecordStatus.Intent, Read().Status);
    }

    /// <summary>
    /// Recovery after the source app is gone: an intent that still decodes can never be acted on,
    /// because there is nothing left to migrate from. Retry cannot clear it, so discard must.
    /// </summary>
    [Fact]
    public void AnAbandonedIntent_IsDiscardableOnceTheSourceIsGone()
    {
        Seed();
        WriteRecord("intent");
        _detector.Status = InnoInstallationStatus.NotInstalled;

        Assert.True(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.Discarded, Discarder().Discard());

        Assert.False(File.Exists(Intent));
        Assert.Equal(MigrationStartupRecordStatus.None, Read().Status);
    }

    [Fact]
    public void AValidIntent_IsKeptWhileTheSourceIsStillInstalled()
    {
        Seed();
        var intent = WriteRecord("intent");
        var before = File.ReadAllBytes(intent);

        Assert.False(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.RecordsAreReadable, Discarder().Discard());

        Assert.Equal(before, File.ReadAllBytes(intent));
    }

    [Theory]
    [InlineData(InnoInstallationStatus.Detected)]
    [InlineData(InnoInstallationStatus.NotInstalled)]
    public void ADecodableReceipt_IsNeverDiscarded(InnoInstallationStatus source)
    {
        Seed();
        var completion = WriteRecord("completed");
        var before = File.ReadAllBytes(completion);
        _detector.Status = source;

        Assert.False(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.RecordsAreReadable, Discarder().Discard());

        Assert.Equal(before, File.ReadAllBytes(completion));
        Assert.Equal(MigrationStartupRecordStatus.Completed, Read().Status);
    }

    /// <summary>
    /// The previous app holds the migration lease for its whole lifetime, so this is the expected
    /// answer whenever it is still running. It must report Busy rather than deleting anything.
    /// </summary>
    [Fact]
    public void RecordsHeldByAnotherOperation_AreNotDiscarded()
    {
        Seed();
        File.WriteAllBytes(Completion, [1, 2, 3]);
        using var held = new FileStream(
            Path.Combine(Directory, "prepare.lock"), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Equal(StoreMigrationDiscardState.Busy, Discarder().Discard());

        Assert.True(File.Exists(Completion));
    }

    [Fact]
    public void NoRecordDirectory_ReportsTheOutcomeTheUserAskedFor()
    {
        Assert.False(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.Discarded, Discarder().Discard());
        Assert.False(System.IO.Directory.Exists(Directory));
    }

    /// <summary>
    /// A missing lease file says nothing about whether records exist. Treating it as "nothing to
    /// do" would report success having deleted nothing, which is the dead end this class removes.
    /// </summary>
    [Fact]
    public void AMissingLeaseFile_DoesNotFakeSuccess()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Completion, [1, 2, 3]);
        Assert.False(File.Exists(Path.Combine(Directory, "prepare.lock")));

        Assert.True(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.Discarded, Discarder().Discard());

        Assert.False(File.Exists(Completion));
    }

    /// <summary>
    /// A record that decrypts but does not parse. BinaryReader reports a malformed string length
    /// as a plain IOException, which reads like a file that was busy; judged that way the offer
    /// disappears and the user is back in the lockout this class exists to remove.
    /// </summary>
    [Fact]
    public void ARecordThatDecryptsButDoesNotParse_CountsAsCorrupt()
    {
        Seed();
        // A negative 7-bit-encoded string length, which is what BinaryReader rejects.
        var plain = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x41, 0x42 };
        var protectedBytes = ProtectedData.Protect(
            plain,
            Encoding.UTF8.GetBytes("OpenClaw.InnoToStore.Migration.v1"),
            DataProtectionScope.CurrentUser);
        // The record must genuinely reach the parser, or this proves nothing.
        Assert.Throws<IOException>(
            () => MigrationRecordCodec.DecodeForRenewedConsent(protectedBytes, _binding, Now));
        File.WriteAllBytes(Completion, protectedBytes);

        Assert.True(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.Discarded, Discarder().Discard());

        Assert.False(File.Exists(Completion));
    }

    /// <summary>
    /// The same judgement when the protected bytes themselves are damaged, which fails in DPAPI
    /// rather than in the parser.
    /// </summary>
    [Fact]
    public void ARecordDamagedAfterWriting_CountsAsCorrupt()
    {
        Seed();
        var path = WriteRecord("completed");
        var written = File.ReadAllBytes(path);
        File.WriteAllBytes(path, written[..(written.Length / 2)]);

        Assert.True(Discarder().CanDiscard());
        Assert.Equal(StoreMigrationDiscardState.Discarded, Discarder().Discard());

        Assert.False(File.Exists(path));
    }

    private void Seed()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, "prepare.lock"), []);
    }

    private string WriteRecord(string kind)
    {
        var created = Now.AddMinutes(-1);
        var record = new MigrationRecord
        {
            Kind = kind,
            MigrationId = Guid.NewGuid().ToString("D"),
            SourceVersion = "2026.9.1",
            TargetVersion = kind == "completed" ? "2026.9.2" : "",
            CreatedUtc = created,
            ExpiresUtc = kind == "completed"
                ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
                : created.AddDays(30),
            Fingerprint = new string('a', 64),
            InventoryJson = kind == "intent" ? "{}" : "",
            Binding = _binding
        };
        var path = kind == "intent" ? Intent : Completion;
        File.WriteAllBytes(path, MigrationRecordCodec.Encode(record, created));
        return path;
    }

    private MigrationStartupRecord Read() =>
        new MigrationStartupRecordReader(_binding, NullLogger.Instance).Read();

    private StoreMigrationRecoveryDiscard Discarder() =>
        new(_binding, _detector, NullLogger.Instance);

    private sealed class Detector : IInnoInstallationDetector
    {
        public InnoInstallationStatus Status { get; set; } = InnoInstallationStatus.Detected;

        public InnoInstallationDetection Detect() => new(Status, Status == InnoInstallationStatus.Detected
            ? new InnoInstallation(@"C:\fixture", @"C:\fixture\app.exe", @"C:\fixture\unins000.exe",
                "x64", new Version(2026, 9, 1))
            : null);
    }

    public void Dispose() => _temp.Dispose();
}
