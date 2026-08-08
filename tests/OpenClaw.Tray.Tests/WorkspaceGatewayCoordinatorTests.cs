using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class WorkspaceGatewayCoordinatorTests
{
    [Fact]
    public void ScopeDisclosure_MapsPrimarySessionLegacyAndResetTransitions()
    {
        var transitions = new[]
        {
            WorkspaceGatewaySource.AgentWorkspace,
            WorkspaceGatewaySource.SessionFiles,
            WorkspaceGatewaySource.LegacyAgentFiles,
            WorkspaceGatewaySource.AgentWorkspace
        };

        Assert.Equal(
            new string?[]
            {
                null,
                "WorkspacePage_LimitedScopeMessage",
                "WorkspacePage_LegacyAgentFilesScopeMessage",
                null
            },
            transitions.Select(WorkspaceScopeDisclosure.ResourceKeyForList));
    }

    [Fact]
    public void ScopeDisclosureRequest_QueuedCallbackCannotApplyAfterTerminalCompletion()
    {
        var request = new WorkspaceScopeDisclosureRequest();
        var callbacks = new Queue<Action>();
        var applications = 0;

        Assert.True(request.TryQueue(WorkspaceGatewaySource.LegacyAgentFiles));
        Assert.False(request.CanApply(WorkspaceGatewaySource.SessionFiles));
        callbacks.Enqueue(() =>
        {
            if (request.CanApply(WorkspaceGatewaySource.LegacyAgentFiles))
                applications++;
        });

        callbacks.Dequeue().Invoke();
        Assert.Equal(1, applications);

        Assert.True(request.TryQueue(WorkspaceGatewaySource.LegacyAgentFiles));
        callbacks.Enqueue(() =>
        {
            if (request.CanApply(WorkspaceGatewaySource.LegacyAgentFiles))
                applications++;
        });
        request.Complete();
        callbacks.Dequeue().Invoke();

        Assert.Equal(1, applications);
        Assert.False(request.TryQueue(WorkspaceGatewaySource.LegacyAgentFiles));
        Assert.False(request.TryQueue(WorkspaceGatewaySource.Unsupported));
    }

    [Fact]
    public void ScopeDisclosureRequest_ListAndGetLifecyclesAreIndependent()
    {
        var list = new WorkspaceScopeDisclosureRequest();
        var get = new WorkspaceScopeDisclosureRequest();

        Assert.True(list.TryQueue(WorkspaceGatewaySource.LegacyAgentFiles));
        Assert.True(get.TryQueue(WorkspaceGatewaySource.LegacyAgentFiles));

        list.Complete();

        Assert.False(list.CanApply(WorkspaceGatewaySource.LegacyAgentFiles));
        Assert.True(get.CanApply(WorkspaceGatewaySource.LegacyAgentFiles));

        get.Complete();
        Assert.False(get.CanApply(WorkspaceGatewaySource.LegacyAgentFiles));
    }

    [Fact]
    public void ScopeDisclosureRequest_SuccessUsesDirectSourceAfterQueuedCallbackRetires()
    {
        var request = new WorkspaceScopeDisclosureRequest();
        Assert.True(request.TryQueue(WorkspaceGatewaySource.LegacyAgentFiles));

        request.Complete();

        Assert.False(request.CanApply(WorkspaceGatewaySource.LegacyAgentFiles));
        Assert.Equal(
            "WorkspacePage_LegacyAgentFilesScopeMessage",
            WorkspaceScopeDisclosure.ResourceKeyForList(
                WorkspaceGatewaySource.LegacyAgentFiles));
    }

    [Fact]
    public void ScopeDisclosure_FullMixedSourceMatrixIsOwnedByList()
    {
        var listSources = new[]
        {
            WorkspaceGatewaySource.AgentWorkspace,
            WorkspaceGatewaySource.SessionFiles,
            WorkspaceGatewaySource.LegacyAgentFiles
        };
        var previewSources = new[]
        {
            WorkspaceGatewaySource.AgentWorkspace,
            WorkspaceGatewaySource.SessionFiles,
            WorkspaceGatewaySource.LegacyAgentFiles,
            WorkspaceGatewaySource.Unsupported
        };

        foreach (var listSource in listSources)
        {
            var expected = WorkspaceScopeDisclosure.ResourceKeyForList(listSource);
            foreach (var previewSource in previewSources)
            {
                _ = previewSource;
                Assert.Equal(
                    expected,
                    WorkspaceScopeDisclosure.ResourceKeyForList(listSource));
            }
        }
    }

    [Fact]
    public void SessionReloadGate_PrimarySourceIgnoresFrequentSessionTicks()
    {
        var gate = new WorkspaceSessionReloadGate();
        gate.RecordCompletedLoad(
            WorkspaceGatewaySource.AgentWorkspace,
            fallbackKeyWasResolved: false,
            resolvedSessionKey: null);

        Assert.False(gate.DependsOnSessionKey);
        for (var tick = 0; tick < 100; tick++)
            Assert.False(gate.ShouldReload($"session-{tick}"));
    }

    [Fact]
    public void SessionReloadGate_UnchangedFallbackKeyIgnoresFrequentSessionTicks()
    {
        var gate = new WorkspaceSessionReloadGate();
        gate.RecordCompletedLoad(
            WorkspaceGatewaySource.SessionFiles,
            fallbackKeyWasResolved: true,
            resolvedSessionKey: "authoritative-key");

        Assert.True(gate.DependsOnSessionKey);
        for (var tick = 0; tick < 100; tick++)
            Assert.False(gate.ShouldReload("authoritative-key"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SessionReloadGate_WaitingFallbackKeyTransitionReloadsExactlyOnce(
        bool useLegacySource)
    {
        var source = useLegacySource
            ? WorkspaceGatewaySource.LegacyAgentFiles
            : WorkspaceGatewaySource.Unsupported;
        var gate = new WorkspaceSessionReloadGate();
        gate.RecordCompletedLoad(
            source,
            fallbackKeyWasResolved: true,
            resolvedSessionKey: null);

        Assert.True(gate.DependsOnSessionKey);
        Assert.True(gate.ShouldReload("new-authoritative-key"));
        Assert.False(gate.ShouldReload("new-authoritative-key"));
        Assert.False(gate.ShouldReload("new-authoritative-key"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SessionReloadGate_CompletedFallbackWithKnownKeyDoesNotWaitForSessionTicks(
        bool useLegacySource)
    {
        var source = useLegacySource
            ? WorkspaceGatewaySource.LegacyAgentFiles
            : WorkspaceGatewaySource.Unsupported;
        var gate = new WorkspaceSessionReloadGate();
        gate.RecordCompletedLoad(
            source,
            fallbackKeyWasResolved: true,
            resolvedSessionKey: "known-key");

        Assert.False(gate.DependsOnSessionKey);
        Assert.False(gate.ShouldReload("different-key"));
    }

    [Fact]
    public void SessionReloadGate_IrrelevantTicksPreserveSelectionPreviewAndScroll()
    {
        var gate = new WorkspaceSessionReloadGate();
        gate.RecordCompletedLoad(
            WorkspaceGatewaySource.SessionFiles,
            fallbackKeyWasResolved: true,
            resolvedSessionKey: "stable-key");
        var view = new WorkspaceViewState(
            SelectedPath: "Opaque/ReadMe.md",
            Preview: "# Loaded preview",
            ScrollOffset: 184.5);

        for (var tick = 0; tick < 100; tick++)
        {
            if (gate.ShouldReload("stable-key"))
                view = new WorkspaceViewState(null, null, 0);
        }

        Assert.Equal("Opaque/ReadMe.md", view.SelectedPath);
        Assert.Equal("# Loaded preview", view.Preview);
        Assert.Equal(184.5, view.ScrollOffset);
    }

    [Fact]
    public async Task ListAsync_UsesAgentIdDirectlyAndMergesAllPrimaryPages()
    {
        var api = new FakeWorkspaceGatewayApi();
        api.ListAgent = request =>
        {
            Assert.Equal("arbitrary-agent", request.AgentId);
            Assert.Equal("Repo/Src", request.Path);
            return Task.FromResult(request.Offset == 0
                ? Page(0, 2, "Repo", Entry("Repo/Src/ReadMe.md", "ReadMe.md"))
                : Page(1, 2, "Repo", Entry("Repo/Src/README.md", "README.md")));
        };
        var resolverCalled = false;

        var result = await new WorkspaceGatewayCoordinator(api).ListAsync(
            "arbitrary-agent",
            "Repo/Src",
            null,
            () =>
            {
                resolverCalled = true;
                return "must-not-resolve";
            });

        Assert.Equal(WorkspaceGatewaySource.AgentWorkspace, result.Source);
        Assert.False(resolverCalled);
        Assert.Equal(2, result.AgentWorkspace!.Entries.Count);
        Assert.Equal("Repo/Src/ReadMe.md", result.AgentWorkspace.Entries[0].Path);
        Assert.Equal("Repo/Src/README.md", result.AgentWorkspace.Entries[1].Path);
        Assert.Equal("Repo", result.AgentWorkspace.ParentPath);
        Assert.Equal(
            new[] { "primary-list:0", "primary-list:1" },
            api.Calls);
    }

    [Fact]
    public async Task ListAsync_RejectsEmptyNonAdvancingPrimaryPage()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = _ => Task.FromResult(Page(0, 1, null))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkspaceGatewayCoordinator(api).ListAsync("a", "", null, () => "session"));
        Assert.Equal(new[] { "primary-list:0" }, api.Calls);
    }

    [Fact]
    public async Task ListAsync_FallsBackOnlyOnUnsupportedInExactOrder()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = request => Task.FromResult(new AgentWorkspaceListResult
            {
                AgentId = request.AgentId,
                Path = request.Path ?? "",
                IsSupported = false
            }),
            ListSession = (key, path, search) => Task.FromResult(new SessionFileList
            {
                Key = key,
                Root = "/limited",
                IsSupported = true
            })
        };

        var result = await new WorkspaceGatewayCoordinator(api).ListAsync(
            "a", "Opaque/Path", "term", () => "agent:a:main");

        Assert.Equal(WorkspaceGatewaySource.SessionFiles, result.Source);
        Assert.Equal(
            new[] { "primary-list:0", "session-list:agent:a:main:Opaque/Path:term" },
            api.Calls);
        Assert.DoesNotContain("legacy-list", api.Calls);
    }

    [Theory]
    [InlineData("authentication failed")]
    [InlineData("invalid agents.workspace.list params")]
    [InlineData("request timed out")]
    [InlineData("invalid path")]
    public async Task ListAsync_PropagatesPrimaryErrorsWithoutFallback(string message)
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = _ => Task.FromException<AgentWorkspaceListResult>(
                new InvalidOperationException(message))
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkspaceGatewayCoordinator(api).ListAsync("a", "", null, () => "session"));
        Assert.Equal(message, error.Message);
        Assert.Equal(new[] { "primary-list:0" }, api.Calls);
    }

    [Fact]
    public async Task GetAsync_MixedSupportStopsWhenSessionGetErrors()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            GetAgent = _ => Task.FromResult(new AgentWorkspaceGetResult
            {
                AgentId = "a",
                IsSupported = false
            }),
            GetSession = (_, _) => Task.FromException<SessionFileContent>(
                new InvalidOperationException("invalid session path"))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkspaceGatewayCoordinator(api).GetAsync(
                "a", "Case/ReadMe.md", () => "agent:a:main"));
        Assert.Equal(
            new[] { "primary-get:Case/ReadMe.md", "session-get:agent:a:main:Case/ReadMe.md" },
            api.Calls);
        Assert.DoesNotContain("legacy-get", api.Calls);
    }

    [Theory]
    [InlineData("authentication failed")]
    [InlineData("request timed out")]
    [InlineData("invalid path")]
    public async Task GetAsync_PropagatesPrimaryErrorsWithoutFallback(string message)
    {
        var api = new FakeWorkspaceGatewayApi
        {
            GetAgent = _ => Task.FromException<AgentWorkspaceGetResult>(
                new InvalidOperationException(message))
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkspaceGatewayCoordinator(api).GetAsync(
                "a", "Opaque/Path.md", () => "session"));
        Assert.Equal(message, error.Message);
        Assert.Equal(new[] { "primary-get:Opaque/Path.md" }, api.Calls);
    }

    [Fact]
    public async Task PrimaryGet_RemainsUsableForPathFromSessionList()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = request => Task.FromResult(new AgentWorkspaceListResult
            {
                AgentId = request.AgentId,
                Path = request.Path ?? "",
                IsSupported = false
            }),
            ListSession = (key, _, _) => Task.FromResult(new SessionFileList
            {
                Key = key,
                Files = new[]
                {
                    new SessionFileEntry { Path = "Session/Only.md", Name = "Only.md" }
                },
                IsSupported = true
            }),
            GetAgent = request => Task.FromResult(new AgentWorkspaceGetResult
            {
                AgentId = request.AgentId,
                File = new AgentWorkspaceFile
                {
                    Path = request.Path,
                    Name = "Only.md",
                    MimeType = "text/plain",
                    Encoding = AgentWorkspaceFileEncoding.Utf8,
                    Content = "primary get"
                }
            })
        };
        var coordinator = new WorkspaceGatewayCoordinator(api);

        var list = await coordinator.ListAsync("a", "", null, () => "agent:a:main");
        var get = await coordinator.GetAsync("a", "Session/Only.md", () => "agent:a:main");

        Assert.Equal(WorkspaceGatewaySource.SessionFiles, list.Source);
        Assert.Equal(WorkspaceGatewaySource.AgentWorkspace, get.Source);
        Assert.Equal("primary get", get.AgentWorkspace!.File!.Content);
        Assert.DoesNotContain(api.Calls, call => call.StartsWith("session-get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedPrimaryAndSession_AwaitsCorrelatedLegacyPayloadsSeparately()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = request => Task.FromResult(new AgentWorkspaceListResult
            {
                AgentId = request.AgentId,
                IsSupported = false
            }),
            ListSession = (key, _, _) => Task.FromResult(new SessionFileList
            {
                Key = key,
                IsSupported = false
            }),
            GetAgent = request => Task.FromResult(new AgentWorkspaceGetResult
            {
                AgentId = request.AgentId,
                IsSupported = false
            }),
            GetSession = (key, path) => Task.FromResult(new SessionFileContent
            {
                Key = key,
                Path = path,
                IsSupported = false
            }),
            ListLegacy = (_, _) => Task.FromResult(SupportedLegacy(
                """{"files":[{"path":"ReadMe.md"}]}""")),
            GetLegacy = (_, _, _) => Task.FromResult(SupportedLegacy(
                """{"file":{"path":"ReadMe.md","content":"legacy"}}"""))
        };
        var coordinator = new WorkspaceGatewayCoordinator(api);

        var list = await coordinator.ListAsync("a", "", null, () => "s");
        var get = await coordinator.GetAsync("a", "ReadMe.md", () => "s");

        Assert.Equal(WorkspaceGatewaySource.LegacyAgentFiles, list.Source);
        Assert.Equal("ReadMe.md", list.LegacyPayload!.Value
            .GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(WorkspaceGatewaySource.LegacyAgentFiles, get.Source);
        Assert.Equal("legacy", get.LegacyPayload!.Value
            .GetProperty("file").GetProperty("content").GetString());
        Assert.Equal(
            new[]
            {
                "primary-list:0", "session-list:s::", "legacy-list:a",
                "primary-get:ReadMe.md", "session-get:s:ReadMe.md", "legacy-get:a:ReadMe.md"
            },
            api.Calls);
    }

    [Fact]
    public async Task MissingAuthoritativeSession_SkipsSessionAndUsesLegacy()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = request => Task.FromResult(new AgentWorkspaceListResult
            {
                AgentId = request.AgentId,
                IsSupported = false
            }),
            ListLegacy = (_, _) => Task.FromResult(SupportedLegacy("""{"files":[]}"""))
        };

        var result = await new WorkspaceGatewayCoordinator(api)
            .ListAsync("custom", "", null, () => null);

        Assert.Equal(WorkspaceGatewaySource.LegacyAgentFiles, result.Source);
        Assert.Equal(new[] { "primary-list:0", "legacy-list:custom" }, api.Calls);
    }

    [Fact]
    public async Task LegacyUnsupported_IsACompletedUnsupportedOutcome()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = request => Task.FromResult(new AgentWorkspaceListResult
            {
                AgentId = request.AgentId,
                IsSupported = false
            }),
            GetAgent = request => Task.FromResult(new AgentWorkspaceGetResult
            {
                AgentId = request.AgentId,
                IsSupported = false
            })
        };
        var coordinator = new WorkspaceGatewayCoordinator(api);

        var list = await coordinator.ListAsync("a", "", null, () => null);
        var get = await coordinator.GetAsync("a", "ReadMe.md", () => null);

        Assert.Equal(WorkspaceGatewaySource.Unsupported, list.Source);
        Assert.Equal(WorkspaceGatewaySource.Unsupported, get.Source);
    }

    [Fact]
    public async Task LegacyListCancellation_DoesNotInventCompletion()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgent = request => Task.FromResult(new AgentWorkspaceListResult
            {
                AgentId = request.AgentId,
                IsSupported = false
            }),
            ListLegacy = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SupportedLegacy("""{"files":[]}""");
            }
        };
        using var cancellation = new CancellationTokenSource();

        var request = new WorkspaceGatewayCoordinator(api).ListAsync(
            "a",
            "",
            null,
            () => null,
            cancellationToken: cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task PrimaryListCancellation_StopsTheInFlightGatewayWait()
    {
        var api = new FakeWorkspaceGatewayApi
        {
            ListAgentWithCancellation = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        };
        using var cancellation = new CancellationTokenSource();

        var request = new WorkspaceGatewayCoordinator(api).ListAsync(
            "a",
            "",
            null,
            () => null,
            cancellationToken: cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(new[] { "primary-list:0" }, api.Calls);
    }

    private static LegacyAgentFilesResponse SupportedLegacy(string json) =>
        new()
        {
            Payload = JsonDocument.Parse(json).RootElement.Clone()
        };

    private static AgentWorkspaceEntry Entry(string path, string name) => new()
    {
        Path = path,
        Name = name,
        Kind = AgentWorkspaceEntryKind.File
    };

    private static AgentWorkspaceListResult Page(
        int offset,
        long total,
        string? parent,
        params AgentWorkspaceEntry[] entries) => new()
    {
        AgentId = "arbitrary-agent",
        Path = "Repo/Src",
        ParentPath = parent,
        Entries = entries,
        TotalEntries = total,
        Offset = offset
    };

    private sealed record WorkspaceViewState(
        string? SelectedPath,
        string? Preview,
        double ScrollOffset);

    private sealed class FakeWorkspaceGatewayApi : IWorkspaceGatewayApi
    {
        public List<string> Calls { get; } = new();

        public Func<AgentWorkspaceListRequest, Task<AgentWorkspaceListResult>> ListAgent { get; set; } =
            request => Task.FromResult(new AgentWorkspaceListResult
            {
                AgentId = request.AgentId,
                Path = request.Path ?? ""
            });

        public Func<AgentWorkspaceListRequest, CancellationToken, Task<AgentWorkspaceListResult>>?
            ListAgentWithCancellation { get; set; }

        public Func<AgentWorkspaceGetRequest, Task<AgentWorkspaceGetResult>> GetAgent { get; set; } =
            request => Task.FromResult(new AgentWorkspaceGetResult { AgentId = request.AgentId });

        public Func<string, string?, string?, Task<SessionFileList>> ListSession { get; set; } =
            (key, _, _) => Task.FromResult(new SessionFileList { Key = key });

        public Func<string, string, Task<SessionFileContent>> GetSession { get; set; } =
            (key, path) => Task.FromResult(new SessionFileContent { Key = key, Path = path });

        public Func<string, CancellationToken, Task<LegacyAgentFilesResponse>> ListLegacy { get; set; } =
            (_, _) => Task.FromResult(new LegacyAgentFilesResponse { IsSupported = false });

        public Func<string, string, CancellationToken, Task<LegacyAgentFilesResponse>> GetLegacy { get; set; } =
            (_, _, _) => Task.FromResult(new LegacyAgentFilesResponse { IsSupported = false });

        public Task<AgentWorkspaceListResult> ListAgentWorkspaceAsync(
            AgentWorkspaceListRequest request,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"primary-list:{request.Offset}");
            return ListAgentWithCancellation?.Invoke(request, cancellationToken) ?? ListAgent(request);
        }

        public Task<AgentWorkspaceGetResult> GetAgentWorkspaceFileAsync(
            AgentWorkspaceGetRequest request,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"primary-get:{request.Path}");
            return GetAgent(request);
        }

        public Task<SessionFileList> ListSessionFilesAsync(
            string key,
            string? path = null,
            string? search = null,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"session-list:{key}:{path}:{search}");
            return ListSession(key, path, search);
        }

        public Task<SessionFileContent> GetSessionFileAsync(
            string key,
            string path,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"session-get:{key}:{path}");
            return GetSession(key, path);
        }

        public Task<LegacyAgentFilesResponse> ListLegacyAgentFilesAsync(
            string agentId,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"legacy-list:{agentId}");
            return ListLegacy(agentId, cancellationToken);
        }

        public Task<LegacyAgentFilesResponse> GetLegacyAgentFileAsync(
            string agentId,
            string name,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"legacy-get:{agentId}:{name}");
            return GetLegacy(agentId, name, cancellationToken);
        }
    }

    public sealed class WorkspaceSessionResolverTests
    {
        [Fact]
        public void Resolve_UsesCustomAuthoritativeMainKeyForSelectedAgent()
        {
            var sessions = new[]
            {
                Session("unrelated", "other", isMain: true),
                Session("custom-key/not-derived", "research-agent", isMain: true)
            };

            Assert.Equal(
                "custom-key/not-derived",
                WorkspaceSessionResolver.Resolve("research-agent", sessions, "main-key"));
        }

        [Fact]
        public void Resolve_CustomAgentUsesServerReturnedMainShapedRowAfterGlobalNormalization()
        {
            var sessions = new[]
            {
                Session("agent:main:main", "main", isMain: true),
                Session("agent:research-agent:child", "research-agent", isMain: false),
                Session("agent:research-agent:main", "research-agent", isMain: false)
            };

            Assert.Equal(
                "agent:research-agent:main",
                WorkspaceSessionResolver.Resolve("research-agent", sessions, "agent:main:main"));
        }

        [Fact]
        public void Resolve_MultipleAuthoritativeSessionsUsesOrdinalKeyOrder()
        {
            var sessions = new[]
            {
                Session("z-main", "agent-a", isMain: true),
                Session("child", "agent-a", isMain: false),
                Session("a-main", "agent-a", isMain: true)
            };

            Assert.Equal("a-main", WorkspaceSessionResolver.Resolve("agent-a", sessions, null));
        }

        [Fact]
        public void Resolve_MissingAuthoritativeSessionReturnsNull()
        {
            var sessions = new[]
            {
                Session("child-only", "agent-a", isMain: false),
                Session("other-main", "agent-b", isMain: true)
            };

            Assert.Null(WorkspaceSessionResolver.Resolve("agent-a", sessions, "main-key"));
        }

        [Fact]
        public void Resolve_MainAgentUsesCurrentCanonicalClientKey()
        {
            var sessions = new[] { Session("snapshot-main", "main", isMain: true) };

            Assert.Equal("client-main-a", WorkspaceSessionResolver.Resolve("main", sessions, "client-main-a"));
            Assert.Equal("client-main-b", WorkspaceSessionResolver.Resolve("main", sessions, "client-main-b"));
        }

        [Fact]
        public void Resolve_UsesRefreshedSessionSnapshotOnEveryCall()
        {
            var oldSnapshot = new[] { Session("old-custom-main", "agent-a", isMain: true) };
            var newSnapshot = new[] { Session("new-custom-main", "agent-a", isMain: true) };

            Assert.Equal("old-custom-main", WorkspaceSessionResolver.Resolve("agent-a", oldSnapshot, null));
            Assert.Equal("new-custom-main", WorkspaceSessionResolver.Resolve("agent-a", newSnapshot, null));
        }

        [Fact]
        public void Resolve_DirectMainMetadataIsRequired()
        {
            var session = Session("not-main", "agent-a", isMain: true);
            session.IsMain = false;

            Assert.Null(WorkspaceSessionResolver.Resolve("agent-a", new[] { session }, null));
        }

        private static SessionInfo Session(string key, string agentId, bool isMain) => new()
        {
            Key = key,
            IsMain = isMain,
            AgentId = agentId
        };
    }
}
