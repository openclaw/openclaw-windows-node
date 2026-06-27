using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class WorkspacePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;

    // All entries from the latest list result, in display (sorted) order.
    private readonly List<WorkspaceFilesModel.WorkspaceFileEntry> _allEntries = new();

    // relativePath → entry, for selection lookup. Case-sensitive: workspace
    // paths may differ only by case.
    private readonly Dictionary<string, WorkspaceFilesModel.WorkspaceFileEntry> _entriesByPath =
        new(StringComparer.Ordinal);

    private enum FileBodyKind { Loaded, Missing }

    // relativePath → resolved body. Only stable outcomes are cached (loaded
    // content or confirmed-missing); transient/unavailable errors are NOT cached
    // so re-selecting the file retries the fetch.
    private readonly Dictionary<string, (FileBodyKind Kind, string? Content)> _fileContent =
        new(StringComparer.Ordinal);

    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private string _browserPath = string.Empty;
    private string? _browserParentPath;
    private string _searchQuery = string.Empty;
    private bool _suppressSearchTextChanged;
    private bool _renderMarkdown = true;

    // Monotonic token guarding against out-of-order async results: a list/file
    // load applies only when its token still matches the latest request.
    private int _loadToken;

    /// <summary>Set by HubWindow before <see cref="Initialize"/> to specify the active agent scope.</summary>
    public string AgentId { get; set; } = "main";
    public string CurrentAgentId => AgentId;

    public WorkspacePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _ = LoadAsync();
        };
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = e.NewSize.Width;
        if (double.IsNaN(available) || available <= 0) return;
        var max = ContentRoot.MaxWidth;
        ContentRoot.Width = double.IsNaN(max) || double.IsInfinity(max)
            ? available
            : Math.Min(available, max);
    }

    public void Initialize()
    {
        _ = LoadAsync();
    }

    /// <summary>
    /// Resolve the gateway session key for the current agent. The typed
    /// <c>sessions.files.*</c> API is keyed by session key (e.g.
    /// <c>agent:main:main</c>), so map the agent to its canonical main session,
    /// preferring the handshake-resolved key for the default agent. The
    /// <c>agent:&lt;id&gt;:main</c> fallback assumes a simple slug agent id.
    /// </summary>
    private string? ResolveSessionKey()
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) return null;

        var agentId = string.IsNullOrWhiteSpace(AgentId) ? "main" : AgentId.Trim();
        if (string.Equals(agentId, "main", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(client.MainSessionKey))
        {
            return client.MainSessionKey;
        }
        return $"agent:{agentId}:main";
    }

    private async Task LoadAsync()
    {
        // Invalidate any in-flight list/file loads up front — before the
        // connected/key early-returns — so a stale result can never render
        // after a newer (even failed) load.
        var token = ++_loadToken;

        var client = CurrentApp.GatewayClient;
        var status = CurrentApp.AppState?.Status ?? ConnectionStatus.Disconnected;
        if (client == null || status != ConnectionStatus.Connected)
        {
            ShowDisconnected();
            return;
        }

        var key = ResolveSessionKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowDisconnected();
            return;
        }

        BeginLoading();

        SessionFileList result;
        try
        {
            var search = string.IsNullOrWhiteSpace(_searchQuery) ? null : _searchQuery.Trim();
            var path = search is null ? _browserPath : string.Empty;
            result = await client.ListSessionFilesAsync(key, path, search);
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            Services.Logger.Warn($"[WorkspacePage] sessions.files.list failed: {ex.Message}");
            EndLoading();
            if (CurrentApp.AppState?.Status == ConnectionStatus.Connected)
                ShowLoadError();
            else
                ShowDisconnected();
            return;
        }

        if (token != _loadToken) return; // a newer load superseded this one
        EndLoading();
        ApplyListResult(result);
    }

    private void ApplyListResult(SessionFileList result)
    {
        ClearFiles();

        var state = WorkspaceFilesModel.FromSessionFileList(result);
        WorkspacePathText.Text = state.WorkspacePath;
        _browserPath = state.BrowserPath;
        _browserParentPath = state.BrowserParentPath;
        UpdateBrowserChrome(state);

        if (!state.Supported)
        {
            ShowUnsupported();
            return;
        }

        foreach (var entry in state.Entries)
        {
            _allEntries.Add(entry);
            _entriesByPath[entry.RelativePath] = entry;
        }

        if (_allEntries.Count == 0 && string.IsNullOrWhiteSpace(_searchQuery) && string.IsNullOrEmpty(_browserPath))
        {
            ShowNoFiles();
            return;
        }

        HideFallback();
        BodyGrid.Visibility = Visibility.Visible;
        ApplyFilter();
    }

    private void BeginLoading()
    {
        HideFallback();
        LoadingRing.IsActive = true;
        LoadingPanel.Visibility = Visibility.Visible;
        ClearFiles();
        BrowserNoticeText.Visibility = Visibility.Collapsed;
    }

    private void EndLoading()
    {
        LoadingRing.IsActive = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        var filtered = WorkspaceFilesModel.Filter(_allEntries, _searchQuery);

        FileList.SelectionChanged -= FileList_SelectionChanged;
        FileList.Items.Clear();
        foreach (var entry in filtered)
            FileList.Items.Add(BuildFileRow(entry));
        FileList.SelectionChanged += FileList_SelectionChanged;

        FileCountText.Text = _allEntries.Count > 0
            ? filtered.Count == _allEntries.Count ? $"({_allEntries.Count})" : $"({filtered.Count} of {_allEntries.Count})"
            : string.Empty;

        bool hasResults = filtered.Count > 0;
        bool searching = !string.IsNullOrWhiteSpace(_searchQuery);
        NoResultsText.Text = LocalizationHelper.GetString(searching
            ? "WorkspacePage_NoSearchResults.Text"
            : "WorkspacePage_NoFolderResults");
        NoResultsText.Visibility = !hasResults ? Visibility.Visible : Visibility.Collapsed;

        if (hasResults)
        {
            SelectInitialRow(filtered);
        }
        else
        {
            FileBodyPresenter.Content = null;
            SelectedFileText.Text = string.Empty;
            SelectedFileMeta.Visibility = Visibility.Collapsed;
            ViewModeSelector.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput &&
            args.Reason != AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            return;
        }
        if (_suppressSearchTextChanged) return;
        _searchQuery = sender.Text ?? string.Empty;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SelectInitialRow(IReadOnlyList<WorkspaceFilesModel.WorkspaceFileEntry> filtered)
    {
        int index = -1;
        for (int i = 0; i < filtered.Count; i++)
        {
            if (filtered[i].CanPreview || !filtered[i].Exists)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            FileList.SelectedIndex = -1;
            SelectedFileText.Text = string.Empty;
            SelectedFileMeta.Visibility = Visibility.Collapsed;
            ViewModeSelector.Visibility = Visibility.Collapsed;
            FileBodyPresenter.Content = null;
            return;
        }

        FileList.SelectedIndex = index;
    }

    private ListViewItem BuildFileRow(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new FontIcon
        {
            Glyph = entry.IsDirectory ? FluentIconCatalog.Folder : FluentIconCatalog.Document,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            IsTextScaleFactorEnabled = false,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var stack = new StackPanel { Spacing = 4 };
        Grid.SetColumn(stack, 1);

        stack.Children.Add(new TextBlock
        {
            Text = entry.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        var meta = BuildRowMeta(entry);
        if (!string.IsNullOrEmpty(meta))
        {
            stack.Children.Add(new TextBlock
            {
                Text = meta,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
        }

        row.Children.Add(stack);

        var badges = BuildBadges(entry);
        if (badges != null)
        {
            Grid.SetColumn(badges, 2);
            row.Children.Add(badges);
        }

        var item = new ListViewItem { Content = row, Tag = entry.RelativePath };
        AutomationProperties.SetName(item, BuildAutomationName(entry));
        item.ContextFlyout = BuildRowContextFlyout(entry);
        return item;
    }

    private MenuFlyout BuildRowContextFlyout(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var copyLabel = LocalizationHelper.GetString("WorkspacePage_CopyPath");
        var copy = new MenuFlyoutItem
        {
            Text = copyLabel,
            Tag = entry.RequestPath,
            Icon = new FontIcon
            {
                Glyph = FluentIconCatalog.Copy,
                FontSize = 16,
                IsTextScaleFactorEnabled = false,
            },
        };
        copy.Click += CopyPathButton_Click;

        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        return menu;
    }

    private static string BuildAutomationName(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var role = LocalizationHelper.GetString(entry.IsDirectory
            ? "WorkspacePage_FileType_Folder"
            : "WorkspacePage_FileType_File");
        var parts = new List<string> { role, entry.Name };
        var meta = BuildRowMeta(entry);
        if (!string.IsNullOrEmpty(meta)) parts.Add(meta);
        if (!entry.Exists) parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Missing"));
        if (entry.Touched) parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Edited"));
        else if (entry.Read) parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Read"));
        return string.Join(", ", parts);
    }

    // Second-line caption: parent folder · size. Modified time, when present,
    // is shown in the detail header rather than crowding every row.
    private static string BuildRowMeta(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var parts = new List<string>(2);
        var dir = WorkspaceFilesModel.DirectoryOf(entry.RelativePath);
        if (!string.IsNullOrEmpty(dir)) parts.Add(dir);
        var size = WorkspaceFilesModel.FormatSize(entry.Size);
        if (!string.IsNullOrEmpty(size)) parts.Add(size);
        return string.Join(" · ", parts);
    }

    private StackPanel? BuildBadges(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (!entry.Exists)
            badges.Children.Add(BuildBadge("WorkspacePage_Badge_Missing", "SystemFillColorCautionBrush"));
        if (entry.Touched)
            badges.Children.Add(BuildBadge("WorkspacePage_Badge_Edited", "AccentTextFillColorPrimaryBrush"));
        else if (entry.Read)
            badges.Children.Add(BuildBadge("WorkspacePage_Badge_Read", "TextFillColorSecondaryBrush"));

        return badges.Children.Count > 0 ? badges : null;
    }

    private static Border BuildBadge(string resKey, string foregroundBrushKey)
    {
        return new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 0, 8, 0),
            Child = new TextBlock
            {
                Text = LocalizationHelper.GetString(resKey),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources[foregroundBrushKey],
            },
        };
    }

    private void ClearFiles()
    {
        FileList.SelectionChanged -= FileList_SelectionChanged;
        FileList.Items.Clear();
        FileList.SelectionChanged += FileList_SelectionChanged;
        _allEntries.Clear();
        _entriesByPath.Clear();
        _fileContent.Clear();
        FileBodyPresenter.Content = null;
        SelectedFileText.Text = string.Empty;
        SelectedFileMeta.Visibility = Visibility.Collapsed;
        FileCountText.Text = string.Empty;
        NoResultsText.Visibility = Visibility.Collapsed;
        BodyGrid.Visibility = Visibility.Collapsed;
        ViewModeSelector.Visibility = Visibility.Collapsed;
    }

    private string? SelectedRelativePath() =>
        FileList.SelectedItem is ListViewItem { Tag: string path } ? path : null;

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRelativePath() is not string relativePath ||
            !_entriesByPath.TryGetValue(relativePath, out var entry))
        {
            return;
        }

        SelectedFileText.Text = entry.Name;
        UpdateDetailMeta(entry);

        if (entry.IsDirectory)
        {
            ViewModeSelector.Visibility = Visibility.Collapsed;
            BrowseToPath(entry.RequestPath);
            return;
        }

        ViewModeSelector.Visibility = IsMarkdown(entry.Name) ? Visibility.Visible : Visibility.Collapsed;

        if (!entry.CanPreview)
        {
            ViewModeSelector.Visibility = Visibility.Collapsed;
            FileBodyPresenter.Content = BuildNoteBody(
                LocalizationHelper.GetString("WorkspacePage_BrowserOnlyFileNote"));
            return;
        }

        // Files the list already reported as missing on disk never need a fetch.
        if (!entry.Exists)
        {
            _fileContent[relativePath] = (FileBodyKind.Missing, null);
            RenderSelectedFile();
            return;
        }

        if (_fileContent.ContainsKey(relativePath))
        {
            RenderSelectedFile();
        }
        else
        {
            ShowLoadingBody();
            _ = LoadFileAsync(entry);
        }
    }

    private async Task LoadFileAsync(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var client = CurrentApp.GatewayClient;
        var key = ResolveSessionKey();
        if (client == null || string.IsNullOrWhiteSpace(key))
        {
            ShowFileUnavailable(entry.RelativePath);
            return;
        }

        var token = _loadToken;
        try
        {
            // Use the gateway's original path string, not the normalized display
            // path, so the request matches exactly what the gateway indexed.
            var result = await client.GetSessionFileAsync(key, entry.RequestPath);
            if (token != _loadToken) return; // list reloaded underneath us

            if (!result.IsSupported)
            {
                // A gateway that lists but can't serve a single file is an
                // inconsistent edge — surface it inline rather than tearing down
                // the whole rail, and don't cache so re-selection can retry.
                ShowFileUnavailable(entry.RelativePath);
                return;
            }

            if (result.Missing)
                SetFileBody(entry.RelativePath, FileBodyKind.Missing, null);
            else if (result.Content is null)
                ShowFileUnavailable(entry.RelativePath);
            else
                SetFileBody(entry.RelativePath, FileBodyKind.Loaded, result.Content);
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            // The gateway returns an error for missing / too-large files. Show it
            // inline without caching so the user can retry by reselecting.
            Services.Logger.Warn($"[WorkspacePage] sessions.files.get failed for '{entry.RequestPath}': {ex.Message}");
            ShowFileUnavailable(entry.RelativePath);
        }
    }

    // Cache a stable body outcome (loaded content or confirmed-missing) and
    // render it if the file is still selected.
    private void SetFileBody(string relativePath, FileBodyKind kind, string? content)
    {
        _fileContent[relativePath] = (kind, content);
        if (string.Equals(SelectedRelativePath(), relativePath, StringComparison.Ordinal))
            RenderSelectedFile();
    }

    // Transient/unavailable error: shown inline but NOT cached, so re-selecting
    // the file retries the fetch instead of permanently reading as "missing".
    private void ShowFileUnavailable(string relativePath)
    {
        if (string.Equals(SelectedRelativePath(), relativePath, StringComparison.Ordinal))
        {
            FileBodyPresenter.Content = BuildNoteBody(
                LocalizationHelper.GetString("WorkspacePage_FileUnavailable"));
        }
    }

    private void UpdateDetailMeta(WorkspaceFilesModel.WorkspaceFileEntry entry)
    {
        var parts = new List<string>(3);
        var size = WorkspaceFilesModel.FormatSize(entry.Size);
        if (!string.IsNullOrEmpty(size)) parts.Add(size);
        if (entry.ModifiedUtc is { } modified)
            parts.Add(modified.ToLocalTime().ToString("g"));
        if (!entry.Exists)
            parts.Add(LocalizationHelper.GetString("WorkspacePage_Badge_Missing"));

        if (parts.Count > 0)
        {
            SelectedFileMeta.Text = string.Join(" · ", parts);
            SelectedFileMeta.Visibility = Visibility.Visible;
        }
        else
        {
            SelectedFileMeta.Visibility = Visibility.Collapsed;
        }
    }

    private void ViewModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _renderMarkdown = ViewModeSelector.SelectedItem == ViewModeRenderedItem;
        RenderSelectedFile();
    }

    private void ShowLoadingBody()
    {
        var loading = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8
        };
        loading.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24 });
        loading.Children.Add(new TextBlock
        {
            Text = LocalizationHelper.GetString("WorkspacePage_LoadingContent"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        FileBodyPresenter.Content = loading;
    }

    private static TextBlock BuildNoteBody(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
    };

    private void RenderSelectedFile()
    {
        if (SelectedRelativePath() is not string relativePath)
        {
            FileBodyPresenter.Content = null;
            return;
        }

        if (!_fileContent.TryGetValue(relativePath, out var body))
        {
            ShowLoadingBody();
            return;
        }

        if (body.Kind == FileBodyKind.Missing || body.Content == null)
        {
            FileBodyPresenter.Content = BuildNoteBody(
                LocalizationHelper.GetString("WorkspacePage_MissingFile"));
            return;
        }

        var name = _entriesByPath.TryGetValue(relativePath, out var entry) ? entry.Name : relativePath;
        if (IsMarkdown(name) && _renderMarkdown)
        {
            FileBodyPresenter.Content = BuildMarkdownView(body.Content);
        }
        else
        {
            FileBodyPresenter.Content = BuildRawView(body.Content);
        }
    }

    private UIElement BuildRawView(string content)
    {
        return new TextBlock
        {
            Text = content,
            Style = (Style)Resources["WorkspaceCodeTextStyle"],
        };
    }

    private static bool IsMarkdown(string fileName)
    {
        return fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    // Minimal Markdown renderer: ATX headings, paragraphs, lists, fenced
    // code, inline `code`, **bold**, *italic*. Links render as label only.
    // Block styles come from Page.Resources so no raw FontSize is used.

    private UIElement BuildMarkdownView(string markdown)
    {
        var root = new StackPanel { Spacing = 0 };

        var h1 = (Style)Resources["WorkspaceMarkdownH1Style"];
        var h2 = (Style)Resources["WorkspaceMarkdownH2Style"];
        var h3 = (Style)Resources["WorkspaceMarkdownH3Style"];
        var para = (Style)Resources["WorkspaceMarkdownParagraphStyle"];
        var listItem = (Style)Resources["WorkspaceMarkdownListItemStyle"];
        var codeBlock = (Style)Resources["WorkspaceCodeBlockStyle"];

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing ```
                root.Children.Add(new TextBlock
                {
                    Text = code.ToString(),
                    Style = codeBlock
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (TryParseHeading(line, out var headingLevel, out var headingText))
            {
                var headingStyle = headingLevel switch
                {
                    1 => h1,
                    2 => h2,
                    _ => h3, // h3..h6 all share BodyStrong styling
                };
                root.Children.Add(BuildInlineTextBlock(headingText, headingStyle));
                i++;
                continue;
            }

            if (IsListItem(line, out _, out _))
            {
                while (i < lines.Length && IsListItem(lines[i], out var marker, out var body))
                {
                    root.Children.Add(BuildInlineTextBlock(marker + body, listItem));
                    i++;
                }
                continue;
            }

            // Paragraph: absorb continuation lines until a block-ending marker
            var sb = new StringBuilder(line);
            i++;
            while (i < lines.Length)
            {
                var next = lines[i];
                if (string.IsNullOrWhiteSpace(next)) break;
                if (TryParseHeading(next, out _, out _)) break;
                if (next.TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                if (IsListItem(next, out _, out _)) break;
                sb.Append(' ').Append(next.Trim());
                i++;
            }
            root.Children.Add(BuildInlineTextBlock(sb.ToString(), para));
        }

        return root;
    }

    private static TextBlock BuildInlineTextBlock(string text, Style style)
    {
        var tb = new TextBlock { Style = style };
        AppendInlineMarkdown(tb.Inlines, text);
        return tb;
    }

    // ATX heading: 1–6 leading `#`, then at least one space, then the text.
    // Optional trailing `#`s (closing sequence) are stripped.
    private static bool TryParseHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        int i = 0;
        while (i < line.Length && line[i] == '#' && i < 6) i++;
        if (i == 0 || i >= line.Length || line[i] != ' ') return false;
        level = i;
        var body = line[(i + 1)..].TrimEnd();
        // Strip optional closing # # # sequence
        int end = body.Length;
        while (end > 0 && body[end - 1] == '#') end--;
        if (end < body.Length && (end == 0 || body[end - 1] == ' '))
            body = body[..end].TrimEnd();
        text = body;
        return true;
    }

    private static bool IsListItem(string line, out string marker, out string body)
    {
        marker = "";
        body = "";
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            marker = "•  ";
            body = trimmed[2..];
            return true;
        }
        // numbered: digits + "." + space
        int dot = trimmed.IndexOf('.');
        if (dot > 0 && dot < trimmed.Length - 1 && trimmed[dot + 1] == ' ')
        {
            bool allDigits = true;
            for (int k = 0; k < dot; k++)
                if (!char.IsDigit(trimmed[k])) { allDigits = false; break; }
            if (allDigits)
            {
                marker = trimmed[..(dot + 1)] + "  ";
                body = trimmed[(dot + 2)..];
                return true;
            }
        }
        return false;
    }

    private static void AppendInlineMarkdown(InlineCollection inlines, string text)
    {
        text = StripLinks(text);

        int i = 0;
        var buf = new StringBuilder();
        void FlushPlain()
        {
            if (buf.Length == 0) return;
            inlines.Add(new Run { Text = buf.ToString() });
            buf.Clear();
        }

        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain();
                    inlines.Add(new Run
                    {
                        Text = text.Substring(i + 1, end - i - 1),
                        FontFamily = new FontFamily("Consolas")
                    });
                    i = end + 1;
                    continue;
                }
            }
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 1)
                {
                    FlushPlain();
                    var bold = new Bold();
                    bold.Inlines.Add(new Run { Text = text.Substring(i + 2, end - i - 2) });
                    inlines.Add(bold);
                    i = end + 2;
                    continue;
                }
            }
            // Italic: single asterisk, not part of a bold ** pair
            if (text[i] == '*' &&
                (i == 0 || text[i - 1] != '*') &&
                (i + 1 >= text.Length || text[i + 1] != '*'))
            {
                int end = -1;
                for (int k = i + 1; k < text.Length; k++)
                {
                    if (text[k] == '*' && (k + 1 >= text.Length || text[k + 1] != '*'))
                    {
                        end = k;
                        break;
                    }
                }
                if (end > i)
                {
                    FlushPlain();
                    var italic = new Italic();
                    italic.Inlines.Add(new Run { Text = text.Substring(i + 1, end - i - 1) });
                    inlines.Add(italic);
                    i = end + 1;
                    continue;
                }
            }

            buf.Append(text[i]);
            i++;
        }
        FlushPlain();
    }

    private static string StripLinks(string text)
    {
        // [label](url) → label; non-nested only.
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                int closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    int closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket)
                    {
                        sb.Append(text, i + 1, closeBracket - i - 1);
                        i = closeParen + 1;
                        continue;
                    }
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadAsync();
    }

    private void ParentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserParentPath is not null)
            BrowseToPath(_browserParentPath);
    }

    private void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var data = new DataPackage();
            data.SetText(path);
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[WorkspacePage] Clipboard copy failed: {ex.Message}");
            BrowserNoticeText.Text = LocalizationHelper.GetString("WorkspacePage_CopyPathFailed");
            BrowserNoticeText.Visibility = Visibility.Visible;
        }
    }

    private void BrowseToPath(string? path)
    {
        _browserPath = WorkspaceFilesModel.NormalizeBrowserPath(path);
        _suppressSearchTextChanged = true;
        try
        {
            SearchBox.Text = string.Empty;
            _searchQuery = string.Empty;
        }
        finally
        {
            _suppressSearchTextChanged = false;
        }
        _ = LoadAsync();
    }

    private void UpdateBrowserChrome(WorkspaceFilesModel.WorkspaceListState state)
    {
        bool searching = !string.IsNullOrWhiteSpace(state.BrowserSearch);
        ParentFolderButton.IsEnabled = !searching && state.BrowserParentPath is not null;
        CurrentFolderText.Text = searching
            ? LocalizationHelper.Format("WorkspacePage_SearchResultsPath", state.BrowserSearch.Trim())
            : string.IsNullOrEmpty(state.BrowserPath)
                ? LocalizationHelper.GetString("WorkspacePage_RootFolder")
                : state.BrowserPath;

        if (state.BrowserTruncated)
        {
            BrowserNoticeText.Text = LocalizationHelper.GetString("WorkspacePage_BrowserTruncated");
            BrowserNoticeText.Visibility = Visibility.Visible;
        }
        else
        {
            BrowserNoticeText.Visibility = Visibility.Collapsed;
        }
    }

    private void RepairLink_Click(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void HideFallback()
    {
        FallbackInfoBar.IsOpen = false;
        RepairLink.Visibility = Visibility.Collapsed;
    }

    private void ShowLoadError()
    {
        EndLoading();
        ClearFiles();
        FallbackInfoBar.Severity = InfoBarSeverity.Warning;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_LoadErrorMessage");
        RepairLink.Visibility = Visibility.Collapsed;
        FallbackInfoBar.IsOpen = true;
    }

    // Gateway unreachable: offer a one-tap route to Connection settings so the
    // user can repair pairing instead of hitting a silent dead end.
    private void ShowDisconnected()
    {
        EndLoading();
        ClearFiles();
        WorkspacePathText.Text = string.Empty;
        FallbackInfoBar.Severity = InfoBarSeverity.Warning;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_DisconnectedMessage");
        RepairLink.Visibility = Visibility.Visible;
        FallbackInfoBar.IsOpen = true;
    }

    // Connected gateway that doesn't implement sessions.files.list (older
    // gateway) or errored serving it. Same repair affordance, distinct copy so
    // the user knows it's a capability gap.
    private void ShowUnsupported()
    {
        EndLoading();
        FallbackInfoBar.Severity = InfoBarSeverity.Warning;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_UnsupportedMessage");
        RepairLink.Visibility = Visibility.Visible;
        FallbackInfoBar.IsOpen = true;
        BodyGrid.Visibility = Visibility.Collapsed;
    }

    private void ShowNoFiles()
    {
        FallbackInfoBar.Severity = InfoBarSeverity.Informational;
        FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_NoFilesMessage");
        RepairLink.Visibility = Visibility.Collapsed;
        FallbackInfoBar.IsOpen = true;
        BodyGrid.Visibility = Visibility.Collapsed;
    }
}
