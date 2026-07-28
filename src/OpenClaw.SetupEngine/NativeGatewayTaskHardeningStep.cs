using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenClaw.SetupEngine;

public sealed class NativeGatewayTaskHardeningStep : SetupStep
{
    public override string Id => "harden-native-task";
    public override string DisplayName => "Enable native gateway crash recovery";

    public override bool CanSkip(SetupContext ctx) =>
        ctx.Config.InstallMode != GatewayInstallMode.NativeWindows;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.Config.InstallMode != GatewayInstallMode.NativeWindows)
            return StepResult.Skip("Native task hardening applies only to the Windows gateway");
        if (!GatewayInstallModeDetector.IsNativeProfileOwned(ctx.LocalDataDir, ctx.Config))
            return StepResult.Fail("Native gateway ownership could not be verified before task hardening.");

        var taskName = GatewayCliRunner.GetManagedNativeTaskName(ctx.Config);
        var vbsPath = Path.Combine(GatewayCliRunner.GetManagedNativeStateDir(ctx.Config), "gateway.vbs");
        if (!File.Exists(vbsPath))
            return StepResult.Fail($"Native gateway launcher was not found: '{vbsPath}'.");

        var schtasks = ResolveSchtasksPath();
        var export = await ctx.Commands.RunAsync(
            schtasks,
            ["/Query", "/TN", taskName, "/XML"],
            TimeSpan.FromSeconds(15),
            ct: ct);
        if (export.ExitCode != 0 || string.IsNullOrWhiteSpace(export.Stdout))
            return StepResult.Fail($"Could not export native gateway task '{taskName}'.");

        string hardenedVbs;
        string hardenedXml;
        try
        {
            hardenedVbs = BuildHardenedVbs(await File.ReadAllTextAsync(vbsPath, ct));
            hardenedXml = BuildHardenedTaskXml(export.Stdout);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            return StepResult.Fail($"Could not prepare native gateway crash recovery: {ex.Message}");
        }

        ctx.PreviousNativeTaskHardening = new NativeTaskHardeningRollbackState(
            taskName,
            export.Stdout,
            vbsPath,
            await File.ReadAllBytesAsync(vbsPath, ct));

        try
        {
            AtomicFile.WriteAllText(vbsPath, hardenedVbs);
            var register = await RegisterTaskXmlAsync(ctx, schtasks, taskName, hardenedXml, ct);
            if (register.ExitCode != 0)
            {
                await RestoreAsync(ctx, schtasks, ct);
                return StepResult.Fail($"Could not register hardened native gateway task '{taskName}'.");
            }

            var restart = await GatewayCliRunner.RunNativeAsync(
                ctx,
                ["gateway", "restart"],
                TimeSpan.FromSeconds(60),
                ct: ct);
            if (restart.ExitCode != 0)
            {
                await RestoreAsync(ctx, schtasks, ct);
                return StepResult.Fail($"Hardened native gateway task could not be restarted (exit {restart.ExitCode}).");
            }

            GatewayInstallModeDetector.DeleteNativeStopIntent(ctx.LocalDataDir);
            ctx.Logger.Info("Enabled bounded Task Scheduler restart-on-failure for the native gateway");
            return StepResult.Ok("Native gateway task now supervises the gateway process");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await RestoreAsync(ctx, schtasks, CancellationToken.None);
            return StepResult.Fail($"Could not harden native gateway task: {ex.Message}");
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.PreviousNativeTaskHardening is null)
            return;

        await RestoreAsync(ctx, ResolveSchtasksPath(), ct);
    }

    internal static string BuildHardenedVbs(string source)
    {
        if (source.Contains("WScript.Quit", StringComparison.OrdinalIgnoreCase)
            && source.Contains("WScript.Sleep 60000", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(source, @",\s*0,\s*True\s*\)", RegexOptions.IgnoreCase))
        {
            return source;
        }

        var match = Regex.Match(
            source,
            @"CreateObject\(""WScript\.Shell""\)\.Run\s+(?<command>.+?),\s*0,\s*False\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!match.Success)
        {
            match = Regex.Match(
                source,
                @"exitCode\s*=\s*shell\.Run\((?<command>.+?),\s*0,\s*True\)",
                RegexOptions.IgnoreCase);
        }
        if (!match.Success)
            throw new InvalidDataException("Native gateway VBS launcher did not match the expected managed shape.");

        var command = match.Groups["command"].Value.Trim();
        return
            "' OpenClaw Companion-managed supervised gateway launcher\r\n" +
            "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
            "attempts = 0\r\n" +
            "Do\r\n" +
            "  startedAt = Timer\r\n" +
            $"  exitCode = shell.Run({command}, 0, True)\r\n" +
            "  elapsed = Timer - startedAt\r\n" +
            "  If elapsed < 0 Then elapsed = elapsed + 86400\r\n" +
            "  If exitCode = 0 Then WScript.Quit 0\r\n" +
            "  If elapsed >= 900 Then attempts = 0\r\n" +
            "  attempts = attempts + 1\r\n" +
            "  If attempts >= 5 Then WScript.Quit exitCode\r\n" +
            "  WScript.Sleep 60000\r\n" +
            "Loop\r\n";
    }

    internal static string BuildHardenedTaskXml(string source)
    {
        var document = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("Scheduled Task XML has no root element.");
        var ns = root.Name.Namespace;
        var settings = root.Element(ns + "Settings")
            ?? throw new InvalidDataException("Scheduled Task XML has no Settings element.");

        SetElement(settings, ns + "StartWhenAvailable", "true");
        SetElement(settings, ns + "ExecutionTimeLimit", "PT0S");
        var restart = settings.Element(ns + "RestartOnFailure");
        if (restart is null)
        {
            restart = new XElement(ns + "RestartOnFailure");
            settings.Add(restart);
        }
        SetElement(restart, ns + "Interval", "PT1M");
        SetElement(restart, ns + "Count", "5");
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void SetElement(XElement parent, XName name, string value)
    {
        var element = parent.Element(name);
        if (element is null)
            parent.Add(new XElement(name, value));
        else
            element.Value = value;
    }

    private static async Task<CommandResult> RegisterTaskXmlAsync(
        SetupContext ctx,
        string schtasks,
        string taskName,
        string taskXml,
        CancellationToken ct)
    {
        var tempDirectory = Path.Combine(ctx.LocalDataDir, "setup-temp");
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, $"native-task-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tempPath, taskXml, Encoding.Unicode, ct);
            return await ctx.Commands.RunAsync(
                schtasks,
                ["/Create", "/TN", taskName, "/XML", tempPath, "/F"],
                TimeSpan.FromSeconds(30),
                ct: ct);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task RestoreAsync(
        SetupContext ctx,
        string schtasks,
        CancellationToken ct)
    {
        if (ctx.PreviousNativeTaskHardening is not { } previous)
            return;

        await AtomicFile.WriteAllBytesAsync(previous.VbsPath, previous.VbsContents, ct);
        var restore = await RegisterTaskXmlAsync(ctx, schtasks, previous.TaskName, previous.TaskXml, ct);
        if (restore.ExitCode != 0)
            throw new InvalidOperationException($"Could not restore native gateway task '{previous.TaskName}'.");
        ctx.PreviousNativeTaskHardening = null;
    }

    private static string ResolveSchtasksPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrWhiteSpace(windowsDirectory)
            ? "schtasks.exe"
            : Path.Combine(windowsDirectory, "System32", "schtasks.exe");
    }
}
