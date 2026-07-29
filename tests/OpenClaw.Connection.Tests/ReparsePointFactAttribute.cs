namespace OpenClaw.Connection.Tests;

internal sealed class ReparsePointFactAttribute : FactAttribute
{
    public ReparsePointFactAttribute()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openclaw-reparse-probe-{Guid.NewGuid():N}");
        var directoryTarget = Path.Combine(root, "directory-target");
        var directoryLink = Path.Combine(root, "directory-link");
        var fileTarget = Path.Combine(root, "file-target");
        var fileLink = Path.Combine(root, "file-link");

        try
        {
            Directory.CreateDirectory(directoryTarget);
            File.WriteAllText(fileTarget, "probe");
            Directory.CreateSymbolicLink(directoryLink, directoryTarget);
            File.CreateSymbolicLink(fileLink, fileTarget);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip = $"Reparse-point fixtures are unavailable on this host: {ex.GetType().Name}.";
        }
        finally
        {
            TryDeleteFile(fileLink);
            TryDeleteDirectory(directoryLink);
            TryDeleteDirectory(root, recursive: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path, bool recursive = false)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
