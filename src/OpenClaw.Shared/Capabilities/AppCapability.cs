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
        return Success(SettingsSetHandler(name, value));
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
}
