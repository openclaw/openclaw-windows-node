using System;
using System.IO;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal interface IUpdateBackend : IUpdateCheckBoundary
{
    string? GetChangelog();
    Task<bool> DownloadAndInstallAsync(Action downloadCompleted);
}

/// <summary>
/// Owns an optional updater for one app lifetime. Call only after channel
/// admission; dependency failures stay unavailable until the app restarts.
/// </summary>
internal sealed class UpdateBackendLoader(Func<IUpdateBackend> factory)
{
    internal const string ReleasesUrl = "https://github.com/openclaw/openclaw-windows-node/releases/latest";
    private IUpdateBackend? _backend;
    public Exception? Failure { get; private set; }

    public IUpdateBackend? Load()
    {
        if (Failure is not null)
            return null;

        try
        {
            return _backend ??= factory();
        }
        catch (Exception ex) when (IsDependencyFailure(ex))
        {
            MarkUnavailable(ex);
            return null;
        }
    }

    public void MarkUnavailable(Exception error)
    {
        Failure = error;
        _backend = null;
    }

    public static bool IsDependencyFailure(Exception error) => error switch
    {
        FileLoadException or FileNotFoundException or BadImageFormatException
            or TypeLoadException or MissingMemberException or DllNotFoundException => true,
        TypeInitializationException { InnerException: { } inner } => IsDependencyFailure(inner),
        _ => false
    };
}
