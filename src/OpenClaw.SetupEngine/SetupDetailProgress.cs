namespace OpenClaw.SetupEngine;

public enum SetupDetailProgressUnit
{
    None = 0,
    Bytes = 1,
    Items = 2,
}

public sealed record SetupDetailProgressEvent(
    string StepId,
    string Detail,
    long Completed,
    long? Total,
    SetupDetailProgressUnit Unit);

internal sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
{
    private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public void Report(T value) => _callback(value);
}
