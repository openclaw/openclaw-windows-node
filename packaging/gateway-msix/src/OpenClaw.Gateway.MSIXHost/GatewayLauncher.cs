using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace OpenClaw.MSIXHost;

public static class GatewayLauncher
{
    public static async Task<int> RunAsync(
        string nodePath,
        string payloadDirectory,
        IReadOnlyList<string> openClawArguments,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        ProcessStartInfo startInfo = CreateStartInfo(
            nodePath,
            payloadDirectory,
            openClawArguments);
        log?.Invoke(
            openClawArguments.Count == 0
                ? "Launching OpenClaw Gateway in the foreground."
                : "Launching OpenClaw with forwarded command arguments.");
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start the OpenClaw process.");
        log?.Invoke($"OpenClaw child process started with PID {process.Id}.");
        GatewayProcessRegistration? registration = null;
        if (IsGatewayRun(openClawArguments))
        {
            try
            {
                registration = GatewayProcessRegistration.Create(
                    process,
                    payloadDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                log?.Invoke(
                    $"Unable to record the gateway process for reset: {exception.Message}");
            }
        }

        Task stderrForwarding = startInfo.RedirectStandardError
            ? ForwardStandardErrorAsync(
                process.StandardError,
                Console.Error,
                cancellationToken)
            : Task.CompletedTask;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await stderrForwarding;
            log?.Invoke($"OpenClaw child process exited with code {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            registration?.Dispose();
        }
    }

    public static ProcessStartInfo CreateStartInfo(
        string nodePath,
        string payloadDirectory,
        IReadOnlyList<string> openClawArguments)
    {
        string entryPoint = Path.Combine(payloadDirectory, "openclaw.mjs");
        if (!File.Exists(entryPoint))
        {
            throw new FileNotFoundException(
                "The staged OpenClaw entry point was not found.",
                entryPoint);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = payloadDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = openClawArguments.Count == 0
        };
        startInfo.ArgumentList.Add(entryPoint);

        if (openClawArguments.Count == 0)
        {
            startInfo.ArgumentList.Add("gateway");
            startInfo.ArgumentList.Add("run");
        }
        else
        {
            foreach (string argument in PrepareOpenClawArguments(openClawArguments))
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    private static IReadOnlyList<string> PrepareOpenClawArguments(
        IReadOnlyList<string> arguments)
    {
        bool IsArgument(int index, string expected) =>
            arguments.Count > index &&
            string.Equals(
                arguments[index],
                expected,
                StringComparison.OrdinalIgnoreCase);

        if (IsArgument(0, "gateway") && IsArgument(1, "install"))
        {
            throw new HostUsageException(
                "The MSIX host runs the Gateway in the foreground and does not " +
                "support installing OpenClaw's separate Windows Scheduled Task.");
        }

        bool isSetupCommand = IsArgument(0, "setup") || IsArgument(0, "onboard");
        if (!isSetupCommand)
        {
            return arguments;
        }

        bool requestsDaemonInstall = arguments.Any(argument =>
            string.Equals(
                argument,
                "--install-daemon",
                StringComparison.OrdinalIgnoreCase));
        if (requestsDaemonInstall)
        {
            throw new HostUsageException(
                "Daemon installation is not supported by the MSIX host. " +
                "Run setup without --install-daemon.");
        }

        bool alreadySkipsDaemon = arguments.Any(argument =>
            string.Equals(
                argument,
                "--skip-daemon",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                argument,
                "--no-install-daemon",
                StringComparison.OrdinalIgnoreCase));
        return alreadySkipsDaemon
            ? arguments
            : [.. arguments, "--skip-daemon"];
    }

    private static bool IsGatewayRun(IReadOnlyList<string> arguments) =>
        arguments.Count == 0 ||
        (arguments.Count >= 2 &&
            string.Equals(
                arguments[0],
                "gateway",
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                arguments[1],
                "run",
                StringComparison.OrdinalIgnoreCase));

    public static async Task ForwardStandardErrorAsync(
        TextReader reader,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        const string clixmlMarker = "#< CLIXML";
        const int maximumBufferedClixmlCharacters = 256 * 1024;
        StringBuilder? clixml = null;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (clixml is null)
            {
                if (line == clixmlMarker)
                {
                    clixml = new StringBuilder().AppendLine(line);
                    continue;
                }

                await writer.WriteLineAsync(line);
                continue;
            }

            clixml.AppendLine(line);
            bool complete = line.Contains("</Objs>", StringComparison.Ordinal);
            bool tooLarge = clixml.Length > maximumBufferedClixmlCharacters;
            if (!complete && !tooLarge)
            {
                continue;
            }

            string record = clixml.ToString();
            clixml = null;
            if (complete && IsBenignModulePreparationClixml(record))
            {
                continue;
            }

            await writer.WriteAsync(record);
        }

        if (clixml is not null)
        {
            await writer.WriteAsync(clixml.ToString());
        }
    }

    private static bool IsBenignModulePreparationClixml(string record)
    {
        int xmlStart = record.IndexOf("<Objs", StringComparison.Ordinal);
        if (xmlStart < 0)
        {
            return false;
        }

        try
        {
            XDocument document = XDocument.Parse(record[xmlStart..]);
            XElement? root = document.Root;
            if (root is null ||
                root.Name.LocalName != "Objs")
            {
                return false;
            }

            XElement[] children = root.Elements().ToArray();
            return children.Length == 1 &&
                children[0].Name.LocalName == "Obj" &&
                string.Equals(
                    children[0].Attribute("S")?.Value,
                    "progress",
                    StringComparison.OrdinalIgnoreCase) &&
                children[0].Value.Contains(
                    "Preparing modules for first use.",
                    StringComparison.Ordinal);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
