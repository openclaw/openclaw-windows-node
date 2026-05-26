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

namespace OpenClawTray.Pages;

public sealed partial class WorkspacePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;

    // file name (case-insensitive) → its list item
    private readonly Dictionary<string, ListViewItem> _fileItems = new(StringComparer.OrdinalIgnoreCase);

    // file name → raw text content (null = missing on disk, absent = not loaded yet)
    private readonly Dictionary<string, string?> _fileContent = new(StringComparer.OrdinalIgnoreCase);

    private bool _renderMarkdown = true;

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
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;

        if (_appState.TryGetCachedAgentFilesList(AgentId, out var cachedData))
        {
            UpdateAgentFilesList(cachedData);
            return;
        }

        var hasMatchingCache = _appState?.AgentFilesList.HasValue == true &&
            string.Equals(_appState?.AgentFilesListAgentId, AgentId, StringComparison.OrdinalIgnoreCase);
        var status = CurrentApp.AppState?.Status ?? ConnectionStatus.Disconnected;
        if (CurrentApp.GatewayClient != null && status == ConnectionStatus.Connected && !hasMatchingCache)
        {
            FallbackInfoBar.IsOpen = false;
            LoadingRing.IsActive = true;
            LoadingPanel.Visibility = Visibility.Visible;
            ClearFiles();
            _ = CurrentApp.GatewayClient.RequestAgentFilesListAsync(AgentId);
        }
        else if (hasMatchingCache)
        {
            UpdateAgentFilesList(_appState!.AgentFilesList!.Value);
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
                if (_appState!.AgentFilesList.HasValue) UpdateAgentFilesList(_appState.AgentFilesList.Value);
                break;
            case nameof(AppState.AgentFileContent):
                if (_appState!.AgentFileContent.HasValue) UpdateAgentFileContent(_appState.AgentFileContent.Value);
                break;
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

        if (data.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileEl in filesEl.EnumerateArray())
            {
                var name = fileEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                long size = fileEl.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number ? sizeEl.GetInt64() : 0;
                bool exists = !fileEl.TryGetProperty("exists", out var existsEl) || existsEl.ValueKind != JsonValueKind.False;

                if (!string.IsNullOrEmpty(name) && exists)
                    AddFileItem(name, size);
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
            FileList.SelectedIndex = 0;
            RequestSelectedFileIfNeeded();
        }
    }

    public void UpdateAgentFileContent(JsonElement data)
    {
        if (!data.TryGetProperty("file", out var fileEl)) return;

        var name = fileEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
        var content = fileEl.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
        bool missing = fileEl.TryGetProperty("missing", out var missingEl) && missingEl.ValueKind == JsonValueKind.True;

        if (string.IsNullOrEmpty(name) || !_fileItems.ContainsKey(name)) return;

        _fileContent[name] = missing ? null : content;

        if (FileList.SelectedItem is ListViewItem selected &&
            selected.Tag is string selectedName &&
            string.Equals(selectedName, name, StringComparison.OrdinalIgnoreCase))
        {
            RenderSelectedFile();
        }
    }

    private void AddFileItem(string fileName, long size)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = fileName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        if (size > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = FormatSize(size),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }

        var item = new ListViewItem { Content = stack, Tag = fileName };
        _fileItems[fileName] = item;
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
            selected.Tag is not string fileName)
        {
            return;
        }

        bool isMarkdown = IsMarkdown(fileName);
        ViewModeSelector.Visibility = isMarkdown ? Visibility.Visible : Visibility.Collapsed;
        SelectedFileText.Text = fileName;

        if (_fileContent.ContainsKey(fileName))
        {
            RenderSelectedFile();
        }
        else
        {
            ShowLoadingBody();
            if (CurrentApp.GatewayClient != null)
                _ = CurrentApp.GatewayClient.RequestAgentFileGetAsync(AgentId, fileName);
        }
    }

    private void RequestSelectedFileIfNeeded()
    {
        if (FileList.SelectedItem is not ListViewItem selected ||
            selected.Tag is not string fileName ||
            _fileContent.ContainsKey(fileName))
        {
            return;
        }

        ShowLoadingBody();
        if (CurrentApp.GatewayClient != null)
            _ = CurrentApp.GatewayClient.RequestAgentFileGetAsync(AgentId, fileName);
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
            selected.Tag is not string fileName)
        {
            FileBodyPresenter.Content = null;
            return;
        }

        if (!_fileContent.TryGetValue(fileName, out var content))
        {
            ShowLoadingBody();
            return;
        }

        if (content == null)
        {
            FileBodyPresenter.Content = new TextBlock
            {
                Text = LocalizationHelper.GetString("WorkspacePage_MissingFile"),
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            return;
        }

        if (IsMarkdown(fileName) && _renderMarkdown)
        {
            FileBodyPresenter.Content = BuildMarkdownView(content);
        }
        else
        {
            FileBodyPresenter.Content = BuildRawView(content);
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
            LoadingRing.IsActive = true;
            LoadingPanel.Visibility = Visibility.Visible;
            FallbackInfoBar.IsOpen = false;
            ClearFiles();
            _ = CurrentApp.GatewayClient.RequestAgentFilesListAsync(AgentId);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
