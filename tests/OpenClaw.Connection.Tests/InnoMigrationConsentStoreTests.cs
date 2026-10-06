using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
[Collection("Migration preparation")]
public sealed class InnoMigrationConsentStoreTests
{
    private const string SourceVersion = "2026.9.17.0";
    private static readonly DateTime Now = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Grant_RoundTripsProtectedSeparateConsentWithoutInventoryOrCompletion()
    {
        using var fixture = new Fixture();

        fixture.Store.Grant(SourceVersion);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        var record = MigrationRecordCodec.Decode(bytes, fixture.Binding, Now);

        Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Equal("consent", record.Kind);
        Assert.Equal(SourceVersion, record.SourceVersion);
        Assert.Equal(Now, record.CreatedUtc);
        Assert.Equal(Now.AddDays(30), record.ExpiresUtc);
        Assert.Equal(MigrationRecordCodec.ConsentFingerprint, record.Fingerprint);
        Assert.Empty(record.InventoryJson);
        Assert.Empty(record.TargetVersion);
        Assert.False(record.AutoStart);
        Assert.DoesNotContain(record.MigrationId, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.False(Directory.Exists(fixture.Binding.LocalDirectory));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp"));
        var acl = new DirectoryInfo(fixture.Directory).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(fixture.Binding.UserSid,
            Assert.IsType<SecurityIdentifier>(acl.GetOwner(typeof(SecurityIdentifier))).Value);
        var allowed = new[] { fixture.Binding.UserSid, "S-1-5-18", "S-1-5-32-544" };
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            Assert.Contains(rule.IdentityReference.Value, allowed);
    }

    [Fact]
    public void Grant_LeavesAnAlreadyHardenedDirectoryAlone()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var security = new DirectoryInfo(fixture.Directory).GetAccessControl()
            .GetSecurityDescriptorBinaryForm();

        Assert.False(MigrationOperationLock.RequiresDirectoryHardening(
            new DirectoryInfo(fixture.Directory),
            new SecurityIdentifier(fixture.Binding.UserSid)));

        fixture.Store.Grant(SourceVersion);

        Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Equal(security,
            new DirectoryInfo(fixture.Directory).GetAccessControl().GetSecurityDescriptorBinaryForm());
    }

    [Fact]
    public void Grant_StillCorrectsADirectoryThatLostItsProtection()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var directory = new DirectoryInfo(fixture.Directory);
        var drifted = directory.GetAccessControl();
        drifted.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        directory.SetAccessControl(drifted);
        Assert.False(directory.GetAccessControl().AreAccessRulesProtected);

        Assert.True(MigrationOperationLock.RequiresDirectoryHardening(
            new DirectoryInfo(fixture.Directory),
            new SecurityIdentifier(fixture.Binding.UserSid)));

        fixture.Store.Grant(SourceVersion);

        Assert.True(new DirectoryInfo(fixture.Directory).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void Grant_RemovesAForeignGrantFromAnOtherwiseProtectedDirectory()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var directory = new DirectoryInfo(fixture.Directory);

        // A protected DACL owned by this user can still let an unrelated principal delete
        // completed.dpapi, which the uninstall checker reads as "no migration happened".
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.True(directory.GetAccessControl().AreAccessRulesProtected);

        fixture.Store.Grant(SourceVersion);

        Assert.DoesNotContain(
            new DirectoryInfo(fixture.Directory).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => Equals(rule.IdentityReference, users));
    }

