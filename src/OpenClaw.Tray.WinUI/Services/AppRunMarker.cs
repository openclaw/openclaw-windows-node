namespace OpenClawTray.Services;

/// <summary>
/// Writes and clears a run marker file so the next launch can detect an unclean exit.
/// </summary>
internal sealed class AppRunMarker
{
    private readonly string _path;

    public AppRunMarker(string path) => _path = path;

    public void Check()
    {
        try
        {
            if (File.Exists(_path))
            {
                var startedAt = File.ReadAllText(_path);
                Logger.Error($"Previous session did not exit cleanly (started {startedAt})");
                File.Delete(_path);
            }
        }
        catch (Exception ex) { Logger.Debug($"AppRunMarker.Check failed: {ex.Message}"); }
    }

    public void MarkStarted()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, DateTime.Now.ToString("O"));
        }
        catch (Exception ex) { Logger.Debug($"AppRunMarker.MarkStarted failed: {ex.Message}"); }
    }

    public void MarkEnded()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception ex) { Logger.Debug($"AppRunMarker.MarkEnded failed: {ex.Message}"); }
    }
}
