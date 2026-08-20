namespace OpenClaw.MSIXHost;

public enum BootstrapAction
{
    PrepareFast,
    PrepareFull,
    ResetGateway,
    ResetAll
}

public static class BootstrapConsole
{
    public static BootstrapAction PromptForAction(
        string installDirectory,
        string stateDirectory,
        TextReader input,
        TextWriter output)
    {
        string? installRoot = Path.GetDirectoryName(
            Path.GetFullPath(installDirectory));
        bool hasGatewayData =
            Directory.Exists(installDirectory) ||
            (installRoot is not null &&
                string.Equals(
                    Path.GetFileName(installRoot),
                    ".openclaw-msix",
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(installRoot));
        if (!hasGatewayData &&
            !Directory.Exists(stateDirectory))
        {
            return BootstrapAction.PrepareFast;
        }

        output.WriteLine();
        if (Directory.Exists(installDirectory))
        {
            output.WriteLine("OpenClaw gateway files were prepared by an earlier launch:");
            output.WriteLine($"  {installDirectory}");
            output.WriteLine();
            output.WriteLine("If OpenClaw is already configured and working, you can close");
            output.WriteLine("this window and launch it with:");
            output.WriteLine("  openclaw gateway run");
        }
        else if (hasGatewayData)
        {
            output.WriteLine("Existing OpenClaw MSIX gateway data was found:");
            output.WriteLine($"  {installRoot}");
        }
        else
        {
            output.WriteLine("Existing OpenClaw configuration or user data was found:");
            output.WriteLine($"  {stateDirectory}");
        }

        output.WriteLine();
        output.WriteLine("[C] Continue with fast verification [recommended]");
        output.WriteLine("[R] Retry preparation with full verification and repair");
        output.WriteLine("[G] Reset prepared gateway files, then exit");
        output.WriteLine("[A] Reset gateway files and all OpenClaw user data, then exit");

        while (true)
        {
            output.Write("Choose an option [C]: ");
            string? response = input.ReadLine();
            if (string.IsNullOrWhiteSpace(response) ||
                response.Equals("c", StringComparison.OrdinalIgnoreCase))
            {
                return BootstrapAction.PrepareFast;
            }

            if (response.Equals("r", StringComparison.OrdinalIgnoreCase))
            {
                return BootstrapAction.PrepareFull;
            }

            if (response.Equals("g", StringComparison.OrdinalIgnoreCase))
            {
                output.Write(
                    "Remove the prepared gateway files? The MSIX will remain installed. [y/N]: ");
                if (IsYes(input.ReadLine()))
                {
                    return BootstrapAction.ResetGateway;
                }

                output.WriteLine("Gateway reset canceled.");
                continue;
            }

            if (response.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine(
                    "This removes prepared gateway files, configuration, credentials, " +
                    "agents, and sessions.");
                output.Write("Type RESET to continue: ");
                if (string.Equals(
                    input.ReadLine()?.Trim(),
                    "RESET",
                    StringComparison.Ordinal))
                {
                    return BootstrapAction.ResetAll;
                }

                output.WriteLine("Full reset canceled.");
                continue;
            }

            output.WriteLine("Enter C, R, G, or A.");
        }
    }

    public static void WritePreparationSummary(
        TextWriter output,
        StagedPayload payload)
    {
        output.WriteLine();
        output.WriteLine("OpenClaw gateway files are ready.");
        output.WriteLine(
            payload.Reused
                ? "The existing prepared payload was verified and reused."
                : "The packaged payload was verified and prepared.");
        output.WriteLine($"Prepared files: {payload.DirectoryPath}");
        output.WriteLine();
        output.WriteLine("Next steps:");
        output.WriteLine("  1. Configure OpenClaw:");
        output.WriteLine("     openclaw setup");
        output.WriteLine("     or: openclaw onboard --mode local");
        output.WriteLine("  2. Start the gateway after setup:");
        output.WriteLine("     openclaw gateway run");
        output.WriteLine();
        output.WriteLine(
            "This bootstrap launch did not start the gateway automatically.");
        output.WriteLine(
            "You can close this window after noting the commands above.");
    }

    public static void WaitForExit(TextReader input, TextWriter output)
    {
        output.WriteLine();
        output.Write("Press Enter to close this window...");
        input.ReadLine();
    }

    private static bool IsYes(string? value) =>
        string.Equals(value?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
}
