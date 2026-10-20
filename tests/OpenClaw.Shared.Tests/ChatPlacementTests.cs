using System.Text.Json;

namespace OpenClaw.Shared.Tests;

public sealed class ChatPlacementTests
{
    private const string Key = "agent:main:chat:exact-key";
    private static readonly ChatWorkspace Folder = new("folder:raw", "Project", @"C:\Gateway work\Repo");
    private static readonly JsonElement Agents = JsonSerializer.SerializeToElement(new
    {
        agents = new[] { new { id = "main", workspace = "/srv/agent/workspace" } }
    });
    private static JsonElement Environments => JsonSerializer.SerializeToElement(new
    {
        environments = new object[]
        {
            new { id = "gateway:actual-id", type = "local", label = "Gateway host", status = "available" },
            new { id = "node:exact-device-id", type = "node", label = "Windows host", status = "available",
                sessionHost = true, workerSlots = new { total = 2, available = 1 } }
        }
    });
    private static ChatPlacementSelection Selection(bool remote = false, ChatWorkspace? workspace = null) =>
        new(GatewayChatPlacementClient.ParseEnvironments(Environments)[remote ? 1 : 0], workspace ?? Folder);

    [Theory]
    [InlineData("Research assistant", "Configured name", "Research assistant")]
    [InlineData(null, "Configured name", "Configured name")]
    [InlineData(" ", null, "main")]
    public void Catalog_UsesSelectedAgentIdentityName(string? identityName, string? name, string expected)
    {
        var agents = JsonSerializer.SerializeToElement(new
        {
            agents = new object[]
            {
                new { id = "other", name = "Other assistant", identity = new { name = "Wrong identity" } },
                new { id = "main", name, identity = new { name = identityName } }
            }
        });
        var catalog = GatewayChatPlacementClient.ParseCatalog(42, "main", Environments,
            JsonSerializer.SerializeToElement(new { projects = Array.Empty<object>() }), agents);
        Assert.Equal(expected, catalog.AgentName);
    }

    [Fact]
    public void Catalog_UsesOnlySelectedAgentAvatarAndPrefersResolvedUrl()
    {
        var agents = JsonSerializer.SerializeToElement(new
        {
            agents = new[]
            {
                new { id = "other", identity = new { emoji = "Wrong", avatar = "wrong.png", avatarUrl = "https://example.com/wrong.png" } },
                new { id = "main", identity = new { emoji = "\u2728", avatar = "agent.png", avatarUrl = "data:image/png;base64,AQID" } }
            }
        });
        var projects = JsonSerializer.SerializeToElement(new { projects = Array.Empty<object>() });
        var catalog = GatewayChatPlacementClient.ParseCatalog(42, "main", Environments, projects, agents);
        Assert.Equal("\u2728", catalog.AgentEmoji);
        Assert.Equal("data:image/png;base64,AQID", catalog.AgentAvatarUrl);

        var unknown = GatewayChatPlacementClient.ParseCatalog(42, "missing", Environments, projects, agents);
        Assert.Null(unknown.AgentEmoji);
        Assert.Null(unknown.AgentAvatarUrl);
    }

    [Theory]
    [InlineData("https://example.com/agent.png", "https://example.com/agent.png")]
    [InlineData(" ", null)]
    public void Catalog_AvatarFallsBackToIdentityAvatar(string avatar, string? expected)
    {
        var agents = JsonSerializer.SerializeToElement(new { agents = new[] { new { id = "main", identity = new { avatar } } } });
        var catalog = GatewayChatPlacementClient.ParseCatalog(42, "main", Environments,
            JsonSerializer.SerializeToElement(new { projects = Array.Empty<object>() }), agents);
        Assert.Equal(expected, catalog.AgentAvatarUrl);
    }

    [Fact]
    public void Catalog_PreservesGatewayPathsAndExcludesNodeFolderRecents()
    {
        var projects = JsonSerializer.SerializeToElement(new
        {
            projects = new[] { new { id = "project:raw", displayName = "Repository", repoRoot = @"\\server\share\Repo" } },
            recents = new[]
            {
                new { kind = "folder", folder = "/srv/shared", displayName = "Shared", execNode = (string?)null },
                new { kind = "folder", folder = @"D:\Remote", displayName = "Node folder", execNode = (string?)"node-id" }
            }
        });
        var catalog = GatewayChatPlacementClient.ParseCatalog(42, "main", Environments, projects, Agents);
        Assert.Equal(42, catalog.ConnectionEpoch);
        Assert.Equal("gateway:actual-id", catalog.Environments[0].Id);
        Assert.Equal("/srv/agent/workspace", catalog.DefaultWorkspace.Path);
        Assert.Equal(@"\\server\share\Repo", catalog.Workspaces[0].Path);
        Assert.Equal("project:raw", catalog.Workspaces[0].ProjectId);
        Assert.Equal("/srv/shared", catalog.Workspaces[1].Path);
        Assert.Equal(2, catalog.Workspaces.Length);
    }

