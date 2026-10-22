using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenClaw.Connection.Migration;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
[Collection("Migration preparation")]
public sealed class MigrationPreparationTests
{
    [Fact]
    public void Preparation_IsProtectedAtomicAndIdempotent()
    {
        using var temp = new TempDirectory();
        var binding = CreateBinding(temp);
        using var environment = IsolateApprovals();
        File.WriteAllText(Path.Combine(binding.RoamingDirectory, "settings.json"), "{\"AutoStart\":true}");
        var preparation = new MigrationPreparation(binding);
        var first = preparation.Prepare("2026.9.17.0");
        var second = preparation.Prepare("2026.9.17.0");
        Assert.Equal(first.MigrationId, second.MigrationId);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.True(first.AutoStart);
        Assert.Equal(first.CreatedUtc.AddDays(30), first.ExpiresUtc);
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var bytes = File.ReadAllBytes(Path.Combine(directory, MigrationRecordCodec.IntentFileName));
        var decoded = MigrationRecordCodec.Decode(bytes, binding, DateTime.UtcNow);
        Assert.Equal(first.MigrationId, decoded.MigrationId);
        Assert.Equal("intent", decoded.Kind);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(directory, MigrationRecordCodec.CompletionFileName)));
        var acl = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(binding.UserSid, Assert.IsType<SecurityIdentifier>(acl.GetOwner(typeof(SecurityIdentifier))).Value);
        var allowed = new[] { binding.UserSid, "S-1-5-18", "S-1-5-32-544" };
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            Assert.Contains(rule.IdentityReference.Value, allowed);
    }

    [Fact]
    public void ChangedSource_CreatesFreshConsentInventoryWithoutMutatingSource()
    {
        using var temp = new TempDirectory();
        var binding = CreateBinding(temp);
        using var environment = IsolateApprovals();
        var settings = Path.Combine(binding.RoamingDirectory, "settings.json");
        File.WriteAllText(settings, "{\"AutoStart\":false}");
        var preparation = new MigrationPreparation(binding);
        var first = preparation.Prepare("2026.9.17.0");
        File.WriteAllText(settings, "{\"AutoStart\":true}");
        var second = preparation.Prepare("2026.9.17.0");
        Assert.NotEqual(first.MigrationId, second.MigrationId);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal("{\"AutoStart\":true}", File.ReadAllText(settings));
    }

    [Fact]
    public void CorruptIntent_IsNotSilentlyReplaced()
    {
        using var temp = new TempDirectory();
        var binding = CreateBinding(temp);
        using var environment = IsolateApprovals();
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, MigrationRecordCodec.IntentFileName);
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            new MigrationPreparation(binding).Prepare("2026.9.17.0"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExistingCompletion_PreventsNewPreparationEvenIfUnreadable()
    {
        using var temp = new TempDirectory();
        var binding = CreateBinding(temp);
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, MigrationRecordCodec.CompletionFileName), "invalid");
        Assert.Throws<InvalidOperationException>(() => new MigrationPreparation(binding).Prepare("2026.9.17.0"));
        Assert.False(File.Exists(Path.Combine(directory, MigrationRecordCodec.IntentFileName)));
    }

    [Fact]
    public void RenewedConsent_ReplacesExpiredIntentButNotBeforeExpiry()
    {
        using var temp = new TempDirectory();
        var binding = CreateBinding(temp);
        using var environment = IsolateApprovals();
        var now = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        var first = new MigrationPreparation(binding, new FixedClock(now)).Prepare("2026.9.17.0");
        var reused = new MigrationPreparation(binding, new FixedClock(now.AddDays(29))).Prepare("2026.9.17.0");
        var renewed = new MigrationPreparation(binding, new FixedClock(now.AddDays(30))).Prepare("2026.9.17.0");
        Assert.Equal(first.MigrationId, reused.MigrationId);
        Assert.NotEqual(first.MigrationId, renewed.MigrationId);
        Assert.Equal(now.AddDays(60).UtcDateTime, renewed.ExpiresUtc);
    }

    [Fact]
    public void ConcurrentPreparation_IsRejectedWithoutReplacingIntent()
    {
        using var temp = new TempDirectory();
        var binding = CreateBinding(temp);
        using var environment = IsolateApprovals();
        var preparation = new MigrationPreparation(binding);
        var first = preparation.Prepare("2026.9.17.0");
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var path = Path.Combine(directory, MigrationRecordCodec.IntentFileName);
        var bytes = File.ReadAllBytes(path);
        using var locked = new FileStream(Path.Combine(directory, "prepare.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => preparation.Prepare("2026.9.17.0"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static MigrationBinding CreateBinding(TempDirectory temp)
    {
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        Directory.CreateDirectory(binding.RoamingDirectory);
        Directory.CreateDirectory(binding.LocalDirectory);
        return binding;
    }

    private static EnvironmentScope IsolateApprovals() =>
        new EnvironmentScope().Set("OPENCLAW_STATE_DIR", null).Set("OPENCLAW_HOME", null);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

[CollectionDefinition("Migration preparation", DisableParallelization = true)]
public sealed class MigrationPreparationCollection;
