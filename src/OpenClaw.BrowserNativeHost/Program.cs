using System.IO.Pipes;
using OpenClaw.Shared.Browser;

if (!OperatingSystem.IsWindows()) return 1;
if (args.Length == 1 && args[0] is "--register" or "--unregister")
{
    try
    {
        return BrowserNativeRegistration.Apply(Environment.ProcessPath!, args[0] == "--unregister") ? 0 : 1;
    }
    catch { return 1; } // Never emit manifest paths or user data to Chrome's stdout.
}

byte[] response;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
try
{
    if (!BrowserNativeProtocol.IsAllowedCaller(args))
        response = BrowserNativeProtocol.Failure("origin_forbidden");
    else if (!BrowserNativeRegistration.IsRegistered(Environment.ProcessPath!))
        response = BrowserNativeProtocol.Failure("manifest_invalid");
    else
    {
        var payload = await BrowserNativeProtocol.ReadAsync(Console.OpenStandardInput(), BrowserNativeProtocol.RequestLimit, deadline.Token);
        _ = BrowserNativeProtocol.ParseRequest(payload);
        // Windows authenticates both ends to the current user. No HTTP endpoint or copied bearer token.
        using var pipe = new NamedPipeClientStream(".", BrowserNativeProtocol.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000, deadline.Token);
        await BrowserNativeProtocol.WriteAsync(pipe, payload, deadline.Token);
        response = await BrowserNativeProtocol.ReadAsync(pipe, BrowserNativeProtocol.ResponseLimit, deadline.Token);
    }
}
catch (InvalidDataException ex) when (ex.Message is "invalid_frame" or "invalid_utf8" or "invalid_request")
{
    response = BrowserNativeProtocol.Failure(ex.Message);
}
catch
{
    response = BrowserNativeProtocol.Failure("pairing_unavailable");
}
try
{
    using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await BrowserNativeProtocol.WriteAsync(Console.OpenStandardOutput(), response, writeDeadline.Token);
    return 0;
}
catch { return 1; }
