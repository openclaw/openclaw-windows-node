namespace OpenClawTray.Services;

internal enum CanvasSurfaceDestination
{
    Capabilities,
    Connection,
    Canvas,
}

internal sealed record CanvasWindowRequest(
    CanvasSurfaceDestination Destination,
    Action? ShowCanvas)
{
    internal void Dispatch(Action<string> showHub)
    {
        ArgumentNullException.ThrowIfNull(showHub);

        switch (Destination)
        {
            case CanvasSurfaceDestination.Capabilities:
                showHub("capabilities");
                break;
            case CanvasSurfaceDestination.Connection:
                showHub("connection");
                break;
            case CanvasSurfaceDestination.Canvas when ShowCanvas is not null:
                ShowCanvas();
                break;
            case CanvasSurfaceDestination.Canvas:
                throw new InvalidOperationException("A Canvas surface request requires a Canvas callback.");
            default:
                throw new ArgumentOutOfRangeException(nameof(Destination));
        }
    }
}
