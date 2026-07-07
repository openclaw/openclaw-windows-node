using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// App-level capability exposing navigation, status, and configuration
/// through the MCP server for programmatic testing and CLI agents.
/// </summary>
public class AppCapability : NodeCapabilityBase
{
    public override string Category => "app";

    private static readonly string[] _commands = new[]
    {
        "app.navigate",
        "app.status",
        "app.sessions",
        "app.agents",
        "app.nodes",
        "app.config.get",
        "app.settings.get",
        "app.settings.set",
        "app.menu",
        "app.search",
        "app.dashboard.url",
        "app.chat.snapshot",
        "app.chat.send",
        "app.chat.reset",
        "app.chat.queue.list",
        "app.chat.queue.cancel",
    };

    public override IReadOnlyList<string> Commands => _commands;

    // Handler delegates — wired up by App.xaml.cs after construction.
    public Func<string, Task<object?>>? NavigateHandler;
    public Func<object?>? StatusHandler;
    public Func<string?, Task<object?>>? SessionsHandler;
    public Func<Task<object?>>? AgentsHandler;
    public Func<object?>? NodesHandler;
    public Func<string?, Task<object?>>? ConfigGetHandler;
    public Func<string, object?>? SettingsGetHandler;
    public Func<string, string, object?>? SettingsSetHandler;
    public Func<object?>? MenuHandler;
    public Func<string, object?>? SearchHandler;
    public Func<string?, object?>? DashboardUrlHandler;
    public Func<string?, Task<object?>>? ChatSnapshotHandler;
    public Func<string?, string, Task<object?>>? ChatSendHandler;
    public Func<string?, Task<object?>>? ChatResetHandler;
    public Func<string?, Task<object?>>? ChatQueueListHandler;
    public Func<string?, string, Task<object?>>? ChatQueueCancelHandler;

