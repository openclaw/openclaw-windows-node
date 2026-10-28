using System.Reflection;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ProfileCompletionTests
{
    private sealed class FakeProvider(AccountIdentityResult result) : IWindowsAccountIdentityProvider
    {
        public int Reads;
        public Task<AccountIdentityResult> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    public class Proxy : DispatchProxy
    {
        public bool Connected = true;
        public IReadOnlyList<string> Scopes = ["operator.write"];
        public object SelfProfile = new { profile = new { id = "gateway-owner", displayName = (string?)null, hasAvatar = false } };
        public Func<string, Task<JsonElement>>? OnWrite;
        public Action? OnSelfRead;
        public List<string> Methods = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_GrantedOperatorScopes" => Scopes,
            "SendWizardRequestAsync" => Send((string)args![0]!),
            _ => throw new NotSupportedException(method?.Name),
        };

        private Task<JsonElement> Send(string method)
        {
            Methods.Add(method);
            if (method == "users.self")
            {
                OnSelfRead?.Invoke();
                return Task.FromResult(JsonSerializer.SerializeToElement(SelfProfile));
            }
            return OnWrite is null
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { profile = new { id = "gateway-owner" } }))
                : OnWrite(method);
        }
    }

    private static readonly AccountPhotoDraft Photo = new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], AccountPhotoPolicy.Png);

    private static AccountIdentityResult Both => new(AccountFieldStatus.Available, "Ada Lovelace", AccountFieldStatus.Available, Photo);

    private static (ProfileCompletionService Service, Proxy Proxy, FakeProvider Provider, IOperatorGatewayClient Client)
        Create(AccountIdentityResult? account = null, Func<string?>? gatewayId = null, Func<IOperatorGatewayClient?>? clientOverride = null, Func<bool>? eligible = null)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, Proxy>();
        var provider = new FakeProvider(account ?? Both);
        var service = new ProfileCompletionService(
            () => clientOverride is null ? client : clientOverride() ?? client, gatewayId ?? (() => "g1"), eligible ?? (() => true), provider);
        return (service, (Proxy)(object)client, provider, client);
    }

    [Fact]
    public async Task Save_GatewaySwitchedBeforeSave_SendsNoWindowsDataToTheOtherGateway()
    {
        var id = "g1";
        var (service, proxyA, _, _) = Create(gatewayId: () => id);
        var prep = await service.PrepareAsync(CancellationToken.None);
        id = "g2"; // both gateways expose a blank gateway-owner profile
        proxyA.Methods.Clear();
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(new ProfileSaveResult(ProfileStep.ContextChanged, ProfileStep.ContextChanged), result);
        Assert.Empty(proxyA.Methods);
    }

    [Fact]
    public async Task Save_SameGatewayNewClient_IsContextChanged_WithoutRpc()
    {
        IOperatorGatewayClient? replacement = null;
        var (service, proxyA, _, _) = Create(clientOverride: () => replacement);
        var prep = await service.PrepareAsync(CancellationToken.None);
        replacement = DispatchProxy.Create<IOperatorGatewayClient, Proxy>();
        var other = (Proxy)(object)replacement;
        proxyA.Methods.Clear();
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.ContextChanged, result.Name);
        Assert.Empty(proxyA.Methods);
        Assert.Empty(other.Methods);
    }

    [Fact]
    public async Task Save_RecordNoLongerEligible_IsContextChanged_WithoutRpc()
    {
        var eligible = true;
        var (service, proxy, _, _) = Create(eligible: () => eligible);
        var prep = await service.PrepareAsync(CancellationToken.None);
        eligible = false; // remote or tunnel record now active
        proxy.Methods.Clear();
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.ContextChanged, result.Photo);
        Assert.Empty(proxy.Methods);
    }

    [Fact]
    public async Task Save_SwitchAfterNameWrite_ReportsTruthfulPartial_AndNeverWritesPhoto()
    {
        var id = "g1";
        var (service, proxy, _, _) = Create(gatewayId: () => id);
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.Methods.Clear();
        proxy.OnWrite = method => { id = "g2"; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); };
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.Saved, result.Name);
        Assert.Equal(ProfileStep.ContextChanged, result.Photo);
        Assert.DoesNotContain("users.setAvatar", proxy.Methods);
    }

    [Fact]
    public async Task Prepare_NotEligible_IsUnavailable_AndNeverReadsAccount()
    {
        var (service, proxy, provider, _) = Create(eligible: () => false);
        Assert.Equal(ProfileOffer.Unavailable, (await service.PrepareAsync(CancellationToken.None)).Offer);
        Assert.Equal(0, provider.Reads);
        Assert.Empty(proxy.Methods);
    }

    [Fact]
    public void Eligibility_RequiresManagedMarkers_NotLocalhostOrCredentialType()
    {
        Assert.True(ProfileCompletionEligibility.IsEligible(new GatewayRecord { Id = "a", Url = "ws://127.0.0.1:1", SetupManagedDistroName = "OpenClawGateway" }));
        Assert.True(ProfileCompletionEligibility.IsEligible(new GatewayRecord { Id = "a", Url = "ws://127.0.0.1:1", NativePackageFamilyName = "OpenClaw_x" }));
        Assert.True(ProfileCompletionEligibility.IsEligible(new GatewayRecord { Id = "a", Url = "ws://127.0.0.1:1", SetupManagedDistroName = "d", SharedGatewayToken = "t" }));
        Assert.False(ProfileCompletionEligibility.IsEligible(new GatewayRecord { Id = "a", Url = "ws://127.0.0.1:1", IsLocal = true }));
        Assert.False(ProfileCompletionEligibility.IsEligible(new GatewayRecord { Id = "a", Url = "wss://remote.example" }));
        Assert.False(ProfileCompletionEligibility.IsEligible(null));
    }

    [Fact]
    public async Task Prepare_BlankOwner_ReadsSelfThenAccount_WithoutAnyWrite()
    {
        var (service, proxy, provider, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        Assert.Equal(ProfileOffer.Offer, prep.Offer);
        Assert.Equal(1, provider.Reads);
        Assert.Equal(["users.self"], proxy.Methods);
    }

    [Theory]
    [InlineData("named")]
    [InlineData("email")]
    [InlineData("avatar")]
    [InlineData("provider")]
    public async Task Prepare_ExistingProfile_IsNotOfferedAndAccountIsNeverRead(string kind)
    {
        var (service, proxy, provider, _) = Create();
        proxy.SelfProfile = kind switch
        {
            "named" => new { profile = new { id = "gateway-owner", displayName = "Barbara" } },
            "email" => new { profile = new { id = "gateway-owner", emails = new[] { "a@example.com" } } },
            "avatar" => new { profile = new { id = "gateway-owner", hasAvatar = true } },
            _ => new { profile = new { id = "github-123", displayName = (string?)null } },
        };
        var prep = await service.PrepareAsync(CancellationToken.None);
        Assert.Equal(ProfileOffer.NotNeeded, prep.Offer);
        Assert.Equal(0, provider.Reads);
    }

    [Fact]
    public async Task Prepare_Disconnected_IsUnavailable()
    {
        var (service, proxy, provider, _) = Create();
        proxy.Connected = false;
        Assert.Equal(ProfileOffer.Unavailable, (await service.PrepareAsync(CancellationToken.None)).Offer);
        Assert.Equal(0, provider.Reads);
    }

    [Fact]
    public async Task Prepare_AccountDenied_StillOffersManualEntry()
    {
        var (service, _, _, _) = Create(AccountIdentityResult.Unavailable(AccountFieldStatus.Denied));
        var prep = await service.PrepareAsync(CancellationToken.None);
        Assert.Equal(ProfileOffer.Offer, prep.Offer);
        Assert.Null(prep.Account!.DisplayName);
    }

    [Fact]
    public async Task Prepare_Cancellation_Propagates()
    {
        var (service, _, _, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(cts.Token));
    }

    [Fact]
    public async Task Prepare_GatewayChangesDuringRead_IsUnavailable()
    {
        var id = "g1";
        var (service, proxy, _, _) = Create(gatewayId: () => id);
        proxy.OnSelfRead = () => id = "g2";
        Assert.Equal(ProfileOffer.Unavailable, (await service.PrepareAsync(CancellationToken.None)).Offer);
    }

    [Fact]
    public async Task Save_NameAndPhoto_RereadsBeforeEachWrite_AndSendsExactPayloads()
    {
        var (service, proxy, _, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.Methods.Clear();
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(new ProfileSaveResult(ProfileStep.Saved, ProfileStep.Saved), result);
        Assert.Equal(["users.self", "users.setDisplayName", "users.self", "users.setAvatar"], proxy.Methods);
    }

    [Fact]
    public async Task Save_ExistingNameAppearsBeforeWrite_IsKept()
    {
        var (service, proxy, _, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.SelfProfile = new { profile = new { id = "gateway-owner", displayName = "Someone" } };
        proxy.Methods.Clear();
        var result = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.ExistingValue, result.Name);
        Assert.DoesNotContain("users.setDisplayName", proxy.Methods);
    }

    [Fact]
    public async Task Save_WithoutWriteScope_DeniesWithoutRpc()
    {
        var (service, proxy, _, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.Scopes = ["operator.read"];
        proxy.Methods.Clear();
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(new ProfileSaveResult(ProfileStep.Denied, ProfileStep.Denied), result);
        Assert.Empty(proxy.Methods);
    }

    [Fact]
    public async Task Save_ForbiddenReply_IsDenied_AndNameSurvivesPhotoFailure()
    {
        var (service, proxy, _, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.OnWrite = method => method == "users.setAvatar"
            ? Task.FromException<JsonElement>(GatewayRequestErrors.Create("x", "FORBIDDEN", null))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.Saved, result.Name);
        Assert.Equal(ProfileStep.Denied, result.Photo);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task Save_Retry_OnlyWritesMissingField()
    {
        var (service, proxy, _, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.Methods.Clear();
        var result = await service.SaveAsync(prep, "Ada", Photo, retryNameOnly: false, retryPhotoOnly: true, CancellationToken.None);
        Assert.Equal(ProfileStep.NotAttempted, result.Name);
        Assert.Equal(["users.self", "users.setAvatar"], proxy.Methods);
    }

    [Fact]
    public async Task Save_GatewayChangesBeforeWrite_WritesNothing()
    {
        var id = "g1";
        var (service, proxy, _, _) = Create(gatewayId: () => id);
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.Methods.Clear();
        proxy.OnSelfRead = () => id = "g2";
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.ContextChanged, result.Name);
        Assert.Equal(ProfileStep.ContextChanged, result.Photo);
        Assert.DoesNotContain(proxy.Methods, m => m.StartsWith("users.set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_ConcurrentSave_IsRejected()
    {
        var (service, proxy, _, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        var gate = new TaskCompletionSource<JsonElement>();
        proxy.OnWrite = _ => gate.Task;
        var first = service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None));
        gate.SetResult(JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(ProfileStep.Saved, (await first).Name);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Ada", true)]
    [InlineData("A\u0007da", false)]
    public void NamePolicy_RejectsBlankAndControlCharacters(string raw, bool valid) =>
        Assert.Equal(valid, ProfileNamePolicy.TryNormalize(raw, out _));

    [Fact]
    public void NamePolicy_EnforcesSchemaMaxLength()
    {
        Assert.True(ProfileNamePolicy.TryNormalize(new string('a', 256), out _));
        Assert.False(ProfileNamePolicy.TryNormalize(new string('a', 257), out _));
    }

    [Fact]
    public void PhotoPolicy_ChecksHeaderDimensionsBeforeDecode()
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 208);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 208);
        Assert.Equal(AccountFieldStatus.Available, AccountPhotoPolicy.CheckSource(png.Length, png));

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 30000);
        Assert.Equal(AccountFieldStatus.TooLarge, AccountPhotoPolicy.CheckSource(png.Length, png));
        Assert.Equal(AccountFieldStatus.TooLarge, AccountPhotoPolicy.CheckSource(AccountPhotoPolicy.MaxSourceBytes + 1, png));
        Assert.Equal(AccountFieldStatus.Unsupported, AccountPhotoPolicy.CheckSource(4, [1, 2, 3, 4]));
        Assert.Equal(AccountFieldStatus.Empty, AccountPhotoPolicy.CheckSource(0, []));
    }

    [Fact]
    public void PhotoPolicy_OutputCapsMatchGatewaySchema()
    {
        Assert.Equal(512 * 1024, AccountPhotoPolicy.MaxEncodedBytes);
        Assert.True(AccountPhotoPolicy.Base64Length(AccountPhotoPolicy.MaxEncodedBytes) <= AccountPhotoPolicy.MaxBase64Length);
        Assert.False(AccountPhotoPolicy.TryCreateDraft(new byte[AccountPhotoPolicy.MaxEncodedBytes + 1], out _));
    }

    [Fact]
    public void DeclineStore_HoldsOnlyRecordIds_AndPrunesRemovedRecords()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-decline-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ProfileCompletionDeclineStore(path);
            store.Decline("g1", ["g1", "g2"]);
            store.Decline("g2", ["g2"]);
            Assert.False(store.IsDeclined("g1"));
            Assert.True(store.IsDeclined("g2"));
            Assert.DoesNotContain("Ada", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ManifestDeclaresUserAccountInformation_AndProviderUsesDocumentedCurrentUserContract()
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? FindRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Package.appxmanifest"));
        Assert.Contains("<uap:Capability Name=\"userAccountInformation\" />", manifest);

        var provider = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Services", "WindowsAccountIdentityProvider.cs"));
        Assert.Contains("User.GetDefault()", provider);
        Assert.Contains("ApiInformation.IsMethodPresent", provider);
        Assert.DoesNotContain("FindAllAsync", provider);
        Assert.DoesNotContain("GetSystemIdForUser", provider);
        Assert.DoesNotContain("Logger", provider);
    }

    private static GatewayConnectionLostException Lost() => new(1006, null);

    // Production paths: Drain(wizardDisconnect) yields GatewayConnectionLostException; parameterless Drain and
    // connection-lifetime WaitAsync yield a plain OperationCanceledException or TaskCanceledException.
    public static TheoryData<Exception> TransportLosses => new()
    {
        Lost(),
        new OperationCanceledException("Request canceled"),
        new TaskCanceledException(),
    };

    private static async Task<(ProfileCompletionService Service, Proxy Proxy, ProfilePreparation Prep)> Prepared()
    {
        var (service, proxy, _, _) = Create();
        var prep = await service.PrepareAsync(CancellationToken.None);
        proxy.Methods.Clear();
        return (service, proxy, prep);
    }

    [Theory]
    [MemberData(nameof(TransportLosses))]
    public async Task Prepare_TransportLossDuringRead_IsUnavailable_NotCancellation(Exception loss)
    {
        var (service, proxy, _, _) = Create();
        proxy.OnSelfRead = () => throw loss;
        Assert.Equal(ProfileOffer.Unavailable, (await service.PrepareAsync(CancellationToken.None)).Offer);
    }

    [Theory]
    [MemberData(nameof(TransportLosses))]
    public async Task Save_TransportLossAtPhotoRecheck_KeepsSavedName_AndIsIncomplete(Exception loss)
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnSelfRead = () => { if (proxy.Methods.Count(m => m == "users.self") == 2) throw loss; };
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(new ProfileSaveResult(ProfileStep.Saved, ProfileStep.ConnectionLost), result);
        Assert.False(result.IsComplete);
        Assert.DoesNotContain("users.setAvatar", proxy.Methods);
    }

    [Theory]
    [MemberData(nameof(TransportLosses))]
    public async Task Save_TransportLossAtNameRecheck_ReportsLost_AndWritesNothing(Exception loss)
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnSelfRead = () => throw loss;
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(new ProfileSaveResult(ProfileStep.ConnectionLost, ProfileStep.ConnectionLost), result);
        Assert.DoesNotContain(proxy.Methods, m => m.StartsWith("users.set", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(TransportLosses))]
    public async Task Save_TransportLossAtNameWrite_IsUncertain_AndPhotoIsNotSent(Exception loss)
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = _ => Task.FromException<JsonElement>(loss);
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.Uncertain, result.Name);
        Assert.False(result.IsComplete);
        Assert.DoesNotContain("users.setAvatar", proxy.Methods);
    }

    [Theory]
    [MemberData(nameof(TransportLosses))]
    public async Task Save_TransportLossAtPhotoWrite_KeepsSavedName_PhotoUncertain(Exception loss)
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = method => method == "users.setAvatar"
            ? Task.FromException<JsonElement>(loss)
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Equal(new ProfileSaveResult(ProfileStep.Saved, ProfileStep.Uncertain), result);
        Assert.False(result.IsComplete);
    }

    [Theory]
    [InlineData("users.setDisplayName")]
    [InlineData("users.setAvatar")]
    public async Task Save_WriteReplyTimeout_IsUncertain_NotDefiniteFailure(string timedOut)
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = method => method == timedOut
            ? Task.FromException<JsonElement>(new TimeoutException("Timed out waiting for response"))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Contains(ProfileStep.Uncertain, new[] { result.Name, result.Photo });
        Assert.DoesNotContain(ProfileStep.Failed, new[] { result.Name, result.Photo });
        Assert.False(result.IsComplete);
        if (timedOut == "users.setAvatar") Assert.Equal(ProfileStep.Saved, result.Name);
    }

    [Fact]
    public async Task Save_RetryAfterUncertain_RereadsAndReportsAppliedValueAsExisting_WithoutRewriting()
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = _ => Task.FromException<JsonElement>(new TimeoutException("late"));
        Assert.Equal(ProfileStep.Uncertain, (await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None)).Name);

        proxy.OnWrite = null;
        proxy.SelfProfile = new { profile = new { id = "gateway-owner", displayName = "Ada" } };
        proxy.Methods.Clear();
        var retry = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.ExistingValue, retry.Name);
        Assert.Equal(["users.self"], proxy.Methods);
    }

    [Fact]
    public async Task Save_RetryAfterUncertain_IsFencedToConsentedContext()
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = _ => Task.FromException<JsonElement>(new TimeoutException("late"));
        await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        proxy.Connected = false;
        proxy.Methods.Clear();
        var retry = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.ContextChanged, retry.Name);
        Assert.Empty(proxy.Methods);
    }

    [Fact]
    public async Task Save_CallerCancellationAfterName_KeepsSavedName_AndRequestedPhotoIsNeverComplete()
    {
        var (service, proxy, prep) = await Prepared();
        using var cts = new CancellationTokenSource();
        proxy.OnWrite = _ => { cts.Cancel(); return Task.FromResult(JsonSerializer.SerializeToElement(new { })); };
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, cts.Token);
        Assert.Equal(new ProfileSaveResult(ProfileStep.Saved, ProfileStep.Uncertain), result);
        Assert.False(result.IsComplete);
        Assert.DoesNotContain("users.setAvatar", proxy.Methods);

        using var early = new CancellationTokenSource();
        early.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(prep, "Ada", Photo, false, false, early.Token));
    }
    [Theory]
    [InlineData("users.setDisplayName")]
    [InlineData("users.setAvatar")]
    public async Task Save_UncodedLocalDispatchFailureAtWrite_IsUncertain_NotDefiniteFailure(string method)
    {
        // Mirrors SendResponseRequestAsync: SendRawAsync returns false (even after SendAsync completed when the
        // generation changed) and the RPC raises an uncoded InvalidOperationException.
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = m => m == method
            ? Task.FromException<JsonElement>(new InvalidOperationException($"Gateway connection changed before {m} could be sent."))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var result = await service.SaveAsync(prep, "Ada", Photo, false, false, CancellationToken.None);
        Assert.Contains(ProfileStep.Uncertain, new[] { result.Name, result.Photo });
        Assert.DoesNotContain(ProfileStep.Failed, new[] { result.Name, result.Photo });
        Assert.False(result.IsComplete);
        if (method == "users.setAvatar") Assert.Equal(ProfileStep.Saved, result.Name);
    }

    [Fact]
    public async Task Save_CodedGatewayRejectionAtWrite_StaysDefiniteFailure()
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = _ => Task.FromException<JsonElement>(GatewayRequestErrors.Create("nope", "INVALID_REQUEST", null));
        var result = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.Failed, result.Name);
    }

    [Fact]
    public async Task Save_UncodedFailureAtRead_StaysFailed_BecauseNothingWasWritten()
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnSelfRead = () => throw new InvalidOperationException("Gateway connection changed before users.self could be sent.");
        var result = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.Failed, result.Name);
        Assert.DoesNotContain(proxy.Methods, m => m.StartsWith("users.set", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("UNAVAILABLE")]
    [InlineData("UNKNOWN")]
    public async Task Save_CodedUnavailableOrUnknownAfterAppliedWrite_IsUncertain_AndRetrySeesExistingValue(string code)
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = _ =>
        {
            proxy.SelfProfile = new { profile = new { id = "gateway-owner", displayName = "Ada", hasAvatar = false } };
            return Task.FromException<JsonElement>(GatewayRequestErrors.Create("post-write check failed", code, null));
        };
        var first = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.Uncertain, first.Name);
        Assert.False(first.IsComplete);

        proxy.OnWrite = null;
        proxy.Methods.Clear();
        var retry = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.ExistingValue, retry.Name);
        Assert.Equal(["users.self"], proxy.Methods);
    }

    [Fact]
    public async Task Save_CodedForbiddenAndInvalidRequestAtWrite_StayDefinite()
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnWrite = _ => Task.FromException<JsonElement>(GatewayRequestErrors.Create("x", "FORBIDDEN", null));
        Assert.Equal(ProfileStep.Denied, (await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None)).Name);
        proxy.OnWrite = _ => Task.FromException<JsonElement>(GatewayRequestErrors.Create("x", "INVALID_REQUEST", null));
        Assert.Equal(ProfileStep.Failed, (await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None)).Name);
    }

    [Fact]
    public async Task Save_CodedUnavailableAtRead_StaysFailed_BecauseNothingWasWritten()
    {
        var (service, proxy, prep) = await Prepared();
        proxy.OnSelfRead = () => throw GatewayRequestErrors.Create("down", "UNAVAILABLE", null);
        var result = await service.SaveAsync(prep, "Ada", null, false, false, CancellationToken.None);
        Assert.Equal(ProfileStep.Failed, result.Name);
        Assert.DoesNotContain(proxy.Methods, m => m.StartsWith("users.set", StringComparison.Ordinal));
    }

    [Fact]
    public void Window_RechecksForOpenModalAfterPreparation_AndDoesNotConsumeAutomaticOffer()
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? FindRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", "WorkspaceWindow.xaml.cs"));
        var body = source[source.IndexOf("private async Task CompleteProfileAsync", StringComparison.Ordinal)..];
        var prepare = body.IndexOf("PrepareAsync", StringComparison.Ordinal);
        var recheck = body.IndexOf("OpenModalDialog() is { } blocker", StringComparison.Ordinal);
        var show = body.IndexOf("dialog.ShowAsync()", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && prepare < recheck && recheck < show);
        Assert.Contains("if (!fallbackToDashboard) _profileOfferedGatewayId = null;", source);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenClawTray.slnx")) &&
               !Directory.Exists(Path.Combine(dir.FullName, "src", "OpenClaw.Tray.WinUI")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
