// <summary>
// Resolves the qualified model definition represented by a validated Local AI install receipt.
// Catalog receipts resolve by their pinned source, while custom receipts own their persisted recipe.
// Callers receive null when a catalog model is no longer qualified and must not launch it.
// Usage:
//   LocalModelInfo? model = LocalAiInstalledModel.Resolve(manifest);
//   if (model is null) throw new InvalidDataException("The installed model is not qualified.");
//   string displayName = model.DisplayName;
// </summary>
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Connection.LocalAi;

/// <summary>
/// Resolves the model a validated receipt records: its persisted custom recipe, or the
/// catalog entry (including retired ones) for catalog installs, matched against the
/// receipt's download source.
/// </summary>
public static class LocalAiInstalledModel
{
    /// <summary>
    /// Resolves the model described by a manifest, or null when its catalog receipt is no longer qualified.
    /// </summary>
    public static LocalModelInfo? Resolve(LocalAiInstallManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.CustomModel is { } recipe
            ? LocalCustomModel.ToModelInfo(recipe)
            : LocalModelCatalog.FindInstalled(manifest.ModelCatalogId, manifest.ModelAsset.SourceUrl);
    }
}
