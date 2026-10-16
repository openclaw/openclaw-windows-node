using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Invalidates process-local Ready on normal-owner changes. Never starts or repairs a runtime.</summary>
internal sealed class SetupReadyObservation : IDisposable
{
    private readonly GatewayConnectionManager _manager;
    private readonly GatewayRegistry _registry;
    private readonly OpenClawGatewayClient _client;
    private readonly ILocalAiRuntime? _runtime;
    private readonly LocalAiRuntimeSnapshot? _model;
    private readonly SetupNativeReadyBinding _binding;
    private readonly Action _invalidated;
    private volatile bool _disposed;
    public SetupNativeReadyBinding Binding => _binding;

    public SetupReadyObservation(GatewayConnectionManager manager, GatewayRegistry registry,
        ILocalAiRuntime? runtime, SetupNativeReadyBinding binding, Action invalidated)
    {
        (_manager, _registry, _binding, _invalidated) = (manager, registry, binding, invalidated);
        _client = manager.OperatorClient as OpenClawGatewayClient ??
            throw new InvalidOperationException("The Gateway is disconnected.");
        _runtime = binding.Proof.RequiresManagedLocalAi ? runtime : null;
        _model = _runtime?.Snapshot;
        manager.StateChanged += OnState;
        manager.OperatorClientChanged += OnOperator;
        registry.Changed += OnRegistry;
        _client.ConfigUpdated += OnConfig;
        _client.SessionsUpdated += OnSessions;
        if (_runtime is not null) _runtime.StateChanged += OnRuntime;
        try { RequireCurrent(); }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void RequireCurrent()
    {
        _binding.RequireCurrent();
        if (_registry.GetActive() is not { } active ||
            active.Id != _binding.Proof.GatewayId ||
            GatewayDashboardBinding.Capture(active) != _binding.Proof.EndpointBinding)
            throw new SetupNativeOwnershipException();
        if (_disposed || !ReferenceEquals(_client, _manager.OperatorClient) ||
            _manager.CurrentSnapshot.OperatorState != RoleConnectionState.Connected ||
            SetupDashboardLiveFacts.Capture(_manager) is not { } facts)
            throw new SetupNativeReadinessExpiredException();
        if (!facts.Matches(_binding.Proof)) throw new SetupNativeOwnershipException();
        if (_binding.Proof.RequiresManagedLocalAi)
        {
            if (!IsSameManagedRuntime(_binding.Proof.ModelRef, _model, _runtime?.Snapshot))
                throw new InvalidOperationException("The verified Local AI runtime is no longer available.");
        }
    }

    internal static bool IsSameManagedRuntime(string modelRef, LocalAiRuntimeSnapshot? captured, LocalAiRuntimeSnapshot? current) =>
        current is { State: LocalAiRuntimeState.Healthy, Ownership: LocalAiOwnership.CompanionManaged, ModelId: not null } &&
        captured is not null && "llamacpp/" + current.ModelId == modelRef &&
        current.ProcessId == captured.ProcessId && current.ProcessStartedAtUtc == captured.ProcessStartedAtUtc &&
        current.ModelId == captured.ModelId && current.Endpoint == captured.Endpoint;

    private void Check()
    {
        if (_disposed) return;
        try { RequireCurrent(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _binding.Invalidate();
            _invalidated();
        }
    }

    private void OnState(object? sender, GatewayConnectionSnapshot args) => Check();
    private void OnOperator(object? sender, OperatorClientChangedEventArgs args) => Check();
    private void OnRegistry(object? sender, EventArgs args) => Check();
    private void OnRuntime(object? sender, LocalAiRuntimeSnapshotChangedEventArgs args) => Check();
    private void OnSessions(object? sender, SessionInfo[] args) => Check();
    private void OnConfig(object? sender, System.Text.Json.JsonElement args)
    {
        if (_disposed) return;
        if (!_binding.ObserveConfiguration(args))
            _invalidated();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.StateChanged -= OnState;
        _manager.OperatorClientChanged -= OnOperator;
        _registry.Changed -= OnRegistry;
        _client.ConfigUpdated -= OnConfig;
        _client.SessionsUpdated -= OnSessions;
        if (_runtime is not null) _runtime.StateChanged -= OnRuntime;
    }
}
