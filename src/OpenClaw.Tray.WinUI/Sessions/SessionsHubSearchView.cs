using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;
using OpenClawTray.Helpers;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Sessions;

/// <summary>
/// Full-text transcript search over the roster the current filters select
/// (agent, status, global and unknown). Results open the session in chat or
/// show its details when it is on the loaded roster.
/// </summary>
internal static class SessionsHubSearchView
{
    private const string SearchGlyph = "\uE721"; // Search
    private static string T(string key) => SessionsHubPresentation.T(key);
    private static string F(string key, params object?[] args) => SessionsHubPresentation.F(key, args);

    public static Element Render(SessionsHubViewContext context, IReadOnlyDictionary<string, string> titles)
    {
        var state = context.State;
        var controller = context.Controller;
        var transcript = state.Transcript;
        var scope = ScopeText(state);

        var input = Grid([GridSize.Star(), GridSize.Auto, GridSize.Auto], [GridSize.Auto],
            TextBox(transcript.Query, controller.SetTranscriptQuery, T("TranscriptPlaceholder"))
                .IsEnabled(state.IsConnected)
                .OnKeyDown((_, args) =>
                {
                    if (args.Key == global::Windows.System.VirtualKey.Enter)
                    {
                        args.Handled = true;
                        _ = controller.RunTranscriptSearchAsync();
                    }
                })
                .AutomationName(T("TranscriptLabel"))
                .AutomationId("SessionsHubTranscriptQuery")
                .Grid(column: 0),
            SessionsHubView.CommandButton(SearchGlyph, T("Search"), () => _ = controller.RunTranscriptSearchAsync(),
                    state.IsConnected && transcript.Query.Trim().Length > 0 && !transcript.IsSearching, "SessionsHubTranscriptRun", accent: true)
                .Margin(8, 0, 0, 0).Grid(column: 1),
            Button(T("CloseSearch"), () => controller.SetTranscriptOpen(false)).AutomationName(T("CloseSearch"))
                .Margin(8, 0, 0, 0).AutomationId("SessionsHubTranscriptClose").Grid(column: 2));

        var children = new List<Element>
        {
            input,
            TextBlock(F("TranscriptScope", scope)).FontSize(12).Foreground(Theme.SecondaryText).TextWrapping(TextWrapping.Wrap),
        };

        if (!state.IsConnected)
        {
            children.Add(SessionsHubView.EmptyState(SearchGlyph, T("TranscriptTitle"), T("TranscriptNeedsConnection"),
                automationId: "SessionsHubTranscriptDisconnected"));
        }
        else if (transcript.IsSearching)
        {
            children.Add(HStack(12, ProgressRing().IsActive(true).Size(20, 20), TextBlock(T("Searching")).VAlign(VerticalAlignment.Center))
                .Margin(0, 24).HAlign(HorizontalAlignment.Center).LiveRegion().AutomationId("SessionsHubTranscriptSearching"));
        }
        else if (transcript.Error is { } error)
        {
            children.Add((InfoBar(T("TranscriptFailed"), error) with
                {
                    IsOpen = true,
                    ActionButtonContent = T("Retry"),
                    OnActionButtonClick = () => _ = controller.RunTranscriptSearchAsync(),
                })
                .Severity(InfoBarSeverity.Error).IsClosable(false).AutomationId("SessionsHubTranscriptError"));
        }
        else if (transcript.Result is { } result)
        {
            children.AddRange(Results(context, result, titles));
        }
        else
        {
            children.Add(SessionsHubView.EmptyState(SearchGlyph, T("TranscriptTitle"), T("TranscriptHint"),
                automationId: "SessionsHubTranscriptIdle"));
        }

        return ScrollView(VStack(8, children.ToArray()).Padding(0, 0, 12, 12))
            .AutomationId("SessionsHubTranscript");
    }

    private static string ScopeText(SessionsHubState state)
    {
        var agent = state.Filters.AgentId is { } id
            ? state.Agents.FirstOrDefault(a => a.Id == id)?.Name ?? id
            : T("AllAgents");
        var status = state.Filters.Status switch
        {
            SessionArchiveFilter.Archived => T("StatusArchived"),
            SessionArchiveFilter.All => T("StatusAll"),
            _ => T("StatusActive"),
        };
        var parts = new List<string> { agent, status };
        if (state.Filters.Status == SessionArchiveFilter.Active
            && SessionListQuery.ParsePositiveInteger(state.Filters.ActiveMinutes) is { } minutes)
            parts.Add(F("ScopeActiveWithin", minutes));
        if (state.Filters.Search.Trim() is { Length: > 0 } name)
            parts.Add(F("ScopeNameContains", name));
        parts.Add(T(state.Filters.IncludeGlobal ? "ScopeGlobalIncluded" : "ScopeGlobalExcluded"));
        parts.Add(T(state.Filters.IncludeUnknown ? "ScopeUnknownIncluded" : "ScopeUnknownExcluded"));
        return string.Join(" · ", parts);
    }

