using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared;
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
    public void MissingNameAndEmailsHasNoIdentity() =>
        Assert.Null(WorkspaceIdentitySource.ReadDisplayName(Profile(null)));

    [Fact]
    public void AvatarRequestedOnlyForUploadOrEmail()
    {
        var none = WorkspaceIdentitySource.ReadProfile(Profile("Ada"));
        Assert.Null(none.AvatarProfileId);
        var email = WorkspaceIdentitySource.ReadProfile(Profile("Ada", "a@example.test"));
        Assert.Equal("person", email.AvatarProfileId);
        var uploaded = WorkspaceIdentitySource.ReadProfile(JsonSerializer.SerializeToElement(
            new { profile = new { id = "person", hasAvatar = true, updatedAt = 42 } }));
        Assert.Equal("person", uploaded.AvatarProfileId);
        Assert.Equal("42", uploaded.AvatarRevision);
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
    public async Task GatewayOwnerRequestsGatewayRouteWithoutEmailOrUpload()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(JsonSerializer.SerializeToElement(
            new { profile = new { id = "gateway-owner", displayName = "Owner Person" } }));
        proxy.Avatar = (_, _) => Task.FromResult(UserAvatarResult.Of(UserAvatarStatus.NotFound));
        var source = new WorkspaceIdentitySource(() => { }, category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal("gateway-owner", Assert.Single(proxy.AvatarRequests).ProfileId);
        Assert.Equal("Owner Person", source.DisplayName);
        Assert.Null(source.Avatar);
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
    public async Task LoadingStateIsObservableAtEveryNotification_Success()
    {
        var (client, proxy) = Gateway();
        var pending = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => pending.Task;
        var states = new List<(bool Loading, string? Name)>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add((source!.IsLoading, source.DisplayName)), _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        var load = source.RefreshAsync();
        Assert.True(source.IsLoading);
        pending.SetResult(Profile("Ada"));
        await load;
        Assert.False(source.IsLoading);
        Assert.Equal([(true, (string?)null), (false, "Ada")], states);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("unsupported")]
    public async Task LoadingClearsBeforeTerminalNotification_WhenNoUsableProfile(string outcome)
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => outcome switch
        {
            "blank" => Task.FromResult(Profile(null)),
            "timeout" => throw new TimeoutException("x"),
            "malformed" => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            _ => throw new InvalidOperationException("x"),
        };
        var states = new List<(bool Loading, string? Name)>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add((source!.IsLoading, source.DisplayName)), _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal([(true, (string?)null), (false, (string?)null)], states);
        Assert.False(source.IsLoading);
    }

    [Fact]
    public async Task ValidNamelessProfileIsUnnamedNotUnavailable_AtTerminalNotification()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile(null));
        var states = new List<(bool Loading, bool Has, bool Unnamed)>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(
            () => states.Add((source!.IsLoading, source.HasProfile, source.IsUnnamedProfile)), _ => Assert.Fail("reported"));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.Equal([(true, false, false), (false, true, true)], states);
    }

    [Fact]
    public async Task NamedProfileIsNeverUnnamed()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile("Ada"));
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.True(source.HasProfile);
        Assert.False(source.IsUnnamedProfile);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("forbidden")]
    public async Task FailedLookupAfterValidProfileClearsHasProfile(string outcome)
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
            : Task.FromResult(Profile(null));
        var states = new List<bool>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add(source!.HasProfile), _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.True(source.IsUnnamedProfile);
        fail = true;
        states.Clear();
        await source.RefreshAsync(invalidate: true);
        Assert.False(source.HasProfile);
        Assert.False(source.IsUnnamedProfile);
        Assert.All(states, has => Assert.False(has));
    }

    [Fact]
    public async Task ContextSwitchAndDisconnectClearHasProfile()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile(null));
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.True(source.HasProfile);
        var (next, _) = Gateway();
        source.SetConnection(next, ConnectionStatus.Connected);
        Assert.False(source.HasProfile);
        Assert.False(source.IsUnnamedProfile);
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        source.SetConnection(null, ConnectionStatus.Disconnected);
        Assert.False(source.HasProfile);
    }

    [Fact]
    public async Task OldGenerationCompletionNeverClearsNewContextLoading()
    {
        var (client, proxy) = Gateway();
        var oldRequest = new TaskCompletionSource<JsonElement>();
        proxy.Response = () => oldRequest.Task;
        var states = new List<(bool Loading, string? Name)>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add((source!.IsLoading, source.DisplayName)), _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        var old = source.RefreshAsync();
        var (next, nextProxy) = Gateway();
        var newRequest = new TaskCompletionSource<JsonElement>();
        nextProxy.Response = () => newRequest.Task;
        source.SetConnection(next, ConnectionStatus.Connected);
        var current = source.RefreshAsync();
        oldRequest.SetResult(Profile("Old"));
        await old;
        Assert.True(source.IsLoading);
        Assert.Null(source.DisplayName);
        newRequest.SetResult(Profile("New"));
        await current;
        Assert.False(source.IsLoading);
        Assert.Equal("New", source.DisplayName);
        Assert.DoesNotContain(states, s => s.Name == "Old");
    }

    [Fact]
    public async Task DisconnectEndsLoadingAndMissingGrantDoesNotLoadForever()
    {
        var (client, proxy) = Gateway();
        proxy.Scopes = ["operator.pairing"];
        var source = new WorkspaceIdentitySource(() => { }, _ => { });
        source.SetConnection(client, ConnectionStatus.Connected);
        Assert.True(source.IsLoading);
        await source.RefreshAsync();
        Assert.False(source.IsLoading);
        var (c2, p2) = Gateway();
        p2.Response = () => new TaskCompletionSource<JsonElement>().Task;
        source.SetConnection(c2, ConnectionStatus.Connected);
        _ = source.RefreshAsync();
        Assert.True(source.IsLoading);
        source.SetConnection(c2, ConnectionStatus.Disconnected);
        Assert.False(source.IsLoading);
    }

    [Fact]
    public void DiagnosticsNameTheTrueSourceAndCarryNoPayload()
    {
        Assert.Contains("avatar", WorkspaceIdentitySource.DescribeUnavailable("AvatarFailed"));
        Assert.Contains("name is kept", WorkspaceIdentitySource.DescribeUnavailable("AvatarFailed"));
        var profile = WorkspaceIdentitySource.DescribeUnavailable("TimeoutException");
        Assert.Contains("users.self", profile);
        Assert.DoesNotContain("fallback", profile);
    }

    [Theory]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(ArgumentException), true)]
    [InlineData(typeof(ObjectDisposedException), true)]
    [InlineData(typeof(System.Runtime.InteropServices.COMException), true)]
    [InlineData(typeof(OutOfMemoryException), false)]
    [InlineData(typeof(NullReferenceException), false)]
    public void DecodeFailureClassifierIsNarrow(Type type, bool nonFatal)
    {
        var ex = type == typeof(ObjectDisposedException) ? new ObjectDisposedException("x") : (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(nonFatal, WorkspaceIdentitySource.IsAvatarDecodeFailure(ex));
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
    public void UnnamedProfileUsesDistinctCopyAndDashboardProfileAction()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root,
            "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        Assert.Contains("\"OwnerUnnamed.Text\"", source);
        Assert.Contains("OpenDashboard(\"settings/profile\")", source);
        Assert.Contains("unnamed ? Visibility.Visible : Visibility.Collapsed", source);
        Assert.Contains("Add(\"Settings\"", source);
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "pt-br", "zh-cn", "zh-tw" })
        {
            var resw = File.ReadAllText(Path.Combine(root,
                "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            Assert.Contains("WorkspaceShell_OwnerUnnamed.Text", resw);
            Assert.Contains("WorkspaceShell_SetUpProfile", resw);
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
        Assert.False(source.IsLoading);

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
    public async Task UnchangedOrdinaryRefreshKeepsImageWithoutRefetchOrLoadingFlash()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(AvatarProfile("Ada", "1"));
        var image = new UserAvatarImage([5], "image/png");
        proxy.Avatar = (_, _) => Task.FromResult(new UserAvatarResult(UserAvatarStatus.Ready, image));
        var states = new List<(bool Loading, bool HasAvatar)>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add((source!.IsLoading, source.Avatar is not null)), category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        states.Clear();
        await source.RefreshAsync();
        await source.RefreshAsync();
        Assert.Same(image, source.Avatar);
        Assert.Single(proxy.AvatarRequests);
        Assert.DoesNotContain(states, s => s.Loading || !s.HasAvatar);
    }

    [Fact]
    public async Task KnownUnnamedProfileNeverReturnsToLoadingOnOrdinaryRefresh()
    {
        var (client, proxy) = Gateway();
        proxy.Response = () => Task.FromResult(Profile(null));
        var states = new List<(bool Loading, bool Unnamed)>();
        WorkspaceIdentitySource? source = null;
        source = new WorkspaceIdentitySource(() => states.Add((source!.IsLoading, source.IsUnnamedProfile)), category => Assert.Fail(category));
        source.SetConnection(client, ConnectionStatus.Connected);
        await source.RefreshAsync();
        Assert.True(source.IsUnnamedProfile);
        states.Clear();
        await source.RefreshAsync();
        Assert.True(source.IsUnnamedProfile);
        Assert.DoesNotContain(states, s => s.Loading || !s.Unnamed);
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
}
