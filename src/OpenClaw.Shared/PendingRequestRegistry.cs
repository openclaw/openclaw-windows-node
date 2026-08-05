using System.Text.Json;

namespace OpenClaw.Shared;

internal enum PendingRequestCategory
{
    Tracked,
    ChatSend,
    Wizard,
    Approval,
}

internal readonly record struct PendingRequestRegistration<T>(
    Task<T> Task,
    PendingRequestRegistry.RegistrationHandle Handle);

internal abstract class PendingRequestResolution
{
    protected PendingRequestResolution(string method, PendingRequestCategory category)
    {
        Method = method;
        Category = category;
    }

    internal string Method { get; }
    internal PendingRequestCategory Category { get; }
}

internal sealed class TrackedRequestResolution(string method)
    : PendingRequestResolution(method, PendingRequestCategory.Tracked);

internal sealed class ChatSendRequestResolution(
    string method,
    TaskCompletionSource<ChatSendResult> completion)
    : PendingRequestResolution(method, PendingRequestCategory.ChatSend)
{
    internal bool TryComplete(ChatSendResult result) => completion.TrySetResult(result);
    internal bool TryFault(Exception exception) => completion.TrySetException(exception);
}

internal sealed class WizardRequestResolution(
    string method,
    TaskCompletionSource<JsonElement> completion)
    : PendingRequestResolution(method, PendingRequestCategory.Wizard)
{
    internal bool TryComplete(JsonElement result) => completion.TrySetResult(result);
    internal bool TryFault(Exception exception) => completion.TrySetException(exception);
}

internal sealed class ApprovalRequestResolution(
    string method,
    TaskCompletionSource<bool> completion)
    : PendingRequestResolution(method, PendingRequestCategory.Approval)
{
    internal bool TryComplete(bool result) => completion.TrySetResult(result);
    internal bool TryFault(Exception exception) => completion.TrySetException(exception);
}

