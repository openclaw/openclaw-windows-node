using System.Text.Json;

namespace OpenClaw.SetupEngine;

/// <summary>Lost freshness, not proof of unchanged authority. Receipt retry requires independent confirmation.</summary>
public sealed class SetupNativeReadinessExpiredException : InvalidOperationException
{
    public SetupNativeReadinessExpiredException() : base("AI readiness expired. Recheck the saved route before retrying.") { }
}

/// <summary>
/// Process-local proof tied to one live transport generation and configuration revision.
/// Navigation rechecks observations only, never activation, recovery or inference.
/// </summary>
public sealed class SetupNativeReadyBinding
{
    private readonly IGatewayAiSetupTransport _transport;
    private readonly string _configHash;
    private readonly GatewayAiSetupRoute _route;
    private volatile bool _invalid;
    public GatewayAiSetupCompletion Proof { get; }

    private SetupNativeReadyBinding(IGatewayAiSetupTransport transport, GatewayAiSetupCompletion proof, string configHash)
        => (_transport, Proof, _configHash, _route) = (transport, proof, configHash, transport.Route);

    public static Task<SetupNativeReadyBinding> VerifyAsync(IGatewayAiSetupTransport transport,
        GatewayAiSetupCompletion expected, CancellationToken ct, TimeProvider? timeProvider = null,
        IProgress<SetupLoadingStep>? progress = null)
        => SetupNativeCompletionTiming.RunAsync(
            token => VerifyCoreAsync(transport, expected, token, timeProvider, progress),
            SetupNativeCompletionTiming.ModelVerification, SetupNativeCompletionPhase.ModelVerification, ct, timeProvider);

    private static async Task<SetupNativeReadyBinding> VerifyCoreAsync(IGatewayAiSetupTransport transport,
        GatewayAiSetupCompletion expected, CancellationToken ct, TimeProvider? timeProvider,
        IProgress<SetupLoadingStep>? progress)
    {
        SetupNativeVerification.RequireRoute(expected, transport.Route);
        var generation = transport.Generation;
        progress?.Report(SetupLoadingStep.CheckConfiguration);
        var hash = await ReadConfigHashAsync(transport, ct);
        var client = new GatewayAiSetupClient(transport, expected.ModelRef, expected.Intent, expected.RequiresManagedLocalAi);
        progress?.Report(SetupLoadingStep.VerifyModel);
        var proof = await SetupModelVerification.VerifyAsync(client, expected.ModelRef, ct, timeProvider);
        SetupNativeVerification.RequireSame(expected, new(proof, transport.Route.SessionKey ?? ""));
        if (generation <= 0 || generation != proof.VerifiedGeneration)
            throw new SetupNativeReadinessExpiredException();
        var binding = new SetupNativeReadyBinding(transport, proof, hash);
        await binding.RequireCurrentAsync(ct, progress);
        return binding;
    }

    public void Invalidate() => _invalid = true;

