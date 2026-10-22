using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Adapters;
using static Microsoft.UI.Reactor.Factories;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ConversationPanelHostProofTests(UIThreadFixture ui)
{
    private static readonly TimeSpan NativeReadinessTimeout = TimeSpan.FromSeconds(10);
    private Window? _proofWindow;
    private uint? _inputTick;
    private nint _activationForeground;
    private nint _beforeCloseForeground;
    private nint _ownWindowHandle;
    private string? _caseName;

    private static bool OnScreen => Environment.GetEnvironmentVariable("OPENCLAW_UITEST_ONSCREEN") == "1";

    [Theory]
    [InlineData(960)]
    [InlineData(480)]
    public async Task PanelOpenClosePreservesProductionTimelineComposerSelectionAndScroll(int width)
    {
        await WithRootAsync(width, async (surface, panels, _, _, _) =>
        {
            TextBox? input = null;
            ItemsView? timeline = null;
            ScrollView? scroll = null;
            ReaderAnchor? anchor = null;
            await ui.RunOnUIAsync(() =>
            {
                input = Find<TextBox>(surface, "ChatComposerInput");
                timeline = Assert.Single(FindDescendants<ItemsView>(surface));
                scroll = timeline.ScrollView;
                Assert.True(scroll.ScrollableHeight > 160);
            });
            var readerScroll = scroll ?? throw new InvalidOperationException("The production timeline has no ScrollView.");
            var editor = input ?? throw new InvalidOperationException("The production composer has no input.");
            await WaitForInitialTailAsync(surface, readerScroll, finalMessage: "Panel message 50");
            await ui.RunOnUIAsync(() =>
            {
                editor.Select(5, 8);
                editor.Focus(FocusState.Programmatic);
            });
            await SettleAsync();
            await ScrollReaderAsync(surface, readerScroll, 120);
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.InRange(readerScroll.VerticalOffset, 1, readerScroll.ScrollableHeight - 60);
                anchor = CaptureReaderAnchor(surface, readerScroll);
                Console.WriteLine($"Reader anchor before panel: message={anchor.Message}, top={anchor.Top}, offset={readerScroll.VerticalOffset}");
                Assert.True(panels.Open("details"));
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(input, Find<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(timeline, Assert.Single(FindDescendants<ItemsView>(surface)));
                Assert.Same(scroll, timeline!.ScrollView);
                Assert.Equal("Keep this conversation draft.", input!.Text);
                Assert.Equal(5, input.SelectionStart);
                Assert.Equal(8, input.SelectionLength);
                var panel = Find<Border>(surface, "ConversationPanel");
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.InRange(panel.ActualWidth, 280, width);
                var close = Find<Button>(surface, "ConversationPanelClose");
                Assert.Equal("Close panel", AutomationProperties.GetName(close));
                if (width == 960) AssertReaderAnchor(surface, readerScroll, anchor!);
            });
            await CaptureAsync(surface, $"Panel-{width}-open");
            await ui.RunOnUIAsync(() => Invoke(Find<Button>(surface, "ConversationPanelClose")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Null(panels.Layout.SelectedPanel);
                Assert.Same(input, Find<TextBox>(surface, "ChatComposerInput"));
                Assert.Same(timeline, Assert.Single(FindDescendants<ItemsView>(surface)));
                AssertReaderAnchor(surface, readerScroll, anchor!);
                Assert.Equal("Keep this conversation draft.", input!.Text);
                Assert.Equal(5, input.SelectionStart);
                Assert.Equal(8, input.SelectionLength);
                Assert.Same(input, FocusManager.GetFocusedElement(surface.XamlRoot));
            });
            await CaptureAsync(surface, $"Panel-{width}-closed");
        });
    }

    [Fact]
    public async Task FreshLongHistoryAndSessionSwitchRevealTheActualFinalMessage()
    {
        await WithRootAsync(960, async (surface, panels, _, _, composer) =>
        {
            var scroll = await ui.RunOnUIAsync(() =>
                Task.FromResult(Assert.Single(FindDescendants<ItemsView>(surface)).ScrollView));
            await WaitForInitialTailAsync(surface, scroll, finalMessage: "Panel message 50");
            await CaptureAsync(surface, "Initial-tail-one");
            await ui.RunOnUIAsync(() => Assert.True(composer.Controller.TrySelectSession("two")));
            await SettleAsync();
            var nextScroll = await ui.RunOnUIAsync(() =>
                Task.FromResult(Assert.Single(FindDescendants<ItemsView>(surface)).ScrollView));
            await WaitForInitialTailAsync(surface, nextScroll, finalMessage: "Second message 50");
            await ui.RunOnUIAsync(() => Assert.Equal("two", panels.Context?.Identity.SessionKey));
            await CaptureAsync(surface, "Initial-tail-two");
        });
    }

    [Fact]
    public async Task InitialTailRecapturesSameKeyIndexChangedBeforeDispatch()
    {
        await ui.ResetContainerAsync();
        _caseName = nameof(InitialTailRecapturesSameKeyIndexChangedBeforeDispatch);
        InitialTailPositioner? positioner = null;
        Window? window = null;
        var proofCompleted = false;
        try
        {
            Border? surface = null;
            ItemsView? timeline = null;
            var rows = new System.Collections.ObjectModel.ObservableCollection<string>(
                Enumerable.Range(1, 24).Select(index => $"Original row {index}").Append("Tail item"));
            await ui.RunOnUIAsync(() =>
            {
                timeline = new ItemsView
                {
                    ItemsSource = rows,
                    SelectionMode = ItemsViewSelectionMode.None,
                    ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                        """<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemContainer><TextBlock Text="{Binding}" Height="64" /></ItemContainer></DataTemplate>""")
                };
                surface = new Border { Width = 480, Height = 600, Child = timeline };
                window = new Window { Content = surface };
                _proofWindow = window;
                PlaceOwnedWindow(window, 800, 900);
                ActivateOwnedWindow(window);
                RecordOwnedWindow(window);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => timeline!.IsLoaded && timeline.ScrollView is { IsLoaded: true, ViewportHeight: > 0 },
                "loaded ItemsView before initial-tail dispatch race"));
            var scroll = await ui.RunOnUIAsync(() =>
            {
                var scale = surface!.XamlRoot.RasterizationScale;
                window!.AppWindow.ResizeClient(new(
                    (int)Math.Ceiling(surface.Width * scale), (int)Math.Ceiling(surface.Height * scale)));
                RecordOwnedWindow(window);
                positioner = new(timeline!);
                Assert.False(IsMessageVisible(surface, timeline!.ScrollView, "Tail item"));
                Console.WriteLine($"Initial-tail race before dispatch: index=24,count={rows.Count},key=same-tail");
                positioner.Request(24, 25, "same-request", "same-tail");
                for (var index = 0; index < 25; index++) rows.Insert(rows.Count - 1, $"Inserted row {index}");
                positioner.UpdateTail(49, 50, "same-tail");
                Console.WriteLine($"Initial-tail race still before dispatch: index=49,count={rows.Count},key=same-tail");
                return Task.FromResult(timeline!.ScrollView);
            });
            await WaitForStablePositionAsync(surface!, scroll,
                () => scroll.ScrollableHeight - scroll.VerticalOffset <= 60 &&
                    IsMessageVisible(surface!, scroll, "Tail item"),
                "current same-key tail after index/count changed before queued dispatch");
            proofCompleted = true;
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                positioner?.Dispose();
                CloseOwnedWindow(window);
                _proofWindow = null;
            });
            VerifyNoInputCollision(proofCompleted);
        }
    }

    [Fact]
    public async Task SuspendedHostReplacesPanelBodyWithWaitingWithoutDiscardingSelection()
    {
        await WithRootAsync(960, async (surface, panels, _, _, _) =>
        {
            await ui.RunOnUIAsync(() => Assert.True(panels.Open("details")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Contains(FindDescendants<TextBlock>(surface),
                    text => text.Text == "These details are no longer current. Reopen details from the current tool.");
                panels.Suspend();
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("details", panels.Layout.SelectedPanel);
                Assert.Contains(FindDescendants<TextBlock>(surface),
                    text => text.Text == "Waiting for this conversation to connect. Your panel selection is retained.");
                Assert.DoesNotContain(FindDescendants<TextBlock>(surface),
                    text => text.Text == "These details are no longer current. Reopen details from the current tool.");
                Assert.Null(panels.BeginOperation("details"));
            });
        });
    }

    [Fact]
    public async Task NativeToolActionUpdatesLiveOutputAndRequiresReopenAfterHistoryCommit()
    {
        await WithRootAsync(960, async (surface, panels, provider, _, _) =>
        {
            var scroll = await ui.RunOnUIAsync(() =>
                Task.FromResult(Assert.Single(FindDescendants<ItemsView>(surface)).ScrollView));
            await WaitForInitialTailAsync(surface, scroll, requireScrollable: false);
            await ui.RunOnUIAsync(() => Find<Expander>(surface, "ChatToolCall_tool_entry").IsExpanded = true);
            await WaitForToolActionAsync(surface);
            await ui.RunOnUIAsync(() => Invoke(Find<Button>(surface, "ChatToolOpenDetails_tool_entry")));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("details", panels.Layout.SelectedPanel);
                Assert.Equal("initial output", DetailText(surface));
                provider.PublishOutput("latest <script>literal text</script>");
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("latest <script>literal text</script>", DetailText(surface));
            });
            await CaptureAsync(surface, "Tool-details-live");
            await ui.RunOnUIAsync(() => provider.PublishOutput("committed history output", historyRevision: 6));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal("details", panels.Layout.SelectedPanel);
                var panel = Find<Border>(surface, "ConversationPanel");
                Assert.Contains(FindDescendants<TextBlock>(panel), text => text.Text ==
                    "These details are no longer current. Reopen details from the current tool.");
                Assert.Equal(string.Empty, DetailText(surface));
            });
            await CaptureAsync(surface, "Tool-details-history-unavailable");
            await ui.RunOnUIAsync(() => Invoke(Find<Button>(surface, "ChatToolOpenDetails_tool_entry")));
            await SettleAsync();
            await ui.RunOnUIAsync(() => Assert.Equal("committed history output", DetailText(surface)));
            await CaptureAsync(surface, "Tool-details-reopened");
        }, dense: false);
    }

    private static string DetailText(DependencyObject surface) => string.Concat(
        FindDescendants<RichTextBlock>(Find<Border>(surface, "ConversationPanel"))
            .SelectMany(text => text.Blocks.OfType<Paragraph>())
            .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
            .Select(run => run.Text));

    [Fact]
    public async Task UnavailableAccountExplainsWhyPanelsCannotOpenAndKeepsComposerUsable()
    {
        await WithRootAsync(960, async (surface, panels, _, changeAccountStatus, _) =>
        {
            TextBlock? originalNotice = null;
            TextBlock? captionReference = null;
            await ui.RunOnUIAsync(() =>
            {
                var notice = Find<TextBlock>(surface, "ConversationPanelAccountStatus");
                originalNotice = notice;
                captionReference = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
                Assert.Equal(Visibility.Visible, notice.Visibility);
                Assert.Equal("This Gateway cannot verify the operator account. Conversation panels are unavailable.", notice.Text);
                Assert.Equal(notice.Text, AutomationProperties.GetName(notice));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(notice));
                Assert.Equal(12, notice.FontSize);
                Assert.Equal(captionReference.FontWeight, notice.FontWeight);
                Assert.True(Find<TextBox>(surface, "ChatComposerInput").IsEnabled);
                Assert.False(panels.Open("details"));
                Assert.Null(panels.Context);
                Assert.Empty(panels.Layout.OpenPanels);
            });
            await CaptureAsync(surface, "Account-unavailable");
            await ui.RunOnUIAsync(() => changeAccountStatus(ChatConversationAccountStatus.Loading));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var notice = Find<TextBlock>(surface, "ConversationPanelAccountStatus");
                Assert.Same(originalNotice, notice);
                Assert.Equal("Verifying the operator account for this conversation.", notice.Text);
                Assert.Equal(notice.Text, AutomationProperties.GetName(notice));
                Assert.Equal(12, notice.FontSize);
                Assert.Equal(captionReference!.FontWeight, notice.FontWeight);
            });
            await CaptureAsync(surface, "Account-loading");
            await ui.RunOnUIAsync(() => changeAccountStatus(ChatConversationAccountStatus.RequiresReconnect));
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var notice = Find<TextBlock>(surface, "ConversationPanelAccountStatus");
                Assert.Same(originalNotice, notice);
                Assert.Equal("The operator account changed. Reconnect before opening conversation panels.", notice.Text);
                Assert.Equal(notice.Text, AutomationProperties.GetName(notice));
                Assert.Equal(12, notice.FontSize);
                Assert.Equal(captionReference!.FontWeight, notice.FontWeight);
            });
            await CaptureAsync(surface, "Account-reconnect-required");
        }, unverifiedAccount: true);
    }

    private async Task WithRootAsync(
        int width, Func<Border, ChatConversationPanelState, PanelProvider, Action<ChatConversationAccountStatus>, ChatComposerSession, Task> proof, bool dense = true,
        bool unverifiedAccount = false, [CallerMemberName] string? caseName = null)
    {
        await ui.ResetContainerAsync();
        _caseName = $"{caseName}:{width}";
        var provider = new PanelProvider(dense);
        var client = DispatchProxy.Create<IOperatorGatewayClient, ReadyClient>();
        var accountStatus = unverifiedAccount ? ChatConversationAccountStatus.Unavailable : ChatConversationAccountStatus.Known;
        using var panels = new ChatConversationPanelState(key => accountStatus != ChatConversationAccountStatus.Known ? null : ChatConversationContext.Capture(
            "gateway-proof", client, new() { Key = key, AgentId = "writing", SessionId = "session-" + key }, "profile-proof"),
            accountStatus: () => accountStatus);
        Border? surface = null;
        Window? window = null;
        ReactorHostControl? host = null;
        ChatComposerSession? composer = null;
        ChatThemeProofScope? theme = null;
        var proofCompleted = false;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                LocalizationHelper.SetLanguageOverride("en-us");
                theme = new("Light");
                OpenClawReactorChatRoot.SetToolCallsVisible(true);
                composer = new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher)).Create(
                    provider, new ChatComposerHostActions(null, null, null, null), initialSpeakerMuted: true);
                composer.ViewModel.SetDraft("Keep this conversation draft.");
                host = new ReactorHostControl { RequestedTheme = theme.ElementTheme };
                host.Mount(_ => Component<OpenClawReactorChatRoot, OpenClawReactorChatRootProps>(
                    new(provider, composer, InitialThreadId: "one", Panels: panels)));
                surface = new Border { Width = width, Height = 720, Child = host, RequestedTheme = theme.ElementTheme };
                window = new Window { Content = surface };
                _proofWindow = window;
                PlaceOwnedWindow(window, 1200, 900);
                ActivateOwnedWindow(window);
                RecordOwnedWindow(window);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() =>
            {
                var scale = surface!.XamlRoot.RasterizationScale;
                window!.AppWindow.ResizeClient(new(
                    (int)Math.Ceiling(surface.Width * scale), (int)Math.Ceiling(surface.Height * scale)));
                surface.UpdateLayout();
                RecordOwnedWindow(window);
            });
            await SettleAsync();
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(
                () => FindDescendants<TextBox>(surface!).Any(control =>
                    AutomationProperties.GetAutomationId(control) == "ChatComposerInput") &&
                    (unverifiedAccount
                        ? FindDescendants<TextBlock>(surface!).Any(text => text.Text ==
                            "This Gateway cannot verify the operator account. Conversation panels are unavailable.")
                        : panels.IsReady),
                "production conversation panel host"));
            await proof(surface!, panels, provider, status =>
            {
                accountStatus = status;
                panels.Refresh();
            }, composer!);
            proofCompleted = true;
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                composer?.Dispose();
                if (surface is not null) surface.Child = null;
                host?.Dispose();
                CloseOwnedWindow(window);
                _proofWindow = null;
                theme?.Dispose();
            });
            await provider.DisposeAsync();
            VerifyNoInputCollision(proofCompleted);
        }
    }

    private void PlaceOwnedWindow(Window window, int width, int height)
    {
        _inputTick = ReadInputTick();
        if (OnScreen)
        {
            var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            Assert.True(width + 32 <= area.Width && height + 64 <= area.Height,
                "The owned proof window must fit the primary monitor work area without changing scale.");
            window.AppWindow.MoveAndResize(new(area.X + 16, area.Y + 16, width, height));
        }
        else
        {
            window.AppWindow.MoveAndResize(new(-32000, -32000, width, height));
        }
    }

    private void ActivateOwnedWindow(Window window)
    {
        window.Activate();
        _activationForeground = GetForegroundWindow();
        var own = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _ownWindowHandle = own;
        Console.WriteLine($"Owned native activation: case={_caseName},own={own},foreground={_activationForeground},matched={own == _activationForeground}");
    }

    private void CloseOwnedWindow(Window? window)
    {
        if (window is null) return;
        _beforeCloseForeground = GetForegroundWindow();
        window.Close();
    }

    private void VerifyNoInputCollision(bool proofCompleted)
    {
        if (_inputTick is not { } before) return;
        _inputTick = null;
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var read = GetLastInputInfo(ref input);
        var error = read ? 0 : Marshal.GetLastWin32Error();
        var audit = new
        {
            Case = _caseName, Before = before, After = input.Tick,
            OnScreen, BodyPassed = proofCompleted, ReadSucceeded = read, Win32Error = error,
            Collision = read && before != input.Tick,
            OwnWindow = _ownWindowHandle.ToInt64(),
            ActivationForeground = _activationForeground.ToInt64(),
            BeforeCloseForeground = _beforeCloseForeground.ToInt64(),
            ActivationMatchedOwnWindow = _activationForeground == _ownWindowHandle,
            BeforeCloseMatchedOwnWindow = _beforeCloseForeground == _ownWindowHandle
        };
        var json = System.Text.Json.JsonSerializer.Serialize(audit);
        Console.WriteLine($"Owned native input audit: {json}");
        if (Environment.GetEnvironmentVariable("OPENCLAW_CONVERSATION_PANEL_PROOF_DIR") is { Length: > 0 } directory)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, $"input-audit-{Guid.NewGuid():N}.json"), json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Additional native input audit write failure: {ex.GetType().Name}.");
                if (proofCompleted) throw;
            }
        }
        if (!proofCompleted) return;
        Assert.True(read, $"Could not complete the physical-input collision audit (Win32 error {error}).");
        Assert.Equal(before, input.Tick);
    }

    private static uint ReadInputTick()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return input.Tick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size;
        internal uint Tick;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private void RecordOwnedWindow(Window window)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_CONVERSATION_PANEL_PROOF_DIR") is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        if (OnScreen)
        {
            Assert.True(position.X >= area.X && position.Y >= area.Y &&
                position.X + size.Width <= area.X + area.Width &&
                position.Y + size.Height <= area.Y + area.Height,
                "The complete owned window must remain inside the primary monitor work area.");
        }
        var metadata = new
        {
            RunId = Path.GetFileName(directory),
            Case = _caseName,
            ProcessId = process.Id,
            StartedUtc = process.StartTime.ToUniversalTime(),
            Executable = Environment.ProcessPath,
            CommandLine = Environment.CommandLine,
            Window = handle.ToInt64(),
            OnScreen,
            Position = new { position.X, position.Y },
            OuterSize = new { size.Width, size.Height },
            Monitor = "Primary",
            MonitorWorkArea = new { area.X, area.Y, area.Width, area.Height },
            WindowDpi = GetDpiForWindow(handle),
            ForegroundWindow = GetForegroundWindow().ToInt64(),
            ActivationForegroundWindow = _activationForeground.ToInt64(),
            ActivationMatchedOwnWindow = _activationForeground == handle,
            ClientWidth = window.AppWindow.ClientSize.Width,
            ClientHeight = window.AppWindow.ClientSize.Height,
            Port = (int?)null,
            Data = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR"),
            AppData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR"),
            LocalAppData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"),
            LegacyLocal = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR"),
            SyntheticProvider = true,
            RealGateway = false,
            Binaries = new[]
            {
                typeof(ConversationPanelHostProofTests).Assembly.Location,
                typeof(OpenClawReactorChatRoot).Assembly.Location,
                typeof(ReactorHostControl).Assembly.Location
            }.Select(path => new
            {
                Path = path,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(directory, $"owned-window-{handle.ToInt64()}.json"),
            System.Text.Json.JsonSerializer.Serialize(metadata));
    }

    private async Task CaptureAsync(FrameworkElement surface, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1") return;
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR")
            ?? throw new InvalidOperationException("A proof capture directory is required.");
        await ui.RunOnUIAsync(async () =>
        {
            await VisualTestCapture.CaptureAsync(surface, name);
            var path = Path.Combine(directory, name, "capture-00.png");
            Assert.True(File.Exists(path), $"Missing current-state capture: {path}");
            using var bitmap = new System.Drawing.Bitmap(path);
            var window = _proofWindow ?? throw new InvalidOperationException("No owned capture window.");
            Assert.Same(surface, window.Content);
            var scale = surface.XamlRoot.RasterizationScale;
            var expectedWidth = (int)Math.Ceiling(surface.ActualWidth * scale);
            var expectedHeight = (int)Math.Ceiling(surface.ActualHeight * scale);
            Assert.True(window.AppWindow.ClientSize.Width >= expectedWidth &&
                window.AppWindow.ClientSize.Height >= expectedHeight,
                "The owned physical client viewport must not clip the asserted component root.");
            Assert.InRange(bitmap.Width, expectedWidth - 1, expectedWidth + 1);
            Assert.InRange(bitmap.Height, expectedHeight - 1, expectedHeight + 1);
            var colors = new HashSet<int>();
            for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 64))
                for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 64))
                    colors.Add(bitmap.GetPixel(x, y).ToArgb());
            Assert.True(colors.Count > 8, "Blank or unrendered proof frames are not evidence.");
            File.WriteAllText(Path.Combine(directory, name, "capture.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
                    bitmap.Width, bitmap.Height, RootWidth = surface.ActualWidth, RootHeight = surface.ActualHeight,
                    ColorSamples = colors.Count, Theme = surface.ActualTheme.ToString(),
                    SyntheticProvider = true, CompositorCapture = false
                }));
        });
    }

    private Task WaitForInitialTailAsync(Border surface, ScrollView scroll, bool requireScrollable = true, string? finalMessage = null) =>
        WaitForStablePositionAsync(surface, scroll,
            () => (!requireScrollable || scroll.ScrollableHeight > 160) &&
                (finalMessage is null
                    ? Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1
                    : scroll.ScrollableHeight - scroll.VerticalOffset <= 60 &&
                        IsMessageVisible(surface, scroll, finalMessage)),
            $"initial tail (requiresScrollableOver160={requireScrollable})");

    private sealed record ReaderAnchor(string Message, double Top);

    private static bool IsMessageVisible(Border surface, ScrollView scroll, string message)
    {
        var matches = FindDescendants<TextBlock>(surface)
            .Where(text => text.IsLoaded && text.Text == message).ToArray();
        Assert.True(matches.Length <= 1, $"The realized final message must be unique: {message}.");
        return matches.Length == 1 && IsVisibleInScroll(matches[0], scroll);
    }

    private static global::Windows.Foundation.Rect BoundsInScroll(FrameworkElement element, ScrollView scroll) =>
        element.TransformToVisual(scroll).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));

    private static bool IsVisibleInScroll(FrameworkElement element, ScrollView scroll)
    {
        if (!element.IsLoaded || element.Visibility != Visibility.Visible || element.Opacity <= 0 ||
            element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;
        var bounds = BoundsInScroll(element, scroll);
        return bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight &&
            bounds.Left >= 0 && bounds.Right <= scroll.ViewportWidth;
    }

    private static ReaderAnchor CaptureReaderAnchor(Border surface, ScrollView scroll) =>
        FindDescendants<TextBlock>(surface)
            .Where(text => text.Text.StartsWith("Panel message ", StringComparison.Ordinal) && IsVisibleInScroll(text, scroll))
            .Select(text => new ReaderAnchor(text.Text, BoundsInScroll(text, scroll).Top))
            .OrderBy(anchor => anchor.Top).First();

    private static void AssertReaderAnchor(Border surface, ScrollView scroll, ReaderAnchor anchor)
    {
        var text = Assert.Single(FindDescendants<TextBlock>(surface),
            candidate => candidate.Text == anchor.Message && IsVisibleInScroll(candidate, scroll));
        var tolerance = 1 / surface.XamlRoot.RasterizationScale;
        var top = BoundsInScroll(text, scroll).Top;
        Console.WriteLine($"Reader anchor after panel: message={anchor.Message}, expectedTop={anchor.Top}, actualTop={top}, offset={scroll.VerticalOffset}");
        Assert.InRange(top, anchor.Top - tolerance, anchor.Top + tolerance);
    }

    private Task WaitForStablePositionAsync(
        Border surface, ScrollView scroll, Func<bool> positionReady, string description) => ui.RunOnUIAsync(async () =>
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stablePasses = 0;
        var previousExtent = double.NaN;
        var previousViewport = double.NaN;
        void CheckLayout(object? sender, object args)
        {
            var atPosition = scroll.IsLoaded && scroll.ViewportHeight > 0 && scroll.ExtentHeight > 0 && positionReady();
            stablePasses = atPosition && Math.Abs(previousExtent - scroll.ExtentHeight) < 0.5 &&
                Math.Abs(previousViewport - scroll.ViewportHeight) < 0.5 ? stablePasses + 1 : 0;
            previousExtent = scroll.ExtentHeight;
            previousViewport = scroll.ViewportHeight;
            if (stablePasses >= 2) settled.TrySetResult();
        }
        surface.LayoutUpdated += CheckLayout;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += CheckLayout;
        CheckLayout(null, EventArgs.Empty);
        try
        {
            await settled.Task.WaitAsync(NativeReadinessTimeout);
            Assert.True(stablePasses >= 2);
            Assert.True(positionReady());
        }
        catch (TimeoutException)
        {
            var messages = FindDescendants<TextBlock>(surface)
                .Where(text => text.Text.StartsWith("Panel message ", StringComparison.Ordinal) ||
                    text.Text.StartsWith("Second message ", StringComparison.Ordinal) ||
                    text.Text is "Tail item" ||
                    text.Text.StartsWith("Original row ", StringComparison.Ordinal) ||
                    text.Text.StartsWith("Inserted row ", StringComparison.Ordinal))
                .Select(text => new
                {
                    text.Text, text.IsLoaded, text.Visibility, text.Opacity,
                    text.ActualWidth, text.ActualHeight, Bounds = BoundsInScroll(text, scroll),
                    Visible = IsVisibleInScroll(text, scroll)
                }).ToArray();
            Console.WriteLine("Native final-message diagnostic: " +
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Description = description, scroll.VerticalOffset, scroll.ScrollableHeight,
                    scroll.ExtentHeight, scroll.ViewportHeight, scroll.ViewportWidth, Messages = messages
                }));
            Assert.Fail($"{description} never settled for two native layout/render observations: offset={scroll.VerticalOffset}, " +
                $"maximum={scroll.ScrollableHeight}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}.");
        }
        finally
        {
            surface.LayoutUpdated -= CheckLayout;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CheckLayout;
        }
    });

    private async Task ScrollReaderAsync(Border surface, ScrollView scroll, double offset)
    {
        var correlation = await ScrollOnceAsync(scroll, offset);
        await WaitForStablePositionAsync(surface, scroll, () => true, "reader extent after correlated scroll");
        await ui.RunOnUIAsync(() =>
        {
            Console.WriteLine($"correlation={correlation},requested={offset},actual={scroll.VerticalOffset}," +
                $"extent={scroll.ExtentHeight},viewport={scroll.ViewportHeight}");
            Assert.InRange(scroll.VerticalOffset, 1, scroll.ScrollableHeight - 60);
        });
    }

    private Task<int> ScrollOnceAsync(ScrollView scroll, double offset) => ui.RunOnUIAsync(async () =>
    {
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = -2;
        var observed = new List<string>();
        Windows.Foundation.TypedEventHandler<ScrollView, ScrollingScrollCompletedEventArgs> handler = (_, args) =>
        {
            observed.Add($"{args.CorrelationId}:{scroll.VerticalOffset}");
            if (args.CorrelationId == expected) completed.TrySetResult(args.CorrelationId);
        };
        scroll.ScrollCompleted += handler;
        try
        {
            expected = scroll.ScrollTo(0, offset, new ScrollingScrollOptions(
                ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
            Assert.True(expected >= 0, "The native reader scroll was not admitted.");
            Assert.Equal(expected, await completed.Task.WaitAsync(NativeReadinessTimeout));
            return expected;
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Native reader scroll {expected} never completed: " +
                $"offset={scroll.VerticalOffset}, extent={scroll.ExtentHeight}, observed={string.Join(",", observed)}.");
            throw;
        }
        finally { scroll.ScrollCompleted -= handler; }
    });

    private Task WaitForToolActionAsync(Border surface) => ui.RunOnUIAsync(async () =>
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Button? action = null;
        void Check(object? sender, object args)
        {
            action = FindDescendants<Button>(surface).FirstOrDefault(button =>
                AutomationProperties.GetAutomationId(button) == "ChatToolOpenDetails_tool_entry");
            var scale = surface.XamlRoot.RasterizationScale;
            if (action is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible } &&
                Math.Abs(action.ActualHeight * scale - 32 * scale) <= 1 && action.ActualWidth > 0)
                ready.TrySetResult();
        }
        surface.LayoutUpdated += Check;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Check;
        try
        {
            Check(null, EventArgs.Empty);
            await ready.Task.WaitAsync(NativeReadinessTimeout);
            Assert.Equal(32, action!.MinHeight);
        }
        catch (TimeoutException)
        {
            var expander = Find<Expander>(surface, "ChatToolCall_tool_entry");
            Assert.Fail($"Native tool action never became ready: expanded={expander.IsExpanded}, " +
                $"loaded={action?.IsLoaded}, enabled={action?.IsEnabled}, height={action?.ActualHeight}, " +
                $"width={action?.ActualWidth}, scale={surface.XamlRoot.RasterizationScale}.");
        }
        finally
        {
            surface.LayoutUpdated -= Check;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Check;
        }
    });

    private async Task SettleAsync()
    {
        await ui.YieldToRenderAsync();
        await ui.YieldToRenderAsync();
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(FindDescendants<T>(root), control => AutomationProperties.GetAutomationId(control) == id);

    private static void Invoke(Button button) =>
        Assert.IsAssignableFrom<IInvokeProvider>(
            new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    public class ReadyClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsConnectedToGateway" => true,
            "get_SessionMutationConnectionEpoch" => (long?)1,
            "get_AuthenticatedSigningDeviceId" => "operator-proof",
            _ => throw new NotSupportedException(method?.Name)
        };
    }

    private sealed class PanelProvider(bool dense) : IChatDataProvider
    {
        private ChatDataSnapshot? _snapshot;
        public string DisplayName => "Conversation panel native proof";
#pragma warning disable CS0067
        public event EventHandler<ChatDataChangedEventArgs>? Changed;
        public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;
#pragma warning restore CS0067
        public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (_snapshot is not null) return Task.FromResult(_snapshot);
            var thread = new ChatThread { Id = "one", Title = "Panel proof", AgentId = "writing" };
            var timeline = ChatTimelineState.Initial() with
            {
                Entries = dense
                    ? Enumerable.Range(1, 50).Select(index => new ChatTimelineItem(
                        "entry-" + index, ChatTimelineItemKind.User, "Panel message " + index)).ToImmutableList()
                    : ImmutableList.Create(
                        new ChatTimelineItem("user", ChatTimelineItemKind.User, "Inspect this tool"),
                        new ChatTimelineItem("tool-entry", ChatTimelineItemKind.ToolCall, string.Empty,
                            ToolName: "read", ToolOutput: "initial output",
                            ToolCallId: "call-one", ToolRunId: "run-one")),
                HistoryLoaded = true
            };
            var timelines = new Dictionary<string, ChatTimelineState> { ["one"] = timeline };
            if (dense) timelines["two"] = timeline with
            {
                Entries = Enumerable.Range(1, 50).Select(index => new ChatTimelineItem(
                    "second-entry-" + index, ChatTimelineItemKind.User, "Second message " + index)).ToImmutableList()
            };
            return Task.FromResult(_snapshot = new ChatDataSnapshot(
                dense ? [thread, new ChatThread { Id = "two", Title = "Second proof", AgentId = "writing" }] : [thread],
                timelines,
                "one", "Connected", [], new("one", true, "writing"),
                TimelineGenerations: new Dictionary<string, long> { ["one"] = 3, ["two"] = 3 },
                HistoryRevisions: new Dictionary<string, long> { ["one"] = 5, ["two"] = 5 }));
        }

        internal void PublishOutput(string output, long historyRevision = 5)
        {
            var snapshot = _snapshot ?? throw new InvalidOperationException("Proof provider is not loaded.");
            var timeline = snapshot.Timelines["one"];
            _snapshot = snapshot with
            {
                Timelines = new Dictionary<string, ChatTimelineState>
                {
                    ["one"] = timeline with
                    {
                        Entries = timeline.Entries.SetItem(1, timeline.Entries[1] with { ToolOutput = output })
                    }
                },
                HistoryRevisions = new Dictionary<string, long> { ["one"] = historyRevision }
            };
            Changed?.Invoke(this, new(_snapshot));
        }

        private static Task Unexpected() => Task.FromException(new InvalidOperationException("Unexpected proof mutation."));
        public Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default) => Unexpected();
        public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default) => Unexpected();
        public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default) => Unexpected();
        public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default) => Unexpected();
        public Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default) => Unexpected();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
