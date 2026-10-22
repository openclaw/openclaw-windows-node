using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using OpenClaw.Connection.Migration;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Covers the signal that keeps a deleted completion receipt from authorizing gateway removal.
/// An earlier attempt stored that signal in HKCU, which cannot work: the app that writes it is
/// MSIX packaged, so the write is virtualized into the package's private hive and is invisible
/// to the unpackaged uninstaller. Package registration is read the same way from both sides.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoreMigrationPackagePresenceTests
{
    // The P1 itself. Deleting the receipt, and optionally the whole directory holding the ACL
    // evidence the authority check reads, previously produced cleanup-authorizing exit 0.
    // This drives the real Windows PowerShell 5.1 checker the uninstaller runs.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletedReceipt_WithStorePackageInstalled_PreservesGateway(bool removeDirectory)
    {
        using var temp = new TempDirectory();
        using var repository = new TempPackageRepository();
        repository.AddPackage(MigrationRecordCodec.PackageName + "_2026.9.23.0_x64__rfcbke2p71se2");
        var record = StageChecker(temp);
        var directory = Path.Combine(record.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);

        // The attacker deletes the receipt, leaving state that looks like a machine
        // that never migrated.
        if (removeDirectory)
            Directory.Delete(directory, recursive: true);

        // Exit 11, not 2: the checker positively identified a registered Store app, so the
        // uninstaller must say so rather than repeat its generic "could not confirm" advice,
        // which tells the user to run wsl --unregister and destroy this very gateway.
        Assert.Equal(11, await RunCheckerAsync(record, temp, repository));
    }

    // Without the package the same fixture must still report "no migration", or the tests above
    // would pass for the wrong reason and every ordinary uninstall would strand a gateway.
    // Both directory states are covered because the deleted-directory case is the one that
    // reaches the authority check that previously returned true for a missing directory.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletedReceipt_WithoutStorePackage_ReportsNoMigration(bool removeDirectory)
    {
        using var temp = new TempDirectory();
        using var repository = new TempPackageRepository();
        var record = StageChecker(temp);
        var directory = Path.Combine(record.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);
        if (removeDirectory)
            Directory.Delete(directory, recursive: true);

        Assert.Equal(0, await RunCheckerAsync(record, temp, repository));
    }

    // A populated repository that holds no package of ours must not preserve, or any machine
    // with apps installed would keep every gateway forever.
    [Fact]
    public async Task UnrelatedPackages_DoNotPreserveTheGateway()
    {
        using var temp = new TempDirectory();
        using var repository = new TempPackageRepository();
        repository.AddPackage("NotOpenClawFoundation.OpenClaw_1.0.0.0_x64__abcdefghijklm");
        var record = StageChecker(temp);

        Assert.Equal(0, await RunCheckerAsync(record, temp, repository));
    }

    // The repository is writable by the current user, so the principal who can delete the receipt
    // can also delete the package registration that replaces it as the completion signal. An
    // empty or absent repository must therefore read as tampering, not as "no Store app".
    // Every real profile has hundreds of registered packages.
    [Fact]
    public async Task EmptyRepository_PreservesGateway()
    {
        using var temp = new TempDirectory();
        using var repository = new TempPackageRepository(seedUnrelated: false);
        var record = StageChecker(temp);

        Assert.Equal(2, await RunCheckerAsync(record, temp, repository));
    }

    [Fact]
    public async Task MissingRepository_PreservesGateway()
    {
        using var temp = new TempDirectory();
        using var repository = new TempPackageRepository();
        var record = StageChecker(temp);

        Assert.Equal(2, await RunCheckerAsync(
            record, temp, repository, keyPathOverride: repository.KeyPath + "\\does-not-exist"));
    }

    // Every behavioural test overrides the repository key, so nothing else proves the shipped
    // default names a real key. A typo or a trailing-separator regression there would make the
    // lookup fail on every real machine, reopening the vulnerability with no attacker involved,
    // while the rest of this class stayed green. The literal is asserted unconditionally; the
    // live probe is advisory because a service account, container, or freshly provisioned CI
    // profile can legitimately lack the AppX stack, and that must not fail the build.
    [Fact]
    public void DefaultRepositoryKey_IsTheAppModelRepository()
    {
        const string expected =
            "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages";
        var script = File.ReadAllText(Path.Combine(
            MigrationRecordTests.RepositoryRoot(), "scripts", "Test-InnoMigration.ps1"));
        var match = System.Text.RegularExpressions.Regex.Match(
            script, @"\$PackageRepositoryKey\s*=\s*'([^']+)'");

        Assert.True(match.Success, "The checker must declare a default package repository key.");
        Assert.Equal(expected, match.Groups[1].Value);

        using var key = Registry.CurrentUser.OpenSubKey(expected, writable: false);
        // Advisory rather than required: a service account, container image, or freshly
        // provisioned CI profile can legitimately lack the AppX stack, and that must not
        // fail the build. Where the key does exist it must be populated, which is the
        // premise the empty-means-tampering hardening rests on.
        Assert.True(key is null || key.SubKeyCount > 0,
            "The AppModel repository exists but is empty, which the checker treats as tampering.");
    }

    // A refactor that stopped consulting package presence would leave the fixture tests green on
    // a developer machine while the shipped decision returned to authorizing destruction.
    [Fact]
    public void Checker_GatesExitZeroOnPackagePresence()
    {
        var script = File.ReadAllText(Path.Combine(
            MigrationRecordTests.RepositoryRoot(), "scripts", "Test-InnoMigration.ps1"));
        var absentBranch = script[script.IndexOf("FileNotFoundException", StringComparison.Ordinal)..];
        var presence = absentBranch.IndexOf("Get-StorePackageState", StringComparison.Ordinal);
        var exitZero = absentBranch.IndexOf("exit 0", StringComparison.Ordinal);

        Assert.True(presence >= 0, "The absent-receipt branch must consult package presence.");
        Assert.True(presence < exitZero, "Package presence must gate exit 0 rather than follow it.");
    }

    private static MigrationRecord StageChecker(TempDirectory temp)
    {
        var root = MigrationRecordTests.RepositoryRoot();
        var record = MigrationRecordTests.CreateRecord(temp, "completed");
        record.CreatedUtc = DateTime.UtcNow.AddMinutes(-1);
        Directory.CreateDirectory(record.Binding.InstallDirectory);
        Directory.CreateDirectory(record.Binding.LocalDirectory);
        Directory.CreateDirectory(record.Binding.RoamingDirectory);
        File.Copy(Path.Combine(root, "src", "OpenClaw.Connection", "Migration", "MigrationRecordCodec.cs"),
            Path.Combine(record.Binding.InstallDirectory, "MigrationRecordCodec.cs"));
        File.Copy(Path.Combine(root, "scripts", "Test-InnoMigration.ps1"),
            Path.Combine(record.Binding.InstallDirectory, "Test-InnoMigration.ps1"));
        return record;
    }

    private static async Task<int> RunCheckerAsync(
        MigrationRecord record, TempDirectory temp, TempPackageRepository repository,
        string? keyPathOverride = null)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_STATE_DIR"] = temp.Combine("approvals");
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(record.Binding.InstallDirectory, "Test-InnoMigration.ps1"),
            "-AppRoot", record.Binding.InstallDirectory,
            "-Architecture", record.Binding.Architecture,
            "-RoamingDirectory", record.Binding.RoamingDirectory,
            "-LocalDirectory", record.Binding.LocalDirectory,
            "-PackageRepositoryKey", keyPathOverride ?? repository.KeyPath
        })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Checker did not exit. stdout={await stdout} stderr={await stderr}");
        }
        return process.ExitCode;
    }

    /// <summary>
    /// An isolated stand-in for the AppModel package repository. Tests cannot install or remove a
    /// real MSIX package, and the developer machine running them usually has the real package
    /// installed, which would make the negative cases unrunnable against the live repository.
    /// Unrelated packages are seeded by default because a real profile always has hundreds, and
    /// the checker treats an empty repository as tampering rather than as proof of absence.
    /// </summary>
    internal sealed class TempPackageRepository : IDisposable
    {
        private readonly string _root = "Software\\OpenClawTests\\" + Guid.NewGuid().ToString("n");

        public string KeyPath => _root + "\\Packages";

        public TempPackageRepository(bool seedUnrelated = true)
        {
            Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)?.Dispose();
            if (!seedUnrelated)
                return;
            AddPackage("Microsoft.WindowsCalculator_11.2_x64__8wekyb3d8bbwe");
            AddPackage("Contoso.Example_1.0.0.0_x64__abcdefghijklm");
        }

        public void AddPackage(string packageFullName)
            => Registry.CurrentUser.CreateSubKey(KeyPath + "\\" + packageFullName, writable: true)?.Dispose();

        public void Dispose()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