    public async Task<IDisposable> ObserveAndCheckAsync(
        Func<SetupNativeReadyBinding, IDisposable> observe, CancellationToken ct,
        IProgress<SetupLoadingStep>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        var subscription = observe(this);
        try
        {
            await RequireCurrentAsync(ct, progress);
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    public bool ObserveConfiguration(JsonElement config)
    {
        if (!config.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String ||
            hash.GetString() != _configHash)
            Invalidate();
        return !_invalid;
    }

    public void RequireCurrent()
    {
        try
        {
            if (_transport.Route != _route) throw new SetupNativeOwnershipException();
            SetupNativeVerification.RequireRoute(Proof, _transport.Route);
            if (_invalid || !_transport.IsConnected || _transport.Generation != Proof.VerifiedGeneration)
                throw new SetupNativeReadinessExpiredException();
        }
        catch
        {
            _invalid = true;
            throw;
        }
    }

    public async Task RequireCurrentAsync(CancellationToken ct, IProgress<SetupLoadingStep>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        RequireCurrent();
        try
        {
            progress?.Report(SetupLoadingStep.CheckConfiguration);
            if (await ReadConfigHashAsync(_transport, ct) != _configHash)
                throw new SetupNativeReadinessExpiredException();
            ct.ThrowIfCancellationRequested();
            RequireCurrent();
        }
        catch
        {
            _invalid = true;
            throw;
        }
    }

    public static async Task RequireStableAuthorityAsync(IGatewayAiSetupTransport transport,
        GatewayAiSetupCompletion expected, CancellationToken ct)
    {
        if (!SetupNativePreparation.Matches(expected, expected.SessionKey))
            throw new SetupNativeOwnershipException();
        var route = transport.Route;
        SetupNativeVerification.RequireRoute(expected, route);
        var generation = transport.Generation;
        if (!transport.IsConnected || generation <= 0)
            throw new InvalidOperationException("Current authority is unavailable.");
        var revision = await ReadConfigHashAsync(transport, ct);
        var discovery = await new GatewayAiSetupClient(transport).DetectAsync(ct)
            ?? throw new InvalidOperationException("The selected model authority cannot be inspected.");
        if (!discovery.SetupComplete || discovery.ConfiguredModel != expected.ModelRef)
            throw new SetupNativeOwnershipException();
        if (await ReadConfigHashAsync(transport, ct) != revision || !transport.IsConnected ||
            transport.Generation != generation)
            throw new SetupNativeReadinessExpiredException();
        ct.ThrowIfCancellationRequested();
        SetupNativeVerification.RequireRoute(expected, transport.Route);
        if (transport.Route != route) throw new SetupNativeOwnershipException();
    }

    private static async Task<string> ReadConfigHashAsync(IGatewayAiSetupTransport transport, CancellationToken ct)
    {
        var result = await transport.RequestAsync("config.get", new { }, 15_000, ct);
        if (!result.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(hash.GetString()) ||
            result.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("The current Gateway configuration revision is unavailable.");
        return hash.GetString()!;
    }
}

/// <summary>Destination-only owner. Failure can retry navigation, never repeat setup.</summary>
public sealed class SetupReadyCoordinator(SetupNativeReadyBinding binding,
    Func<SetupNativeCompletion, CancellationToken, Task> navigate,
    Action? requireLiveModel = null, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private Task _cleanup = Task.CompletedTask;
    private int _busy;
    public GatewayAiSetupCompletion Proof => binding.Proof;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool IsCompleted { get; private set; }
    public Task ActiveTask { get; private set; } = Task.CompletedTask;
    public Task CleanupCompleted => _cleanup;

    public void RequireCurrent()
    {
        binding.RequireCurrent();
        requireLiveModel?.Invoke();
    }

    public Task SelectAsync(SetupNativeDestination destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(destination) || IsCompleted || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("This completion choice is not available.");
        return ActiveTask = SelectCoreAsync(destination);
    }

    private async Task SelectCoreAsync(SetupNativeDestination destination)
    {
        await Task.Yield();
        using var deadline = new CancellationTokenSource(SetupNativeCompletionTiming.Navigation, timeProvider ?? TimeProvider.System);
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, deadline.Token);
        var ct = navigation.Token;
        try
        {
            // Revision inspection and page mounting share the existing navigation budget.
            await binding.RequireCurrentAsync(ct);
            requireLiveModel?.Invoke();
            await navigate(new(Proof, new(destination, Proof.SessionKey!)), ct);
            ct.ThrowIfCancellationRequested();
            binding.RequireCurrent();
            requireLiveModel?.Invoke();
            IsCompleted = true;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        binding.Invalidate();
        _lifetime.Cancel();
        _cleanup = DisposeAfterSelectionAsync();
    }

    private async Task DisposeAfterSelectionAsync()
    {
        try { await ActiveTask; }
        catch (Exception) { /* Selection reports its own result; disposal only joins it. */ }
        finally { _lifetime.Dispose(); }
    }
}
