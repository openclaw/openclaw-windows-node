namespace OpenClawTray.Presentation.Usage;

internal sealed record UsageSavedExport(string Name, string? Folder);
internal sealed record UsageExportCoverage(int Loaded, int? Total);
internal sealed record UsageExportBlock(UsageFilters Filters, UsageExportCoverage Coverage);

/// <summary>Coordinates the guarded read, alternate export, system save picker and page-local result notification.</summary>
internal sealed class UsageExportWorkflow(
    UsagePageController controller,
    Func<string, string, Task<UsageSavedExport?>> save,
    Func<bool> isCurrent,
    Action<string> warn)
{
    private bool _running;

    public async Task ExportAsync(UsageExportFormat format)
    {
        if (_running) return;
        _running = true;
        controller.SetExportInteraction(true);
        controller.SetExportNotice(null);
        var filters = controller.State.Filters;
        var connection = controller.ExportConnectionIdentity;
        bool Current() => isCurrent() && ReferenceEquals(connection, controller.ExportConnectionIdentity);
        try
        {
            var (content, error) = await controller.ExportAsync(format, filters);
            if (!Current() || (content is null && error is null)) return;
            if (format == UsageExportFormat.SessionsCsv && error is UsageExportError.Incomplete or UsageExportError.TooMany)
            {
                var accepted = await controller.OfferDailyExportAsync(filters);
                if (!accepted || !Current()) return;
                format = UsageExportFormat.DailyCsv;
                (content, error) = await controller.ExportAsync(format, filters);
                if (!Current() || (content is null && error is null)) return;
            }
            if (content is null)
            {
                controller.SetExportNotice(new(null, error ?? UsageExportError.Failed));
                return;
            }
            var saved = await save(UsageExport.FileName(format, filters), content);
            if (Current() && saved is not null)
                controller.SetExportNotice(new(saved.Name, null, saved.Folder));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            warn($"[UsageExport] Save failed: {ex.GetType().Name}");
            if (Current()) controller.SetExportNotice(new(null, UsageExportError.Failed));
        }
        finally
        {
            controller.SetExportInteraction(false);
            _running = false;
        }
    }
}
