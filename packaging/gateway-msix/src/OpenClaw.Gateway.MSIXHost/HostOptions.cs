using System.Runtime.InteropServices;

namespace OpenClaw.MSIXHost;

public sealed record HostOptions(
    string PayloadPath,
    string MetadataPath,
    string NodePath,
    string InstallDirectory,
    string StateDirectory,
    IReadOnlyList<string> OpenClawArguments,
    bool VerifyInstalledPayload,
    bool ShowHelp)
{
    public static HostOptions Parse(IReadOnlyList<string> arguments)
    {
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"Unsupported process architecture: {RuntimeInformation.ProcessArchitecture}.")
        };

        string payloadDirectory = Path.Combine(AppContext.BaseDirectory, "payload");
        string payloadPath = Path.Combine(payloadDirectory, $"app-{architecture}.tar.gz");
        string metadataPath = Path.Combine(payloadDirectory, "payload-metadata.json");
        string packagedNodePath = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
        string nodePath = File.Exists(packagedNodePath) ? packagedNodePath : "node";
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        string installDirectory = Path.Combine(
            userProfile,
            ".openclaw-msix",
            "app");
        string stateDirectory = Path.Combine(userProfile, ".openclaw");
        var openClawArguments = new List<string>();
        bool verifyInstalledPayload = false;
        bool showHelp = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--")
            {
                for (index++; index < arguments.Count; index++)
                {
                    openClawArguments.Add(arguments[index]);
                }

                break;
            }

            switch (argument)
            {
                case "--host-help":
                    showHelp = true;
                    break;
                case "--host-payload":
                    payloadPath = ReadValue(arguments, ref index, argument);
                    break;
                case "--host-metadata":
                    metadataPath = ReadValue(arguments, ref index, argument);
                    break;
                case "--host-node":
                    nodePath = ReadValue(arguments, ref index, argument);
                    break;
                case "--host-install-directory":
                    installDirectory = ReadValue(arguments, ref index, argument);
                    break;
                case "--host-verify-installed-payload":
                    verifyInstalledPayload = true;
                    break;
                default:
                    openClawArguments.Add(argument);
                    break;
            }
        }

        return new HostOptions(
            payloadPath,
            metadataPath,
            nodePath,
            installDirectory,
            stateDirectory,
            openClawArguments,
            verifyInstalledPayload,
            showHelp);
    }

    public static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine(
            """
            Usage: openclaw [host options] [--] [openclaw arguments]

            Host options:
              --host-payload <path>     OpenClaw .tar.gz payload
              --host-metadata <path>    payload-metadata.json
              --host-node <path>        node executable (packaged runtime or PATH)
              --host-install-directory <path>
                                        stable OpenClaw install directory
              --host-verify-installed-payload
                                        re-hash every installed payload file
              --host-help               show this help

            With no OpenClaw arguments, the host prepares the gateway files and
            prints setup instructions without starting OpenClaw.
            All non-host arguments are forwarded unchanged to OpenClaw after
            the gateway files are prepared.
            """);
    }

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new HostUsageException($"{option} requires a value.");
        }

        return arguments[index];
    }
}

public sealed class HostUsageException(string message) : Exception(message);
