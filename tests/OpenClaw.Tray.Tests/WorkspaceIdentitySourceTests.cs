using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class WorkspaceIdentitySourceTests
{
    // Carries the same Data keys production attaches, via the production factory.
    private sealed class GatewayRequestException : InvalidOperationException
    {
        public GatewayRequestException(string message, string code, string? detail) : base(message)
        {
            foreach (System.Collections.DictionaryEntry entry in GatewayRequestErrors.Create(message, code, detail).Data)
                Data[entry.Key] = entry.Value;
        }
    }

    private static JsonElement Profile(string? name, params string[] emails) =>
        JsonSerializer.SerializeToElement(new { profile = new { id = "person", displayName = name, emails } });

    private static (IOperatorGatewayClient Client, AgentCreationServiceTests.GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, AgentCreationServiceTests.GatewayProxy>();
        return (client, (AgentCreationServiceTests.GatewayProxy)client);
    }

    [Theory]
    [InlineData("Ada", "ada@example.test", "Ada")]
    [InlineData("  Ada  ", "ada@example.test", "Ada")]
    [InlineData(null, "ada@example.test", "ada@example.test")]
    [InlineData("", "ada@example.test", "ada@example.test")]
    [InlineData("   ", " ada@example.test ", "ada@example.test")]
    public void TrimmedNameThenFirstNonBlankEmail(string? name, string email, string expected) =>
        Assert.Equal(expected, WorkspaceIdentitySource.ReadDisplayName(Profile(name, email)));

    [Fact]
    public void SkipsBlankEmailsBeforeFallingBack() =>
        Assert.Equal("b@example.test", WorkspaceIdentitySource.ReadDisplayName(Profile(null, "  ", "b@example.test")));

    [Fact]
    public void MissingNameAndEmailsFallsBackToProfileIdLikeMacOS() =>
        Assert.Equal("person", WorkspaceIdentitySource.ReadDisplayName(Profile(null)));

    [Fact]
    public void EveryValidProfileRequestsItsAvatar()
    {
        var nameOnly = WorkspaceIdentitySource.ReadProfile(Profile("Ada"));
        Assert.Equal("person", nameOnly.ProfileId);
        Assert.Null(nameOnly.AvatarRevision);
        var uploaded = WorkspaceIdentitySource.ReadProfile(JsonSerializer.SerializeToElement(
            new { profile = new { id = "person", hasAvatar = true, updatedAt = 42 } }));
        Assert.Equal("person", uploaded.ProfileId);
        Assert.Equal("42", uploaded.AvatarRevision);
    }

    [Fact]
    public async Task NameOnlyProfileStillShowsTheGatewayResolvedAvatar()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        var image = new UserAvatarImage([8], "image/png");
        proxy.Avatar = (_, _) => Task.FromResult(new UserAvatarResult(UserAvatarStatus.Ready, image));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        Assert.Same(image, source.Avatar);
        Assert.Equal("person", Assert.Single(proxy.AvatarRequests).ProfileId);
    }

    [Fact]
    public async Task LoadsAvatarThroughGatewayOwnedPath()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada", "a@example.test"));
        var image = new UserAvatarImage([1, 2, 3], "image/png");
        proxy.Avatar = (_, _) => Task.FromResult(new UserAvatarResult(UserAvatarStatus.Ready, image));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Same(image, source.Avatar);
        Assert.Equal("person", Assert.Single(proxy.AvatarRequests).ProfileId);
        source.SetConnection(client, ConnectionStatus.Disconnected);
        Assert.Null(source.Avatar);
    }

    [Fact]
    public async Task LateAvatarCannotLeakIntoNewConnection()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Old", "a@example.test"));
        var pending = new TaskCompletionSource<UserAvatarResult>();
        proxy.Avatar = (_, _) => pending.Task;
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        var old = source.RefreshAsync();
        source.SetConnection(null, ConnectionStatus.Disconnected);
        pending.SetResult(new UserAvatarResult(UserAvatarStatus.Ready, new UserAvatarImage([9], "image/png")));
        await old;
        Assert.Null(source.Avatar);
        Assert.Null(source.DisplayName);
    }

    [Fact]
    public async Task GatewayOwnerIsMarkedSharedAndRequestsGatewayRoute()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(JsonSerializer.SerializeToElement(
            new { profile = new { id = "gateway-owner", displayName = "Owner Person" } }));
        proxy.Avatar = (_, _) => Task.FromResult(UserAvatarResult.Of(UserAvatarStatus.NotFound));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("gateway-owner", Assert.Single(proxy.AvatarRequests).ProfileId);
        Assert.True(source.IsSharedOwner);
        Assert.Equal("Owner Person", source.DisplayName);
        Assert.Equal("Owner Person", source.SharedOwnerName);
        Assert.Null(source.Avatar);
    }

    private static string Label(WorkspaceIdentitySource source) =>
        source.FooterLabel("Owner", "Shared owner", name => name + " · Shared owner");

    [Theory]
    [InlineData("""{"profile":{"id":"gateway-owner","displayName":"  Barbara  "}}""", "Barbara · Shared owner")]
    [InlineData("""{"profile":{"id":"gateway-owner","displayName":"Barbara Person"}}""", "Barbara · Shared owner")]
    [InlineData("""{"profile":{"id":"gateway-owner","displayName":"   ","emails":["owner@example.test"]}}""", "Shared owner")]
    [InlineData("""{"profile":{"id":"gateway-owner","emails":["owner@example.test"]}}""", "Shared owner")]
    [InlineData("""{"profile":{"id":"gateway-owner"}}""", "Shared owner")]
    [InlineData("""{"profile":{"id":"person","displayName":"Ada"}}""", "Ada")]
    [InlineData("""{"profile":{"id":"person","displayName":"  Ada Lovelace  "}}""", "Ada")]
    [InlineData("""{"profile":{"id":"person","displayName":"Ada\tAugusta\nLovelace"}}""", "Ada")]
    [InlineData("""{"profile":{"id":"person","displayName":"Ada\u00a0Lovelace"}}""", "Ada")]
    [InlineData("""{"profile":{"id":"person","displayName":"Mary-Jane Watson"}}""", "Mary-Jane")]
    [InlineData("""{"profile":{"id":"person","displayName":"\u674e\u660e"}}""", "\u674e\u660e")]
    [InlineData("""{"profile":{"id":"person","emails":["ada@example.test"]}}""", "ada@example.test")]
    [InlineData("""{"profile":{"id":"person with spaces"}}""", "person with spaces")]
    [InlineData("""{"profile":{"id":"person"}}""", "person")]
    public async Task FooterShowsSavedSharedOwnerNameWithMarkerButNeverItsEmailOrId(string profile, string expected)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(JsonDocument.Parse(profile).RootElement.Clone());
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        Assert.Equal("Owner", Label(source));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal(expected, Label(source));
    }

    [Fact]
    public async Task SharedOwnerNameFollowsTheCurrentProfile()
    {
        var (client, proxy) = Gateway();
        object current = new { profile = new { id = "gateway-owner", displayName = "Barbara" } };
        proxy.Response = () => Task.FromResult(JsonSerializer.SerializeToElement(current));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Barbara · Shared owner", Label(source));
        current = new { profile = new { id = "person", displayName = "Ada" } };
        await source.RefreshAsync(invalidate: true);
        Assert.Null(source.SharedOwnerName);
        Assert.Equal("Ada", Label(source));
        current = new { profile = new { id = "gateway-owner" } };
        await source.RefreshAsync(invalidate: true);
        Assert.Null(source.SharedOwnerName);
        Assert.Equal("Shared owner", Label(source));
    }

    [Fact]
    public async Task FooterShortensOnlyTheSavedNameAndClearsItOnDisconnect()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada Lovelace"));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", Label(source));
        Assert.Equal("Ada Lovelace", source.DisplayName);

        proxy.Response = () => Task.FromResult(Profile(null, "ada@example.test"));
        await source.RefreshAsync(invalidate: true);
        Assert.Equal("ada@example.test", Label(source));

        proxy.Response = () => Task.FromResult(Profile("Grace Hopper"));
        await source.RefreshAsync(invalidate: true);
        Assert.Equal("Grace", Label(source));
        source.SetConnection(null, ConnectionStatus.Disconnected);
        Assert.Equal("Owner", Label(source));
    }

    [Fact]
    public async Task PersonalProfileIsNotSharedOwner()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.False(source.IsSharedOwner);
        Assert.Equal("Ada", source.DisplayName);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("http")]
    [InlineData("failed")]
    [InlineData("unauthorized")]
    public async Task AvatarFailureKeepsNameAndNeverFaultsRefresh(string failure)
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        proxy.Response = () => Task.FromResult(Profile("Ada", "a@example.test"));
        proxy.Avatar = (_, _) => failure switch
        {
            "timeout" => throw new TaskCanceledException("sensitive"),
            "io" => throw new IOException("sensitive"),
            "disposed" => throw new ObjectDisposedException("sensitive"),
            "http" => throw new HttpRequestException("sensitive"),
            "failed" => Task.FromResult(UserAvatarResult.Of(UserAvatarStatus.Failed)),
            _ => Task.FromResult(UserAvatarResult.Of(UserAvatarStatus.Unauthorized)),
        };
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        Assert.Null(source.Avatar);
        Assert.DoesNotContain("sensitive", Assert.Single(errors));
        Assert.StartsWith("Avatar", errors[0]);
    }

    [Theory]
    [InlineData(UserAvatarStatus.NotFound)]
    [InlineData(UserAvatarStatus.Unsupported)]
    [InlineData(UserAvatarStatus.Cancelled)]
    public async Task AbsentOrSupersededAvatarIsSilentInitials(UserAvatarStatus status)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada", "a@example.test"));
        proxy.Avatar = (_, _) => Task.FromResult(UserAvatarResult.Of(status));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        Assert.Null(source.Avatar);
    }

    [Fact]
    public async Task NameIsUnknownUntilTheProfileArrives()
    {
        var (client, proxy) = Gateway();
        var pending = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => pending.Task;
        var states = new List<string?>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add(source!.DisplayName), _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        var load = source.RefreshAsync();
        Assert.Null(source.DisplayName);
        pending.SetResult(Profile("Ada"));
        await load;
        Assert.Equal("Ada", source.DisplayName);
        Assert.Null(states[0]);
        Assert.All(states.Skip(1), name => Assert.Equal("Ada", name));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    public async Task UnreadableProfileLeavesNoNameSoTheFooterShowsOwner(string outcome)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => outcome switch
        {
            "timeout" => throw new TimeoutException("x"),
            "malformed" => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            _ => throw new InvalidOperationException("x"),
        };
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Null(source.DisplayName);
        Assert.False(source.IsSharedOwner);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("forbidden")]
    public async Task FailedLookupAfterSharedOwnerReturnsToOwner(string outcome)
    {
        var (client, proxy) = Gateway();
        var fail = false;
        proxy.Response = () => fail
            ? outcome switch
            {
                "timeout" => throw new TimeoutException("x"),
                "malformed" => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
                _ => throw new GatewayRequestException("x", "FORBIDDEN", null),
            }
            : Task.FromResult(JsonSerializer.SerializeToElement(new { profile = new { id = "gateway-owner", displayName = "Barbara" } }));
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.True(source.IsSharedOwner);
        Assert.Equal("Barbara · Shared owner", Label(source));
        fail = true;
        await source.RefreshAsync(invalidate: true);
        Assert.False(source.IsSharedOwner);
        Assert.Null(source.SharedOwnerName);
        Assert.Null(source.DisplayName);
        Assert.Equal("Owner", Label(source));
    }

    [Fact]
    public async Task ContextSwitchAndDisconnectClearSharedOwner()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(JsonSerializer.SerializeToElement(new { profile = new { id = "gateway-owner", displayName = "Barbara" } }));
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.True(source.IsSharedOwner);
        var (next, _) = Gateway();
        source.SetConnection(next, ConnectionStatus.Connected);
        Assert.False(source.IsSharedOwner);
        Assert.Null(source.SharedOwnerName);
        Assert.Null(source.DisplayName);
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        source.SetConnection(null, ConnectionStatus.Disconnected);
        Assert.False(source.IsSharedOwner);
        Assert.Null(source.SharedOwnerName);
        Assert.Equal("Owner", Label(source));
    }

    [Fact]
    public async Task OldGenerationCompletionNeverWritesTheNewContext()
    {
        var (client, proxy) = Gateway();
        var oldRequest = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => oldRequest.Task;
        var states = new List<string?>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add(source!.DisplayName), _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        var old = source.RefreshAsync();
        var (next, nextProxy) = Gateway();
        var newRequest = new TaskCompletionSource<JsonElement>();
        nextProxy.Response = () => newRequest.Task;
        source.SetConnection(next, ConnectionStatus.Connected);
        var current = source.RefreshAsync();
        oldRequest.SetResult(Profile("Old"));
        await old;
        Assert.Null(source.DisplayName);
        newRequest.SetResult(Profile("New"));
        await current;
        Assert.Equal("New", source.DisplayName);
        Assert.DoesNotContain("Old", states);
    }
    [Fact]
    public async Task AvatarAbsentKeepsNameWithoutAvatar()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada", "a@example.test"));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        Assert.Null(source.Avatar);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"profile":null}""")]
    [InlineData("""{"profile":{"displayName":"Not authenticated"}}""")]
    public void RejectsNonProfilePayload(string json) =>
        Assert.Throws<JsonException>(() => WorkspaceIdentitySource.ReadDisplayName(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("operator.admin")]
    [InlineData("operator.read")]
    [InlineData("operator.write")]
    [InlineData("operator.sessions.read")]
    [InlineData("operator.sessions.write")]
    public async Task LoadsCanonicalSelfWithExactContract(string scope)
    {
        var (client, proxy) = Gateway();
        proxy.Scopes = [scope];
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        var request = Assert.Single(proxy.Requests);
        Assert.Equal("users.self", request.Method);
        Assert.Equal("{}", request.Payload.GetRawText());
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected, "operator.admin", false)]
    [InlineData(ConnectionStatus.Connecting, "operator.admin", false)]
    [InlineData(ConnectionStatus.Error, "operator.admin", false)]
    [InlineData(ConnectionStatus.Connected, "operator.pairing", true)]
    [InlineData(ConnectionStatus.Connected, "", true)]
    public async Task DoesNotReadWithoutConnectedSelfReadGrant(ConnectionStatus status, string scope, bool reportsScope)
    {
        var (client, proxy) = Gateway();
        proxy.Scopes = [scope];
        var errors = new List<string>();
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, status);
        await source.RefreshAsync();
        await source.RefreshAsync();
        Assert.Null(source.DisplayName);
        Assert.Empty(proxy.Requests);
        // Reported once per connection context, as a bounded category with no scope names.
        Assert.Equal(reportsScope ? ["LocalScopeGate"] : [], errors);
    }

    [Fact]
            public async Task LocalScopeGateIsReportedAgainForANewContext()
    {
        var errors = new List<string>();
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        for (var i = 0; i < 2; i++)
        {
            var (client, proxy) = Gateway();
            proxy.Scopes = ["operator.pairing"];
            source.SetConnection(client, ConnectionStatus.Connected);
            await source.RefreshAsync();
        }
        Assert.Equal(["LocalScopeGate", "LocalScopeGate"], errors);
    }

    [Theory]
    [InlineData("FORBIDDEN", null, "Forbidden", true)]
    [InlineData("FORBIDDEN", "MISSING_SCOPE", "ServerMissingScope", true)]
    [InlineData("INVALID_REQUEST", "MISSING_SCOPE", "InvalidRequest", true)]
    [InlineData("SOMETHING_NEW", null, "Unknown", true)]
    [InlineData("UNAVAILABLE", null, "Unavailable", false)]
    public void ServerFailuresAreDistinguishedFromProductionFactoryCodes(string code, string? detail, string expected, bool terminal)
    {
        var failure = WorkspaceIdentitySource.Classify(new GatewayRequestException("scope names must not leak", code, detail));
        Assert.Equal(expected, failure.ToString());
        Assert.Equal(terminal, WorkspaceIdentitySource.IsTerminal(failure));
    }

    [Fact]
    public async Task UnknownCodeAndDetailOnlyUnavailableAreClassifiedFromTheProductionFactory()
    {
        Assert.Equal(WorkspaceIdentitySource.IdentityFailure.Unknown,
            WorkspaceIdentitySource.Classify(new GatewayRequestException("m", "SOMETHING_NEW", null)));
        Assert.Equal(WorkspaceIdentitySource.IdentityFailure.Unavailable,
            WorkspaceIdentitySource.Classify(new GatewayRequestException("m", "NOT_LINKED", "AUTHENTICATED_PROFILE_UNAVAILABLE")));
        Assert.Equal(WorkspaceIdentitySource.IdentityFailure.NotReady,
            WorkspaceIdentitySource.Classify(new TaskCanceledException()));
        Assert.Equal(WorkspaceIdentitySource.IdentityFailure.NotReady,
            WorkspaceIdentitySource.Classify(new InvalidOperationException("hello pending")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RefreshesProfileChangesAndClearsOnDisconnect()
    {
        var (client, proxy) = Gateway();
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        proxy.Response = () => Task.FromResult(Profile(null, "new@example.test"));
        await source.RefreshAsync();
        Assert.Equal("new@example.test", source.DisplayName);
        source.SetConnection(client, ConnectionStatus.Disconnected);
        Assert.Null(source.DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateResponseCannotOverwriteNewConnection(bool reuseClient)
    {
        var (client, proxy) = Gateway();
        var pending = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => pending.Task;
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        var old = source.RefreshAsync();
        source.SetConnection(null, ConnectionStatus.Disconnected);
        var (replacement, replacementProxy) = reuseClient ? (client, proxy) : Gateway();
        replacementProxy.Response = () => Task.FromResult(Profile("New owner"));
        source.SetConnection(replacement, ConnectionStatus.Connected);
        await source.RefreshAsync();
        pending.SetResult(Profile("Old owner"));
        await old;
        Assert.Equal("New owner", source.DisplayName);
    }

    [Fact]
    public async Task CoalescesConcurrentRefreshesButDoesNotLoseInvalidation()
    {
        var (client, proxy) = Gateway();
        var pending = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => pending.Task;
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        var first = source.RefreshAsync();
        Assert.Same(first, source.RefreshAsync());
        Assert.Same(first, source.RefreshAsync());
        Assert.Single(proxy.Requests);
        proxy.Response = () => Task.FromResult(Profile("Updated"));
        pending.SetResult(Profile("Initial"));
        await first;
        Assert.Equal("Updated", source.DisplayName);
        Assert.Equal(2, proxy.Requests.Count);
    }

    [Theory]
    [InlineData("forbidden", "Forbidden")]
    [InlineData("unsupported", "InvalidRequest")]
    [InlineData("timeout", "Timeout")]
    [InlineData("malformed", "MalformedReply")]
    [InlineData("unavailable", "Unavailable")]
    [InlineData("local", "NotReady")]
    public async Task UnavailableProfileClearsNameAndReportsOnlyErrorCategory(string failure, string category)
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        await source.RefreshAsync();
        proxy.Response = () => failure switch
        {
            "timeout" => throw new TimeoutException("sensitive"),
            "malformed" => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            "forbidden" => throw new GatewayRequestException("sensitive", "FORBIDDEN", null),
            "unsupported" => throw new GatewayRequestException("sensitive", "INVALID_REQUEST", null),
            "unavailable" => throw new GatewayRequestException("sensitive", "UNAVAILABLE", "AUTHENTICATED_PROFILE_UNAVAILABLE"),
            _ => throw new InvalidOperationException("sensitive local refusal")
        };
        await source.RefreshAsync();
        Assert.Null(source.DisplayName);
        Assert.Equal(category, Assert.Single(errors));
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("local")]
    [InlineData("timeout")]
    public async Task TransientFailureRetriesOnNextEventAndSuccessRecovers(string failure)
    {
        var (client, proxy) = Gateway();
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        proxy.Response = () => failure switch
        {
            "timeout" => throw new TimeoutException(),
            "unavailable" => throw new GatewayRequestException("m", "UNAVAILABLE", "AUTHENTICATED_PROFILE_UNAVAILABLE"),
            _ => throw new InvalidOperationException("hello pending")
        };
        await source.RefreshAsync();
        Assert.Null(source.DisplayName);
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        Assert.Equal(2, proxy.Requests.Count);
    }

    [Fact]
    public async Task ForbiddenDoesNotSpinAndGatewaySwitchClearsIt()
    {
        var (first, firstProxy) = Gateway();
        var (second, secondProxy) = Gateway();
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(first, ConnectionStatus.Connected);
        firstProxy.Response = () => throw new GatewayRequestException("m", "FORBIDDEN", null);
        await source.RefreshAsync();
        await source.RefreshAsync();
        Assert.Single(firstProxy.Requests);
        secondProxy.Response = () => Task.FromResult(Profile("Grace"));
        source.SetConnection(second, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Grace", source.DisplayName);
    }

    [Fact]
    public async Task StableFailureIsNotRetriedUntilProfileInvalidates()
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        proxy.Response = () => throw new GatewayRequestException("unknown method", "INVALID_REQUEST", null);
        await source.RefreshAsync();
        await source.RefreshAsync();
        Assert.Single(proxy.Requests);
        Assert.Single(errors);
        proxy.Response = () => Task.FromResult(Profile("Newly attached"));
        await source.RefreshAsync(invalidate: true);
        Assert.Equal("Newly attached", source.DisplayName);
    }

    [Fact]
    public void WorkspaceFooterUsesUpstreamOwnerLabelsWithoutStatusOrSetupStates()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("_identity.FooterLabel(Text(\"Owner.Text\"), Text(\"SharedOwner.Text\"),", source);
        Assert.Contains("LocalizationHelper.Format(\"WorkspaceShell_SharedOwnerNamedFormat\", ownerName)", source);
        Assert.Contains("footer.ApplyIdentity(label, name ?? \"\")", source);
        Assert.Contains("footer.ApplyAvatar(_ownerImage)", source);
        Assert.Contains("_chat.ApplyUserIdentity(name ?? \"\", _ownerImage)", source);
        Assert.Contains("_chat.ApplyUserIdentity(_identity.DisplayName ?? \"\", image)", source);
        Assert.DoesNotContain("settings/profile", source);
        Assert.DoesNotContain("SetUpProfile", source);
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var resw = File.ReadAllText(Path.Combine(root,
                "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            Assert.Contains("\"WorkspaceShell_Owner.Text\"", resw);
            var values = System.Xml.Linq.XDocument.Parse(resw).Root!.Elements("data")
                .ToDictionary(data => (string)data.Attribute("name")!, data => (string)data.Element("value")!);
            // The named label keeps the same localized marker as the unnamed label.
            Assert.Equal("{0} · " + values["WorkspaceShell_SharedOwner.Text"], values["WorkspaceShell_SharedOwnerNamedFormat"]);
            foreach (var removed in new[] { "OwnerLoading", "OwnerUnavailable", "OwnerUnnamed", "SetUpProfile" })
                Assert.DoesNotContain("WorkspaceShell_" + removed, resw);
        }
    }

    [Fact]
    public void ViewRefreshesOnProfileNotEverySessionAndInvalidatesAtClose()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("nameof(AppState.Presence) or nameof(AppState.SelfProfileRevision)", source);
        Assert.DoesNotContain("nameof(AppState.Presence) or nameof(AppState.Sessions)", source);
        Assert.Contains("_identity.SetConnection(null, ConnectionStatus.Disconnected);", source);
        var gatewayService = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "GatewayService.cs"));
        Assert.Contains("client.SelfProfileChanged += OnSelfProfileChanged;", gatewayService);
        Assert.Contains("client.SelfProfileChanged -= OnSelfProfileChanged;", gatewayService);
        Assert.Contains("EnqueueModelUpdate(() => _state.SelfProfileRevision++);", gatewayService);
    }

    [Fact]
    public async Task ProfileInvalidationSupersedesStalledAvatarAndRefreshesWithoutReconnect()
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var reads = 0;
        proxy.Response = () => Task.FromResult(++reads == 1
            ? Profile("Ada", "a@example.test")
            : Profile("Grace", "g@example.test"));
        var stalledStarted = new TaskCompletionSource();
        var image = new UserAvatarImage([7], "image/png");
        proxy.AvatarWithToken = async (_, _, token) =>
        {
            if (proxy.AvatarRequests.Count == 1)
            {
                stalledStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new UserAvatarResult(UserAvatarStatus.Ready, image);
        };
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        var first = source.RefreshAsync();
        await stalledStarted.Task;
        Assert.Equal("Ada", source.DisplayName);

        var second = source.RefreshAsync(invalidate: true);
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await second;

        Assert.Equal("Grace", source.DisplayName);
        Assert.Same(image, source.Avatar);
        Assert.Equal(2, reads);
        Assert.Empty(errors);
    }

    private static JsonElement AvatarProfile(string? name, string revision, bool uploaded = true) =>
        JsonSerializer.SerializeToElement(new { profile = new { id = "person", displayName = name, hasAvatar = uploaded, updatedAt = revision } });

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task RepeatedOrdinaryRefreshDuringSlowAvatarDoesNotRestartOrStarveIt()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        var release = new TaskCompletionSource();
        var image = new UserAvatarImage([5], "image/png");
        var cancelled = 0;
        proxy.AvatarWithToken = async (_, _, token) =>
        {
            using var _ = token.Register(() => Interlocked.Increment(ref cancelled));
            await release.Task;
            return new UserAvatarResult(UserAvatarStatus.Ready, image);
        };
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        var load = source.RefreshAsync();
        await SpinUntilAsync(() => proxy.AvatarRequests.Count == 1);
        var repeats = new List<Task>();
        for (var i = 0; i < 25; i++)
            repeats.Add(source.RefreshAsync());
        await Task.Delay(50);
        Assert.Equal(0, cancelled);
        Assert.Single(proxy.AvatarRequests);
        release.SetResult();
        await Task.WhenAll(repeats.Append(load)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(image, source.Avatar);
        Assert.Single(proxy.AvatarRequests);
        Assert.Equal("Ada", source.DisplayName);
    }

    [Fact]
    public async Task UnchangedOrdinaryRefreshKeepsImageWithoutRefetchOrFlash()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        var image = new UserAvatarImage([5], "image/png");
        proxy.Avatar = (_, _) => Task.FromResult(new UserAvatarResult(UserAvatarStatus.Ready, image));
        var states = new List<bool>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add(source!.Avatar is not null), category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        states.Clear();
        await source.RefreshAsync();
        await source.RefreshAsync();
        Assert.Same(image, source.Avatar);
        Assert.Single(proxy.AvatarRequests);
        Assert.DoesNotContain(false, states);
    }

    [Fact]
    public async Task KnownProfileNeverReturnsToOwnerOnOrdinaryRefresh()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile(null));
        var states = new List<string?>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add(source!.DisplayName), category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("person", source.DisplayName);
        states.Clear();
        await source.RefreshAsync();
        Assert.Equal("person", source.DisplayName);
        Assert.DoesNotContain(null, states);
    }

    [Fact]
    public async Task ChangedRevisionAndExplicitInvalidationRefetchWhileConnectionSwitchDropsOldImage()
    {
        var (client, proxy) = Gateway();
        var revision = "1";
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", revision));
        var counter = 0;
        proxy.Avatar = (_, _) => Task.FromResult(new UserAvatarResult(
            UserAvatarStatus.Ready, new UserAvatarImage([(byte)Interlocked.Increment(ref counter)], "image/png")));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal(1, counter);
        revision = "2";
        await source.RefreshAsync();
        Assert.Equal(2, counter);
        Assert.Equal(new byte[] { 2 }, source.Avatar!.Bytes);
        await source.RefreshAsync(invalidate: true);
        Assert.Equal(3, counter);
        Assert.Equal(new byte[] { 3 }, source.Avatar!.Bytes);

        var (other, otherProxy) = Gateway();
        otherProxy.Response = () => Task.FromResult(Profile("Grace"));
        source.SetConnection(other, ConnectionStatus.Connected);
        Assert.Null(source.Avatar);
        await source.RefreshAsync();
        Assert.Null(source.Avatar);
        Assert.Equal("Grace", source.DisplayName);
    }

    [Fact]
    public async Task FailedAvatarKeepsNameAndLaterInvalidationRefreshes()
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var reads = 0;
        proxy.Response = () => Task.FromResult(++reads == 1 ? Profile("Ada", "a@example.test") : Profile("Grace", "g@example.test"));
        proxy.Avatar = (_, _) => Task.FromResult(UserAvatarResult.Of(UserAvatarStatus.Failed));
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("Ada", source.DisplayName);
        Assert.Equal("AvatarFailed", Assert.Single(errors));

        await source.RefreshAsync(invalidate: true);
        Assert.Equal("Grace", source.DisplayName);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(UserAvatarStatus.NotFound)]
    [InlineData(UserAvatarStatus.Unsupported)]
    public async Task SettledNoImageOutcomeIsNotRefetchedOrRepaintedByOrdinaryRefresh(UserAvatarStatus status)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        proxy.Avatar = (_, _) => Task.FromResult(UserAvatarResult.Of(status));
        var states = new List<string?>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(
            () => states.Add(source!.DisplayName), category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        states.Clear();

        await source.RefreshAsync();
        await source.RefreshAsync();

        Assert.Single(proxy.AvatarRequests);
        Assert.Equal(3, proxy.Requests.Count);
        Assert.Null(source.Avatar);
        Assert.NotEmpty(states);
        Assert.All(states, name => Assert.Equal("Ada", name));
    }

    [Theory]
    [InlineData(UserAvatarStatus.Failed, "AvatarFailed")]
    [InlineData(UserAvatarStatus.Unauthorized, "AvatarUnauthorized")]
    public async Task FailedAvatarOutcomeIsReportedOnceAndNotRefetchedByOrdinaryRefresh(
        UserAvatarStatus status, string category)
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        proxy.Avatar = (_, _) => Task.FromResult(UserAvatarResult.Of(status));
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);

        await source.RefreshAsync();
        await source.RefreshAsync();
        await source.RefreshAsync();

        Assert.Equal([category], errors);
        Assert.Single(proxy.AvatarRequests);
        Assert.Equal(3, proxy.Requests.Count);
        Assert.Equal("Ada", source.DisplayName);
    }

    [Fact]
    public async Task ChangedRevisionRefetchesAfterARememberedFailureAndRecovers()
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var revision = "1";
        var image = new UserAvatarImage([9], "image/png");
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", revision));
        proxy.Avatar = (_, requested) => Task.FromResult(requested == "1"
            ? UserAvatarResult.Of(UserAvatarStatus.Failed)
            : new UserAvatarResult(UserAvatarStatus.Ready, image));
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Null(source.Avatar);

        revision = "2";
        await source.RefreshAsync();

        Assert.Same(image, source.Avatar);
        Assert.Equal(["1", "2"], proxy.AvatarRequests.Select(request => request.Revision));
        Assert.Equal(["AvatarFailed"], errors);
    }

    [Fact]
    public async Task OnlyExplicitInvalidationRetriesARememberedAvatarFailure()
    {
        var (client, proxy) = Gateway();
        var image = new UserAvatarImage([4], "image/png");
        var healthy = false;
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        proxy.Avatar = (_, _) => Task.FromResult(healthy
            ? new UserAvatarResult(UserAvatarStatus.Ready, image)
            : UserAvatarResult.Of(UserAvatarStatus.Failed));
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();

        healthy = true;
        await source.RefreshAsync();
        Assert.Null(source.Avatar);
        Assert.Single(proxy.AvatarRequests);

        await source.RefreshAsync(invalidate: true);
        Assert.Same(image, source.Avatar);
        Assert.Equal(2, proxy.AvatarRequests.Count);
    }

    [Fact]
    public async Task CancelledAvatarOutcomeIsNotRememberedSoTheNextRefreshRetries()
    {
        var (client, proxy) = Gateway();
        var image = new UserAvatarImage([6], "image/png");
        var superseded = true;
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        proxy.Avatar = (_, _) => Task.FromResult(superseded
            ? UserAvatarResult.Of(UserAvatarStatus.Cancelled)
            : new UserAvatarResult(UserAvatarStatus.Ready, image));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Null(source.Avatar);

        superseded = false;
        await source.RefreshAsync();

        Assert.Same(image, source.Avatar);
        Assert.Equal(2, proxy.AvatarRequests.Count);
    }

    [Fact]
    public async Task UnresolvedExplicitRefetchKeepsTheImageAndTheNextRefreshRetriesItOnce()
    {
        var (client, proxy) = Gateway();
        var first = new UserAvatarImage([1], "image/png");
        var second = new UserAvatarImage([2], "image/png");
        var attempts = 0;
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        proxy.Avatar = (_, _) => Task.FromResult(++attempts switch
        {
            1 => new UserAvatarResult(UserAvatarStatus.Ready, first),
            2 => UserAvatarResult.Of(UserAvatarStatus.Cancelled),
            _ => new UserAvatarResult(UserAvatarStatus.Ready, second),
        });
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Same(first, source.Avatar);

        await source.RefreshAsync(invalidate: true);
        Assert.Equal(2, proxy.AvatarRequests.Count);
        Assert.Same(first, source.Avatar);

        await source.RefreshAsync();
        Assert.Equal(3, proxy.AvatarRequests.Count);
        Assert.Same(second, source.Avatar);

        await source.RefreshAsync();
        Assert.Equal(3, proxy.AvatarRequests.Count);
        Assert.Same(second, source.Avatar);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(UriFormatException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task UnexpectedAvatarExceptionKeepsTheProfileAndReportsOnlyItsTypeOnce(Type exceptionType)
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        proxy.Response = () => Task.FromResult(Profile("Ada", "a@example.test"));
        proxy.Avatar = (_, _) => throw (Exception)Activator.CreateInstance(exceptionType, "sensitive")!;
        var source = new WorkspaceIdentitySource(() => { }, errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);

        await source.RefreshAsync();
        await source.RefreshAsync();

        Assert.Equal("Ada", source.DisplayName);
        Assert.Null(source.Avatar);
        Assert.Equal(["Avatar" + exceptionType.Name], errors);
        Assert.Single(proxy.AvatarRequests);
    }

    [Fact]
    public async Task FailedReadWithQueuedOrdinaryRefreshSettlesInsteadOfRetrying()
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var read = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => read.Task;
        var states = new List<string?>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(
            () => states.Add(source!.DisplayName), errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        var load = source.RefreshAsync();
        var queued = source.RefreshAsync();

        read.SetException(new GatewayRequestException("busy", "UNAVAILABLE", null));
        await Task.WhenAll(load, queued).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Unavailable"], errors);
        Assert.Single(proxy.Requests);
        Assert.Null(source.DisplayName);
        Assert.Null(states[^1]);
    }

    [Fact]
    public async Task InvalidationSupersedesAStalledUsersSelfReadWithoutLosingTheKnownIdentity()
    {
        var (client, proxy) = Gateway();
        var errors = new List<string>();
        var stalled = new TaskCompletionSource<JsonElement>();
        var reads = 0;
        proxy.Response = () => ++reads switch
        {
            1 => Task.FromResult(Profile("Ada")),
            2 => stalled.Task,
            _ => Task.FromResult(Profile("Grace")),
        };
        var states = new List<string?>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(
            () => states.Add(source!.DisplayName), errors.Add);
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        states.Clear();

        var stalledRead = source.RefreshAsync();
        Assert.Equal(2, reads);
        Assert.Equal("Ada", source.DisplayName);

        var invalidation = source.RefreshAsync(invalidate: true);
        await Task.WhenAll(stalledRead, invalidation).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, reads);
        Assert.Equal("Grace", source.DisplayName);
        Assert.Empty(errors);
        Assert.NotEmpty(states);
        Assert.DoesNotContain(null, states);

        stalled.SetResult(Profile("Stale owner"));
        Assert.Equal("Grace", source.DisplayName);
        Assert.Equal(3, reads);
    }

    private sealed class SupersededReadFault : Exception
    {
    }

    private sealed class UnobservedControlFault : Exception
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> SupersedeAStalledReadThatLaterFaultsAsync()
    {
        var (client, proxy) = Gateway();
        var stalled = new TaskCompletionSource<JsonElement>();
        var reads = 0;
        proxy.Response = () => ++reads == 1 ? stalled.Task : Task.FromResult(Profile("Grace"));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        var load = source.RefreshAsync();
        await Task.WhenAll(load, source.RefreshAsync(invalidate: true)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Grace", source.DisplayName);
        stalled.SetException(new SupersededReadFault());
        return new WeakReference(stalled.Task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultATaskNobodyObserves() =>
        new TaskCompletionSource<JsonElement>().SetException(new UnobservedControlFault());

    private const int CollectionPassBudget = 100;

    [Fact]
    public async Task SupersededReadThatLaterFaultsNeverSurfacesAsAnUnobservedTaskException()
    {
        var reported = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            // Process-wide event: keep only the faults this test creates.
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                if (inner is SupersededReadFault or UnobservedControlFault)
                    reported.Enqueue(inner);
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var abandoned = await SupersedeAStalledReadThatLaterFaultsAsync();
            FaultATaskNobodyObserves();

            // The control proves finalization ran and the detector works. The weak reference proves the
            // abandoned read itself was collected, so the absence check cannot pass vacuously.
            for (var pass = 0;
                pass < CollectionPassBudget &&
                !(reported.Any(fault => fault is UnobservedControlFault) && !abandoned.IsAlive);
                pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // A collection another test triggers can clear the weak reference and run only some finalizers
            // before the loop stops, so drain whatever is still queued before asserting nothing late surfaced.
            GC.WaitForPendingFinalizers();

            Assert.Contains(reported, fault => fault is UnobservedControlFault);
            Assert.False(abandoned.IsAlive);
            Assert.DoesNotContain(reported, fault => fault is SupersededReadFault);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact]
    public void UserSelfDeadlineDefaultsAndHonorsAPositiveEnvironmentOverride()
    {
        const string variable = WorkspaceIdentitySource.UserSelfTimeoutEnvironmentVariable;
        using var environment = new EnvironmentScope(variable, null);
        Assert.Equal(12_000, WorkspaceIdentitySource.UserSelfTimeoutMs());

        environment.Set(variable, "2500");
        Assert.Equal(2_500, WorkspaceIdentitySource.UserSelfTimeoutMs());

        foreach (var invalid in new[] { "0", "-5", "soon" })
        {
            environment.Set(variable, invalid);
            Assert.Equal(12_000, WorkspaceIdentitySource.UserSelfTimeoutMs());
        }
    }

    [Theory]
    [InlineData(null, 12_000)]
    [InlineData("4321", 4_321)]
    public async Task UsersSelfReadIsSentWithTheConfiguredDeadline(string? configured, int expected)
    {
        using var environment = new EnvironmentScope(WorkspaceIdentitySource.UserSelfTimeoutEnvironmentVariable, configured);
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);

        await source.RefreshAsync();

        Assert.Equal([expected], proxy.Timeouts);
    }
}
