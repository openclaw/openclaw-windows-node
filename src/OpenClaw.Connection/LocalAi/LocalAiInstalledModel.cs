using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Connection.LocalAi;

/// <summary>
/// Resolves the model a validated receipt records: its persisted custom recipe, or the
/// catalog entry (including retired ones) for catalog installs.
/// </summary>
public static class LocalAiInstalledModel
{
    public static LocalModelInfo? Resolve(LocalAiInstallManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.CustomModel is { } recipe
            ? LocalCustomModel.ToModelInfo(recipe)
            : LocalModelCatalog.FindInstalled(manifest.ModelCatalogId);
    }
}