    [Theory]
    [InlineData("unavailable", true, 1, ChatEnvironmentUnavailable.Offline)]
    [InlineData("available", false, 1, ChatEnvironmentUnavailable.HostingDisabled)]
    [InlineData("available", true, 0, ChatEnvironmentUnavailable.NoCapacity)]
    [InlineData("available", true, 1, ChatEnvironmentUnavailable.None)]
    public void DeviceEligibility_UsesLiveAuthority(string status, bool sessionHost, int available,
        ChatEnvironmentUnavailable expected)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            environments = new[] { new { id = "node:host", type = "node", status, sessionHost, workerSlots = new { total = 1, available } } }
        });
        Assert.Equal(expected, Assert.Single(GatewayChatPlacementClient.ParseEnvironments(response)).Unavailable);
    }

    [Fact]
    public void DeviceEligibility_HonorsRuntimeRequiredCommandsAndCapacityContract()
    {
        var node = JsonSerializer.SerializeToElement(new
        {
            environments = new[] { new { id = "node:host", type = "node", status = "available", sessionHost = true } }
        });
        var noSlots = new GatewayChatPlacementClient.RuntimeRequirement("external", ConsumesWorkerSlot: false);
        Assert.True(Assert.Single(GatewayChatPlacementClient.ParseEnvironments(node, noSlots)).IsSelectable);
        Assert.Equal(ChatEnvironmentUnavailable.CommandUnavailable,
            Assert.Single(GatewayChatPlacementClient.ParseEnvironments(node, noSlots with { RequiresCommand = true })).Unavailable);
        Assert.Equal(ChatEnvironmentUnavailable.RuntimeUnsupported,
            Assert.Single(GatewayChatPlacementClient.ParseEnvironments(node, noSlots with { Supported = false })).Unavailable);
        Assert.Equal(ChatEnvironmentUnavailable.CapacityUnknown,
            Assert.Single(GatewayChatPlacementClient.ParseEnvironments(node)).Unavailable);
    }

    [Fact]
    public async Task Discovery_IsReadOnlyAndPassesRuntimeIdToEnvironmentInventory()
    {
        var requests = new List<(string Method, JsonElement Parameters)>();
        var client = new GatewayChatPlacementClient(() => 42, (method, parameters, _, epoch) =>
        {
            Assert.Equal(42, epoch);
            requests.Add((method, JsonSerializer.SerializeToElement(parameters)));
            return Task.FromResult(method switch
            {
                "agents.list" => JsonSerializer.SerializeToElement(new
                {
                    agents = new[] { new { id = "main", agentRuntime = new { id = "exact-runtime" } } }
                }),
                "projects.list" => JsonSerializer.SerializeToElement(new { projects = Array.Empty<object>() }),
                "environments.list" => Environments,
                _ => throw new InvalidOperationException("Unexpected mutation")
            });
        });
        var catalog = await client.LoadAsync("main", default);
        Assert.Null(catalog.DefaultWorkspace.Path);
        Assert.Equal("exact-runtime", requests.Single(request => request.Method == "environments.list")
            .Parameters.GetProperty("runtimeId").GetString());
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task LocalPreparation_AdoptsExactKeyAndCwdWithoutResettingOrSending()
    {
        var calls = new List<(string, JsonElement)>();
        var client = Client(calls);
        await client.PrepareAsync(Key, "main", Selection(), 42, default);
        var create = Assert.Single(calls, call => call.Item1 == "sessions.create").Item2;
        Assert.Equal(Key, create.GetProperty("key").GetString());
        Assert.Equal(Folder.Path, create.GetProperty("cwd").GetString());
        Assert.False(create.GetProperty("emitCommandHooks").GetBoolean());
        Assert.False(create.TryGetProperty("parentSessionKey", out _));
        Assert.False(create.TryGetProperty("worktree", out _));
        Assert.DoesNotContain(calls, call => call.Item1 is "chat.send" or "sessions.dispatch");
    }

    [Fact]
    public async Task ProjectPreparation_UsesProjectIdNotControllerFilesystemPath()
    {
        var calls = new List<(string, JsonElement)>();
        await Client(calls).PrepareAsync(Key, "main",
            Selection(workspace: Folder with { ProjectId = "registered-id" }), 42, default);
        var create = calls.Single(call => call.Item1 == "sessions.create").Item2;
        Assert.Equal("registered-id", create.GetProperty("projectId").GetString());
        Assert.False(create.TryGetProperty("cwd", out _));
        Assert.False(create.TryGetProperty("execNode", out _));
    }

    [Fact]
    public async Task RemotePreparation_CreatesOwnedWorktreeThenDispatchesExactDevice()
    {
        var calls = new List<(string, JsonElement)>();
        await Client(calls).PrepareAsync(Key, "main", Selection(remote: true), 42, default);
        var createIndex = calls.FindIndex(call => call.Item1 == "sessions.create");
        var dispatchIndex = calls.FindIndex(call => call.Item1 == "sessions.dispatch");
        Assert.True(calls[createIndex].Item2.GetProperty("worktree").GetBoolean());
        Assert.True(dispatchIndex > createIndex);
        Assert.Equal("exact-device-id", calls[dispatchIndex].Item2.GetProperty("deviceId").GetString());
        Assert.Equal(Key, calls[dispatchIndex].Item2.GetProperty("key").GetString());
    }

    [Fact]
    public async Task UnconfirmedPlacement_FailsRatherThanAllowingLocalSend()
    {
        var calls = new List<(string, JsonElement)>();
        var client = Client(calls, (method, _) => method == "sessions.dispatch"
            ? JsonSerializer.SerializeToElement(new { ok = true, key = Key, placement = new { state = "starting" } })
            : null);
        var error = await Assert.ThrowsAsync<ChatPlacementOutcomeUncertainException>(
            () => client.PrepareAsync(Key, "main", Selection(remote: true), 42, default));
        Assert.Contains("not confirmed", error.InnerException!.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRemotePlacement_IsNotRetargetedOrReallocated(bool remote)
    {
        var calls = new List<(string, JsonElement)>();
        var client = Client(calls, (method, _) => method == "sessions.describe"
            ? JsonSerializer.SerializeToElement(new { session = new { placement = new { state = "active" } } })
            : null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PrepareAsync(Key, "main", Selection(remote), 42, default));
        Assert.DoesNotContain(calls, call => call.Item1 is "sessions.create" or "sessions.dispatch");
    }

    [Fact]
    public void Catalog_DoesNotDuplicateCurrentAgentDefaultWorkspaceAsProject()
    {
        var projects = JsonSerializer.SerializeToElement(new
        {
            projects = new[]
            {
                new { id = "workspace:main", displayName = "workspace", source = "workspace", agentId = "main" },
                new { id = "workspace:other", displayName = "Other workspace", source = "workspace", agentId = "other" }
            }
        });
        var catalog = GatewayChatPlacementClient.ParseCatalog(42, "main", Environments, projects, Agents);
        Assert.Equal("workspace:other", Assert.Single(catalog.Workspaces).ProjectId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrReconnect_AfterAdmittedCreateDoesNotDispatch(bool reconnect)
    {
        var calls = new List<(string, JsonElement)>();
        using var cancellation = new CancellationTokenSource();
        long epoch = 42;
        var client = Client(calls, (method, _) =>
        {
            if (method == "sessions.create")
            {
                if (reconnect) epoch++;
                else cancellation.Cancel();
            }
            return null;
        }, () => epoch);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.PrepareAsync(Key, "main", Selection(remote: true), 42, cancellation.Token));
        Assert.DoesNotContain(calls, call => call.Item1 == "sessions.dispatch");
    }

    [Fact]
    public async Task CancellationWhileAddingRepository_DrainsRequestWithoutCreatingOrDispatchingSession()
    {
        var calls = new List<(string, JsonElement)>();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(calls, wait: async method =>
        {
            if (method != "projects.add") return;
            entered.SetResult();
            await completion.Task;
        });
        var preparing = client.PrepareAsync(Key, "main",
            Selection(remote: true, workspace: new("repository:test", "Repository", GitUrl: "https://example.com/repo.git")),
            42, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        Assert.False(preparing.IsCompleted);
        completion.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Contains(calls, call => call.Item1 == "projects.add");
        Assert.DoesNotContain(calls, call => call.Item1 is "sessions.create" or "sessions.dispatch");
    }

    [Fact]
    public async Task Browse_PreservesCanonicalGatewayPathAndNeverSuppliesLocalNode()
    {
        var calls = new List<(string, JsonElement)>();
        var directory = await Client(calls).BrowseAsync("/srv/agent", 42, default);
        Assert.Equal("/srv/agent", calls.Single().Item2.GetProperty("path").GetString());
        Assert.False(calls.Single().Item2.TryGetProperty("nodeId", out _));
        Assert.Equal("/srv/agent/canonical", directory.Path);
        Assert.Equal("/srv/agent/canonical/child", Assert.Single(directory.Folders).Path);
    }

    [Theory]
    [InlineData("projects.add", false)]
    [InlineData("sessions.create", false)]
    [InlineData("sessions.dispatch", false)]
    [InlineData("projects.add", true)]
    [InlineData("sessions.create", true)]
    [InlineData("sessions.dispatch", true)]
    public async Task AdmittedMutation_TimeoutOrLateEpochChangeReportsUncertainOutcome(string failingMethod, bool reconnect)
    {
        var calls = new List<(string, JsonElement)>();
        long epoch = 42;
        var client = Client(calls, (method, _) =>
        {
            if (method == failingMethod)
            {
                if (reconnect) epoch++;
                else throw new TimeoutException("synthetic timeout after admission");
            }
            return null;
        }, () => epoch);
        var error = await Assert.ThrowsAsync<ChatPlacementOutcomeUncertainException>(() => client.PrepareAsync(
            Key, "main", Selection(remote: true, workspace: new("repository:test", "Repository", GitUrl: "https://example.com/repo.git")),
            42, default));
        Assert.Equal(failingMethod, calls[^1].Item1);
        Assert.NotNull(error.InnerException);
        Assert.Equal(reconnect ? typeof(InvalidOperationException) : typeof(TimeoutException), error.InnerException.GetType());
    }

    [Theory]
    [InlineData("agents.list")]
    [InlineData("environments.list")]
    [InlineData("sessions.describe")]
    public async Task ReadOnlyPreflightTimeout_IsNotReportedAsUncertainMutation(string failingMethod)
    {
        var calls = new List<(string, JsonElement)>();
        var client = Client(calls, (method, _) => method == failingMethod
            ? throw new TimeoutException("synthetic preflight timeout") : null);
        await Assert.ThrowsAsync<TimeoutException>(() => client.PrepareAsync(Key, "main", Selection(), 42, default));
        Assert.DoesNotContain(calls, call => call.Item1 is "projects.add" or "sessions.create" or "sessions.dispatch");
    }

    [Theory]
    [InlineData("projects.add", false)]
    [InlineData("sessions.create", false)]
    [InlineData("sessions.dispatch", false)]
    [InlineData("projects.add", true)]
    [InlineData("sessions.create", true)]
    [InlineData("sessions.dispatch", true)]
    public async Task Preparation_RechecksAuthorityBeforeEachMutation(string pausedMethod, bool cancel)
    {
        var calls = new List<(string, JsonElement)>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        string[] mutations = ["projects.add", "sessions.create", "sessions.dispatch"];
        var guards = 0;
        Task BeforeMutation(CancellationToken token)
        {
            if (mutations[guards++] != pausedMethod)
                return Task.CompletedTask;
            entered.SetResult();
            return ready.Task.WaitAsync(token);
        }
        var preparing = Client(calls).PrepareAsync(Key, "main",
            Selection(remote: true, workspace: new("repository:test", "Repository", GitUrl: "https://example.com/repo.git")),
            42, cancellation.Token, BeforeMutation);
        try
        {
            await Task.WhenAny(entered.Task, preparing);
            Assert.True(entered.Task.IsCompletedSuccessfully);
            Assert.False(preparing.IsCompleted);
            Assert.DoesNotContain(calls, call => call.Item1 == pausedMethod);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
                Assert.DoesNotContain(calls, call => call.Item1 == pausedMethod);
            }
            else
            {
                ready.SetResult();
                await preparing;
                Assert.Equal(3, guards);
                Assert.Equal(mutations, calls.Select(call => call.Item1).Where(mutations.Contains));
            }
        }
        finally
        {
            cancellation.Cancel();
            ready.TrySetResult();
        }
    }

    private static GatewayChatPlacementClient Client(List<(string, JsonElement)> calls,
        Func<string, JsonElement, JsonElement?>? response = null, Func<long?>? epoch = null,
        Func<string, Task>? wait = null) =>
        new(epoch ?? (() => 42), async (method, parameters, _, expectedEpoch) =>
        {
            Assert.Equal(42, expectedEpoch);
            var json = JsonSerializer.SerializeToElement(parameters);
            calls.Add((method, json));
            if (wait is not null) await wait(method);
            return response?.Invoke(method, json) ?? method switch
            {
                "agents.list" => Agents,
                "environments.list" => Environments,
                "sessions.describe" => JsonSerializer.SerializeToElement(new { session = new { } }),
                "sessions.create" => JsonSerializer.SerializeToElement(new { ok = true, key = Key }),
                "sessions.dispatch" => JsonSerializer.SerializeToElement(new { ok = true, key = Key, placement = new { state = "active" } }),
                "projects.add" => JsonSerializer.SerializeToElement(new { id = "one", displayName = "Repository", repoRoot = "/srv/repo" }),
                "fs.listDir" => JsonSerializer.SerializeToElement(new
                {
                    path = "/srv/agent/canonical", parent = "/srv/agent", home = "/srv/agent",
                    entries = new[] { new { name = "child", path = "/srv/agent/canonical/child" } }
                }),
                _ => throw new InvalidOperationException("Unexpected method " + method)
            };
        });
}
