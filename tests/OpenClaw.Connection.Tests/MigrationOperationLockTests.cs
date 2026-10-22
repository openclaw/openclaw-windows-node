using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using OpenClaw.Connection.Migration;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class MigrationOperationLockTests
{
    [Fact]
    public void FirstRuntime_CreatesProtectedLockWithoutMigrationRecords()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using var runtime = MigrationOperationLock.AcquireRuntime(binding);
        using var secondRuntime = MigrationOperationLock.AcquireRuntime(binding);
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);

        Assert.Single(Directory.GetFiles(directory));
        Assert.True(new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal(binding.UserSid, new DirectoryInfo(directory).GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier))!.Value);
        Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
    }

    [Fact]
    public void ExclusiveOwner_BlocksRuntimeWithoutChangingRecords()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireExclusive(binding))
            Assert.Throws<IOException>(() => MigrationOperationLock.AcquireRuntime(binding));

        using var released = MigrationOperationLock.AcquireRuntime(binding);
    }

    [Fact]
    public void ContendedAcquisition_IsDistinguishableFromAnInaccessibleLock()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using var runtime = MigrationOperationLock.AcquireRuntime(binding);

        // Inno startup and uninstall both key their fail-closed decision off this predicate:
        // a contended lock proves a migration is live, while any other failure proves nothing.
        var contended = Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
        Assert.True(MigrationOperationLock.IsBusy(contended));
        Assert.False(MigrationOperationLock.IsBusy(new IOException("unreadable")));
        Assert.False(MigrationOperationLock.IsBusy(new FileNotFoundException()));
    }

    [Fact]
    public void RepeatAcquisition_LeavesAnAlreadyHardenedDirectoryUntouched()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireRuntime(binding)) { }
        var directory = new DirectoryInfo(
            Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName));

        // The canary must be a trusted principal. A foreign grant is drift the acquisition is
        // now required to scrub, so it cannot double as proof that no rewrite happened.
        using var identity = WindowsIdentity.GetCurrent();
        var marker = identity.User!;
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(marker, FileSystemRights.Read,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);

        using var reacquired = MigrationOperationLock.AcquireRuntime(binding);

        // Surviving the marker proves no DACL rewrite happened. Rewriting on every launch
        // needs WRITE_DAC and WRITE_OWNER for a privilege the acquisition does not require.
        Assert.Contains(
            directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Value == marker.Value
                    && rule.InheritanceFlags == InheritanceFlags.None);
    }

    [Fact]
    public void DriftedDirectorySecurity_IsRehardenedOnNextAcquisition()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireRuntime(binding)) { }
        var directory = new DirectoryInfo(
            Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName));

        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        directory.SetAccessControl(security);
        Assert.False(directory.GetAccessControl().AreAccessRulesProtected);

        using var reacquired = MigrationOperationLock.AcquireRuntime(binding);

        Assert.True(directory.GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void ProtectedButPermissiveDirectory_LosesTheForeignGrantOnNextAcquisition()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireRuntime(binding)) { }
        var directory = new DirectoryInfo(
            Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName));

        // A protected DACL owned by this user can still let an unrelated principal delete
        // completed.dpapi, which the uninstall checker reads as "no migration happened".
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.True(directory.GetAccessControl().AreAccessRulesProtected);
        Assert.Contains(
            directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => Equals(rule.IdentityReference, users));

        using var reacquired = MigrationOperationLock.AcquireRuntime(binding);

        Assert.DoesNotContain(
            directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => Equals(rule.IdentityReference, users));
    }

    [Fact]
    public void ExplicitForeignGrantOnTheReceipt_IsScrubbedOnNextAcquisition()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireRuntime(binding)) { }
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var receipt = new FileInfo(Path.Combine(directory, MigrationRecordCodec.CompletionFileName));
        File.WriteAllBytes(receipt.FullName, [1, 2, 3]);

        // Hardening the directory rewrites inherited access only, so an explicit ACE placed
        // directly on the receipt survives it and still permits deletion.
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = receipt.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.FullControl,
            AccessControlType.Allow));
        receipt.SetAccessControl(security);

        using var reacquired = MigrationOperationLock.AcquireRuntime(binding);

        Assert.DoesNotContain(
            receipt.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => Equals(rule.IdentityReference, users));
    }

    [Fact]
    public void ProtectedReceiptOwnedByAnotherPrincipal_StillRequiresHardening()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var foreign = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);

        // Trusted-looking rules are not durable when someone else owns the file: an owner
        // keeps implicit WRITE_DAC and can rewrite this ACL, then delete the receipt.
        var security = new FileSecurity();
        security.SetOwner(foreign);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            owner,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        Assert.True(MigrationOperationLock.RequiresEntryHardening(security, owner));

        security.SetOwner(owner);
        Assert.False(MigrationOperationLock.RequiresEntryHardening(security, owner));
    }

    [Fact]
    public void ReceiptOwnedByAnotherPrincipal_IsNotSoundAuthorityEvenWhenInherited()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var foreign = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        var security = new FileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            AccessControlType.Allow));

        // Inheriting trusted rules is fine, which is why prepare.lock passes. A foreign owner
        // is not, because that owner can rewrite the ACL and delete the receipt afterwards.
        security.SetOwner(owner);
        Assert.True(MigrationOperationLock.EntryAuthorityIsSound(security, owner));

        security.SetOwner(foreign);
        Assert.False(MigrationOperationLock.EntryAuthorityIsSound(security, owner));
    }

    [Theory]
    [InlineData(FileSystemRights.ReadAndExecute, true)]
    [InlineData(FileSystemRights.ListDirectory, true)]
    [InlineData(FileSystemRights.Delete, false)]
    [InlineData(FileSystemRights.WriteData, false)]
    [InlineData(FileSystemRights.ChangePermissions, false)]
    [InlineData(FileSystemRights.TakeOwnership, false)]
    [InlineData(FileSystemRights.FullControl, false)]
    public void ForeignGrantBreaksAuthorityOnlyWhenItCanChangeTheReceipt(
        FileSystemRights granted, bool expectedSound)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            AccessControlType.Allow));

        // Managed profiles inherit read and list grants into %APPDATA%. A principal that cannot
        // write or delete cannot have removed the receipt, so treating those as tampering would
        // make every ordinary uninstall unverifiable and strand the gateway forever.
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null), granted,
            AccessControlType.Allow));

        Assert.Equal(expectedSound, MigrationOperationLock.EntryAuthorityIsSound(security, owner));
    }

    // Generic rights have no names in FileSystemRights and intersect none of the specific bits,
    // so a mask built only from named rights reads GENERIC_ALL as harmless. The kernel maps it to
    // FILE_ALL_ACCESS, which includes DELETE and WRITE_DAC. These ACEs are built through SDDL
    // because the FileSystemRights enum cannot express the raw values.
    [Theory]
    [InlineData("(A;;GA;;;BG)", false)]
    [InlineData("(A;;GW;;;BG)", false)]
    [InlineData("(A;;GR;;;BG)", true)]
    [InlineData("(A;;GX;;;BG)", true)]
    public void GenericRightsCountAsMutatingEvenWithoutNamedBits(string ace, bool expectedSound)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(
            $"O:{owner.Value}G:{owner.Value}D:(A;;FA;;;{owner.Value}){ace}");

        Assert.Equal(expectedSound, MigrationOperationLock.EntryAuthorityIsSound(security, owner));
    }

    [Fact]
    public void WrongUserBinding_IsRejectedBeforeCreatingDirectory()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        binding.UserSid = "S-1-5-18";
        Assert.Throws<InvalidOperationException>(() => MigrationOperationLock.AcquireRuntime(binding));
        Assert.False(Directory.Exists(binding.RoamingDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeInAnotherProcess_ExcludesStoreUntilNormalExitOrCrash(bool crash)
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireRuntime(binding)) { }
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_TEST_LOCK"] =
            Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName, "prepare.lock");
        const string script = """
            $ErrorActionPreference = 'Stop'
            $lease = [IO.File]::Open($env:OPENCLAW_TEST_LOCK, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            [Console]::Out.WriteLine('ready')
            [Console]::Out.Flush()
            [Console]::ReadLine() | Out-Null
            $lease.Dispose()
            """;
        foreach (var argument in new[] { "-NoProfile", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);

        using var child = Process.Start(start)!;
        var errors = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.False(child.HasExited);
            using (MigrationOperationLock.AcquireRuntime(binding))
            {
                Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
                // Independent uninstall readers coexist with a running source.
                using var uninstall = File.Open(start.Environment["OPENCLAW_TEST_LOCK"]!,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
            if (crash)
                child.Kill();
            else
                await child.StandardInput.WriteLineAsync("exit");
            await child.WaitForExitAsync(timeout.Token);
            if (!crash)
                Assert.True(child.ExitCode == 0, await errors);
            using var exclusive = await AcquireAfterExitAsync(binding);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }

    private static async Task<FileStream> AcquireAfterExitAsync(MigrationBinding binding)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return MigrationOperationLock.AcquireExclusive(binding);
            }
            catch (IOException exception) when (MigrationOperationLock.IsBusy(exception) &&
                                               elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Wait for kernel handle cleanup after forcible process termination.
                await Task.Delay(25);
            }
        }
    }
}
