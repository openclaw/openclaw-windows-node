using System;
using OpenClawTray.Services;

// ISOLATED diagnostic host: exact-target (pid + raw FILETIME birth + thread id) hang capture that runs on its own
// bounded worker and writes a bounded, redacted, exclusively-named local receipt. It never activates the installed
// app, opens a listener, or registers a service/watchdog/schedule.
internal static class Program
{
    private static int Main(string[] args)
        => HangCaptureHost.Run(args, new WindowsUiThreadStackSource(), Console.Out).ExitCode;
}