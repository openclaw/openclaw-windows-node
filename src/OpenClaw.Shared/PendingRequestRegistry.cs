using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

internal enum PendingRequestKind
{
    Method,
    Wizard,
    ChatSend,
    ApprovalResolve,
    SessionSnapshot,
}

internal enum PendingResponseDisposition
{
    Active,
    Tombstoned,
    Ownerless,
}

internal enum PendingRequestDiagnosticStage
{
    Registered,
    ResponseClassified,
}

internal readonly record struct PendingRequestDiagnostic(
    PendingRequestDiagnosticStage Stage,
    string RequestId,
    string? Method,
    PendingRequestKind? Kind,
    PendingResponseDisposition? Disposition);

internal sealed class PendingRequest
{
    private PendingRequest(string method, PendingRequestKind kind)
    {
        Method = method;
        Kind = kind;
    }

    public string Method { get; }
    public PendingRequestKind Kind { get; }
    public long RegistrationVersion { get; internal set; }
    public TaskCompletionSource<JsonElement>? WizardCompletion { get; private init; }
    public TaskCompletionSource<ChatSendResult>? ChatSendCompletion { get; private init; }
    public TaskCompletionSource<bool>? ApprovalCompletion { get; private init; }
    public TaskCompletionSource<SessionInfo[]>? SessionSnapshotCompletion { get; private init; }

    public static PendingRequest ForMethod(string method) =>
        new(method, PendingRequestKind.Method);

    public static PendingRequest ForWizard(
        string method,
        TaskCompletionSource<JsonElement> completion) =>
        new(method, PendingRequestKind.Wizard) { WizardCompletion = completion };

    public static PendingRequest ForChatSend(
        string method,
        TaskCompletionSource<ChatSendResult> completion) =>
        new(method, PendingRequestKind.ChatSend) { ChatSendCompletion = completion };

    public static PendingRequest ForApproval(
        string method,
        TaskCompletionSource<bool> completion) =>
        new(method, PendingRequestKind.ApprovalResolve) { ApprovalCompletion = completion };

    public static PendingRequest ForSessionSnapshot(
        string method,
        TaskCompletionSource<SessionInfo[]> completion) =>
        new(method, PendingRequestKind.SessionSnapshot) { SessionSnapshotCompletion = completion };
}

internal readonly record struct PendingResponseTake(
    PendingResponseDisposition Disposition,
    PendingRequest? Request);

internal readonly record struct PendingRequestRegistration(
    string RequestId,
    long RegistrationVersion,
    bool Accepted);

internal sealed class PendingRequestRegistry : IDisposable
{
    internal const int DefaultCompletedIdCapacity = 256;

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingRequest> _active =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> _completed =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _completedOrder = new();
    private readonly int _completedIdCapacity;
    private long _nextRegistrationVersion;
    private bool _acceptingRegistrations;
    private bool _disposed;

    internal Action<PendingRequestDiagnostic>? DiagnosticObserver { get; set; }

    public PendingRequestRegistry(int completedIdCapacity = DefaultCompletedIdCapacity)
    {
        if (completedIdCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(completedIdCapacity),
                "Completed request ID capacity must be positive.");

        _completedIdCapacity = completedIdCapacity;
    }

