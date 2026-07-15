namespace OpenClawTray.Chat;

public record ChannelGroup(
    string AgentLabel,
    (string Id, string Title, string? Model, string? ModelProvider)[] Sessions);
