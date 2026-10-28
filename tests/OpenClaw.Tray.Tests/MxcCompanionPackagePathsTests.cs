using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class MxcCompanionPackagePathsTests
{
    private const string Local = @"C:\Users\fixture\AppData\Local";
    private static readonly MxcCompanionPackage Store = new(
        "OpenClawFoundation.OpenClaw", "CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC",
        "OpenClawFoundation.OpenClaw_0123456789abc");
    private static readonly MxcCompanionPackage Dev = new(
        "OpenClawFoundation.OpenClaw.Dev", "CN=OpenClaw Local Development",
        "OpenClawFoundation.OpenClaw.Dev_abcdef0123456");

    private static MxcRequestContext Context => new()
    {
        WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        SystemDirectory = Environment.SystemDirectory,
        UserFolders = [],
        ProtectedPaths =
        [
            Path.Combine(Local, "OpenClawTray"),
            Path.Combine(Local, "OpenClawTray-Dev"),
            @"C:\Users\fixture\AppData\Roaming\OpenClawTray",
            @"C:\Users\fixture\AppData\Roaming\OpenClawTray-Dev",
        ],
    };

    private static string[] Expected(MxcCompanionPackage package, string leaf, string local = Local) =>
        new[] { "Roaming", "Local" }.Select(folder =>
            Path.Combine(local, "Packages", package.FamilyName, "LocalCache", folder, leaf)).ToArray();

    [Fact]
    public void EmptyInventory_PreservesDefaultProtectionWithoutRequiringAPackageOrLocalPath()
    {
        var original = Context;
        var snapshot = MxcCompanionPackagePaths.Capture(original, () => [],
            () => throw new InvalidOperationException("No package path is needed."));

        Assert.Equal(original.ProtectedPaths, snapshot.ProtectedPaths);
        Assert.Equal(original.WindowsDirectory, snapshot.WindowsDirectory);
        Assert.Equal(original.SystemDirectory, snapshot.SystemDirectory);
        Assert.NotSame(original.ProtectedPaths, snapshot.ProtectedPaths);
    }

    [Fact]
    public void UnrelatedAndWrongPublisherAndNonAppPackages_AreNotCompanion()
    {
        MxcCompanionPackage[] inventory =
        [
            Store with { Name = "OpenClawFoundation.OpenClaw.Other", FamilyName = "../unrelated" },
            Store with { Publisher = Dev.Publisher },
            Dev with { Publisher = Store.Publisher },
            Store with { Publisher = Store.Publisher + ", O=Other" },
            Dev with { Name = "OpenClawFoundation.OpenClaw.Dev.Patch" },
            Store with { Name = "OpenClawFoundation.OpenClawGateway" },
            Store with { IsFramework = true },
            Dev with { IsResourcePackage = true },
        ];

        Assert.Equal(Context.ProtectedPaths, MxcCompanionPackagePaths.Capture(
            Context, () => inventory, () => Local).ProtectedPaths);
    }

    [Fact]
    public void BothRegistrations_AddOnlyTheirFourPrivateStateLeaves()
    {
        var snapshot = MxcCompanionPackagePaths.Capture(Context, () => [Store, Dev], () => Local);
        var expected = Context.ProtectedPaths.Concat(Expected(Store, "OpenClawTray"))
            .Concat(Expected(Dev, "OpenClawTray-Dev"));

        Assert.Equal(expected, snapshot.ProtectedPaths);
        Assert.All(snapshot.ProtectedPaths.Skip(4), path => Assert.Contains(@"\LocalCache\", path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleRegistration_UsesWindowsReportedFamilyRatherThanAPublisherHash(bool development)
    {
        var package = development ? Dev : Store;
        var expected = Expected(package, development ? "OpenClawTray-Dev" : "OpenClawTray");
        var snapshot = MxcCompanionPackagePaths.Capture(Context, () => [package], () => Local);

        Assert.Equal(Context.ProtectedPaths.Concat(expected), snapshot.ProtectedPaths);
    }

    [Fact]
    public void DuplicateRegistrations_DeduplicatePathsWithWindowsCaseSemantics()
    {
        var snapshot = MxcCompanionPackagePaths.Capture(Context,
            () => [Store, Store with { FamilyName = Store.FamilyName.ToUpperInvariant() }, Dev, Dev],
            () => Local);

        Assert.Equal(8, snapshot.ProtectedPaths.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("OpenClawFoundation.OpenClaw_")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789ab")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789abcd")]
    [InlineData("OpenClawFoundation.OpenClaw.Dev_0123456789abc")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789ab.")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789ab ")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789ab*")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789ab:")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789ab/")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789ab\\")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789abé")]
    [InlineData("OpenClawFoundation.OpenClaw_0123456789abc\\..\\Other")]
    public void MalformedMatchingFamily_BlocksInsteadOfCombiningAnUnsafeComponent(string? family)
    {
        var error = Assert.Throws<NotSupportedException>(() => MxcCompanionPackagePaths.Capture(
            Context, () => [Dev, Store with { FamilyName = family! }], () => Local));
        Assert.Contains("Companion package-state protection", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(@"C:\")]
    public void InvalidResolvedLocalPath_Blocks(string local)
    {
        Assert.Throws<NotSupportedException>(() => MxcCompanionPackagePaths.Capture(
            Context, () => [Store], () => local));
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("partial-inventory")]
    [InlineData("local-path")]
    public void ResolutionErrors_FailClosedVisiblyWithoutPartialProtection(string failure)
    {
        var error = Assert.Throws<NotSupportedException>(() => MxcCompanionPackagePaths.Capture(Context,
            () => failure switch
            {
                "inventory" => throw new UnauthorizedAccessException("fixture inventory failure"),
                "partial-inventory" => PartialInventory(),
                _ => [Store],
            },
            () => failure == "local-path" ? throw new IOException("fixture known-folder failure") : Local));

        Assert.Contains("Companion package-state protection could not be resolved", error.Message);
        Assert.NotNull(error.InnerException);
    }

    private static IEnumerable<MxcCompanionPackage> PartialInventory()
    {
        yield return Store;
        throw new IOException("fixture enumeration failure after a matching package");
    }

    [Fact]
    public void FreshCapture_ObservesNewInstallAndDetachesBothContextCollections()
    {
        var inventory = new List<MxcCompanionPackage> { Store };
        var originalPaths = Context.ProtectedPaths.ToList();
        var folders = new List<MxcUserFolder> { new("Profile", @"C:\Users\fixture") };
        var original = Context with { ProtectedPaths = originalPaths, UserFolders = folders };
        var first = MxcCompanionPackagePaths.Capture(original, () => inventory, () => Local);
        inventory.Add(Dev);
        var second = MxcCompanionPackagePaths.Capture(original, () => inventory, () => Local);
        originalPaths.Clear();
        folders.Clear();

        Assert.Equal(6, first.ProtectedPaths.Count);
        Assert.Equal(8, second.ProtectedPaths.Count);
        Assert.Single(first.UserFolders);
        Assert.Single(second.UserFolders);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)first.ProtectedPaths).Add("mutation"));
        Assert.Throws<NotSupportedException>(() => ((IList<MxcUserFolder>)first.UserFolders).Clear());
    }

    [Fact]
    public void Builder_PreservesFutureLeavesAndOwnedPackageScratchWithoutCreatingStateOrBroadDenies()
    {
        using var fixture = new TempDirectory("mxc-companion-paths-");
        var local = fixture.Combine("Local");
        var leaves = Expected(Store, "OpenClawTray", local).Concat(Expected(Dev, "OpenClawTray-Dev", local)).ToArray();
        foreach (var leaf in leaves)
            Directory.CreateDirectory(Path.GetDirectoryName(leaf)!);
        var scratch = Path.Combine(local, "Packages", Store.FamilyName, "LocalCache", "Local", "OpenClawMxc", "Runs", "owned");
        Directory.CreateDirectory(scratch);
        var settings = fixture.Combine("settings");
        var snapshot = MxcCompanionPackagePaths.Capture(Context with { ProtectedPaths = [settings] },
            () => [Store, Dev], () => local);
        var command = new CommandRequest { Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")] };

        var request = MxcRequestBuilder.Build(command, new(), settings, scratch, snapshot);

        Assert.Equal(leaves.Prepend(settings).Order(), request.Filesystem!.DeniedPaths.Order());
        Assert.Equal(scratch, Assert.Single(request.Filesystem.ReadwritePaths));
        Assert.Null(request.Environment);
        Assert.All(leaves, leaf => Assert.False(Directory.Exists(leaf)));
        Assert.Throws<NotSupportedException>(() => MxcRequestBuilder.Build(command,
            new() { SandboxCustomFolders = [new() { Path = leaves[0], Access = SandboxFolderAccess.ReadWrite }] },
            settings, scratch, snapshot));
    }

    [Fact]
    public async Task Runner_RefreshesInventoryPerCommandAndBlocksFailuresBeforeNativeAdmission()
    {
        using var fixture = new TempDirectory("mxc-companion-context-");
        var inventoryCalls = 0;
        var runner = new MxcCommandRunner(() => new(), () => fixture.Combine("settings"),
            () => new(true, false, true, [], isolationTier: "base-container", supportsProtectedPathDenies: true),
            spawn: _ => throw new InvalidOperationException("Native spawn must not run."),
            probe: _ => throw new InvalidOperationException("Native admission must not run."),
            scratchRoot: fixture.Combine("runs"),
            contextProvider: () => MxcCompanionPackagePaths.Capture(Context, () =>
            {
                inventoryCalls++;
                return PartialInventory();
            }, () => Local));
        var command = new CommandRequest { Argv = [Path.Combine(Environment.SystemDirectory, "whoami.exe")] };

        for (var i = 0; i < 2; i++)
        {
            var result = await runner.RunAsync(command);
            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(NodeToolExecutionMode.Sandbox, result.ExecutionMode);
            Assert.Equal(NodeToolErrorCategory.SandboxDenied, result.ErrorCategory);
            Assert.Contains("Companion package-state protection could not be resolved", result.Stderr);
        }
        Assert.Equal(2, inventoryCalls);
        Assert.Empty(Directory.GetDirectories(fixture.Combine("runs")));
    }

    [Fact]
    public void NodeFactory_InjectsFreshWindowsContextAndKeepsPackageDiscoveryOutOfShared()
    {
        // retirement_condition: replace when NodeService composition is executable without WinUI.
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var node = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "NodeService.cs"));
        var factoryStart = node.IndexOf("private ICommandRunner BuildSystemRunRunner()", StringComparison.Ordinal);
        var factoryEnd = node.IndexOf("private SettingsData SnapshotSettings()", factoryStart, StringComparison.Ordinal);
        Assert.Contains("contextProvider: () => MxcWindowsRequestContext.Capture(settingsDirectory)",
            node[factoryStart..factoryEnd]);
        var adapter = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "MxcWindowsRequestContext.cs"));
        Assert.Contains("new PackageManager().FindPackagesForUser(string.Empty)", adapter);
        Assert.Contains("MxcRequestContext.Capture(settingsDirectory), EnumeratePackages, UnredirectedLocalAppData", adapter);
        Assert.Contains("KF_FLAG_NO_PACKAGE_REDIRECTION = 0x00010000", adapter);
        Assert.DoesNotContain("Package.Current", adapter);
        var runner = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Shared", "Mxc", "MxcCommandRunner.cs"));
        Assert.DoesNotContain("Windows.Management.Deployment", runner);
        Assert.DoesNotContain("PackageManager", runner);
    }
}