internal sealed class PendingRequestRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _generation;
    private bool _open;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal void OpenConnection()
    {
        Entry[] staleEntries;
        lock (_gate)
        {
            staleEntries = _entries.Values.ToArray();
            _entries.Clear();
            _generation++;
            _open = true;
        }

        FaultDrainedEntries(staleEntries);
    }

    internal RegistrationHandle RegisterTracked(string requestId, string method) =>
        Register(new TrackedEntry(requestId, method));

    internal PendingRequestRegistration<ChatSendResult> RegisterChatSend(
        string requestId,
        string method)
    {
        var entry = new ChatSendEntry(requestId, method);
        var handle = Register(entry);
        return new PendingRequestRegistration<ChatSendResult>(entry.Task, handle);
    }

    internal PendingRequestRegistration<JsonElement> RegisterWizard(
        string requestId,
        string method)
    {
        var entry = new WizardEntry(requestId, method);
        var handle = Register(entry);
        return new PendingRequestRegistration<JsonElement>(entry.Task, handle);
    }

    internal PendingRequestRegistration<bool> RegisterApproval(
        string requestId,
        string method)
    {
        var entry = new ApprovalEntry(requestId, method);
        var handle = Register(entry);
        return new PendingRequestRegistration<bool>(entry.Task, handle);
    }

    internal bool TryRemove(RegistrationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        lock (_gate)
        {
            if (handle.Generation != _generation ||
                !_entries.TryGetValue(handle.RequestId, out var entry) ||
                !ReferenceEquals(entry, handle.EntryIdentity))
            {
                return false;
            }

            return _entries.Remove(handle.RequestId);
        }
    }

    internal bool TryTake(string? requestId, out PendingRequestResolution? resolution)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            resolution = null;
            return false;
        }

        Entry? entry;
        lock (_gate)
        {
            if (!_entries.Remove(requestId, out entry))
            {
                resolution = null;
                return false;
            }
        }

        resolution = entry.CreateResolution();
        return true;
    }

    internal void Drain(GatewayConnectionLostException? wizardDisconnect = null)
    {
        Entry[] entries;
        lock (_gate)
        {
            if (!_open && _entries.Count == 0)
            {
                return;
            }

            _open = false;
            _generation++;
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }

        FaultDrainedEntries(entries, wizardDisconnect);
    }

    private RegistrationHandle Register(Entry entry)
    {
        lock (_gate)
        {
            if (!_open)
            {
                throw new InvalidOperationException("Gateway connection is not open");
            }

            if (!_entries.TryAdd(entry.RequestId, entry))
            {
                throw new InvalidOperationException(
                    $"A pending request with id '{entry.RequestId}' is already registered");
            }

            return new RegistrationHandle(entry.RequestId, _generation, entry);
        }
    }

    private static void FaultDrainedEntries(
        IEnumerable<Entry> entries,
        GatewayConnectionLostException? wizardDisconnect = null)
    {
        foreach (var entry in entries)
        {
            entry.FaultFromDrain(wizardDisconnect);
        }
    }

    internal sealed class RegistrationHandle
    {
        internal readonly object EntryIdentity;

        internal RegistrationHandle(string requestId, long generation, object entryIdentity)
        {
            RequestId = requestId;
            Generation = generation;
            EntryIdentity = entryIdentity;
        }

        internal string RequestId { get; }
        internal long Generation { get; }
    }

    private abstract class Entry
    {
        protected Entry(string requestId, string method, PendingRequestCategory category)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(method);
            RequestId = requestId;
            Method = method;
            Category = category;
        }

        internal string RequestId { get; }
        protected string Method { get; }
        protected PendingRequestCategory Category { get; }

        internal abstract PendingRequestResolution CreateResolution();
        internal abstract void FaultFromDrain(
            GatewayConnectionLostException? wizardDisconnect);
    }

    private sealed class TrackedEntry(string requestId, string method)
        : Entry(requestId, method, PendingRequestCategory.Tracked)
    {
        internal override PendingRequestResolution CreateResolution() =>
            new TrackedRequestResolution(Method);

        internal override void FaultFromDrain(
            GatewayConnectionLostException? wizardDisconnect)
        {
        }
    }

    private sealed class ChatSendEntry : Entry
    {
        private readonly TaskCompletionSource<ChatSendResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ChatSendEntry(string requestId, string method)
            : base(requestId, method, PendingRequestCategory.ChatSend)
        {
        }

        internal Task<ChatSendResult> Task => _completion.Task;

        internal override PendingRequestResolution CreateResolution() =>
            new ChatSendRequestResolution(Method, _completion);

        internal override void FaultFromDrain(
            GatewayConnectionLostException? wizardDisconnect) =>
            _completion.TrySetException(new OperationCanceledException("Request canceled"));
    }

    private sealed class WizardEntry : Entry
    {
        private readonly TaskCompletionSource<JsonElement> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal WizardEntry(string requestId, string method)
            : base(requestId, method, PendingRequestCategory.Wizard)
        {
        }

        internal Task<JsonElement> Task => _completion.Task;

        internal override PendingRequestResolution CreateResolution() =>
            new WizardRequestResolution(Method, _completion);

        internal override void FaultFromDrain(
            GatewayConnectionLostException? wizardDisconnect) =>
            _completion.TrySetException(
                wizardDisconnect ??
                new OperationCanceledException(
                    "Gateway connection lost while waiting for wizard response"));
    }

    private sealed class ApprovalEntry : Entry
    {
        private readonly TaskCompletionSource<bool> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ApprovalEntry(string requestId, string method)
            : base(requestId, method, PendingRequestCategory.Approval)
        {
        }

        internal Task<bool> Task => _completion.Task;

        internal override PendingRequestResolution CreateResolution() =>
            new ApprovalRequestResolution(Method, _completion);

        internal override void FaultFromDrain(
            GatewayConnectionLostException? wizardDisconnect) =>
            _completion.TrySetException(
                new OperationCanceledException(
                    "Gateway connection lost before exec.approval.resolve response"));
    }
}
