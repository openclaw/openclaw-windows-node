namespace OpenClawTray.Services;

/// <summary>
/// InfoBar.Message cannot be selected. The installer address is returned separately
/// so the page can show it in a selectable text block.
/// </summary>
internal readonly record struct StoreMigrationLinkFailure(string Instruction, string? SelectableAddress)
{
    public static StoreMigrationLinkFailure Present(string messageTemplate, string? address)
    {
        if (string.IsNullOrEmpty(address))
            return new(messageTemplate, null);

        var placeholder = messageTemplate.IndexOf("{0}", StringComparison.Ordinal);
        if (placeholder < 0)
            return new(messageTemplate, address);

        // "browser: {0}." and "浏览器：{0}。" become a sentence, with the address beside it.
        var instruction = messageTemplate.Remove(placeholder, 3);
        instruction = instruction.Replace(" : .", ".", StringComparison.Ordinal);
        instruction = instruction.Replace(": .", ".", StringComparison.Ordinal);
        instruction = instruction.Replace("：。", "。", StringComparison.Ordinal);
        return new(instruction.Trim(), address);
    }
}
