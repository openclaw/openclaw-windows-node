using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class WorkspacePageContractTests
{
    [Fact]
    public void WorkspacePage_DoesNotUseLocalFilesystemOrLogOpaquePaths()
    {
        foreach (var source in new[]
                 {
                     Read("src", "OpenClaw.Tray.WinUI", "Pages", "WorkspacePage.xaml.cs"),
                     Read("src", "OpenClaw.Tray.WinUI", "Pages", "WorkspaceFilesModel.cs"),
                     Read("src", "OpenClaw.Tray.WinUI", "Services", "WorkspaceGatewayCoordinator.cs")
                 })
        {
            foreach (var forbidden in new[]
                     {
                         "using System.IO", "File.", "Directory.", "System.IO.Path",
                         "ShellExecute", "Launcher.", "StorageFile", "StorageFolder"
                     })
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(
                source.Split('\n'),
                line => line.Contains("Logger", StringComparison.Ordinal) &&
                        line.Contains("RequestPath", StringComparison.Ordinal));
        }

        var pageSource = Read("src", "OpenClaw.Tray.WinUI", "Pages", "WorkspacePage.xaml.cs");
        Assert.Contains("_browserPath = path ?? string.Empty;", pageSource);
    }

    [Fact]
    public void HubWindow_SetsSelectedAgentIdBeforeWorkspaceInitialization()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        var workspaceCase = source.IndexOf("case WorkspacePage workspace:", StringComparison.Ordinal);
        var agentAssignment = source.IndexOf("workspace.AgentId = _currentAgentId;", workspaceCase, StringComparison.Ordinal);
        var initialize = source.IndexOf("workspace.Initialize();", workspaceCase, StringComparison.Ordinal);

        Assert.True(workspaceCase >= 0);
        Assert.True(agentAssignment > workspaceCase);
        Assert.True(initialize > agentAssignment);
    }

    [Fact]
    public void WorkspacePage_ResolvesFallbackFromCurrentSessionAndClientSnapshots()
    {
        var source = Read("src", "OpenClaw.Tray.WinUI", "Pages", "WorkspacePage.xaml.cs");

        Assert.Contains("WorkspaceSessionResolver.Resolve(", source);
        Assert.Contains("_appState?.Sessions", source);
        Assert.Contains("CurrentApp.GatewayClient?.MainSessionKey", source);
        Assert.Contains("nameof(AppState.Sessions)", source);
        Assert.Contains("_sessionReloadGate.DependsOnSessionKey", source);
        Assert.Contains("_sessionReloadGate.ShouldReload(ResolveSessionKey())", source);
        Assert.DoesNotContain(
            """
            if (e.PropertyName == nameof(AppState.Sessions))
            {
                _ = LoadAsync();
            }
            """,
            source);
        Assert.DoesNotContain("$\"agent:{agentId}:main\"", source);
    }

    [Fact]
    public void WorkspacePage_AwaitsLegacyResponsesAndCancelsOnClientSwap()
    {
        var page = Read("src", "OpenClaw.Tray.WinUI", "Pages", "WorkspacePage.xaml.cs");
        var coordinator = Read(
            "src", "OpenClaw.Tray.WinUI", "Services", "WorkspaceGatewayCoordinator.cs");

        Assert.Contains("OperatorClientChanged += OnOperatorClientChanged", page);
        Assert.Contains("CancelAllRequests();", page);
        Assert.Contains("cancellationToken: cancellation.Token", page);
        Assert.DoesNotContain("TryGetCachedAgentFilesList", page);
        Assert.DoesNotContain("nameof(AppState.AgentFilesList)", page);
        Assert.DoesNotContain("nameof(AppState.AgentFileContent)", page);
        Assert.Contains("ApplyLegacyAgentFileContent(legacyPayload, entry)", page);
        Assert.Contains(
            "string.Equals(responsePath, requestedEntry.RequestPath, StringComparison.Ordinal)",
            page);
        Assert.Contains("ListLegacyAgentFilesAsync(", coordinator);
        Assert.Contains("GetLegacyAgentFileAsync(", coordinator);
    }

    [Fact]
    public void WorkspaceSourceAndPreviewCopy_IsLocalizedInEveryLocale()
    {
        var expectedByLocale = new Dictionary<string, string[]>
        {
            ["en-us"] = ["Agent workspace", "Showing files from this session.", "Showing managed agent files, not the full workspace.", "Image preview isn't supported here yet."],
            ["zh-cn"] = ["Agent 工作区", "正在显示此会话中的文件。", "正在显示托管的 Agent 文件，而不是完整工作区。", "此处尚不支持图像预览。"],
            ["zh-tw"] = ["Agent 工作區", "正在顯示此工作階段中的檔案。", "正在顯示受管理的 Agent 檔案，而不是完整工作區。", "此處尚不支援影像預覽。"],
            ["nl-nl"] = ["Agentwerkruimte", "Bestanden uit deze sessie worden weergegeven.", "Beheerde agentbestanden worden weergegeven, niet de volledige werkruimte.", "Afbeeldingsvoorbeeld wordt hier nog niet ondersteund."],
            ["fr-fr"] = ["Espace de travail de l’agent", "Affichage des fichiers de cette session.", "Affichage des fichiers d’agent gérés, et non de l’espace de travail complet.", "L’aperçu des images n’est pas encore pris en charge ici."]
        };
        var keys = new[]
        {
            "WorkspacePage_AgentWorkspaceLabel",
            "WorkspacePage_LimitedScopeMessage",
            "WorkspacePage_LegacyAgentFilesScopeMessage",
            "WorkspacePage_ImagePreviewUnsupported"
        };

        foreach (var (locale, expectedValues) in expectedByLocale)
        {
            var doc = XDocument.Load(RepositoryPath(
                "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            for (var index = 0; index < keys.Length; index++)
            {
                var resource = doc.Root!.Elements("data")
                    .Single(element => (string?)element.Attribute("name") == keys[index]);
                Assert.Equal(expectedValues[index], resource.Element("value")!.Value);
            }
        }
    }

    [Fact]
    public void WorkspacePage_MapsSourceTransitionsAndResetsScopeOnConnectionChanges()
    {
        var page = Read("src", "OpenClaw.Tray.WinUI", "Pages", "WorkspacePage.xaml.cs");

        Assert.Contains("ShowScopeDisclosure(_workspaceSource);", page);
        Assert.Contains(
            "ShowScopeDisclosure(WorkspaceGatewaySource.LegacyAgentFiles);",
            page);
        Assert.Contains("nameof(AppState.Status)", page);
        Assert.Contains("ReferenceEquals(e.NewClient, CurrentApp.GatewayClient)", page);
        Assert.Contains("ResetScopeDisclosure();", page);
        Assert.Contains("_workspaceSource = WorkspaceGatewaySource.AgentWorkspace;", page);
        Assert.Contains("HideFallback();", page);
        Assert.Contains("request.CanApply(source)", page);
        Assert.Contains("CompleteScopeRequest(ref _listScopeDisclosureRequest)", page);
        Assert.DoesNotContain("_fileScopeDisclosureRequest", page);
        Assert.Contains(
            "ScopeInfoBar describes the visible list source.",
            page);

        var fileLoadStart = page.IndexOf(
            "private async Task LoadFileAsync(",
            StringComparison.Ordinal);
        var nextMethod = page.IndexOf(
            "private void QueueScopeDisclosure(",
            fileLoadStart,
            StringComparison.Ordinal);
        var fileLoad = page[fileLoadStart..nextMethod];
        Assert.DoesNotContain("ShowScopeDisclosure(", fileLoad);
        Assert.DoesNotContain("QueueScopeDisclosure(", fileLoad);
        Assert.DoesNotContain("_workspaceSource =", fileLoad);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(RepositoryPath(parts));

    private static string RepositoryPath(params string[] parts) =>
        Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray());
}