    internal int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _active.Count;
            }
        }
    }

    internal int CompletedCount
    {
        get
        {
            lock (_lock)
            {
                return _completed.Count;
            }
        }
    }

    internal bool IsAcceptingRegistrations
    {
        get
        {
            lock (_lock)
            {
                return _acceptingRegistrations;
            }
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_lock)
            {
                return _disposed;
            }
        }
    }

    internal int Count(PendingRequestKind kind)
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var request in _active.Values)
            {
                if (request.Kind == kind)
                    count++;
            }

            return count;
        }
    }

    internal void Reopen()
    {
        lock (_lock)
        {
            if (!_disposed)
                _acceptingRegistrations = true;
        }
    }

    internal PendingRequestRegistration RegisterMethod(string requestId, string method) =>
        Register(requestId, PendingRequest.ForMethod(ValidateMethod(method)));

    internal PendingRequestRegistration RegisterWizard(
        string requestId,
        string method,
        TaskCompletionSource<JsonElement> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return Register(
            requestId,
            PendingRequest.ForWizard(ValidateMethod(method), completion));
    }

    internal PendingRequestRegistration RegisterChatSend(
        string requestId,
        TaskCompletionSource<ChatSendResult> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return Register(
            requestId,
            PendingRequest.ForChatSend("chat.send", completion));
    }

    internal PendingRequestRegistration RegisterApproval(
        string requestId,
        TaskCompletionSource<bool> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return Register(
            requestId,
            PendingRequest.ForApproval("exec.approval.resolve", completion));
    }

    internal PendingRequestRegistration RegisterSessionSnapshot(
        string requestId,
        TaskCompletionSource<SessionInfo[]> completion,
        string method = "sessions.list")
    {
        ArgumentNullException.ThrowIfNull(completion);
        return Register(
            requestId,
            PendingRequest.ForSessionSnapshot(ValidateMethod(method), completion));
    }

    internal PendingResponseTake TakeForResponse(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            DiagnosticObserver?.Invoke(new(
                PendingRequestDiagnosticStage.ResponseClassified,
                requestId ?? string.Empty,
                null,
                null,
                PendingResponseDisposition.Ownerless));
            return new(PendingResponseDisposition.Ownerless, null);
        }

        PendingResponseTake take;
        lock (_lock)
        {
            if (_active.Remove(requestId, out var request))
            {
                AddCompletedIdLocked(requestId);
                take = new(PendingResponseDisposition.Active, request);
            }
            else
            {
                take = _completed.ContainsKey(requestId)
                    ? new(PendingResponseDisposition.Tombstoned, null)
                    : new(PendingResponseDisposition.Ownerless, null);
            }
        }

        DiagnosticObserver?.Invoke(new(
            PendingRequestDiagnosticStage.ResponseClassified,
            requestId,
            take.Request?.Method,
            take.Request?.Kind,
            take.Disposition));
        return take;
    }

    internal bool Remove(string requestId)
    {
        ValidateRequestId(requestId);

        lock (_lock)
        {
            if (!_active.Remove(requestId))
                return false;

            AddCompletedIdLocked(requestId);
            return true;
        }
    }

    internal bool Remove(PendingRequestRegistration registration)
    {
        if (!registration.Accepted)
            return false;

        ValidateRequestId(registration.RequestId);
        lock (_lock)
        {
            if (!_active.TryGetValue(registration.RequestId, out var request) ||
                request.RegistrationVersion != registration.RegistrationVersion)
            {
                return false;
            }

            _active.Remove(registration.RequestId);
            AddCompletedIdLocked(registration.RequestId);
            return true;
        }
    }

    internal bool Cancel(
        string requestId,
        OperationCanceledException? cancellation = null)
    {
        ValidateRequestId(requestId);
        PendingRequest? request;

        lock (_lock)
        {
            if (!_active.Remove(requestId, out request))
                return false;

            AddCompletedIdLocked(requestId);
        }

        CancelCompletion(
            request,
            cancellation ?? new OperationCanceledException("Request canceled"));
        return true;
    }

    internal void CloseForDisconnect()
    {
        List<PendingRequest> requests;

        lock (_lock)
        {
            _acceptingRegistrations = false;
            requests = DrainActiveLocked();
        }

        CancelForConnectionLoss(requests);
    }

    public void Dispose()
    {
        List<PendingRequest> requests;

        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _acceptingRegistrations = false;
            requests = DrainActiveLocked();
        }

        CancelForConnectionLoss(requests);
    }

    private PendingRequestRegistration Register(
        string requestId,
        PendingRequest request)
    {
        ValidateRequestId(requestId);
        PendingRequest? replaced = null;
        bool accepted;
        bool disposed;

        lock (_lock)
        {
            disposed = _disposed;
            accepted = _acceptingRegistrations && !disposed;
            if (accepted)
            {
                request.RegistrationVersion = ++_nextRegistrationVersion;
                RemoveCompletedIdLocked(requestId);
                _active.Remove(requestId, out replaced);
                _active.Add(requestId, request);
            }
        }

        if (replaced is not null)
        {
            CancelCompletion(
                replaced,
                new OperationCanceledException(
                    "Request canceled because its ID was registered again"));
        }

        if (!accepted)
        {
            CancelCompletion(
                request,
                CreateConnectionLossCancellation(request.Kind, disposed));
        }

        var registration = new PendingRequestRegistration(
            requestId,
            request.RegistrationVersion,
            accepted);
        if (accepted)
        {
            DiagnosticObserver?.Invoke(new(
                PendingRequestDiagnosticStage.Registered,
                requestId,
                request.Method,
                request.Kind,
                null));
        }

        return registration;
    }

    private List<PendingRequest> DrainActiveLocked()
    {
        var requests = new List<PendingRequest>(_active.Count);
        foreach (var (requestId, request) in _active)
        {
            AddCompletedIdLocked(requestId);
            requests.Add(request);
        }

        _active.Clear();
        return requests;
    }

    private void AddCompletedIdLocked(string requestId)
    {
        RemoveCompletedIdLocked(requestId);
        var node = _completedOrder.AddLast(requestId);
        _completed.Add(requestId, node);

        while (_completed.Count > _completedIdCapacity)
        {
            var oldest = _completedOrder.First!;
            _completedOrder.RemoveFirst();
            _completed.Remove(oldest.Value);
        }
    }

    private void RemoveCompletedIdLocked(string requestId)
    {
        if (_completed.Remove(requestId, out var node))
            _completedOrder.Remove(node);
    }

    private static void CancelForConnectionLoss(IEnumerable<PendingRequest> requests)
    {
        foreach (var request in requests)
        {
            CancelCompletion(
                request,
                CreateConnectionLossCancellation(request.Kind, disposed: false));
        }
    }

    private static OperationCanceledException CreateConnectionLossCancellation(
        PendingRequestKind kind,
        bool disposed) =>
        kind switch
        {
            PendingRequestKind.Wizard => new OperationCanceledException(
                "Gateway connection lost while waiting for wizard response"),
            PendingRequestKind.ApprovalResolve => new OperationCanceledException(
                "Gateway connection lost before exec.approval.resolve response"),
            PendingRequestKind.SessionSnapshot => new OperationCanceledException(
                "Gateway connection lost while waiting for sessions.list response"),
            PendingRequestKind.ChatSend => new OperationCanceledException("Request canceled"),
            _ => new OperationCanceledException(
                disposed ? "Request canceled because the client was disposed" : "Request canceled"),
        };

    private static void CancelCompletion(
        PendingRequest request,
        OperationCanceledException cancellation)
    {
        switch (request.Kind)
        {
            case PendingRequestKind.Wizard:
                request.WizardCompletion!.TrySetException(cancellation);
                break;
            case PendingRequestKind.ChatSend:
                request.ChatSendCompletion!.TrySetException(cancellation);
                break;
            case PendingRequestKind.ApprovalResolve:
                request.ApprovalCompletion!.TrySetException(cancellation);
                break;
            case PendingRequestKind.SessionSnapshot:
                request.SessionSnapshotCompletion!.TrySetException(cancellation);
                break;
        }
    }

    private static void ValidateRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("Request ID is required.", nameof(requestId));
    }

    private static string ValidateMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("Request method is required.", nameof(method));

        return method;
    }
}
