namespace OpenClawTray.Infrastructure.Localization;

/// <summary>
/// Identifies a localizable message by namespace and key.
/// Namespace corresponds to the .resw file name (e.g., "Common" for Common.resw).
/// Key is the data name within the .resw file.
/// </summary>
public readonly record struct MessageKey(string Namespace, string Key)
{
    public override string ToString() => $"{Namespace}.{Key}";
}
