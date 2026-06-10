namespace OpenClaw.SetupEngine;

internal static class SetupDiagnostics
{
    public static void TryWriteStderrWarning(string message)
    {
        try { Console.Error.WriteLine($"WARN: {message}"); }
        catch { }
    }
}
