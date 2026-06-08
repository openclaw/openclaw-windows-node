namespace OpenClaw.SetupEngine;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, contents);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, contents, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete temporary file '{tempPath}': {ex.Message}");
        }
    }
}
