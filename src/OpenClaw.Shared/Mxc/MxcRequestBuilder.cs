using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using OpenClaw.Shared.Commands;

namespace OpenClaw.Shared.Mxc;

/// <summary>Builds the complete SDK policy without expanding caller-selected cwd or environment.</summary>
public static class MxcRequestBuilder
{
    public static SettingsData Snapshot(SettingsData settings) => settings with
    {
        SandboxCustomFolders = settings.SandboxCustomFolders?
            .Select(f => new SandboxCustomFolder { Path = f.Path, Access = f.Access }).ToList(),
    };

    public static string Fingerprint(SettingsData settings) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings.SystemRunFilesystemScope,
            settings.SystemRunAllowOutbound,
            settings.SystemRunAllowWindowsUi,
            settings.SandboxClipboard,
            settings.SandboxDocumentsAccess,
            settings.SandboxDownloadsAccess,
            settings.SandboxDesktopAccess,
            settings.SandboxCustomFolders,
            settings.SandboxTimeoutMs,
            settings.SandboxMaxOutputBytes,
        })));

    public static ContainerRequest Build(
        CommandRequest command,
        SettingsData settings,
        string settingsDirectory,
        string scratchDirectory,
        MxcRequestContext? context = null)
    {
        context ??= MxcRequestContext.Capture(settingsDirectory);
        var scope = settings.SystemRunFilesystemScope ?? SystemRunFilesystemScope.SelectedFolders;
        if (!Enum.IsDefined(scope)) throw new NotSupportedException("The file access scope is unsupported. Review Node Sandbox settings.");
        if (command.Env is { Count: > 0 })
            throw new NotSupportedException("Custom environment variables are not bound to command approval.");
        if (command.Argv is not { Count: > 0 })
            throw new NotSupportedException("MXC requires the approved executable argv.");
        if (!Path.IsPathFullyQualified(command.Argv[0]))
            throw new NotSupportedException("The approved executable must have a fully qualified path.");
        var image = ValidateExecutable(command.Argv[0]);
        if (Path.GetExtension(image).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(image).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Batch files require an explicitly approved canonical cmd carrier.");

        var scratch = Normalize(scratchDirectory);
        var denied = context.ProtectedPaths.Append(settingsDirectory)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(ProtectedBoundary).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ro = new List<string>();
        var rw = new List<string> { scratch };
        if (scope is SystemRunFilesystemScope.UserFilesReadOnly or SystemRunFilesystemScope.UserFilesReadWrite)
        {
            var access = scope == SystemRunFilesystemScope.UserFilesReadWrite ? SandboxFolderAccess.ReadWrite : SandboxFolderAccess.ReadOnly;
            _ = context.RequireFolder("Profile");
            foreach (var folder in context.UserFolders)
            {
                if (folder.IsOptional && folder.Path is null && folder.Error is null) continue;
                AddFolder(folder.RequirePath(), access, ro, rw);
            }
        }
        if (settings.SandboxDocumentsAccess is SandboxFolderAccess.ReadOnly or SandboxFolderAccess.ReadWrite)
            AddFolder(context.RequireFolder("Documents"), settings.SandboxDocumentsAccess, ro, rw);
        if (settings.SandboxDesktopAccess is SandboxFolderAccess.ReadOnly or SandboxFolderAccess.ReadWrite)
            AddFolder(context.RequireFolder("Desktop"), settings.SandboxDesktopAccess, ro, rw);
        if (settings.SandboxDownloadsAccess is SandboxFolderAccess.ReadOnly or SandboxFolderAccess.ReadWrite)
            AddFolder(context.RequireFolder("Downloads"), settings.SandboxDownloadsAccess, ro, rw);
        foreach (var folder in settings.SandboxCustomFolders ?? [])
            AddFolder(folder.Path, folder.Access, ro, rw);

        var windows = Normalize(context.WindowsDirectory);
        var runtimePaths = new[] { windows, Normalize(context.SystemDirectory) }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ro.AddRange(runtimePaths);
        foreach (var path in ro.Concat(rw))
        {
            if (denied.Any(d => Within(path, d)))
                throw new NotSupportedException("A selected folder is protected. Remove the conflicting grant in Node Sandbox settings.");
            if (IsVolumeRoot(path))
                throw new NotSupportedException("Volume roots are not an allowed file-access boundary. Select a non-root folder.");
            RejectReparsePoints(path);
        }

        var cwd = string.IsNullOrWhiteSpace(command.Cwd) ? scratch : Normalize(command.Cwd);
        RejectReparsePoints(cwd);
        if (denied.Any(d => Within(cwd, d)) || !ro.Concat(rw).Any(g => Within(cwd, g)))
            throw new NotSupportedException("The working folder is outside the granted scope. Grant it in Node Sandbox settings before retrying.");
        if (!ro.Concat(rw).Any(g => Within(image, g)) || denied.Any(d => Within(image, d)))
            throw new NotSupportedException("The approved executable is outside the granted scope. Grant its tools folder before retrying.");

        return new ContainerRequest(RenderArgv(command.Argv))
        {
            Containment = new Containment.ProcessContainer
            {
                LearningMode = false,
                Ui = new ProcessContainerUiPolicy
                {
                    Isolation = ProcessContainerUiIsolation.Container,
                    DesktopSystemControl = false,
                    SystemSettings = ProcessContainerSystemSettings.None,
                    Ime = false,
                },
            },
            Filesystem = new FilesystemPolicy
            {
                ReadonlyPaths = ro.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ReadwritePaths = rw.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                DeniedPaths = denied,
                ClearPolicyOnExit = true,
            },
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = settings.SystemRunAllowOutbound ? NetworkAction.Allow : NetworkAction.Deny },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
            },
            Ui = new UiPolicy
            {
                Disable = !settings.SystemRunAllowWindowsUi,
                Clipboard = settings.SandboxClipboard switch
                {
                    SandboxClipboardMode.Read => ClipboardPolicy.Read,
                    SandboxClipboardMode.Write => ClipboardPolicy.Write,
                    SandboxClipboardMode.Both => ClipboardPolicy.All,
                    _ => ClipboardPolicy.None,
                },
                AllowInputInjection = false,
            },
            WorkingDirectory = cwd,
        };
    }

    internal static string RenderArgv(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0) throw new NotSupportedException("Approved executable argv is required.");
        if (argv.Any(a => a is null || a.Contains('\0') || a.Contains('\r') || a.Contains('\n')))
            throw new NotSupportedException("Command arguments cannot contain NUL or line breaks.");
        if (CanonicalCmdCarrier.IsCmdExecutable(argv[0]) &&
            argv.Skip(1).Any(a => a.StartsWith("/c", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("/k", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/r", StringComparison.OrdinalIgnoreCase)))
        {
            if (!CanonicalCmdCarrier.TryGetCanonicalPayload(argv, out var payload))
                throw new NotSupportedException("Only the canonical cmd /d /s /c carrier can be transported.");
            return $"{Quote(argv[0])} {argv[1]} {argv[2]} {argv[3]} \"{payload}\"";
        }

        return string.Join(" ", argv.Select(Quote));
    }

    internal static string ValidateExecutable(string image)
    {
        if (!Path.IsPathFullyQualified(image))
            throw new NotSupportedException("The approved executable must have a fully qualified path.");
        if (image.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(component => component is not ("." or "..") &&
                (component.EndsWith('.') || component.EndsWith(' '))))
            throw new NotSupportedException("Executable paths cannot contain trailing dot or space aliases.");
        return Normalize(image);
    }

    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return argument;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static List<string> ProtectedPaths(string settingsDirectory)
        => MxcRequestContext.Capture(settingsDirectory).ProtectedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void AddFolder(string path, SandboxFolderAccess? access, List<string> ro, List<string> rw)
    {
        if (access is null or SandboxFolderAccess.Blocked) return;
        if (!Enum.IsDefined(access.Value)) throw new NotSupportedException("A folder has an unsupported access value. Review its permissions.");
        if (string.IsNullOrWhiteSpace(path)) throw new NotSupportedException("A selected folder could not be resolved.");
        (access == SandboxFolderAccess.ReadWrite ? rw : ro).Add(Normalize(path));
    }

    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new NotSupportedException("Folder grants and working folders must be fully qualified.");
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException ex)
        {
            throw new NotSupportedException(
                "A folder or executable path is invalid. Review Node Sandbox settings and retry.", ex);
        }
    }

    private static string ProtectedBoundary(string path)
    {
        var boundary = Normalize(path);
        // PSEC accepts a missing leaf, but not missing parents. Deny the first missing
        // ancestor instead of creating host folders or dropping future protection.
        while (Path.GetDirectoryName(boundary) is { Length: > 0 } parent)
        {
            try
            {
                _ = File.GetAttributes(parent);
                return boundary;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                boundary = parent;
            }
        }
        throw new NotSupportedException("A protected path has no available parent. Review Node Sandbox settings.");
    }

    private static bool IsVolumeRoot(string path) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),
        Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);

    private static bool Within(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
        (!IsVolumeRoot(parent) && path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));

    private static void RejectReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrWhiteSpace(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new NotSupportedException("Reparse-point folder grants and working folders require native alias proof and are not supported yet.");
    }

    public static bool LegacyGrantWasFiltered(string path, string settingsDirectory)
    {
        try
        {
            var normalized = Normalize(path);
            return IsVolumeRoot(normalized) ||
                ProtectedPaths(settingsDirectory).Any(d => Within(normalized, d) || Within(d, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { return true; }
    }

    public static string ResolveDownloadsFolder() => MxcRequestContext.Downloads();
}
