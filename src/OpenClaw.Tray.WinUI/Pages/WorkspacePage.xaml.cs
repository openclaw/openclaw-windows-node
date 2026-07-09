using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

public sealed partial class WorkspacePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private const int WorkspaceListLimit = 500;

    private AppState? _appState;

    // workspace-relative path (case-insensitive) → its list item
    private readonly Dictionary<string, ListViewItem> _fileItems = new(StringComparer.OrdinalIgnoreCase);

    // workspace-relative path → raw preview content (null = missing/unavailable, absent = not loaded yet)
    private readonly Dictionary<string, WorkspaceFileContent?> _fileContent = new(StringComparer.OrdinalIgnoreCase);

    private bool _renderMarkdown = true;
    private string _currentPath = string.Empty;
    private long _loadGeneration;

    /// <summary>Set by HubWindow before <see cref="Initialize"/> to specify the active agent scope.</summary>
    public string AgentId { get; set; } = "main";
    public string CurrentAgentId => AgentId;

    public WorkspacePage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
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
        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;

        var status = CurrentApp.AppState?.Status ?? ConnectionStatus.Disconnected;
        if (CurrentApp.GatewayClient != null && status == ConnectionStatus.Connected)
        {
            _ = LoadWorkspaceListAsync(string.Empty);
        }
        else if (CurrentApp.GatewayClient == null || status != ConnectionStatus.Connected)
        {
            FallbackInfoBar.IsOpen = true;
            FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_DisconnectedMessage");
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.AgentFilesList):
                // Compatibility fallback for older gateways that only expose the
                // managed bootstrap-file API.
                if (_appState!.AgentFilesList.HasValue) UpdateAgentFilesList(_appState.AgentFilesList.Value);
                break;
            case nameof(AppState.AgentFileContent):
                // Compatibility fallback for older gateways that only expose the
                // managed bootstrap-file API.
                if (_appState!.AgentFileContent.HasValue) UpdateAgentFileContent(_appState.AgentFileContent.Value);
                break;
        }
    }

    private async Task LoadWorkspaceListAsync(string path)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            FallbackInfoBar.IsOpen = true;
            FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_DisconnectedMessage");
            return;
        }

        var generation = ++_loadGeneration;
        FallbackInfoBar.IsOpen = false;
        LoadingRing.IsActive = true;
        LoadingPanel.Visibility = Visibility.Visible;
        ClearFiles();

        try
        {
            var data = await client.SendWizardRequestAsync(
                "agents.workspace.list",
                new { agentId = AgentId, path = NormalizeWorkspacePath(path), limit = WorkspaceListLimit },
                timeoutMs: 15000);
            if (generation != _loadGeneration) return;
            UpdateAgentFilesList(data);
        }
        catch (Exception ex) when (IsUnknownWorkspaceMethod(ex))
        {
            Logger.Warn($"[WorkspacePage] agents.workspace.list unsupported, falling back to agents.files.list: {ex.Message}");
            await LoadLegacyAgentFilesListAsync(client, generation);
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Logger.Warn($"[WorkspacePage] Failed to load workspace list: {ex.Message}");
            ShowInfoMessage(ex.Message);
        }
    }

    private async Task LoadLegacyAgentFilesListAsync(IOperatorGatewayClient client, long generation)
    {
        try
        {
            var data = await client.SendWizardRequestAsync(
                "agents.files.list",
                new { agentId = AgentId },
                timeoutMs: 15000);
            if (generation != _loadGeneration) return;
            UpdateAgentFilesList(data);
            ShowInfoMessage("This gateway does not support full workspace browsing yet; showing managed agent files.");
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration) return;
            Logger.Warn($"[WorkspacePage] Failed to load managed agent files: {ex.Message}");
            ShowInfoMessage(ex.Message);
        }
    }

    private async Task LoadWorkspaceFileAsync(WorkspaceEntry entry)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) return;

        try
        {
            var data = await client.SendWizardRequestAsync(
                "agents.workspace.get",
                new { agentId = AgentId, path = entry.Path },
                timeoutMs: 15000);
            UpdateAgentFileContent(data);
        }
        catch (Exception ex) when (IsUnknownWorkspaceMethod(ex))
        {
            Logger.Warn($"[WorkspacePage] agents.workspace.get unsupported, falling back to agents.files.get: {ex.Message}");
            await LoadLegacyAgentFileAsync(client, entry);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WorkspacePage] Failed to load workspace file '{entry.Path}': {ex.Message}");
            _fileContent[entry.Path] = new WorkspaceFileContent(
                ex.Message,
                Encoding: null,
                MimeType: "text/plain",
                IsError: true);
            RenderSelectedFile();
        }
    }

    private async Task LoadLegacyAgentFileAsync(IOperatorGatewayClient client, WorkspaceEntry entry)
    {
        try
        {
            var data = await client.SendWizardRequestAsync(
                "agents.files.get",
                new { agentId = AgentId, name = entry.Path },
                timeoutMs: 15000);
            UpdateAgentFileContent(data);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[WorkspacePage] Failed to load managed agent file '{entry.Path}': {ex.Message}");
            _fileContent[entry.Path] = new WorkspaceFileContent(
                ex.Message,
                Encoding: null,
                MimeType: "text/plain",
                IsError: true);
            RenderSelectedFile();
        }
    }

    public void UpdateAgentFilesList(JsonElement data)
    {
        LoadingRing.IsActive = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ClearFiles();

        if (data.TryGetProperty("workspace", out var workspaceEl))
        {
            var workspace = workspaceEl.GetString();
            if (!string.IsNullOrEmpty(workspace))
                WorkspacePathText.Text = workspace;
        }
        else
        {
            var path = data.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
            _currentPath = NormalizeWorkspacePath(path);
            WorkspacePathText.Text = string.IsNullOrEmpty(_currentPath)
                ? "Workspace root"
                : $"Workspace / {_currentPath}";
        }

        var hasEntries = false;
        if (data.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            hasEntries = true;
            if (data.TryGetProperty("parentPath", out var parentEl) && parentEl.ValueKind == JsonValueKind.String)
            {
                AddFileItem(new WorkspaceEntry(
                    NormalizeWorkspacePath(parentEl.GetString()),
                    "..",
                    "parent",
                    Size: 0));
            }

            foreach (var entryEl in entriesEl.EnumerateArray())
            {
                var name = entryEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var entryPath = entryEl.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? name : name;
                var kind = entryEl.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() ?? "file" : "file";
                long size = entryEl.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number ? sizeEl.GetInt64() : 0;

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(entryPath))
                    AddFileItem(new WorkspaceEntry(NormalizeWorkspacePath(entryPath), name, kind, size));
            }
        }

        if (!hasEntries && data.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileEl in filesEl.EnumerateArray())
            {
                var name = fileEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                long size = fileEl.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number ? sizeEl.GetInt64() : 0;
                bool exists = !fileEl.TryGetProperty("exists", out var existsEl) || existsEl.ValueKind != JsonValueKind.False;

                if (!string.IsNullOrEmpty(name) && exists)
                    AddFileItem(new WorkspaceEntry(name, name, "file", size));
            }
        }

        if (_fileItems.Count == 0)
        {
            FallbackInfoBar.IsOpen = true;
            FallbackInfoBar.Message = LocalizationHelper.GetString("WorkspacePage_NoFilesMessage");
            BodyGrid.Visibility = Visibility.Collapsed;
        }
        else
        {
            BodyGrid.Visibility = Visibility.Visible;
            SelectFirstFile();
        }
    }

    public void UpdateAgentFileContent(JsonElement data)
    {
        if (!data.TryGetProperty("file", out var fileEl)) return;

        var name = fileEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
        var rawPath = fileEl.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? name : name;
        var path = NormalizeWorkspacePath(rawPath);
        if (!_fileItems.ContainsKey(path) && _fileItems.ContainsKey(name))
        {
            path = name;
        }
        var content = fileEl.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
        bool missing = fileEl.TryGetProperty("missing", out var missingEl) && missingEl.ValueKind == JsonValueKind.True;
        var encoding = fileEl.TryGetProperty("encoding", out var encodingEl) ? encodingEl.GetString() : null;
        var mimeType = fileEl.TryGetProperty("mimeType", out var mimeEl) ? mimeEl.GetString() : null;

        if (string.IsNullOrEmpty(path) || !_fileItems.ContainsKey(path)) return;

        _fileContent[path] = missing
            ? null
            : new WorkspaceFileContent(content, encoding, mimeType, IsError: false);

        if (FileList.SelectedItem is ListViewItem selected &&
            selected.Tag is WorkspaceEntry selectedEntry &&
            string.Equals(selectedEntry.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            RenderSelectedFile();
        }
    }

    private void AddFileItem(WorkspaceEntry entry)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = entry.Kind == "directory" ? $"[Folder] {entry.Name}" : entry.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        if (entry.Kind != "file" || entry.Size > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.Kind == "file" ? FormatSize(entry.Size) : "Folder",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        var item = new ListViewItem { Content = stack, Tag = entry };
        _fileItems[entry.Path] = item;
        FileList.Items.Add(item);
    }

    private void ClearFiles()
    {
        FileList.Items.Clear();
        _fileItems.Clear();
        _fileContent.Clear();
        FileBodyPresenter.Content = null;
        SelectedFileText.Text = string.Empty;
        BodyGrid.Visibility = Visibility.Collapsed;
        ViewModeSelector.Visibility = Visibility.Collapsed;
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not ListViewItem selected ||
            selected.Tag is not WorkspaceEntry entry)
        {
            return;
        }

        if (entry.Kind == "directory" || entry.Kind == "parent")
        {
            _ = LoadWorkspaceListAsync(entry.Path);
            return;
        }

        bool isMarkdown = IsMarkdown(entry.Path);
        ViewModeSelector.Visibility = isMarkdown ? Visibility.Visible : Visibility.Collapsed;
        SelectedFileText.Text = entry.Path;

        if (_fileContent.ContainsKey(entry.Path))
        {
            RenderSelectedFile();
        }
        else
        {
            ShowLoadingBody();
            _ = LoadWorkspaceFileAsync(entry);
        }
    }

    private void RequestSelectedFileIfNeeded()
    {
        if (FileList.SelectedItem is not ListViewItem selected ||
            selected.Tag is not WorkspaceEntry entry ||
            entry.Kind != "file" ||
            _fileContent.ContainsKey(entry.Path))
        {
            return;
        }

        ShowLoadingBody();
        _ = LoadWorkspaceFileAsync(entry);
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

    private void RenderSelectedFile()
    {
        if (FileList.SelectedItem is not ListViewItem selected ||
            selected.Tag is not WorkspaceEntry entry)
        {
            FileBodyPresenter.Content = null;
            return;
        }

        if (!_fileContent.TryGetValue(entry.Path, out var preview))
        {
            ShowLoadingBody();
            return;
        }

        if (preview == null)
        {
            FileBodyPresenter.Content = new TextBlock
            {
                Text = LocalizationHelper.GetString("WorkspacePage_MissingFile"),
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            return;
        }

        if (preview.IsError)
        {
            FileBodyPresenter.Content = new TextBlock
            {
                Text = preview.Content,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            return;
        }

        if (string.Equals(preview.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            FileBodyPresenter.Content = new TextBlock
            {
                Text = "Binary preview is not supported in this view.",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            return;
        }

        if (IsMarkdown(entry.Path) && _renderMarkdown)
        {
            FileBodyPresenter.Content = BuildMarkdownView(preview.Content);
        }
        else
        {
            FileBodyPresenter.Content = BuildRawView(preview.Content);
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
        if (CurrentApp.GatewayClient != null)
        {
            _ = LoadWorkspaceListAsync(_currentPath);
        }
    }

    private void SelectFirstFile()
    {
        foreach (var item in FileList.Items)
        {
            if (item is ListViewItem listItem &&
                listItem.Tag is WorkspaceEntry { Kind: "file" })
            {
                FileList.SelectedItem = listItem;
                return;
            }
        }

        FileList.SelectedIndex = -1;
        FileBodyPresenter.Content = new TextBlock
        {
            Text = "Select a file to preview.",
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
    }

    private void ShowInfoMessage(string message)
    {
        LoadingRing.IsActive = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        FallbackInfoBar.IsOpen = true;
        FallbackInfoBar.Message = message;
    }

    private static bool IsUnknownWorkspaceMethod(Exception ex)
    {
        return ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("agents.workspace.", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWorkspacePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('\\', '/').Trim('/');
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private sealed record WorkspaceEntry(string Path, string Name, string Kind, long Size);

    private sealed record WorkspaceFileContent(
        string Content,
        string? Encoding,
        string? MimeType,
        bool IsError);
}