    public AppCapability(IOpenClawLogger logger) : base(logger) { }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "app.navigate" => await HandleNavigate(request),
            "app.status" => HandleStatus(),
            "app.sessions" => await HandleSessions(request),
            "app.agents" => await HandleAgents(),
            "app.nodes" => HandleNodes(),
            "app.config.get" => await HandleConfigGet(request),
            "app.settings.get" => HandleSettingsGet(request),
            "app.settings.set" => HandleSettingsSet(request),
            "app.menu" => HandleMenu(),
            "app.search" => HandleSearch(request),
            "app.dashboard.url" => HandleDashboardUrl(request),
            "app.chat.snapshot" => await HandleChatSnapshot(request),
            "app.chat.send" => await HandleChatSend(request),
            "app.chat.reset" => await HandleChatReset(request),
            "app.chat.queue.list" => await HandleChatQueueList(request),
            "app.chat.queue.cancel" => await HandleChatQueueCancel(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandleNavigate(NodeInvokeRequest request)
    {
        var page = GetStringArg(request.Args, "page");
        if (string.IsNullOrEmpty(page))
            return Error("Missing required arg: page");
        if (NavigateHandler == null)
            return Error("Navigate handler not registered");
        var result = await NavigateHandler(page);
        return Success(result);
    }

    private NodeInvokeResponse HandleStatus()
    {
        if (StatusHandler == null)
            return Error("Status handler not registered");
        return Success(StatusHandler());
    }

    private async Task<NodeInvokeResponse> HandleSessions(NodeInvokeRequest request)
    {
        var agentId = GetStringArg(request.Args, "agentId");
        if (SessionsHandler == null)
            return Error("Sessions handler not registered");
        var result = await SessionsHandler(agentId);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleAgents()
    {
        if (AgentsHandler == null)
            return Error("Agents handler not registered");
        var result = await AgentsHandler();
        return Success(result);
    }

    private NodeInvokeResponse HandleNodes()
    {
        if (NodesHandler == null)
            return Error("Nodes handler not registered");
        return Success(NodesHandler());
    }

    private async Task<NodeInvokeResponse> HandleConfigGet(NodeInvokeRequest request)
    {
        var path = GetStringArg(request.Args, "path");
        if (ConfigGetHandler == null)
            return Error("Config handler not registered");
        var result = await ConfigGetHandler(path);
        return Success(result);
    }

    private NodeInvokeResponse HandleSettingsGet(NodeInvokeRequest request)
    {
        var name = GetStringArg(request.Args, "name");
        if (string.IsNullOrEmpty(name))
            return Error("Missing required arg: name");
        if (SettingsGetHandler == null)
            return Error("Settings handler not registered");
        return Success(SettingsGetHandler(name));
    }

    private NodeInvokeResponse HandleSettingsSet(NodeInvokeRequest request)
    {
        var name = GetStringArg(request.Args, "name");
        var value = GetStringArg(request.Args, "value");
        if (string.IsNullOrEmpty(name))
            return Error("Missing required arg: name");
        if (value == null)
            return Error("Missing required arg: value");
        if (SettingsSetHandler == null)
            return Error("Settings handler not registered");

        var result = SettingsSetHandler(name, value);
        if (TryGetErrorPayload(result, out var error))
            return Error(error);

        return Success(result);
    }

    private static bool TryGetErrorPayload(object? result, out string error)
    {
        error = "";
        if (result == null)
            return false;

        var property = result.GetType().GetProperty(
            "error",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.IgnoreCase);

        if (property?.GetValue(result) is not string message ||
            string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        error = message;
        return true;
    }

    private NodeInvokeResponse HandleMenu()
    {
        if (MenuHandler == null)
            return Error("Menu handler not registered");
        return Success(MenuHandler());
    }

    private NodeInvokeResponse HandleSearch(NodeInvokeRequest request)
    {
        var query = GetStringArg(request.Args, "query");
        if (string.IsNullOrEmpty(query))
            return Error("Missing required arg: query");
        if (SearchHandler == null)
            return Error("Search handler not registered");
        return Success(SearchHandler(query));
    }

    private NodeInvokeResponse HandleDashboardUrl(NodeInvokeRequest request)
    {
        if (DashboardUrlHandler == null)
            return Error("Dashboard URL handler not registered");
        return Success(DashboardUrlHandler(GetStringArg(request.Args, "path")));
    }

    private async Task<NodeInvokeResponse> HandleChatSnapshot(NodeInvokeRequest request)
    {
        if (ChatSnapshotHandler == null)
            return Error("Chat snapshot handler not registered");

        var result = await ChatSnapshotHandler(GetOptionalThreadId(request));
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleChatSend(NodeInvokeRequest request)
    {
        if (ChatSendHandler == null)
            return Error("Chat send handler not registered");

        var message = GetStringArg(request.Args, "message");
        if (string.IsNullOrWhiteSpace(message))
            return Error("Missing required arg: message");

        var result = await ChatSendHandler(GetOptionalThreadId(request), message);
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleChatReset(NodeInvokeRequest request)
    {
        if (ChatResetHandler == null)
            return Error("Chat reset handler not registered");

        var result = await ChatResetHandler(GetOptionalThreadId(request));
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleChatQueueList(NodeInvokeRequest request)
    {
        if (ChatQueueListHandler == null)
            return Error("Chat queue list handler not registered");

        var result = await ChatQueueListHandler(GetOptionalThreadId(request));
        return Success(result);
    }

    private async Task<NodeInvokeResponse> HandleChatQueueCancel(NodeInvokeRequest request)
    {
        if (ChatQueueCancelHandler == null)
            return Error("Chat queue cancel handler not registered");

        var queuedMessageId = GetStringArg(request.Args, "queuedMessageId");
        if (string.IsNullOrWhiteSpace(queuedMessageId))
            return Error("Missing required arg: queuedMessageId");

        var result = await ChatQueueCancelHandler(GetOptionalThreadId(request), queuedMessageId);
        return Success(result);
    }

    private string? GetOptionalThreadId(NodeInvokeRequest request) =>
        GetStringArg(request.Args, "threadId")
        ?? GetStringArg(request.Args, "sessionKey");
}
