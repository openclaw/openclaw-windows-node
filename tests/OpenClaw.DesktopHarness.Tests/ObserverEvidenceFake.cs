using System;

public sealed class ObserverEvidenceFake : IObserverThreadNative
{
    public int OpenFailure;
    public uint Pid = 123;
    public int OwnerFailure;
    public bool TimesOk = true;
    public int TimesFailure;
    public int CloseFailure;
    public int LastError { get; private set; }
    public IntPtr OpenLimited(uint tid)
    {
        LastError = OpenFailure;
        return OpenFailure == 0 ? new IntPtr(17) : IntPtr.Zero;
    }
    public uint Owner(IntPtr thread) { LastError = OwnerFailure; return Pid; }
    public bool Times(IntPtr thread, out long created, out long exited)
    {
        created = 900; exited = 0; LastError = TimesFailure; return TimesOk;
    }
    public bool Close(IntPtr thread) { LastError = CloseFailure; return CloseFailure == 0; }
}