    [Fact]
    public void Grant_DoesNotReadOrChangeLiveSettingsAndIdentity()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Binding.RoamingDirectory);
        var settingsPath = Path.Combine(fixture.Binding.RoamingDirectory, "settings.json");
        var identityPath = Path.Combine(fixture.Binding.RoamingDirectory, "device-key-ed25519.json");
        File.WriteAllText(settingsPath, "invalid JSON that inventory would reject");
        File.WriteAllText(identityPath, "isolated identity sentinel");
        using (var settings = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var identity = new FileStream(identityPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.Store.Grant(SourceVersion);
            Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        }

        Assert.Equal("invalid JSON that inventory would reject", File.ReadAllText(settingsPath));
        Assert.Equal("isolated identity sentinel", File.ReadAllText(identityPath));
    }

    [Fact]
    public void RuntimeReadLease_AllowsExplicitGrantAndInspection()
    {
        using var fixture = new Fixture();
        using var runtime = MigrationOperationLock.AcquireRuntime(fixture.Binding);

        fixture.Store.Grant(SourceVersion);

        Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        Assert.False(File.Exists(fixture.IntentPath));
        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.Throws<IOException>(() => new FileStream(
            fixture.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    }

    [Fact]
    public void RuntimeReadLease_ExistingConsentInspectionRemainsReadOnly()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        using var runtime = MigrationOperationLock.AcquireRuntime(fixture.Binding);
        var before = Directory.GetFileSystemEntries(fixture.Directory).Order().ToArray();
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        var timestamp = Directory.GetLastWriteTimeUtc(fixture.Directory);

        Assert.True(fixture.Store.HasValidConsent(SourceVersion));

        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Directory).Order().ToArray());
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(timestamp, Directory.GetLastWriteTimeUtc(fixture.Directory));
    }

    [Fact]
    public void MissingConsent_DoesNotCreateProfileOrMigrationDirectory()
    {
        using var fixture = new Fixture();

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        Assert.False(Directory.Exists(fixture.Binding.RoamingDirectory));
    }

    [Fact]
    public void MissingConsent_PreexistingEmptyMigrationDirectoryRemainsUnchanged()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        var before = Directory.GetFileSystemEntries(fixture.Directory);
        var timestamp = Directory.GetLastWriteTimeUtc(fixture.Directory);
        var security = new DirectoryInfo(fixture.Directory).GetAccessControl().GetSecurityDescriptorBinaryForm();

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));

        Assert.Empty(before);
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Directory));
        Assert.Equal(timestamp, Directory.GetLastWriteTimeUtc(fixture.Directory));
        Assert.Equal(security,
            new DirectoryInfo(fixture.Directory).GetAccessControl().GetSecurityDescriptorBinaryForm());
        Assert.False(File.Exists(fixture.LockPath));
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public void MissingConsent_DoesNotAcquireOrChangeExistingPreparationLock()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        File.WriteAllBytes(fixture.LockPath, [1, 2, 3]);
        var timestamp = File.GetLastWriteTimeUtc(fixture.LockPath);
        using (var locked = new FileStream(fixture.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.LockPath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.LockPath));
        Assert.Equal(new[] { fixture.LockPath }, Directory.GetFileSystemEntries(fixture.Directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingConsentWithoutLock_PropagatesOperationalFailureWithoutCreatingLock(bool corrupt)
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        if (corrupt)
            File.WriteAllBytes(fixture.ConsentPath, [1, 2, 3]);
        File.Delete(fixture.LockPath);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        var before = Directory.GetFileSystemEntries(fixture.Directory);
        var timestamp = Directory.GetLastWriteTimeUtc(fixture.Directory);

        Assert.Throws<FileNotFoundException>(() => fixture.Store.HasValidConsent(SourceVersion));

        Assert.False(File.Exists(fixture.LockPath));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Directory));
        Assert.Equal(timestamp, Directory.GetLastWriteTimeUtc(fixture.Directory));
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public void PreparedInventory_IsNeverImplicitConsent()
    {
        using var fixture = new Fixture();
        using var environment = new EnvironmentScope()
            .Set("OPENCLAW_STATE_DIR", Path.Combine(fixture.Binding.LocalDirectory, "isolated-approvals"))
            .Set("OPENCLAW_HOME", null);
        var intent = new MigrationPreparation(fixture.Binding, fixture.Clock).Prepare(SourceVersion);
        var before = File.ReadAllBytes(fixture.IntentPath);

        Assert.Equal("intent", intent.Kind);
        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        Assert.False(File.Exists(fixture.ConsentPath));
        fixture.Store.Grant(SourceVersion);
        Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Equal(before, File.ReadAllBytes(fixture.IntentPath));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("completed")]
    public void OtherKindInConsentFile_IsInvalidAndCannotBeOverwritten(string kind)
    {
        using var fixture = new Fixture();
        var bytes = fixture.WriteOtherRecord(kind, fixture.ConsentPath);
        File.WriteAllBytes(fixture.LockPath, []);

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        var failure = Assert.Throws<InvalidDataException>(() => fixture.Store.Grant(SourceVersion));
        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(2, fixture.Logger.Warnings.Count);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("install")]
    [InlineData("roaming")]
    [InlineData("local")]
    [InlineData("architecture")]
    public void BindingDrift_InvalidatesConsentAndPreventsOverwrite(string field)
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var record = MigrationRecordCodec.Decode(File.ReadAllBytes(fixture.ConsentPath), fixture.Binding, Now);
        switch (field)
        {
            case "user": record.Binding.UserSid = "S-1-5-21-1-2-3-1001"; break;
            case "install": record.Binding.InstallDirectory += "-other"; break;
            case "roaming": record.Binding.RoamingDirectory += "-other"; break;
            case "local": record.Binding.LocalDirectory += "-other"; break;
            case "architecture": record.Binding.Architecture = "arm64"; break;
        }
        var bytes = MigrationRecordCodec.Encode(record, Now);
        File.WriteAllBytes(fixture.ConsentPath, bytes);

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Grant(SourceVersion));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(2, fixture.Logger.Warnings.Count);
    }

    [Fact]
    public void Grant_RejectsBindingForAnotherWindowsUser()
    {
        using var fixture = new Fixture();
        fixture.Binding.UserSid = "S-1-5-21-1-2-3-1001";

        Assert.Throws<InvalidOperationException>(() => fixture.Store.Grant(SourceVersion));
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public void SourceVersionDrift_RequiresExplicitNewGrant()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var before = File.ReadAllBytes(fixture.ConsentPath);

        Assert.False(fixture.Store.HasValidConsent("2026.9.18.0"));
        Assert.Equal(before, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Single(fixture.Logger.Warnings);
        fixture.Clock.Now = Now.AddDays(1);
        fixture.Store.Grant("2026.9.18.0");

        Assert.True(fixture.Store.HasValidConsent("2026.9.18.0"));
        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        var renewed = MigrationRecordCodec.Decode(File.ReadAllBytes(fixture.ConsentPath), fixture.Binding, fixture.Clock.Now);
        Assert.Equal(fixture.Clock.Now.AddDays(30), renewed.ExpiresUtc);
    }

    [Fact]
    public void Consent_ExpiresExactlyAtThirtyDaysWithoutReadRenewal()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var before = File.ReadAllBytes(fixture.ConsentPath);
        var timestamp = File.GetLastWriteTimeUtc(fixture.ConsentPath);
        fixture.Clock.Now = Now.AddDays(30).AddTicks(-1);
        Assert.True(fixture.Store.HasValidConsent(SourceVersion));

        fixture.Clock.Now = Now.AddDays(30);
        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Equal(before, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.ConsentPath));
        Assert.Single(fixture.Logger.Warnings);

        fixture.Store.Grant(SourceVersion);
        Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        var renewed = MigrationRecordCodec.Decode(File.ReadAllBytes(fixture.ConsentPath), fixture.Binding, fixture.Clock.Now);
        Assert.Equal(Now.AddDays(60), renewed.ExpiresUtc);
    }

    [Fact]
    public void BackwardClock_InvalidatesConsentAndPreventsSilentReplacement()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        fixture.Clock.Now = Now.AddTicks(-1);

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Grant(SourceVersion));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(2, fixture.Logger.Warnings.Count);
    }

    [Fact]
    public void CorruptConsent_IsLoggedAndNeverSilentlyReplaced()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(fixture.ConsentPath, bytes);

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        var failure = Assert.Throws<InvalidDataException>(() => fixture.Store.Grant(SourceVersion));
        Assert.IsAssignableFrom<CryptographicException>(failure.InnerException);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(2, fixture.Logger.Warnings.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MigrationRecordCodec.MaximumRecordBytes + 1)]
    public void InvalidSize_IsLoggedAndNeverReplaced(int size)
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        using (var stream = new FileStream(fixture.ConsentPath, FileMode.Create, FileAccess.Write))
            stream.SetLength(size);

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Grant(SourceVersion));
        Assert.Equal(size, new FileInfo(fixture.ConsentPath).Length);
        Assert.Equal(2, fixture.Logger.Warnings.Count);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("negative-string-length")]
    [InlineData("malformed-string-length")]
    public void MalformedProtectedPayload_IsInvalidNotAnOperationalIoFailure(string format)
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        byte[] plain = format switch
        {
            "negative-string-length" => [0xff, 0xff, 0xff, 0xff, 0x0f],
            "malformed-string-length" => [0x80, 0x80, 0x80, 0x80, 0x80],
            _ => []
        };
        var bytes = ProtectedData.Protect(plain,
            System.Text.Encoding.UTF8.GetBytes("OpenClaw.InnoToStore.Migration.v1"),
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(fixture.ConsentPath, bytes);

        Assert.False(fixture.Store.HasValidConsent(SourceVersion));
        var failure = Assert.Throws<InvalidDataException>(() => fixture.Store.Grant(SourceVersion));
        if (format == "malformed-string-length")
            Assert.IsType<FormatException>(failure.InnerException);
        else
            Assert.IsAssignableFrom<IOException>(
                Assert.IsType<InvalidDataException>(failure.InnerException).InnerException);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Equal(2, fixture.Logger.Warnings.Count);
    }

    [Fact]
    public void InvalidSourceVersion_CannotReplaceExistingConsent()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);

        Assert.Throws<InvalidDataException>(() => fixture.Store.Grant("not-a-version"));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Single(fixture.Logger.Warnings);
    }

    [Fact]
    public void ExclusiveMigrationLock_ExcludesConsentReadsAndWrites()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        using var locked = new FileStream(Path.Combine(fixture.Directory, "prepare.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => fixture.Store.HasValidConsent(SourceVersion));
        Assert.Throws<IOException>(() => fixture.Store.Grant(SourceVersion));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public void ConsentWriterLock_SerializesGrantsWithoutBlockingDurableConsentInspection()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        using var writer = new FileStream(
            Path.Combine(fixture.Directory, InnoMigrationConsentStore.WriterLockFileName),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => fixture.Store.Grant(SourceVersion));
        Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public void LockedConsent_PropagatesOperationalFailureRatherThanTreatingItAsMissingOrInvalid()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        using var locked = new FileStream(fixture.ConsentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => fixture.Store.HasValidConsent(SourceVersion));
        Assert.Throws<IOException>(() => fixture.Store.Grant(SourceVersion));
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public void FailedAtomicReplacement_PreservesConsentAndCleansTemporaryFile()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var bytes = File.ReadAllBytes(fixture.ConsentPath);
        fixture.Clock.Now = Now.AddDays(1);
        using (var locked = new FileStream(fixture.ConsentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => fixture.Store.Grant(SourceVersion));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal(bytes, File.ReadAllBytes(fixture.ConsentPath));
            Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp"));
        }

        Assert.True(fixture.Store.HasValidConsent(SourceVersion));
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Fact]
    public void DirectoryAtConsentPath_PropagatesAccessFailure()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.ConsentPath);
        File.WriteAllBytes(fixture.LockPath, []);

        Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.HasValidConsent(SourceVersion));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Store.Grant(SourceVersion));
        Assert.True(Directory.Exists(fixture.ConsentPath));
        Assert.Empty(fixture.Logger.Warnings);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("directory")]
    public void AnyExistingCompletion_PreventsGrantWithoutChangingConsent(string completion)
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);
        var consent = File.ReadAllBytes(fixture.ConsentPath);
        switch (completion)
        {
            case "valid": fixture.WriteOtherRecord("completed", fixture.CompletionPath); break;
            case "corrupt": File.WriteAllBytes(fixture.CompletionPath, [1, 2, 3]); break;
            case "directory": Directory.CreateDirectory(fixture.CompletionPath); break;
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Store.Grant(SourceVersion));
        Assert.Equal(consent, File.ReadAllBytes(fixture.ConsentPath));
        Assert.True(Path.Exists(fixture.CompletionPath));
    }

    [Fact]
    public void ConsentOnly_DoesNotBecomePendingIntentOrAuthorizeUninstall()
    {
        using var fixture = new Fixture();
        fixture.Store.Grant(SourceVersion);

        Assert.Equal(MigrationStartupRecordStatus.None,
            new MigrationStartupRecordReader(fixture.Binding, fixture.Logger, fixture.Clock).Read().Status);
        Assert.Throws<InvalidDataException>(() =>
            MigrationRecordCodec.ReadCompletion(fixture.ConsentPath, fixture.Binding, Now));
    }

    [Fact]
    public void ReparseMigrationDirectory_RequiresConfirmationAndGrantCannotWriteThroughIt()
    {
        using var fixture = new Fixture();
        using var target = new TempDirectory();
        Directory.CreateDirectory(fixture.Binding.RoamingDirectory);
        var targetPath = target.Combine("migration");
        Directory.CreateDirectory(targetPath);
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{fixture.Directory}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        try
        {
            Assert.False(fixture.Store.HasValidConsent(SourceVersion));
            Assert.Throws<InvalidDataException>(() => fixture.Store.Grant(SourceVersion));
            Assert.Empty(Directory.GetFileSystemEntries(targetPath));
            Assert.Equal(2, fixture.Logger.Warnings.Count);
        }
        finally
        {
            Directory.Delete(fixture.Directory);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public MigrationBinding Binding { get; }
        public Clock Clock { get; } = new();
        public Logger Logger { get; } = new();
        public InnoMigrationConsentStore Store { get; }
        public string Directory => Path.Combine(Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        public string ConsentPath => Path.Combine(Directory, MigrationRecordCodec.ConsentFileName);
        public string LockPath => Path.Combine(Directory, "prepare.lock");
        public string IntentPath => Path.Combine(Directory, MigrationRecordCodec.IntentFileName);
        public string CompletionPath => Path.Combine(Directory, MigrationRecordCodec.CompletionFileName);

        public Fixture()
        {
            Binding = MigrationRecordTests.CreateRecord(_temp, "intent").Binding;
            Store = new InnoMigrationConsentStore(Binding, Logger, Clock);
        }

        public byte[] WriteOtherRecord(string kind, string path)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var record = MigrationRecordTests.CreateRecord(_temp, kind);
            var bytes = MigrationRecordCodec.Encode(record, Now);
            File.WriteAllBytes(path, bytes);
            return bytes;
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; set; } = InnoMigrationConsentStoreTests.Now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class Logger : IOpenClawLogger
    {
        public List<string> Warnings { get; } = [];
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }
}