    private static IEnumerable<Element> Results(
        SessionsHubViewContext context, SessionSearchResult result, IReadOnlyDictionary<string, string> titles)
    {
        var state = context.State;
        if (result.Indexing)
            yield return (InfoBar(T("TranscriptIndexingTitle"), T("TranscriptIndexingBody")) with { IsOpen = true })
                .Severity(InfoBarSeverity.Informational).IsClosable(false).AutomationId("SessionsHubTranscriptIndexing");
        if (result.ArchivedTranscriptsExcluded > 0)
            yield return (InfoBar(null, result.ArchivedTranscriptsExcluded == 1
                        ? T("TranscriptArchivedExcludedOne")
                        : F("TranscriptArchivedExcludedMany", result.ArchivedTranscriptsExcluded)) with { IsOpen = true })
                .Severity(InfoBarSeverity.Informational).IsClosable(false).AutomationId("SessionsHubTranscriptArchived");

        if (result.Hits.Count == 0)
        {
            yield return SessionsHubView.EmptyState(SearchGlyph, T("TranscriptNoMatchesTitle"),
                F("TranscriptNoMatchesBody", state.Transcript.Query), automationId: "SessionsHubTranscriptEmpty");
            yield break;
        }

        var count = result.Hits.Count == 1 ? T("TranscriptMatchOne") : F("TranscriptMatchMany", result.Hits.Count);
        yield return TextBlock(result.Truncated ? $"{count} · {T("TranscriptTruncated")}" : count)
            .FontSize(12).FontWeight(FontWeights.SemiBold).LiveRegion().AutomationId("SessionsHubTranscriptCount");

        var extraRows = result.Sessions.Count > 0 ? SessionsHubPresentation.Titles(result.Sessions) : new Dictionary<string, string>();
        foreach (var (hit, index) in result.Hits.Select((hit, index) => (hit, index)))
        {
            var title = titles.TryGetValue(hit.SessionKey, out var known) ? known
                : extraRows.TryGetValue(hit.SessionKey, out var extra) ? extra
                : hit.SessionKey;
            var onRoster = state.Rows.Any(row => row.Key == hit.SessionKey);
            var when = hit.Timestamp > 0 ? SessionsHubPresentation.RelativeTime(hit.Timestamp, context.NowUtc) : "";
            var role = hit.Role == "user" ? T("RoleUser") : T("RoleAssistant");
            var session = state.Rows.FirstOrDefault(row => row.Key == hit.SessionKey)
                ?? result.Sessions.FirstOrDefault(row => row.Key == hit.SessionKey);
            var agentId = session is null ? null : OpenClaw.Shared.SessionDisplayResolver.Resolve(session).AgentId;
            var agent = agentId is null ? null : state.Agents.FirstOrDefault(entry => entry.Id == agentId);
            var stripe = OpenClaw.Shared.Sessions.SessionAppearance.NormalizeColor(session?.Color);
            Element speaker = hit.Role == "user"
                ? SessionsHubView.IconTile(FluentIconCatalog.Operator, "cyan", 32)
                : SessionsHubView.AgentAvatar(agent, agentId ?? hit.SessionKey, 32);
            yield return SessionsHubView.Card(Grid([GridSize.Px(4), GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    (stripe is null ? Empty() : Border(Empty()).Width(4).CornerRadius(2).Background(SessionsHubView.Strong(stripe))
                            .Margin(0, 10, 0, 10).AccessibilityView(Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw))
                        .Grid(column: 0),
                    speaker.VAlign(VerticalAlignment.Top).Margin(10, 12, 0, 0).Grid(column: 1),
                    VStack(6,
                        Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                            TextBlock(title).FontWeight(FontWeights.SemiBold).TextTrimming(TextTrimming.CharacterEllipsis)
                                .Grid(column: 0),
                            TextBlock(string.IsNullOrEmpty(when) ? role : $"{role} · {when}").FontSize(12)
                                .Foreground(Theme.SecondaryText).Margin(12, 0, 0, 0).Grid(column: 1)),
                        TextBlock(hit.Snippet).TextWrapping(TextWrapping.Wrap).MaxLines(4).IsTextSelectionEnabled(),
                        TextBlock(hit.SessionKey).FontSize(11).Foreground(Theme.TertiaryText).TextTrimming(TextTrimming.CharacterEllipsis),
                        HStack(8,
                            Button(T("OpenInChat"), () => context.Controller.OpenInChat(hit.SessionKey))
                                .AutomationName(F("OpenInChatNamed", title))
                                .AutomationId($"SessionsHubTranscriptOpen:{index}"),
                            onRoster
                                ? Button(T("ShowDetails"), () =>
                                    {
                                        context.Controller.SetTranscriptOpen(false);
                                        context.Controller.OpenDetails(hit.SessionKey);
                                    })
                                    .AutomationName(F("ShowDetailsNamed", title))
                                    .AutomationId($"SessionsHubTranscriptDetails:{index}")
                                : Empty()))
                        .Padding(12).Grid(column: 2)))
                .AutomationName($"{title}: {hit.Snippet}")
                .AutomationId($"SessionsHubTranscriptHit:{index}")
                .WithKey($"hit:{hit.SessionKey}:{hit.MessageId ?? index.ToString()}");
        }
    }
}
