using System.Diagnostics;

namespace OpenClaw.Shared.Mxc;

/// <summary>Approved-argv host start information. Selection and process ownership belong to NodeSandboxExecutor.</summary>
internal static class UnsupportedWindowsCommandExecutor
{
    internal static ProcessStartInfo CreateStartInfo(CommandRequest command, string scratch)
    {
        if (command.Env is { Count: > 0 })
            throw new NotSupportedException("Custom environment variables are not bound to command approval.");
        if (command.Argv is not { Count: > 0 })
            throw new NotSupportedException("Approved executable argv is required.");
        var image = MxcRequestBuilder.ValidateExecutable(command.Argv[0]);
        if (Path.GetExtension(image).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(image).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Batch files require an explicitly approved canonical cmd carrier.");
        var argv = command.Argv.ToArray();
        argv[0] = image;
        var line = MxcRequestBuilder.RenderArgv(argv);
        if (!string.IsNullOrWhiteSpace(command.Cwd) && !Path.IsPathFullyQualified(command.Cwd))
            throw new NotSupportedException("The approved working folder must be fully qualified.");
        var info = new ProcessStartInfo(image)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(command.Cwd) ? scratch : Path.GetFullPath(command.Cwd),
        };
        // cmd parses its raw command tail, not CommandLineToArgvW. Preserve the already-approved carrier.
        if (OpenClaw.Shared.Commands.CanonicalCmdCarrier.IsCmdExecutable(image))
            info.Arguments = line.Length > MxcRequestBuilder.Quote(image).Length
                ? line[(MxcRequestBuilder.Quote(image).Length + 1)..] : "";
        else
            foreach (var argument in argv.Skip(1)) info.ArgumentList.Add(argument);
        return info;
    }
}
