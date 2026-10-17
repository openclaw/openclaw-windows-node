using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;

namespace OpenClaw.Connection.Tests;

// Hold notification only after the real completion source has published its result.
internal sealed class HandshakeCompletionNotificationGate(TimeSpan guard) : EventListener
{
    private bool _captureDelay;
    private int _captureThreadId;
    private int _delayTaskId;
    private int _successTaskId = -1;
    private int _enteredOnce;

    public ManualResetEventSlim Entered { get; } = new(false, 0);
    public ManualResetEventSlim Release { get; } = new(false, 0);
    public ManualResetEventSlim DeadlineElapsed { get; } = new(false, 0);
    public long SuccessPublishedAt { get; private set; }
    public string? Fault { get; private set; }

    public void BeginDelayCapture()
    {
        _captureThreadId = Environment.CurrentManagedThreadId;
        _captureDelay = true;
    }
    public void EndDelayCapture() => _captureDelay = false;
    public void Arm(Task task) => Volatile.Write(ref _successTaskId, task.Id);

    public IDisposable ObserveDeadline(Task completion)
    {
        // The original implementation exposes its Task.Delay through TPL events.
        if (_delayTaskId != 0) return new CancellationTokenRegistration();

        // Read the suspended helper's compiler state, not timer internals or a replacement clock.
        var continuation = typeof(Task).GetField("m_continuationObject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(completion);
        for (var depth = 0; depth < 4 && continuation != null; depth++)
        {
            if (continuation is Delegate notification)
            {
                continuation = notification.Target;
                continue;
            }
            var fields = continuation.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var stateMachine = fields.FirstOrDefault(field => field.Name == "StateMachine")?.GetValue(continuation);
            if (stateMachine != null)
            {
                var deadline = stateMachine.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(field => field.GetValue(stateMachine)).OfType<CancellationTokenSource>().Single();
                return deadline.Token.Register(() => DeadlineElapsed.Set());
            }
            var action = fields.FirstOrDefault(field => field.Name is "m_action" or "_action" or "_continuation")?.GetValue(continuation);
            continuation = (action as Delegate)?.Target;
        }
        throw new InvalidOperationException("The helper's real deadline binding changed. Review the regression gate.");
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Threading.Tasks.TplEventSource")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)0x28);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        if (data.Payload == null || data.Payload.Count == 0 || data.Payload[0] is not int taskId) return;
        if (_captureDelay && Environment.CurrentManagedThreadId == _captureThreadId &&
            data.EventName == "TraceOperationBegin" && data.Payload.Count >= 2 &&
            data.Payload[1] is string name && name == "Task.Delay")
            Interlocked.CompareExchange(ref _delayTaskId, taskId, 0);
        if (data.EventName == "TraceOperationEnd" && taskId == _delayTaskId && taskId > 0 &&
            data.Payload.Count >= 2 && data.Payload[1] is int status && status == 1)
            DeadlineElapsed.Set();
        if (data.EventId != 17 || taskId != Volatile.Read(ref _successTaskId) ||
            data.Payload.Count < 2 || data.Payload[1] is not int work || work != 0 ||
            Interlocked.Exchange(ref _enteredOnce, 1) != 0) return;

        SuccessPublishedAt = Stopwatch.GetTimestamp();
        Entered.Set();
        if (!Release.Wait(guard))
            Fault = "The success-notification gate expired.";
    }

    public override void Dispose()
    {
        Release.Set();
        base.Dispose();
        Entered.Dispose();
        Release.Dispose();
        DeadlineElapsed.Dispose();
    }
}
