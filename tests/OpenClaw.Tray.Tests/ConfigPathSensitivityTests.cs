using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public class ConfigPathSensitivityTests
{
    [Theory]
    [InlineData("channels.nostr.nsec", true)]
    [InlineData("channels.nostr.NSEC", true)]
    [InlineData("nsec", true)]
    [InlineData("channels.nostr.privateKey", true)]
    [InlineData("channels.nostr.PrivateKey", true)]
    [InlineData("privateKey", true)]
    [InlineData("channels.googlechat.webhookUrl", true)]
    [InlineData("channels.googlechat.webhookUrlExtra", false)]
    [InlineData("channels.slack.webhookUrls", true)]
    [InlineData("channels.slack.WebhookUrls", true)]
    [InlineData("channels.discord.token", true)]
    [InlineData("channels.slack.signingSecret", true)]
    [InlineData("channels.telegram.botToken", true)]
    [InlineData("auth.password", true)]
    [InlineData("providers.apiKey", true)]
    [InlineData("providers.api_key", true)]
    [InlineData("channels.nostr.relays", false)]
    [InlineData("channels.nostr.nsecExtra", false)]
    [InlineData("channels.nostr.privateKeyExtra", false)]
    [InlineData("channels.discord.applicationId", false)]
    [InlineData("channels.googlechat.webhook", false)]
    public void IsSensitive_MasksSecretSegmentsAndLegacySecretNames(string path, bool expected)
    {
        Assert.Equal(expected, ConfigPathSensitivity.IsSensitive(path));
    }

    [Fact]
    public void SchemaEditor_HidesStoredValuesInSensitiveArrays()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "OpenClaw.Tray.WinUI", "Controls", "SchemaConfigEditor.xaml.cs"));

        Assert.Contains("itemType == \"string\" && IsSensitive(path)", source, StringComparison.Ordinal);
        Assert.Contains("existingRow: true", source, StringComparison.Ordinal);
        Assert.Contains("if (existingRow)", source, StringComparison.Ordinal);
        Assert.Contains("_keptArraySecrets[passwordBox] = value", source, StringComparison.Ordinal);
        Assert.DoesNotContain("existing.Length > 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordBox.Tag", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Password = value", source, StringComparison.Ordinal);
        Assert.Contains("_keptArraySecrets.TryGetValue(password, out var existing)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("They cannot be edited on this page.", source, StringComparison.Ordinal);
        Assert.Contains("valueSchema: valueSchema", source, StringComparison.Ordinal);
        Assert.Contains("GetLabel(childPath, prop.Name)", source, StringComparison.Ordinal);
        Assert.Contains("value.GetArrayLength()", source, StringComparison.Ordinal);
        Assert.Contains("editor.Text = \"\"", source, StringComparison.Ordinal);
        Assert.Contains("Content = \"Replace all\"", source, StringComparison.Ordinal);
        Assert.Contains("Content = \"Clear all\"", source, StringComparison.Ordinal);
        Assert.Contains("Clear all stored entries?", source, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(source, "JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })"));
        Assert.Contains("if (IsSensitive(childPath))", source, StringComparison.Ordinal);
        Assert.Contains("ConfigEditorModel.UseHiddenObjectEditor(childPath, childSchema)", source, StringComparison.Ordinal);
        Assert.Contains("ConfigEditorModel.UseHiddenObjectEditor(path, schema)", source, StringComparison.Ordinal);
        var hiddenObject = source.IndexOf("UseHiddenObjectEditor(childPath, childSchema)", StringComparison.Ordinal);
        var expandObject = source.IndexOf("RenderObjectSection(childPath,", StringComparison.Ordinal);
        Assert.True(hiddenObject >= 0 && hiddenObject < expandObject);
        Assert.Contains("case JsonValueKind.Object when IsSensitive(childPath):", source, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(source, "expectedKind: JsonValueKind.Object"));
        Assert.Contains("ConfigEditorModel.JsonKindMismatch(kindElement, expectedType)", source, StringComparison.Ordinal);
        Assert.Contains("ConfigEditorModel.FirstArrayItemKindError(jsonArray, itemSchema)", source, StringComparison.Ordinal);
        Assert.Contains("RejectSensitiveDraft(path, session.Error, errorBlock);", source, StringComparison.Ordinal);
        Assert.Contains("RejectSensitiveDraft(path, schemaError, errorBlock);", source, StringComparison.Ordinal);
        Assert.Contains("AbandonSensitiveDraft(path, errorBlock);", source, StringComparison.Ordinal);
        Assert.Contains("TryRestoreCommittedReplacement(out var previous)", source, StringComparison.Ordinal);
    }

    private static int CountOf(string source, string text)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(text, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += text.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
