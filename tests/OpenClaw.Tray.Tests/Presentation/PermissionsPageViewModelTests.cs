using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class PermissionsPageViewModelTests
{
    [Fact]
    public void RuntimeSource_DistinguishesAllowlistEmptyAndMalformedStates()
    {
        var host = new FakePermissionsPageRuntimeHost();
        using var source = new PermissionsPageRuntimeSource(host);

        Assert.Equal(PermissionsGatewayAllowlistState.NoConfig, source.Current.GatewayAllowlistState);

        host.GatewayConfig = JsonDocument.Parse("""{"gateway":{"nodes":{"allowCommands":[]}}}""").RootElement.Clone();
        Assert.Equal(PermissionsGatewayAllowlistState.NoCommands, source.Current.GatewayAllowlistState);

        host.GatewayConfig = JsonDocument.Parse("""{"gateway":{"nodes":{"allowCommands":["system.run"]}}}""").RootElement.Clone();
        Assert.Equal(PermissionsGatewayAllowlistState.Commands, source.Current.GatewayAllowlistState);
        Assert.Equal(new[] { "system.run" }, source.Current.GatewayAllowCommands);

        host.GatewayConfig = JsonDocument.Parse("""{"gateway":{"nodes":{"allowCommands":[42]}}}""").RootElement.Clone();
        Assert.Equal(PermissionsGatewayAllowlistState.ParseFailed, source.Current.GatewayAllowlistState);
        Assert.Empty(source.Current.GatewayAllowCommands);
    }

    [Fact]
    public void Activate_ReadOnlyExecSnapshot_DoesNotCreateFile()
    {
        using var harness = PermissionsHarness.CreateReal();

        harness.ViewModel.Activate(null);

        Assert.False(File.Exists(harness.ExecApprovalsPath));
        Assert.Equal("prompt", harness.ViewModel.DefaultExecActionTag);
        Assert.Empty(harness.ViewModel.ExecApprovalRules);
    }

    [Fact]
    public void SettingsWrites_SaveBeforeNotify_AndCapabilityFieldsMapCorrectly()
    {
        foreach (var (key, readSetting) in new (PermissionsCapabilityKey, Func<SettingsManager, bool>)[]
        {
            (PermissionsCapabilityKey.SystemRun, s => s.NodeSystemRunEnabled),
            (PermissionsCapabilityKey.BrowserProxy, s => s.NodeBrowserProxyEnabled),
            (PermissionsCapabilityKey.Camera, s => s.NodeCameraEnabled),
            (PermissionsCapabilityKey.Canvas, s => s.NodeCanvasEnabled),
            (PermissionsCapabilityKey.Screen, s => s.NodeScreenEnabled),
            (PermissionsCapabilityKey.Location, s => s.NodeLocationEnabled),
            (PermissionsCapabilityKey.TextToSpeech, s => s.NodeTtsEnabled),
            (PermissionsCapabilityKey.SpeechToText, s => s.NodeSttEnabled),
        })
        {
            using var harness = PermissionsHarness.CreateReal();
            harness.SettingsStore.Update(harness.SettingsStore.CreateOrigin(), edit => SetCapability(edit, key, true));
            harness.ViewModel.Activate(null);
            var order = new List<string>();
            harness.Commands.ClearOperationLog();
            harness.Settings.Saved += (_, _) => order.Add("save");

            harness.ViewModel.SetCapabilityEnabled(key, false);

            order.AddRange(harness.Commands.OperationLog);
            Assert.False(readSetting(harness.Settings));
            Assert.Equal(new[] { "save", "notify" }, order);
        }
    }

    [Fact]
    public void CapabilityWrite_PublishesUpdatedProjectionWithoutReentrantSave()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.SettingsStore.Update(
            harness.SettingsStore.CreateOrigin(),
            edit => edit.NodeCameraEnabled = false);
        harness.ViewModel.Activate(null);
        harness.Commands.ClearOperationLog();

        var saveCount = 0;
        var publishedStates = new List<bool>();
        harness.Settings.Saved += (_, _) => saveCount++;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PermissionsPageViewModel.Capabilities))
            {
                return;
            }

            var publishedState = harness.ViewModel.Capabilities
                .Single(capability => capability.Key == PermissionsCapabilityKey.Camera)
                .IsOn;
            publishedStates.Add(publishedState);
            harness.ViewModel.SetCapabilityEnabled(PermissionsCapabilityKey.Camera, publishedState);
        };

        harness.ViewModel.SetCapabilityEnabled(PermissionsCapabilityKey.Camera, true);

        Assert.True(harness.Settings.NodeCameraEnabled);
        Assert.Equal(new[] { true }, publishedStates);
        Assert.Equal(1, saveCount);
        Assert.Equal(new[] { "notify" }, harness.Commands.OperationLog);
    }

    [Fact]
    public void NodeModeAndMcp_WriteThroughStore_SaveBeforeNotify()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);

        var order = new List<string>();
        harness.Settings.Saved += (_, _) => order.Add("save");

        harness.ViewModel.NodeModeEnabled = true;

        Assert.True(harness.Settings.EnableNodeMode);
        Assert.Equal(new[] { "save", "notify" }, order.Concat(harness.Commands.OperationLog).ToArray());

        order.Clear();
        harness.Commands.ClearOperationLog();

        harness.ViewModel.McpEnabled = true;

        Assert.True(harness.Settings.EnableMcpServer);
        Assert.Equal(new[] { "save", "notify" }, order.Concat(harness.Commands.OperationLog).ToArray());
    }

    [Fact]
    public void McpOnly_RuntimeProjectsFeatureAndTokenState()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.Settings.EnableMcpServer = true;
        harness.Settings.EnableNodeMode = false;
        harness.Settings.Save();
        harness.RuntimeHost.ConnectionSnapshot = GatewayConnectionSnapshot.Idle;
        harness.RuntimeHost.McpServedCapabilityCount = NodeCapabilityGating.CountMcpServedCapabilities(harness.Settings);

        harness.ViewModel.Activate(null);

        Assert.True(harness.ViewModel.AreFeaturesEnabled);
        Assert.Equal(PermissionsNodeStatusKind.McpOnly, harness.ViewModel.NodeStatusKind);
        Assert.Equal(PermissionsMcpTokenState.Pending, harness.ViewModel.McpTokenState);
        Assert.False(harness.ViewModel.Capabilities.Single(c => c.Key == PermissionsCapabilityKey.BrowserProxy).IsInteractive);
        Assert.Equal(NodeCapabilityGating.CountMcpServedCapabilities(harness.Settings), harness.ViewModel.McpServedCapabilityCount);
    }

    [Fact]
    public void ConnectedNode_UsesGatewayCapabilitiesProjection()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.Settings.EnableNodeMode = true;
        harness.RuntimeHost.ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
        {
            OperatorState = RoleConnectionState.Connected,
            NodeState = RoleConnectionState.Connected,
        };
        harness.RuntimeHost.LocalNodeDeviceId = "device-1";
        harness.RuntimeHost.Nodes =
        [
            new GatewayNodeInfo
            {
                NodeId = "device-1",
                Capabilities = ["system", "canvas"],
            },
        ];

        harness.ViewModel.Activate(null);

        Assert.Equal(PermissionsNodeStatusKind.Active, harness.ViewModel.NodeStatusKind);
        Assert.Equal(2, harness.ViewModel.LocalNodeCapabilityCount);
        Assert.Equal(new[] { "system", "canvas" }, harness.ViewModel.LocalNodeCapabilities);
    }

    [Fact]
    public void VoiceSetupRequirement_ProjectsSpeechModelAndProviderState()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.Settings.NodeSttEnabled = true;
        harness.Settings.NodeTtsEnabled = true;
        harness.Settings.SttModelName = "missing-model";
        harness.Settings.TtsProvider = TtsCapability.ElevenLabsProvider;
        harness.Settings.TtsElevenLabsApiKey = "";
        harness.Settings.TtsElevenLabsVoiceId = "";
        harness.RuntimeHost.VoiceSetupRequirement = PermissionsVoiceSetupRequirement.SpeechModelAndVoiceSetup;

        harness.ViewModel.Activate(null);

        Assert.True(harness.ViewModel.VoiceSettingsVisible);
        Assert.Equal(PermissionsVoiceSetupRequirement.SpeechModelAndVoiceSetup, harness.ViewModel.VoiceSetupRequirement);
    }

    [Fact]
    public void ExternalSettingsSave_UpdatesOnce_AndOwnWritesAreIgnored()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);
        var external = 0;
        harness.ViewModel.ExternalChanged += (_, _) => external++;

        harness.ViewModel.NodeModeEnabled = true;
        Assert.Equal(0, external);

        harness.Settings.EnableNodeMode = false;
        harness.Settings.Save();

        Assert.Equal(1, external);
        Assert.False(harness.ViewModel.NodeModeEnabled);
    }

    [Fact]
    public void DeactivateAndDispose_Unsubscribe_AndReopenGetsFreshState()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);
        harness.ViewModel.NodeModeEnabled = true;

        harness.ViewModel.Deactivate();
        harness.Settings.EnableNodeMode = false;
        harness.Settings.Save();

        Assert.True(harness.ViewModel.NodeModeEnabled);

        harness.ViewModel.Dispose();
        Assert.True(harness.ViewModel.IsDisposed);

        var reopened = new PermissionsPageViewModel(
            harness.SettingsStore,
            harness.ExecApprovalsStore,
            harness.Commands,
            harness.Dispatcher,
            harness.RuntimeSource);
        reopened.Activate(null);
        Assert.False(reopened.NodeModeEnabled);
        reopened.Dispose();
    }

    [Fact]
    public async Task BackgroundSettingsRuntimeAndApprovalsEvents_MarshalThroughDispatcher()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore();
        harness.Dispatcher.HasThreadAccess = false;
        harness.Dispatcher.RunEnqueuedImmediately = false;
        harness.ViewModel.Activate(null);

        await Task.Run(() =>
        {
            harness.Settings.EnableNodeMode = true;
            harness.Settings.Save();
        });

        Assert.False(harness.ViewModel.NodeModeEnabled);
        Assert.True(harness.Dispatcher.EnqueuedCount >= 1);
        harness.Dispatcher.FlushPending();
        harness.Dispatcher.FlushPending();
        Assert.True(harness.ViewModel.NodeModeEnabled);

        harness.Dispatcher.HasThreadAccess = false;
        harness.Dispatcher.RunEnqueuedImmediately = false;
        await Task.Run(() =>
        {
            harness.RuntimeHost.ConnectionSnapshot = GatewayConnectionSnapshot.Idle with { NodeState = RoleConnectionState.Connecting };
            harness.RuntimeHost.RaiseChanged();
        });

        Assert.NotEqual(PermissionsNodeStatusKind.Starting, harness.ViewModel.NodeStatusKind);
        harness.Dispatcher.FlushPending();
        Assert.Equal(PermissionsNodeStatusKind.Starting, harness.ViewModel.NodeStatusKind);

        harness.Dispatcher.HasThreadAccess = false;
        harness.Dispatcher.RunEnqueuedImmediately = false;
        await Task.Run(() => harness.RecordingExecStore!.RaiseExternalSnapshot(
            BuildSnapshot(
                hash: "external-valid",
                file: BuildFile(defaultAction: "prompt", allowlist: [new ExecAllowlistEntry { Pattern = "**/git.exe" }]))));

        Assert.Empty(harness.ViewModel.ExecApprovalRules);
        harness.Dispatcher.FlushPending();
        Assert.Single(harness.ViewModel.ExecApprovalRules);
    }

    [Fact]
    public async Task DefaultActionAndRuleMutations_UseV2Mappings_AndPreserveMetadata()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);

        Assert.True(await harness.ViewModel.SetDefaultExecActionAsync("allow"));
        Assert.True(File.Exists(harness.ExecApprovalsPath));

        var afterAllow = await harness.RealExecStore!.GetSnapshotReadOnlyAsync();
        Assert.Equal(ExecSecurity.Full, afterAllow.Snapshot!.File.Defaults!.Security);
        Assert.Equal(ExecAsk.Off, afterAllow.Snapshot.File.Defaults.Ask);
        Assert.Equal(ExecSecurity.Deny, afterAllow.Snapshot.File.Defaults.AskFallback);
        Assert.False(afterAllow.Snapshot.File.Defaults.AutoAllowSkills);
        Assert.Equal(ExecSecurity.Full, afterAllow.Snapshot.File.Agents!["main"].Security);
        Assert.Equal(ExecAsk.Off, afterAllow.Snapshot.File.Agents["main"].Ask);

        Assert.True(await harness.ViewModel.TryAddExecApprovalRuleAsync("**/git.exe"));

        var withRule = await harness.RealExecStore.GetSnapshotReadOnlyAsync();
        var entry = Assert.Single(withRule.Snapshot!.File.Agents!["main"].Allowlist!);
        Assert.Equal("**/git.exe", entry.Pattern);

        Assert.True(await harness.ViewModel.RemoveExecApprovalRuleAsync(
            Assert.Single(harness.ViewModel.ExecApprovalRules)));
        var removed = await harness.RealExecStore.GetSnapshotReadOnlyAsync();
        Assert.True(removed.Snapshot!.File.Agents!["main"].Allowlist is null or { Count: 0 });
    }

    [Fact]
    public async Task RemoveRule_UsesCapturedIdentityAfterExternalReorder()
    {
        var capturedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var insertedId = Guid.NewGuid();
        using var harness = PermissionsHarness.CreateWithRecordingStore(BuildSnapshot(
            "base",
            BuildFile(
                defaultAction: "prompt",
                allowlist:
                [
                    new ExecAllowlistEntry { Id = capturedId, Pattern = "**/git.exe" },
                    new ExecAllowlistEntry { Id = survivorId, Pattern = "**/rg.exe" },
                ])));
        harness.ViewModel.Activate(null);
        var removalToken = harness.ViewModel.ExecApprovalRules[0];

        harness.RecordingExecStore!.RaiseExternalSnapshot(BuildSnapshot(
            "reordered",
            BuildFile(
                defaultAction: "prompt",
                allowlist:
                [
                    new ExecAllowlistEntry { Id = insertedId, Pattern = "**/pwsh.exe" },
                    new ExecAllowlistEntry { Id = survivorId, Pattern = "**/rg.exe" },
                    new ExecAllowlistEntry { Id = capturedId, Pattern = "**/git.exe" },
                ])));

        Assert.True(await harness.ViewModel.RemoveExecApprovalRuleAsync(removalToken));

        var remaining = harness.RecordingExecStore.CurrentSnapshot.File.Agents!["main"].Allowlist!;
        Assert.DoesNotContain(remaining, rule => rule.Id == capturedId);
        Assert.Contains(remaining, rule => rule.Id == insertedId);
        Assert.Contains(remaining, rule => rule.Id == survivorId);
    }

    [Fact]
    public async Task RemoveIdlessRule_PatternFallbackDoesNotDeleteIdentifiedRule()
    {
        var identifiedId = Guid.NewGuid();
        using var harness = PermissionsHarness.CreateWithRecordingStore(BuildSnapshot(
            "base",
            BuildFile(
                defaultAction: "prompt",
                allowlist:
                [
                    new ExecAllowlistEntry { Pattern = "**/git.exe" },
                    new ExecAllowlistEntry { Id = identifiedId, Pattern = "**/git.exe" },
                ])));
        harness.ViewModel.Activate(null);
        var removalToken = Assert.Single(harness.ViewModel.ExecApprovalRules, rule => rule.Id is null);

        Assert.True(await harness.ViewModel.RemoveExecApprovalRuleAsync(removalToken));

        var remaining = Assert.Single(
            harness.RecordingExecStore!.CurrentSnapshot.File.Agents!["main"].Allowlist!);
        Assert.Equal(identifiedId, remaining.Id);
    }

    [Fact]
    public async Task AddPathOnlyRule_PreservesBoundRuleWithSamePattern()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(BuildSnapshot(
            "base",
            BuildFile(
                defaultAction: "prompt",
                allowlist:
                [
                    new ExecAllowlistEntry
                    {
                        Id = Guid.NewGuid(),
                        Pattern = "**/git.exe",
                        ArgPattern = "^status$",
                    },
                ])));
        harness.ViewModel.Activate(null);

        Assert.True(await harness.ViewModel.TryAddExecApprovalRuleAsync("**/git.exe"));

        var remaining = harness.RecordingExecStore!.CurrentSnapshot.File.Agents!["main"].Allowlist!;
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, rule => rule.ArgPattern == "^status$");
        Assert.Contains(remaining, rule => rule.ArgPattern is null);
    }

    [Fact]
    public async Task RemoveIdlessBoundRule_PreservesPathOnlyRuleWithSamePattern()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(BuildSnapshot(
            "base",
            BuildFile(
                defaultAction: "prompt",
                allowlist:
                [
                    new ExecAllowlistEntry { Pattern = "**/git.exe" },
                    new ExecAllowlistEntry { Pattern = "**/git.exe", ArgPattern = "^status$" },
                ])));
        harness.ViewModel.Activate(null);
        var removalToken = Assert.Single(
            harness.ViewModel.ExecApprovalRules,
            rule => rule.ArgPattern == "^status$");

        Assert.True(await harness.ViewModel.RemoveExecApprovalRuleAsync(removalToken));

        var remaining = Assert.Single(
            harness.RecordingExecStore!.CurrentSnapshot.File.Agents!["main"].Allowlist!);
        Assert.Equal("**/git.exe", remaining.Pattern);
        Assert.Null(remaining.ArgPattern);
    }

    [Fact]
    public async Task DefaultActionMutation_PreservesExplicitAutoAllowSkills()
    {
        var initial = BuildFile(defaultAction: "deny");
        initial.Agents!["main"].AutoAllowSkills = true;
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", initial));
        harness.ViewModel.Activate(null);

        Assert.True(await harness.ViewModel.SetDefaultExecActionAsync("allow"));

        var saved = harness.RecordingExecStore!.CurrentSnapshot.File;
        Assert.False(saved.Defaults!.AutoAllowSkills);
        Assert.True(saved.Agents!["main"].AutoAllowSkills);
    }

    [Fact]
    public async Task DefaultActionMutation_PreservesDynamicDefaultAutoAllowSkillsInheritance()
    {
        var initial = BuildFile(defaultAction: "deny", autoAllowSkills: true);
        initial.Agents!["main"].AutoAllowSkills = null;
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);
        var current = await harness.RealExecStore!.GetSnapshotReadOnlyAsync();
        Assert.NotNull(await harness.RealExecStore.ReplaceAsync(current.Snapshot!.Hash, initial, origin: null));

        Assert.True(await harness.ViewModel.SetDefaultExecActionAsync("allow"));

        var savedResult = await harness.RealExecStore.GetSnapshotReadOnlyAsync();
        var saved = savedResult.Snapshot!.File;
        Assert.True(saved.Defaults!.AutoAllowSkills);
        Assert.Null(saved.Agents!["main"].AutoAllowSkills);
        Assert.True(harness.RealExecStore.ResolveReadOnly("main").Defaults.AutoAllowSkills);

        saved.Defaults.AutoAllowSkills = false;
        Assert.NotNull(await harness.RealExecStore.ReplaceAsync(savedResult.Snapshot.Hash, saved, origin: null));
        Assert.False(harness.RealExecStore.ResolveReadOnly("main").Defaults.AutoAllowSkills);
    }

    [Fact]
    public async Task DefaultActionMutation_PreservesDynamicWildcardAutoAllowSkillsInheritance()
    {
        var initial = BuildFile(defaultAction: "deny");
        initial.Agents!["main"].AutoAllowSkills = null;
        initial.Agents["*"] = new ExecApprovalsAgent { AutoAllowSkills = true };
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);
        var current = await harness.RealExecStore!.GetSnapshotReadOnlyAsync();
        Assert.NotNull(await harness.RealExecStore.ReplaceAsync(current.Snapshot!.Hash, initial, origin: null));

        Assert.True(await harness.ViewModel.SetDefaultExecActionAsync("allow"));

        var savedResult = await harness.RealExecStore.GetSnapshotReadOnlyAsync();
        var saved = savedResult.Snapshot!.File;
        Assert.False(saved.Defaults!.AutoAllowSkills);
        Assert.Null(saved.Agents!["main"].AutoAllowSkills);
        Assert.True(saved.Agents["*"].AutoAllowSkills);
        Assert.True(harness.RealExecStore.ResolveReadOnly("main").Defaults.AutoAllowSkills);

        saved.Agents["*"].AutoAllowSkills = false;
        Assert.NotNull(await harness.RealExecStore.ReplaceAsync(savedResult.Snapshot.Hash, saved, origin: null));
        Assert.False(harness.RealExecStore.ResolveReadOnly("main").Defaults.AutoAllowSkills);
    }

    [Fact]
    public async Task AddRuleFromDeny_PreservesExplicitAutoAllowSkills()
    {
        var initial = BuildFile(
            defaultAction: "deny",
            allowlist: [new ExecAllowlistEntry { Pattern = "**/rg.exe" }]);
        initial.Defaults!.Security = ExecSecurity.Deny;
        initial.Agents!["main"].Security = ExecSecurity.Deny;
        initial.Agents["main"].AutoAllowSkills = true;
        using var harness = PermissionsHarness.CreateWithRecordingStore(BuildSnapshot("base", initial));
        harness.ViewModel.Activate(null);

        Assert.True(await harness.ViewModel.TryAddExecApprovalRuleAsync("**/git.exe"));

        var saved = harness.RecordingExecStore!.CurrentSnapshot.File;
        Assert.False(saved.Defaults!.AutoAllowSkills);
        Assert.True(saved.Agents!["main"].AutoAllowSkills);
        Assert.Equal(ExecSecurity.Allowlist, saved.Agents["main"].Security);
    }

    [Fact]
    public async Task AddRuleFromDeny_PreservesDynamicDefaultAutoAllowSkillsInheritance()
    {
        var initial = BuildFile(
            defaultAction: "deny",
            allowlist: [new ExecAllowlistEntry { Pattern = "**/rg.exe" }],
            autoAllowSkills: true);
        initial.Defaults!.Security = ExecSecurity.Deny;
        initial.Agents!["main"].Security = ExecSecurity.Deny;
        initial.Agents["main"].AutoAllowSkills = null;
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);
        var current = await harness.RealExecStore!.GetSnapshotReadOnlyAsync();
        Assert.NotNull(await harness.RealExecStore.ReplaceAsync(current.Snapshot!.Hash, initial, origin: null));

        Assert.True(await harness.ViewModel.TryAddExecApprovalRuleAsync("**/git.exe"));

        var savedResult = await harness.RealExecStore.GetSnapshotReadOnlyAsync();
        var saved = savedResult.Snapshot!.File;
        Assert.True(saved.Defaults!.AutoAllowSkills);
        Assert.Null(saved.Agents!["main"].AutoAllowSkills);
        Assert.True(harness.RealExecStore.ResolveReadOnly("main").Defaults.AutoAllowSkills);

        saved.Defaults.AutoAllowSkills = false;
        Assert.NotNull(await harness.RealExecStore.ReplaceAsync(savedResult.Snapshot.Hash, saved, origin: null));
        Assert.False(harness.RealExecStore.ResolveReadOnly("main").Defaults.AutoAllowSkills);
    }

    [Fact]
    public async Task RemoveRuleFromAbsoluteDeny_PreservesDenyAndDormantRules()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var initial = BuildFile(
            defaultAction: "deny",
            allowlist:
            [
                new ExecAllowlistEntry { Id = firstId, Pattern = "**/git.exe" },
                new ExecAllowlistEntry { Id = secondId, Pattern = "**/rg.exe" },
            ]);
        initial.Defaults!.Security = ExecSecurity.Deny;
        initial.Agents!["main"].Security = ExecSecurity.Deny;
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);
        var current = await harness.RealExecStore!.GetSnapshotReadOnlyAsync();
        var seeded = await harness.RealExecStore.ReplaceAsync(current.Snapshot!.Hash, initial, origin: null);
        Assert.NotNull(seeded);
        Assert.Equal(2, harness.ViewModel.ExecApprovalRules.Count);

        Assert.True(await harness.ViewModel.RemoveExecApprovalRuleAsync(
            harness.ViewModel.ExecApprovalRules[0]));

        var savedResult = await harness.RealExecStore.GetSnapshotReadOnlyAsync();
        var saved = savedResult.Snapshot!.File;
        Assert.Equal(ExecSecurity.Deny, saved.Defaults!.Security);
        Assert.Equal(ExecSecurity.Deny, saved.Agents!["main"].Security);
        var remaining = Assert.Single(saved.Agents["main"].Allowlist!);
        Assert.Equal(secondId, remaining.Id);
        Assert.Equal("**/rg.exe", remaining.Pattern);
        Assert.Equal(ExecSecurity.Deny, harness.RealExecStore.ResolveReadOnly("main").Defaults.Security);
    }

    [Fact]
    public async Task DefaultActionSaveFailure_RetainsPersistedPresentation()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", BuildFile(defaultAction: "prompt")));
        harness.ViewModel.Activate(null);
        harness.RecordingExecStore!.OnReplace = _ => throw new IOException("write blocked");

        Assert.False(await harness.ViewModel.SetDefaultExecActionAsync("deny"));

        Assert.Equal("prompt", harness.ViewModel.DefaultExecActionTag);
        Assert.Equal(ExecSecurity.Allowlist, harness.RecordingExecStore.CurrentSnapshot.File.Defaults!.Security);
        Assert.Equal(ExecAsk.OnMiss, harness.RecordingExecStore.CurrentSnapshot.File.Defaults.Ask);
        Assert.Equal(PermissionsExecApprovalsStatus.SaveFailed, harness.ViewModel.ExecApprovalsStatus);
    }

    [Fact]
    public async Task ConcurrentMutations_CommitInInvocationOrder_AndPreserveBothEdits()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", BuildFile(defaultAction: "deny")));
        harness.ViewModel.Activate(null);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.RecordingExecStore!.OnReplaceAsync = async call =>
        {
            if (call.Attempt == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            }

            return harness.RecordingExecStore.Commit(call.Replacement, $"saved-{call.Attempt}");
        };

        var first = harness.ViewModel.SetDefaultExecActionAsync("prompt");
        await firstEntered.Task;
        var second = harness.ViewModel.TryAddExecApprovalRuleAsync("**/git.exe");
        await Task.Delay(50);

        Assert.Single(harness.RecordingExecStore.ReplaceCalls);
        releaseFirst.SetResult();
        Assert.True(await first);
        Assert.True(await second);

        Assert.Equal(2, harness.RecordingExecStore.ReplaceCalls.Count);
        var saved = harness.RecordingExecStore.CurrentSnapshot.File;
        Assert.Equal(ExecSecurity.Allowlist, saved.Defaults!.Security);
        Assert.Equal(ExecAsk.OnMiss, saved.Defaults.Ask);
        Assert.Equal("**/git.exe", Assert.Single(saved.Agents!["main"].Allowlist!).Pattern);
    }

    [Fact]
    public async Task DelayedActivationRefresh_DoesNotOverwriteNewerMutation()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", BuildFile(defaultAction: "prompt")));
        var initialReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        harness.RecordingExecStore!.OnGetSnapshotAsync = async () =>
        {
            var captured = harness.RecordingExecStore.CreateReadResult();
            if (Interlocked.Increment(ref readCount) == 1)
            {
                initialReadStarted.SetResult();
                await releaseInitialRead.Task;
            }

            return captured;
        };

        harness.ViewModel.Activate(null);
        await initialReadStarted.Task;
        Assert.True(await harness.ViewModel.SetDefaultExecActionAsync("allow"));

        releaseInitialRead.SetResult();
        await Task.Delay(50);

        Assert.Equal("allow", harness.ViewModel.DefaultExecActionTag);
        Assert.Equal(ExecSecurity.Full, harness.RecordingExecStore.CurrentSnapshot.File.Defaults!.Security);
    }

    [Fact]
    public async Task AddRuleFromMissingPolicy_PreservesFreshV2Defaults()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);

        Assert.True(await harness.ViewModel.TryAddExecApprovalRuleAsync("**/git.exe"));

        var saved = await harness.RealExecStore!.GetSnapshotReadOnlyAsync();
        Assert.Equal(ExecSecurity.Allowlist, saved.Snapshot!.File.Defaults!.Security);
        Assert.Equal(ExecAsk.OnMiss, saved.Snapshot.File.Defaults.Ask);
        Assert.Equal("**/git.exe", Assert.Single(saved.Snapshot.File.Agents!["main"].Allowlist!).Pattern);
        var resolved = harness.RealExecStore.ResolveReadOnly("main");
        Assert.Equal(ExecSecurity.Allowlist, resolved.Defaults.Security);
        Assert.Equal(ExecAsk.OnMiss, resolved.Defaults.Ask);
    }

    [Fact]
    public async Task InvalidPattern_DoesNotWrite()
    {
        using var harness = PermissionsHarness.CreateReal();
        harness.ViewModel.Activate(null);

        Assert.False(ExecApprovalsStore.IsValidAllowlistPattern("["));
        Assert.False(await harness.ViewModel.TryAddExecApprovalRuleAsync("["));
        Assert.False(File.Exists(harness.ExecApprovalsPath));
    }

    [Fact]
    public async Task CasConflict_RetriesAgainstFreshSnapshots_AndPreservesUnrelatedFields()
    {
        var initial = BuildSnapshot(
            hash: "base",
            file: BuildFile(
                defaultAction: "deny",
                socketToken: "socket-1",
                otherAgentPath: "**/rg.exe"));
        using var harness = PermissionsHarness.CreateWithRecordingStore(initial);
        harness.ViewModel.Activate(null);

        harness.RecordingExecStore!.OnReplace = call =>
        {
            if (call.Attempt == 1)
            {
                var freshFile = BuildFile(
                    defaultAction: "deny",
                    socketToken: "socket-2",
                    otherAgentPath: "**/rg.exe");
                var boundEntry = freshFile.Agents!["other"].Allowlist![0];
                boundEntry.ArgPattern = "^--files\u0000\u0000$";
                boundEntry.CommandText = "rg.exe --files";
                boundEntry.Source = "allow-always";
                boundEntry.LastUsedCommand = "rg.exe --files";
                harness.RecordingExecStore.ReplaceCurrentSnapshot(BuildSnapshot(
                    hash: "fresh",
                    file: freshFile));
                return null;
            }

            return harness.RecordingExecStore.Commit(call.Replacement, "saved");
        };

        Assert.True(await harness.ViewModel.TryAddExecApprovalRuleAsync("**/git.exe"));

        var saved = harness.RecordingExecStore.CurrentSnapshot;
        Assert.Equal("socket-2", saved.File.Socket!.Token);
        var preservedEntry = saved.File.Agents!["other"].Allowlist![0];
        Assert.Equal("**/rg.exe", preservedEntry.Pattern);
        Assert.Equal("^--files\u0000\u0000$", preservedEntry.ArgPattern);
        Assert.Equal("rg.exe --files", preservedEntry.CommandText);
        Assert.Equal("allow-always", preservedEntry.Source);
        Assert.Equal("rg.exe --files", preservedEntry.LastUsedCommand);
        Assert.Equal("**/git.exe", saved.File.Agents!["main"].Allowlist![0].Pattern);
        Assert.Equal(2, harness.RecordingExecStore.ReplaceCalls.Count);
    }

    [Fact]
    public async Task RemoveRule_CasRetry_DoesNotDeleteConcurrentReplacementWithSamePattern()
    {
        var originalId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        using var harness = PermissionsHarness.CreateWithRecordingStore(BuildSnapshot(
            hash: "base",
            file: BuildFile(
                defaultAction: "prompt",
                allowlist: [new ExecAllowlistEntry { Id = originalId, Pattern = "**/git.exe" }])));
        harness.ViewModel.Activate(null);
        harness.RecordingExecStore!.OnReplace = call =>
        {
            if (call.Attempt == 1)
            {
                harness.RecordingExecStore.ReplaceCurrentSnapshot(BuildSnapshot(
                    hash: "fresh",
                    file: BuildFile(
                        defaultAction: "prompt",
                        allowlist: [new ExecAllowlistEntry { Id = replacementId, Pattern = "**/git.exe" }])));
                return null;
            }

            return harness.RecordingExecStore.Commit(call.Replacement, "saved");
        };

        var removalToken = Assert.Single(harness.ViewModel.ExecApprovalRules);
        Assert.True(await harness.ViewModel.RemoveExecApprovalRuleAsync(removalToken));

        var saved = harness.RecordingExecStore.CurrentSnapshot.File;
        var remaining = Assert.Single(saved.Agents!["main"].Allowlist!);
        Assert.Equal(replacementId, remaining.Id);
        Assert.Equal("**/git.exe", remaining.Pattern);
        Assert.Equal(2, harness.RecordingExecStore.ReplaceCalls.Count);
    }

    [Fact]
    public async Task MutationCompletion_AfterExternalChange_RefreshesWithoutStaleSavedState()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", BuildFile(defaultAction: "prompt")));
        harness.ViewModel.Activate(null);
        var mutationCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.RecordingExecStore!.OnReplaceAsync = async call =>
        {
            var committed = harness.RecordingExecStore.Commit(call.Replacement, "mutation");
            mutationCommitted.SetResult();
            await releaseMutation.Task;
            return committed;
        };

        var mutation = harness.ViewModel.SetDefaultExecActionAsync("allow");
        await mutationCommitted.Task;
        harness.RecordingExecStore.RaiseExternalSnapshot(BuildSnapshot(
            "external",
            BuildFile(defaultAction: "deny")));
        releaseMutation.SetResult();

        Assert.True(await mutation);
        Assert.Equal("deny", harness.ViewModel.DefaultExecActionTag);
        Assert.NotEqual(PermissionsExecApprovalsStatus.Saved, harness.ViewModel.ExecApprovalsStatus);
    }

    [Fact]
    public async Task MutationCompletion_AfterDeactivate_DoesNotUpdatePresentation()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", BuildFile(defaultAction: "prompt")));
        harness.ViewModel.Activate(null);
        var mutationCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.RecordingExecStore!.OnReplaceAsync = async call =>
        {
            var committed = harness.RecordingExecStore.Commit(call.Replacement, "mutation");
            mutationCommitted.SetResult();
            await releaseMutation.Task;
            return committed;
        };

        var mutation = harness.ViewModel.SetDefaultExecActionAsync("allow");
        await mutationCommitted.Task;
        harness.ViewModel.Deactivate();
        releaseMutation.SetResult();

        Assert.True(await mutation);
        Assert.Equal("prompt", harness.ViewModel.DefaultExecActionTag);
        Assert.NotEqual(PermissionsExecApprovalsStatus.Saved, harness.ViewModel.ExecApprovalsStatus);
    }

    [Fact]
    public async Task ExecApprovalsChanges_RejectOlderSequenceAfterNewerOwnOriginEvent()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", BuildFile(defaultAction: "deny")));
        harness.ViewModel.Activate(null);
        harness.RecordingExecStore!.OnReplace = call =>
        {
            var committed = harness.RecordingExecStore.Commit(call.Replacement, "own");
            harness.RecordingExecStore.RaiseSnapshot(committed, sequence: 2, origin: call.Origin);
            return committed;
        };

        Assert.True(await harness.ViewModel.SetDefaultExecActionAsync("prompt"));
        harness.RecordingExecStore.RaiseSnapshot(
            BuildSnapshot("older-external", BuildFile(defaultAction: "allow")),
            sequence: 1,
            origin: null);

        Assert.Equal("prompt", harness.ViewModel.DefaultExecActionTag);
        Assert.Equal(PermissionsExecApprovalsStatus.Saved, harness.ViewModel.ExecApprovalsStatus);
    }

    [Fact]
    public async Task ExecApprovalsChanges_QueuedOlderExternalDoesNotApplyAfterNewerOwnOriginEvent()
    {
        using var harness = PermissionsHarness.CreateWithRecordingStore(
            BuildSnapshot("base", BuildFile(defaultAction: "deny")));
        harness.ViewModel.Activate(null);
        harness.Dispatcher.HasThreadAccess = false;
        harness.Dispatcher.RunEnqueuedImmediately = false;
        var defaultActionChanges = 0;
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PermissionsPageViewModel.DefaultExecActionTag))
            {
                defaultActionChanges++;
            }
        };

        harness.RecordingExecStore!.RaiseSnapshot(
            BuildSnapshot("older-external", BuildFile(defaultAction: "allow")),
            sequence: 1,
            origin: null);
        harness.RecordingExecStore.OnReplace = call =>
        {
            var committed = harness.RecordingExecStore.Commit(call.Replacement, "own");
            harness.RecordingExecStore.RaiseSnapshot(committed, sequence: 2, origin: call.Origin);
            return committed;
        };

        Assert.True(await harness.ViewModel.SetDefaultExecActionAsync("prompt"));
        Assert.Equal("deny", harness.ViewModel.DefaultExecActionTag);

        harness.Dispatcher.FlushPending();

        Assert.Equal("prompt", harness.ViewModel.DefaultExecActionTag);
        Assert.Equal(1, defaultActionChanges);
        Assert.Equal(PermissionsExecApprovalsStatus.Saved, harness.ViewModel.ExecApprovalsStatus);
    }

    [Fact]
    public void ExternalValidChange_UpdatesOnce_AndCorruptRetainsLastValidDisplay()
    {
        var initial = BuildSnapshot(
            hash: "initial",
            file: BuildFile(defaultAction: "deny"));
        using var harness = PermissionsHarness.CreateWithRecordingStore(initial);
        harness.ViewModel.Activate(null);

        var propertyChanges = 0;
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PermissionsPageViewModel.DefaultExecActionTag))
            {
                propertyChanges++;
            }
        };

        harness.RecordingExecStore!.RaiseExternalSnapshot(BuildSnapshot(
            hash: "valid-1",
            file: BuildFile(defaultAction: "prompt", allowlist: [new ExecAllowlistEntry { Pattern = "**/git.exe" }])));

        Assert.Equal(1, propertyChanges);
        Assert.Equal("prompt", harness.ViewModel.DefaultExecActionTag);
        Assert.Single(harness.ViewModel.ExecApprovalRules);

        harness.RecordingExecStore.RaiseExternalInvalid(
            new ExecApprovalsSnapshotFailure(
                ExecApprovalsSnapshotFailureKind.MalformedJson,
                "bad-hash",
                null,
                "malformed"),
            harness.RecordingExecStore.CurrentSnapshot);

        Assert.Equal("prompt", harness.ViewModel.DefaultExecActionTag);
        Assert.Single(harness.ViewModel.ExecApprovalRules);
        Assert.Equal(ExecApprovalsSnapshotFailureKind.MalformedJson, harness.ViewModel.ExecApprovalsFailure!.Kind);
        Assert.Equal(PermissionsExecApprovalsStatus.ExternalInvalid, harness.ViewModel.ExecApprovalsStatus);
    }

    private static ExecApprovalsSnapshot BuildSnapshot(string hash, ExecApprovalsFile file) =>
        new("D:\\exec-approvals.json", true, hash, file);

    private static void SetCapability(ISettingsEditor edit, PermissionsCapabilityKey key, bool value)
    {
        switch (key)
        {
            case PermissionsCapabilityKey.SystemRun:
                edit.NodeSystemRunEnabled = value;
                break;
            case PermissionsCapabilityKey.BrowserProxy:
                edit.NodeBrowserProxyEnabled = value;
                break;
            case PermissionsCapabilityKey.Camera:
                edit.NodeCameraEnabled = value;
                break;
            case PermissionsCapabilityKey.Canvas:
                edit.NodeCanvasEnabled = value;
                break;
            case PermissionsCapabilityKey.Screen:
                edit.NodeScreenEnabled = value;
                break;
            case PermissionsCapabilityKey.Location:
                edit.NodeLocationEnabled = value;
                break;
            case PermissionsCapabilityKey.TextToSpeech:
                edit.NodeTtsEnabled = value;
                break;
            case PermissionsCapabilityKey.SpeechToText:
                edit.NodeSttEnabled = value;
                break;
        }
    }

    private static ExecApprovalsFile BuildFile(
        string defaultAction,
        IEnumerable<ExecAllowlistEntry>? allowlist = null,
        string? socketToken = null,
        string? otherAgentPath = null,
        bool autoAllowSkills = false)
    {
        var (security, ask) = defaultAction switch
        {
            "allow" => (ExecSecurity.Full, ExecAsk.Off),
            "prompt" => (ExecSecurity.Allowlist, ExecAsk.OnMiss),
            _ => (ExecSecurity.Allowlist, ExecAsk.Off),
        };

        return new ExecApprovalsFile
        {
            Version = 1,
            Socket = socketToken is null ? null : new ExecApprovalsSocketConfig { Token = socketToken, Path = "socket" },
            Defaults = new ExecApprovalsDefaults
            {
                Security = security,
                Ask = ask,
                AskFallback = ExecSecurity.Deny,
                AutoAllowSkills = autoAllowSkills,
            },
            Agents = new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal)
            {
                ["main"] = new ExecApprovalsAgent
                {
                    Security = security,
                    Ask = ask,
                    AskFallback = ExecSecurity.Deny,
                    AutoAllowSkills = autoAllowSkills,
                    Allowlist = allowlist?.ToList() ?? [],
                },
                ["other"] = new ExecApprovalsAgent
                {
                    Allowlist = otherAgentPath is null ? [] : [new ExecAllowlistEntry { Pattern = otherAgentPath }],
                },
            },
        };
    }

    private sealed class PermissionsHarness : IDisposable
    {
        private PermissionsHarness(
            TempDir temp,
            SettingsManager settings,
            SettingsStore settingsStore,
            FakeAppCommands commands,
            RecordingUiDispatcher dispatcher,
            FakePermissionsPageRuntimeHost runtimeHost,
            IPermissionsPageRuntimeSource runtimeSource,
            PermissionsPageViewModel viewModel,
            IExecApprovalsPresentationStore execApprovalsStore)
        {
            Temp = temp;
            Settings = settings;
            SettingsStore = settingsStore;
            Commands = commands;
            Dispatcher = dispatcher;
            RuntimeHost = runtimeHost;
            RuntimeSource = runtimeSource;
            ViewModel = viewModel;
            ExecApprovalsStore = execApprovalsStore;
            ExecApprovalsPath = OpenClaw.Shared.ExecApprovals.ExecApprovalsStore.ResolveFilePath(temp.Path);
        }

        public TempDir Temp { get; }
        public SettingsManager Settings { get; }
        public SettingsStore SettingsStore { get; }
        public FakeAppCommands Commands { get; }
        public RecordingUiDispatcher Dispatcher { get; }
        public FakePermissionsPageRuntimeHost RuntimeHost { get; }
        public IPermissionsPageRuntimeSource RuntimeSource { get; }
        public PermissionsPageViewModel ViewModel { get; }
        public IExecApprovalsPresentationStore ExecApprovalsStore { get; }
        public string ExecApprovalsPath { get; }
        public ExecApprovalsStore? RealExecStore => ExecApprovalsStore as ExecApprovalsStore;
        public RecordingExecApprovalsStore? RecordingExecStore => ExecApprovalsStore as RecordingExecApprovalsStore;

        public static PermissionsHarness CreateReal()
        {
            var temp = new TempDir();
            var settings = new SettingsManager(temp.Path);
            var commands = new FakeAppCommands();
            var dispatcher = new RecordingUiDispatcher();
            var settingsStore = new SettingsStore(settings, dispatcher);
            var runtimeHost = new FakePermissionsPageRuntimeHost();
            var runtimeSource = new PermissionsPageRuntimeSource(runtimeHost);
            var execStore = new ExecApprovalsStore(temp.Path, NullLogger.Instance);
            var viewModel = new PermissionsPageViewModel(settingsStore, execStore, commands, dispatcher, runtimeSource);
            return new PermissionsHarness(temp, settings, settingsStore, commands, dispatcher, runtimeHost, runtimeSource, viewModel, execStore);
        }

        public static PermissionsHarness CreateWithRecordingStore(ExecApprovalsSnapshot? initial = null)
        {
            var temp = new TempDir();
            var settings = new SettingsManager(temp.Path);
            var dispatcher = new RecordingUiDispatcher();
            var settingsStore = new SettingsStore(settings, dispatcher);
            var commands = new FakeAppCommands();
            var runtimeHost = new FakePermissionsPageRuntimeHost();
            var runtimeSource = new PermissionsPageRuntimeSource(runtimeHost);
            var execStore = new RecordingExecApprovalsStore(initial);
            var viewModel = new PermissionsPageViewModel(settingsStore, execStore, commands, dispatcher, runtimeSource);
            return new PermissionsHarness(temp, settings, settingsStore, commands, dispatcher, runtimeHost, runtimeSource, viewModel, execStore);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            SettingsStore.Dispose();
            if (ExecApprovalsStore is IDisposable disposableExecStore)
            {
                disposableExecStore.Dispose();
            }
            if (RuntimeSource is IDisposable disposableRuntimeSource)
            {
                disposableRuntimeSource.Dispose();
            }
            Temp.Dispose();
        }
    }

    private sealed class RecordingExecApprovalsStore : IExecApprovalsPresentationStore
    {
        private static readonly ConstructorInfo OriginCtor =
            typeof(ExecApprovalsWriterOrigin).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null)!;

        private ExecApprovalsSnapshot _current;
        private ExecApprovalsSnapshot? _lastValid;
        private long _nextSequence;

        public RecordingExecApprovalsStore(ExecApprovalsSnapshot? initial = null)
        {
            _current = initial ?? BuildSnapshot("initial", BuildFile(defaultAction: "deny"));
            _lastValid = _current;
        }

        public event EventHandler<ExecApprovalsChangedEventArgs>? Changed;

        public List<ReplaceCall> ReplaceCalls { get; } = new();
        public Func<ReplaceCall, ExecApprovalsSnapshot?>? OnReplace { get; set; }
        public Func<ReplaceCall, Task<ExecApprovalsSnapshot?>>? OnReplaceAsync { get; set; }
        public Func<Task<ExecApprovalsReadOnlySnapshotResult>>? OnGetSnapshotAsync { get; set; }
        public ExecApprovalsSnapshot CurrentSnapshot => _current;

        public Task<ExecApprovalsReadOnlySnapshotResult> GetSnapshotReadOnlyAsync(CancellationToken cancellationToken = default) =>
            OnGetSnapshotAsync?.Invoke() ?? Task.FromResult(CreateReadResult());

        public ExecApprovalsReadOnlySnapshotResult CreateReadResult() =>
            new(CloneSnapshot(_current), null, null);

        public ExecApprovalsWriterOrigin CreateWriterOrigin() =>
            (ExecApprovalsWriterOrigin)OriginCtor.Invoke(null);

        public async Task<ExecApprovalsSnapshot?> ReplaceAsync(
            string baseHash,
            ExecApprovalsFile replacement,
            ExecApprovalsWriterOrigin? origin,
            Func<ExecApprovalsFile, ExecApprovalsFile, string?>? deltaValidator = null)
        {
            var validationError = deltaValidator?.Invoke(_current.File, replacement);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                throw new IOException(validationError);
            }

            var call = new ReplaceCall(ReplaceCalls.Count + 1, baseHash, CloneFile(replacement), origin);
            ReplaceCalls.Add(call);
            var currentHashBeforeCallback = _current.Hash;
            var result = OnReplaceAsync is not null
                ? await OnReplaceAsync(call)
                : OnReplace?.Invoke(call);
            if (result is not null)
            {
                if (string.Equals(_current.Hash, currentHashBeforeCallback, StringComparison.Ordinal)
                    || string.Equals(_current.Hash, result.Hash, StringComparison.Ordinal))
                {
                    _current = CloneSnapshot(result);
                    _lastValid = _current;
                }

                return CloneSnapshot(result);
            }

            if (!string.Equals(baseHash, _current.Hash, StringComparison.Ordinal))
            {
                return null;
            }

            return Commit(replacement, $"hash-{ReplaceCalls.Count}");
        }

        public ExecApprovalsSnapshot Commit(ExecApprovalsFile replacement, string hash)
        {
            _current = new ExecApprovalsSnapshot(_current.Path, true, hash, CloneFile(replacement));
            _lastValid = _current;
            return CloneSnapshot(_current);
        }

        public void ReplaceCurrentSnapshot(ExecApprovalsSnapshot snapshot)
        {
            _current = CloneSnapshot(snapshot);
            _lastValid = _current;
        }

        public void RaiseExternalSnapshot(ExecApprovalsSnapshot snapshot)
            => RaiseSnapshot(snapshot, Interlocked.Increment(ref _nextSequence), origin: null);

        public void RaiseSnapshot(
            ExecApprovalsSnapshot snapshot,
            long sequence,
            ExecApprovalsWriterOrigin? origin)
        {
            _current = CloneSnapshot(snapshot);
            _lastValid = _current;
            Changed?.Invoke(this, new ExecApprovalsChangedEventArgs(
                sequence,
                ExecApprovalsChangeKind.SnapshotUpdated,
                snapshot.Hash,
                snapshot.File.Version,
                CloneSnapshot(snapshot),
                failure: null,
                lastValidSnapshot: null,
                origin));
        }

        public void RaiseExternalInvalid(ExecApprovalsSnapshotFailure failure, ExecApprovalsSnapshot? lastValidSnapshot)
        {
            Changed?.Invoke(this, new ExecApprovalsChangedEventArgs(
                Interlocked.Increment(ref _nextSequence),
                ExecApprovalsChangeKind.SnapshotInvalid,
                failure.Hash,
                failure.Version,
                snapshot: null,
                failure,
                lastValidSnapshot is null ? null : CloneSnapshot(lastValidSnapshot),
                origin: null));
        }

        private static ExecApprovalsSnapshot CloneSnapshot(ExecApprovalsSnapshot snapshot) =>
            new(snapshot.Path, snapshot.Exists, snapshot.Hash, CloneFile(snapshot.File));

        private static ExecApprovalsFile CloneFile(ExecApprovalsFile file) =>
            new()
            {
                Version = file.Version,
                Socket = file.Socket is null ? null : new ExecApprovalsSocketConfig
                {
                    Path = file.Socket.Path,
                    Token = file.Socket.Token,
                },
                Defaults = file.Defaults is null ? null : new ExecApprovalsDefaults
                {
                    Security = file.Defaults.Security,
                    Ask = file.Defaults.Ask,
                    AskFallback = file.Defaults.AskFallback,
                    AutoAllowSkills = file.Defaults.AutoAllowSkills,
                },
                Agents = file.Agents?.ToDictionary(
                    pair => pair.Key,
                    pair => new ExecApprovalsAgent
                    {
                        Security = pair.Value.Security,
                        Ask = pair.Value.Ask,
                        AskFallback = pair.Value.AskFallback,
                        AutoAllowSkills = pair.Value.AutoAllowSkills,
                        Allowlist = pair.Value.Allowlist?.Select(entry => new ExecAllowlistEntry
                        {
                            Id = entry.Id,
                            Pattern = entry.Pattern,
                            ArgPattern = entry.ArgPattern,
                            CommandText = entry.CommandText,
                            Source = entry.Source,
                            LastUsedAt = entry.LastUsedAt,
                            LastResolvedPath = entry.LastResolvedPath,
                            LastUsedCommand = entry.LastUsedCommand,
                        }).ToList(),
                    },
                    StringComparer.Ordinal),
            };
    }

    private sealed record ReplaceCall(
        int Attempt,
        string BaseHash,
        ExecApprovalsFile Replacement,
        ExecApprovalsWriterOrigin? Origin);
}
