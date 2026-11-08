using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcFolderPermissionsTests
{
    [Fact]
    public void NoAccessHasAnAppendedWireValueAndRoundTripsWithoutReinterpretingLegacyBlocked()
    {
        Assert.Equal(0, (int)SandboxFolderAccess.ReadOnly);
        Assert.Equal(1, (int)SandboxFolderAccess.ReadWrite);
        Assert.Equal(2, (int)SandboxFolderAccess.Blocked);
        Assert.Equal(3, (int)SandboxFolderAccess.NoAccess);
        var settings = new SettingsData
        {
            SandboxDocumentsAccess = SandboxFolderAccess.NoAccess,
            SandboxDownloadsAccess = SandboxFolderAccess.NoAccess,
            SandboxDesktopAccess = SandboxFolderAccess.Blocked,
            SandboxCustomFolders =
            [
                new() { Path = @"D:\Private", Access = SandboxFolderAccess.NoAccess },
                new() { Path = @"D:\Legacy", Access = SandboxFolderAccess.Blocked },
            ],
        };
        var restored = SettingsData.FromJson(settings.ToJson())!;
        Assert.Equal(SandboxFolderAccess.NoAccess, restored.SandboxDocumentsAccess);
        Assert.Equal(SandboxFolderAccess.NoAccess, restored.SandboxDownloadsAccess);
        Assert.Equal(SandboxFolderAccess.Blocked, restored.SandboxDesktopAccess);
        Assert.Equal(SandboxFolderAccess.NoAccess, restored.SandboxCustomFolders![0].Access);
        Assert.Equal(SandboxFolderAccess.Blocked, restored.SandboxCustomFolders[1].Access);
        var snapshot = MxcRequestBuilder.Snapshot(restored);
        var fingerprint = MxcRequestBuilder.Fingerprint(snapshot);
        restored.SandboxCustomFolders[0].Access = SandboxFolderAccess.ReadWrite;
        Assert.Equal(SandboxFolderAccess.NoAccess, snapshot.SandboxCustomFolders![0].Access);
        Assert.Equal(fingerprint, MxcRequestBuilder.Fingerprint(snapshot));
        Assert.NotEqual(fingerprint, MxcRequestBuilder.Fingerprint(restored));
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData("Profile")]
    public void NoAccessOverridesAPresetAndRemainsVisibleAndReversible(string name)
    {
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Open);
        var row = MxcFolderPermissions.Resolve(settings, Context).Single(r => r.KnownFolders.Contains(name));
        var edited = MxcFolderPermissions.Edit(settings, row, SandboxFolderAccess.NoAccess);
        var denied = MxcFolderPermissions.Resolve(edited, Context).Single(r => r.Path == row.Path);
        Assert.Equal(SandboxFolderAccess.NoAccess, denied.Access);
        Assert.True(denied.HasOverride);
        Assert.False(denied.HasLegacyBlocked);
        Assert.Null(SystemRunPermissionPresets.Detect(edited));
        var restored = MxcFolderPermissions.Edit(edited, denied, SandboxFolderAccess.ReadOnly);
        Assert.Equal(SandboxFolderAccess.ReadOnly, MxcFolderPermissions.Resolve(restored, Context).Single(r => r.Path == row.Path).Access);
        Assert.Equal(SystemRunAccessPreset.Open, SystemRunPermissionPresets.Detect(settings));
    }

    [Fact]
    public void DuplicateNoAccessBeatsReadWriteAndReadOnlyButNotByConvertingBlocked()
    {
        var settings = new SettingsData { SandboxCustomFolders =
        [
            new() { Path = @"D:\Work", Access = SandboxFolderAccess.ReadWrite },
            new() { Path = @"d:\work\", Access = SandboxFolderAccess.ReadOnly },
            new() { Path = @"D:\Work\child\..", Access = SandboxFolderAccess.NoAccess },
            new() { Path = @"D:\Work", Access = SandboxFolderAccess.Blocked },
        ] };
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, Context));
        Assert.Equal(SandboxFolderAccess.NoAccess, row.Access);
        Assert.True(row.HasLegacyBlocked);
        settings.SandboxCustomFolders.Reverse();
        Assert.Equal(row.Access, Assert.Single(MxcFolderPermissions.Resolve(settings, Context)).Access);
        var edited = MxcFolderPermissions.Edit(settings, row, SandboxFolderAccess.ReadWrite);
        Assert.Single(edited.SandboxCustomFolders!);
        Assert.Equal(SandboxFolderAccess.ReadWrite, Assert.Single(MxcFolderPermissions.Resolve(edited, Context)).Access);
    }

    [Theory]
    [InlineData(SandboxFolderAccess.ReadOnly)]
    [InlineData(SandboxFolderAccess.ReadWrite)]
    public void EffectiveAccessHonorsExplicitChildrenOfUserDeniedParents(SandboxFolderAccess access)
    {
        string[] ro = access == SandboxFolderAccess.ReadOnly ? [@"D:\Parent\Child"] : [];
        string[] rw = access == SandboxFolderAccess.ReadWrite ? [@"D:\Parent\Child"] : [];
        string[] denied = [@"D:\Parent"];
        Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(@"D:\Parent\file", ro, rw, denied));
        Assert.Equal(access, MxcFolderPermissions.EffectiveAccess(@"D:\Parent\Child\file", ro, rw, denied));
        Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(@"D:\Parent\ChildElse\file", ro, rw, denied));
        Assert.Null(MxcFolderPermissions.EffectiveAccess(@"D:\ParentElse", ro, rw, denied));
    }

    [Fact]
    public void EffectiveAccessDenyBeatsBroaderAndSamePathGrants()
    {
        string[] ro = [@"D:\Parent\Private"];
        string[] rw = [@"D:\Parent", @"D:\Parent\Private"];
        Assert.Equal(SandboxFolderAccess.NoAccess,
            MxcFolderPermissions.EffectiveAccess(@"d:\parent\private\file", ro, rw, [@"D:\Parent\Private\"]));
        Assert.Equal(SandboxFolderAccess.ReadWrite,
            MxcFolderPermissions.EffectiveAccess(@"D:\Parent\public\file", ro, rw, [@"D:\Parent\Private"]));
    }

    [Fact]
    public void ExtendedDosSpellingsUseTheSameLexicalBoundaryAsSdkAndProtectedPaths()
    {
        Assert.True(MxcFolderPermissions.SamePath(@"\\?\D:\Parent\Private", @"D:\Parent\Private"));
        Assert.Equal(SandboxFolderAccess.NoAccess, MxcFolderPermissions.EffectiveAccess(
            @"\\?\D:\Parent\Private\file", [], [@"D:\Parent"], [@"D:\Parent\Private"]));
        var settings = new SettingsData { SandboxCustomFolders =
        [
            new() { Path = @"\\?\D:\Private", Access = SandboxFolderAccess.ReadWrite },
            new() { Path = @"D:\Private", Access = SandboxFolderAccess.NoAccess },
        ] };
        Assert.Equal(SandboxFolderAccess.NoAccess, Assert.Single(MxcFolderPermissions.Resolve(settings, Context)).Access);
    }

    [Fact]
    public void RuntimeNoAccessIsAVisibleErrorNotSilentlyDowngradedToReadOnly()
    {
        var settings = new SettingsData { SandboxCustomFolders = [new() { Path = @"C:\Windows", Access = SandboxFolderAccess.NoAccess }] };
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, Context));
        Assert.Equal(SandboxFolderAccess.NoAccess, row.Access);
        Assert.NotNull(row.Error);
        Assert.Throws<InvalidOperationException>(() => MxcFolderPermissions.Edit(settings, row, SandboxFolderAccess.NoAccess));
    }

    private static MxcRequestContext Context => new()
    {
        WindowsDirectory = @"C:\Windows",
        SystemDirectory = @"C:\Windows\System32",
        ProtectedPaths = [@"C:\Users\Example\.ssh"],
        UserFolders =
        [
            new("Profile", @"C:\Users\Example"),
            new("Documents", @"C:\Users\Example\Documents"),
            new("Desktop", @"C:\Users\Example\Desktop"),
            new("Downloads", @"D:\Redirected\Downloads"),
            new("OneDrive", null, IsOptional: true),
        ],
    };

    [Theory]
    [InlineData(SystemRunAccessPreset.Strict, 0, SandboxFolderAccess.ReadOnly)]
    [InlineData(SystemRunAccessPreset.Balanced, 4, SandboxFolderAccess.ReadOnly)]
    [InlineData(SystemRunAccessPreset.Open, 4, SandboxFolderAccess.ReadWrite)]
    public void PresetsSeedOnlyResolvedPersonalRootsWithoutMutatingSettings(
        SystemRunAccessPreset preset, int count, SandboxFolderAccess access)
    {
        var settings = SystemRunPermissionPresets.Apply(new(), preset);
        var fingerprint = MxcRequestBuilder.Fingerprint(settings);
        var rows = MxcFolderPermissions.Resolve(settings, Context);
        Assert.Equal(count, rows.Count);
        Assert.All(rows, r => { Assert.Equal(access, r.Access); Assert.True(r.IsPreset); Assert.False(r.HasOverride); });
        Assert.Equal(fingerprint, MxcRequestBuilder.Fingerprint(settings));
        Assert.Equal(preset, SystemRunPermissionPresets.Detect(settings));
    }

    [Theory]
    [InlineData(SystemRunAccessPreset.Balanced, SandboxFolderAccess.ReadWrite)]
    [InlineData(SystemRunAccessPreset.Open, SandboxFolderAccess.ReadOnly)]
    public void EditingKnownRootReplacesItsExactPresetEntryAndPreservesParent(
        SystemRunAccessPreset preset, SandboxFolderAccess access)
    {
        var settings = SystemRunPermissionPresets.Apply(new(), preset);
        var before = MxcRequestBuilder.Fingerprint(settings);
        var row = MxcFolderPermissions.Resolve(settings, Context).Single(r => r.KnownFolders.Contains("Documents"));
        var edited = MxcFolderPermissions.Edit(settings, row, access);
        var rows = MxcFolderPermissions.Resolve(edited, Context);
        Assert.Equal(access, rows.Single(r => r.Path == row.Path).Access);
        Assert.Equal(preset == SystemRunAccessPreset.Open ? SandboxFolderAccess.ReadWrite : SandboxFolderAccess.ReadOnly,
            rows.Single(r => r.KnownFolders.Contains("Profile")).Access);
        Assert.Equal(access, edited.SandboxDocumentsAccess);
        Assert.Empty(edited.SandboxCustomFolders!);
        Assert.Null(SystemRunPermissionPresets.Detect(edited));
        Assert.Equal(before, MxcRequestBuilder.Fingerprint(settings));
    }

    [Theory]
    [InlineData("Profile")]
    [InlineData("OneDrive")]
    public void RootsWithoutDedicatedFieldsUseAnExplicitPathAndCanReset(string name)
    {
        var context = Context with { UserFolders = [new(name, @"D:\Personal")] };
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Balanced);
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, context));
        var edited = MxcFolderPermissions.Edit(settings, row, SandboxFolderAccess.ReadWrite);
        Assert.Equal(SandboxFolderAccess.ReadWrite, Assert.Single(edited.SandboxCustomFolders!).Access);
        row = Assert.Single(MxcFolderPermissions.Resolve(edited, context));
        Assert.Equal(SandboxFolderAccess.ReadWrite, row.Access);
        var reset = MxcFolderPermissions.Edit(edited, row, null);
        Assert.Equal(SystemRunAccessPreset.Balanced, SystemRunPermissionPresets.Detect(reset));
        Assert.Equal(SandboxFolderAccess.ReadOnly, Assert.Single(MxcFolderPermissions.Resolve(reset, context)).Access);
    }

    [Fact]
    public void NormalizedAliasesUseOneRowAndAnEditUpdatesEveryRepresentedSource()
    {
        var context = Context with
        {
            UserFolders = [new("Profile", @"D:\Personal"), new("Documents", @"D:\Personal\"),
                new("OneDrive", @"d:\personal"), new("Desktop", @"D:\Personal\child\..")],
        };
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Open) with
        {
            SandboxDocumentsAccess = SandboxFolderAccess.ReadOnly,
            SandboxCustomFolders = [new() { Path = @"d:\personal\", Access = SandboxFolderAccess.ReadWrite }],
        };
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, context));
        Assert.Equal(4, row.KnownFolders.Count);
        Assert.Equal(SandboxFolderAccess.ReadWrite, row.Access); // Explicit path beats a known-folder override.
        var edited = MxcFolderPermissions.Edit(settings, row, SandboxFolderAccess.ReadOnly);
        Assert.Equal(SandboxFolderAccess.ReadOnly, edited.SandboxDocumentsAccess);
        Assert.Equal(SandboxFolderAccess.ReadOnly, edited.SandboxDesktopAccess);
        Assert.Equal(SandboxFolderAccess.ReadOnly, Assert.Single(edited.SandboxCustomFolders!).Access);
        Assert.Equal(SandboxFolderAccess.ReadOnly, Assert.Single(MxcFolderPermissions.Resolve(edited, context)).Access);
    }

    [Fact]
    public void ConflictingDuplicateCustomPathsStayReadOnlyUntilExplicitlyEdited()
    {
        var settings = new SettingsData
        {
            SandboxCustomFolders =
            [
                new() { Path = @"D:\Work", Access = SandboxFolderAccess.ReadWrite },
                new() { Path = @"d:\work\", Access = SandboxFolderAccess.ReadOnly },
            ],
        };
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, Context));
        Assert.Equal(SandboxFolderAccess.ReadOnly, row.Access);
        settings.SandboxCustomFolders.Reverse();
        Assert.Equal(row.Access, Assert.Single(MxcFolderPermissions.Resolve(settings, Context)).Access);
        var edited = MxcFolderPermissions.Edit(settings, row, SandboxFolderAccess.ReadWrite);
        Assert.Single(edited.SandboxCustomFolders!);
        Assert.Equal(SandboxFolderAccess.ReadWrite, Assert.Single(MxcFolderPermissions.Resolve(edited, Context)).Access);
        Assert.Empty(MxcFolderPermissions.Resolve(MxcFolderPermissions.Edit(edited, row, null), Context));
    }

    [Fact]
    public void LegacyBlockedIsNotReinterpretedAsADenyOrAnActiveGrant()
    {
        var settings = new SettingsData
        {
            SandboxDocumentsAccess = SandboxFolderAccess.Blocked,
            SandboxCustomFolders = [new() { Path = @"D:\Work", Access = SandboxFolderAccess.Blocked }],
        };
        var rows = MxcFolderPermissions.Resolve(settings, Context);
        Assert.All(rows, r => { Assert.Null(r.Access); Assert.True(r.HasLegacyBlocked); });
        var edited = MxcFolderPermissions.Edit(settings, rows.Single(r => r.KnownFolders.Contains("Documents")),
            SandboxFolderAccess.ReadOnly);
        Assert.Equal(SandboxFolderAccess.ReadOnly, edited.SandboxDocumentsAccess);
        Assert.Equal(SandboxFolderAccess.Blocked, Assert.Single(edited.SandboxCustomFolders!).Access);
        Assert.Equal(SandboxFolderAccess.Blocked, settings.SandboxDocumentsAccess);
    }

    [Fact]
    public void BeforeFolderResolutionSavedRowsRemainDetachedAndDoNotInventPathsOrErrors()
    {
        var settings = new SettingsData { SandboxDocumentsAccess = SandboxFolderAccess.ReadWrite };
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, null));
        Assert.Equal("", row.Path);
        Assert.Null(row.Error);
        Assert.Equal(SandboxFolderAccess.ReadWrite, row.Access);
        Assert.Equal(["Documents"], row.KnownFolders);
        Assert.Null(settings.SandboxCustomFolders);
    }

    [Fact]
    public void QuarantinedKnownFolderErrorsDoNotChangeAnIndependentExplicitGrant()
    {
        var context = Context with { UserFolders = [new("Documents", @"D:\Work", "Old location error")] };
        var settings = new SettingsData
        {
            SandboxDocumentsAccess = SandboxFolderAccess.Blocked,
            SandboxCustomFolders = [new() { Path = @"D:\Work", Access = SandboxFolderAccess.ReadOnly }],
        };
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, context));
        Assert.True(row.HasLegacyBlocked);
        Assert.Equal(SandboxFolderAccess.ReadOnly, row.Access);
        Assert.Null(row.Error);
        Assert.Equal(SandboxFolderAccess.Blocked, settings.SandboxDocumentsAccess);
    }

    [Fact]
    public void MissingRootsAndInvalidCustomEntriesStayVisibleAndCannotHideErrors()
    {
        var settings = SystemRunPermissionPresets.Apply(new(), SystemRunAccessPreset.Open) with
        {
            SandboxCustomFolders = [new() { Path = "relative", Access = SandboxFolderAccess.ReadOnly },
                new() { Path = @"D:\Work", Access = (SandboxFolderAccess)99 },
                new() { Path = @"D:\Work", Access = SandboxFolderAccess.ReadOnly }],
        };
        var context = Context with { UserFolders = [new("Profile", null, "Offline profile")] };
        var rows = MxcFolderPermissions.Resolve(settings, context);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.Error));
    }

    [Fact]
    public void FixedRuntimeSamePathRemainsReadOnlyRatherThanShowingAnIneffectiveWriteGrant()
    {
        var settings = new SettingsData { SandboxCustomFolders = [new() { Path = @"c:\windows\", Access = SandboxFolderAccess.ReadWrite }] };
        var row = Assert.Single(MxcFolderPermissions.Resolve(settings, Context));
        Assert.True(row.IsRuntime);
        Assert.Equal(SandboxFolderAccess.ReadOnly, row.Access);
        Assert.Throws<InvalidOperationException>(() => MxcFolderPermissions.Edit(settings, row, SandboxFolderAccess.ReadWrite));
        Assert.Empty(MxcFolderPermissions.Edit(settings, row, null).SandboxCustomFolders!);
    }
}
