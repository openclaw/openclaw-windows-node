using System.Diagnostics;

namespace OpenClaw.Shared;

public enum StartupTaskRegistrationOutcome { Registered, Rejected, Unknown }
public enum StartupTaskInspection { Absent, ExpectedEnabled, Different, Unknown }

internal sealed record StartupTaskDescription(
    bool Enabled, string Path, string Executable, string? Arguments, string? WorkingDirectory,
    string PrincipalSid, bool EnabledLogonTrigger);

public static class WindowsStartupTaskRegistration
{
    public const string TaskName = "OpenClaw Companion";

    public static bool Register(string trayExecutablePath, string taskName = TaskName)
    {
        if (string.IsNullOrWhiteSpace(trayExecutablePath) || !File.Exists(trayExecutablePath))
            return false;

        return Run(CreateRegisterProcessStartInfo(trayExecutablePath, taskName));
    }

    public static bool Unregister(string taskName = TaskName) => Run(CreateUnregisterProcessStartInfo(taskName));

    public static bool Exists(string taskName = TaskName) => Run(CreateQueryProcessStartInfo(taskName));

    public static StartupTaskRegistrationOutcome RegisterForSetup(string executable, string taskName = TaskName)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return StartupTaskRegistrationOutcome.Rejected;
        try
        {
            Process? started;
            try { started = Process.Start(CreateRegisterProcessStartInfo(executable, taskName)); }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 2 or 3 or 5)
            {
                return StartupTaskRegistrationOutcome.Rejected;
            }
            using var process = started;
            if (process is null) return StartupTaskRegistrationOutcome.Rejected;
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: false); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return StartupTaskRegistrationOutcome.Unknown;
            }
            // A failed CLI exit can still represent an uncertain service-side operation.
            return process.ExitCode == 0 ? StartupTaskRegistrationOutcome.Registered : StartupTaskRegistrationOutcome.Unknown;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return StartupTaskRegistrationOutcome.Unknown;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static StartupTaskInspection InspectStrict(string taskName, string? expectedExecutable)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        object? definition = null;
        object? actions = null;
        object? action = null;
        object? principal = null;
        object? triggers = null;
        object? trigger = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
            service = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Windows Task Scheduler could not be opened.");
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            try { task = ((dynamic)folder).GetTask(taskName); }
            catch (Exception error) when (
                error is System.Runtime.InteropServices.COMException or FileNotFoundException &&
                error.HResult == unchecked((int)0x80070002))
            {
                return StartupTaskInspection.Absent;
            }
            if (expectedExecutable is null) return StartupTaskInspection.Different;
            definition = ((dynamic)task).Definition;
            actions = ((dynamic)definition).Actions;
            principal = ((dynamic)definition).Principal;
            triggers = ((dynamic)definition).Triggers;
            if ((int)((dynamic)actions).Count != 1 || (int)((dynamic)triggers).Count != 1)
                return StartupTaskInspection.Different;
            action = ((dynamic)actions)[1];
            trigger = ((dynamic)triggers)[1];
            if ((int)((dynamic)action).Type != 0 || (int)((dynamic)trigger).Type != 9)
                return StartupTaskInspection.Different;
            var user = (string?)((dynamic)principal).UserId;
            if (string.IsNullOrWhiteSpace(user)) return StartupTaskInspection.Unknown;
            using var current = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (current.User is null) return StartupTaskInspection.Unknown;
            var sid = user.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new System.Security.Principal.SecurityIdentifier(user)
                : (System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(user)
                    .Translate(typeof(System.Security.Principal.SecurityIdentifier));
            var description = new StartupTaskDescription((bool)((dynamic)task).Enabled,
                (string)((dynamic)task).Path, (string)((dynamic)action).Path,
                (string?)((dynamic)action).Arguments, (string?)((dynamic)action).WorkingDirectory, sid.Value,
                (bool)((dynamic)trigger).Enabled && string.IsNullOrWhiteSpace((string?)((dynamic)trigger).UserId));
            return MatchesExpectedTask(description, taskName, expectedExecutable, current.User.Value)
                ? StartupTaskInspection.ExpectedEnabled : StartupTaskInspection.Different;
        }
        catch (Exception error) when (error is System.Security.Principal.IdentityNotMappedException or ArgumentException)
        {
            return StartupTaskInspection.Unknown;
        }
        finally
        {
            foreach (var value in new[] { trigger, triggers, principal, action, actions, definition, task, folder, service })
                if (value is not null && System.Runtime.InteropServices.Marshal.IsComObject(value))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(value);
        }
    }

    internal static bool MatchesExpectedTask(StartupTaskDescription actual, string taskName, string executable, string principalSid) =>
        actual.Enabled && actual.EnabledLogonTrigger &&
        string.Equals(actual.Path, "\\" + taskName.TrimStart('\\'), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.PrincipalSid, principalSid, StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(actual.Arguments) && string.IsNullOrWhiteSpace(actual.WorkingDirectory) &&
        string.Equals(Path.GetFullPath(actual.Executable.Trim('"')), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo CreateRegisterProcessStartInfo(string trayExecutablePath, string taskName = TaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        var fullPath = Path.GetFullPath(trayExecutablePath);
        return CreateStartInfo(
            "/Create",
            "/TN", taskName,
            "/TR", Quote(fullPath),
            "/SC", "ONLOGON",
            "/F");
    }

    internal static ProcessStartInfo CreateUnregisterProcessStartInfo(string taskName = TaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return CreateStartInfo(
            "/Delete",
            "/TN", taskName,
            "/F");
    }

    internal static ProcessStartInfo CreateQueryProcessStartInfo(string taskName = TaskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return CreateStartInfo(
            "/Query",
            "/TN", taskName);
    }

    private static bool Run(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            if (process.WaitForExit(10_000))
                return process.ExitCode == 0;

            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch
            {
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string ResolveSchtasksPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = Environment.GetEnvironmentVariable("SystemRoot");

        return !string.IsNullOrWhiteSpace(systemRoot)
            ? Path.Combine(systemRoot, "System32", "schtasks.exe")
            : Path.Combine("C:\\", "Windows", "System32", "schtasks.exe");
    }

    private static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveSchtasksPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
