using System;

namespace OpenClaw.Shared;

public static class NonFatalAction
{
    public static void Run(Action action, Action<string> onError)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            onError(ex.Message);
        }
    }
}
