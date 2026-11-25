using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using OpenClaw.Shared;
using OpenClaw.Shared.Themes;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.Theming;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class GatewayThemeSettingsTests
{
    // Voice's status badge and transcript bubble put text on the voice accent, which takes the theme's primary color;
    // fixed white text was unreadable on light primaries such as CRT's dark one.
    [Fact]
    public void VoiceAccentTextIsReadableOnEveryBuiltInThemesAccent()
    {
        foreach (var theme in BuiltinGatewayThemes.All)
        {
            foreach (var palette in new[] { theme.Light, theme.Dark }.OfType<ResolvedThemePalette>())
            {
                var projection = ThemeResourceProjection.Project(palette);
                var contrast = ThemeLegibility.Contrast(projection.Brushes["VoiceAccentForegroundBrush"], projection.Colors["SystemAccentColor"]);
                Assert.True(contrast >= 3, $"{theme.Id} {palette.Mode}: {contrast:F2}");
            }
        }
    }
    [Theory]
    [InlineData("dark", "#000000", true)]
    [InlineData("light", "#050505", false)]
    public void TransparentThemesAreJudgedOnTheirProjectedWindowCanvas(string mode, string expectedRoot, bool readable)
    {
        // #5a5a5a clears 3:1 on black but not on the light sidebar's 2% white tint over black.
        using var json = System.Text.Json.JsonDocument.Parse($$"""
            { "name": "Glass", "{{mode}}": {
              "background": "#00000000", "foreground": "#5a5a5a", "card": "#00000000", "card-foreground": "#ffffff",
              "popover": "#00000000", "popover-foreground": "#ffffff", "primary": "#5ab6d8", "primary-foreground": "#000000",
              "secondary": "#00000000", "secondary-foreground": "#ffffff", "muted": "#00000000", "muted-foreground": "#cccccc",
              "accent": "#5ab6d8", "accent-foreground": "#000000", "destructive": "#f87171", "destructive-foreground": "#000000",
              "border": "#ffffff33", "input": "#ffffff33", "ring": "#5ab6d8" } }
            """);
        Assert.True(GatewayThemeProtocol.TryParseDefinition(json.RootElement, out var definition, out var error), error);
        var colorMode = mode == "light" ? ThemeColorMode.Light : ThemeColorMode.Dark;
        var glass = ThemePaletteExpander.Resolve(
            new GatewayThemeDescriptor("user/glass", "Glass", "", GatewayThemeSource.User, [colorMode], null), definition);
        var canvas = ThemeResourceProjection.Project(glass.PaletteFor(colorMode)).Brushes["SolidBackgroundFillColorBaseBrush"];
        Assert.Equal(ThemeColor.FromHex(expectedRoot), canvas);
        Assert.Equal(readable, ThemeLegibility.IsReadable(glass));
    }

    private const string NeonDefinition = """
        { "name": "Neon", "description": "", "dark": {
          "background": "#05010a", "foreground": "#f5e9ff", "card": "#120320", "card-foreground": "#f5e9ff",
          "popover": "#120320", "popover-foreground": "#f5e9ff", "primary": "#ff2bd6", "primary-foreground": "#05010a",
          "secondary": "#1d0a33", "secondary-foreground": "#f5e9ff", "muted": "#1d0a33", "muted-foreground": "#b9a3d1",
          "accent": "#28f0ff", "accent-foreground": "#05010a", "destructive": "#ff4d6d", "destructive-foreground": "#05010a",
          "border": "#2c1446", "input": "#2c1446", "ring": "#28f0ff", "font-sans": "\"Space Grotesk\", sans-serif" } }
        """;

    public class GatewayProxy : DispatchProxy
    {
        public bool Connected = true;
        public long? Epoch = 1;
        public IReadOnlyList<string> Methods = ["themes.list", "themes.get"];
        public List<string> Calls = [];
        public Func<string, object?, JsonElement> Reply = (_, _) => throw new InvalidOperationException();
        public Func<string, object?, Task<JsonElement>>? ReplyAsync;
        public EventHandler<string>? ThemeCatalogInvalidated;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_SessionMutationConnectionEpoch" => Epoch,
            "get_AdvertisedServerMethods" => Methods,
            "get_GrantedOperatorScopes" => (IReadOnlyList<string>)["operator.read"],
            "SendWizardRequestAsync" => Send((string)args![0]!, args[1]),
            "add_ThemeCatalogInvalidated" => ThemeCatalogInvalidated += (EventHandler<string>)args![0]!,
            "remove_ThemeCatalogInvalidated" => ThemeCatalogInvalidated -= (EventHandler<string>)args![0]!,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };

        private Task<JsonElement> Send(string method, object? parameters)
        {
            Calls.Add($"{method}:{JsonSerializer.Serialize(parameters)}");
            if (ReplyAsync is not null)
                return ReplyAsync(method, parameters);
            try
            {
                return Task.FromResult(Reply(method, parameters));
            }
            catch (Exception ex)
            {
                return Task.FromException<JsonElement>(ex);
            }
        }
    }

    private static (IOperatorGatewayClient Client, GatewayProxy Proxy) Gateway()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)client);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string ListReply => """
        { "themes": [
          { "id": "claw", "name": "Claw", "description": "", "source": "builtin", "modes": ["light", "dark"] },
          { "id": "acme/neon", "name": "Neon", "description": "", "source": "plugin", "pluginId": "acme", "modes": ["dark"] },
          { "id": "user/gone", "name": "Gone", "description": "", "source": "user", "modes": ["light"] } ] }
        """;

    [Fact]
    public async Task CatalogAppendsReadableGatewayThemesWithoutWritingTheGatewaySelection()
    {
        var (client, proxy) = Gateway();
        proxy.Reply = (method, parameters) => method switch
        {
            "themes.list" => Json(ListReply),
            "themes.get" when JsonSerializer.Serialize(parameters).Contains("acme/neon") => Json(
                $$"""{ "theme": { "id": "acme/neon", "name": "Neon", "source": "plugin", "modes": ["dark"] }, "definition": {{NeonDefinition}} }"""),
            _ => throw new InvalidOperationException("theme not found"),
        };
        var catalog = new GatewayThemeCatalog(new RecordingUiDispatcher());

        catalog.SetConnection(client, "gw-1");
        await catalog.RefreshAsync();

        var options = catalog.Options;
        Assert.Equal(BuiltinGatewayThemes.All.Length + 1, options.Count);
        var neon = Assert.Single(options, option => option.Id == "acme/neon");
        Assert.Equal("gw-1", neon.GatewayId);
        Assert.NotNull(neon.Definition!.Dark);
        Assert.DoesNotContain(options, option => option.Id == "user/gone");
        Assert.All(proxy.Calls, call => Assert.Matches("^themes\\.(list|get):", call));
    }

    [Fact]
    public async Task CatalogOffersOnlyBuiltinsForGatewaysWithoutThemesAndAfterDisconnect()
    {
        var (client, proxy) = Gateway();
        proxy.Reply = (method, _) => method == "themes.list" ? Json(ListReply) : Json(
            $$"""{ "theme": { "id": "acme/neon", "name": "Neon", "source": "plugin", "modes": ["dark"] }, "definition": {{NeonDefinition}} }""");
        var catalog = new GatewayThemeCatalog(new RecordingUiDispatcher());
        catalog.SetConnection(client, "gw-1");
        await catalog.RefreshAsync();
        Assert.Contains(catalog.Options, option => option.Id == "acme/neon");

        catalog.SetConnection(null, null);
        Assert.Equal(BuiltinGatewayThemes.All.Select(theme => theme.Id), catalog.Options.Select(option => option.Id));

        var (older, olderProxy) = Gateway();
        olderProxy.Methods = [];
        catalog.SetConnection(older, "gw-old");
        await catalog.RefreshAsync();
        Assert.Empty(olderProxy.Calls);
        Assert.Equal(BuiltinGatewayThemes.All.Length, catalog.Options.Count);
    }

    [Fact]
    public Task SelectionIsPublishedBeforeASlowThemeDefinitionArrives() => Task.Run(() =>
    {
        // No synchronization context, so continuations run inline when the pending definition read completes.
        var (client, proxy) = Gateway();
        var neon = new TaskCompletionSource<JsonElement>();
        proxy.ReplyAsync = (method, _) => method == "themes.list"
            ? Task.FromResult(Json("""
                { "themes": [
                    { "id": "tide", "name": "Tide", "description": "", "source": "builtin", "modes": ["light", "dark"] },
                    { "id": "acme/neon", "name": "Neon", "description": "", "source": "plugin", "pluginId": "acme", "modes": ["dark"] } ],
                  "current": { "id": "tide", "mode": "system", "scope": "profile" },
                  "theme": { "id": "tide", "name": "Tide", "description": "", "source": "builtin", "modes": ["light", "dark"] } }
                """))
            : neon.Task;
        var catalog = new GatewayThemeCatalog(new RecordingUiDispatcher());

        catalog.SetConnection(client, "gw-1");

        // A following Companion can switch to the Gateway's built-in selection while another theme's read stalls.
        Assert.Equal("tide", catalog.GatewayCurrent?.Id);
        Assert.DoesNotContain(catalog.Options, option => option.Id == "acme/neon");

        neon.SetResult(Json(
            $$"""{ "theme": { "id": "acme/neon", "name": "Neon", "source": "plugin", "modes": ["dark"] }, "definition": {{NeonDefinition}} }"""));
        Assert.Contains(catalog.Options, option => option.Id == "acme/neon");
        Assert.Equal("tide", catalog.GatewayCurrent?.Id);
    });

    [Fact]
    public Task PushedCatalogChangeIsReadAndWinsOverAnOlderReadThatFinishesLater() => Task.Run(async () =>
    {
        // No synchronization context here, so the catalog's ConfigureAwait(false) continuations run inline
        // when each reply completes and both reads have finished before the assertions.
        var (client, proxy) = Gateway();
        var lists = new Queue<TaskCompletionSource<JsonElement>>();
        proxy.ReplyAsync = (method, parameters) =>
        {
            if (method == "themes.list")
            {
                var pending = new TaskCompletionSource<JsonElement>();
                lists.Enqueue(pending);
                return pending.Task;
            }
            var id = JsonSerializer.Serialize(parameters).Contains("acme/aurora") ? "acme/aurora" : "acme/neon";
            return Task.FromResult(Json(
                $$"""{ "theme": { "id": "{{id}}", "name": "x", "source": "plugin", "modes": ["dark"] }, "definition": {{NeonDefinition}} }"""));
        };
        var catalog = new GatewayThemeCatalog(new RecordingUiDispatcher());

        catalog.SetConnection(client, "gw-1");
        proxy.ThemeCatalogInvalidated!.Invoke(client, "plugins.changed");
        Assert.Equal(2, lists.Count);
        var initial = lists.Dequeue();
        var pushed = lists.Dequeue();
        pushed.SetResult(Json("""
            { "themes": [
              { "id": "acme/neon", "name": "Neon", "source": "plugin", "pluginId": "acme", "modes": ["dark"] },
              { "id": "acme/aurora", "name": "Aurora", "source": "plugin", "pluginId": "acme", "modes": ["dark"] } ] }
            """));
        initial.SetResult(Json(ListReply));
        await Task.WhenAll(initial.Task, pushed.Task);

        Assert.Contains(catalog.Options, option => option.Id == "acme/aurora");
        catalog.SetConnection(null, null);
        Assert.Null(proxy.ThemeCatalogInvalidated);
    });

    [Fact]
    public void SameClientReconnectingRereadsTheCatalogItMissedWhileOffline()
    {
        var (client, proxy) = Gateway();
        proxy.Reply = (method, _) => method == "themes.list" ? Json(ListReply) : Json(
            $$"""{ "theme": { "id": "acme/neon", "name": "Neon", "source": "plugin", "modes": ["dark"] }, "definition": {{NeonDefinition}} }""");
        var catalog = new GatewayThemeCatalog(new RecordingUiDispatcher());
        int Lists() => proxy.Calls.Count(call => call.StartsWith("themes.list:", StringComparison.Ordinal));

        catalog.SetConnection(client, "gw-1");
        catalog.SetConnection(client, "gw-1");
        Assert.Equal(1, Lists());

        // A disconnect and reconnect that both finish before the UI thread observes them leave the same
        // connected client object, with a new connection epoch.
        proxy.Epoch = 2;
        catalog.SetConnection(client, "gw-1");
        Assert.Equal(2, Lists());
        Assert.Contains(catalog.Options, option => option.Id == "acme/neon");
    }

    [Fact]
    public void FailedSaveOfTheFollowedThemeDoesNotEscapeTheCatalogEvent()
    {
        var catalog = new StaticCatalog
        {
            GatewayCurrent = new GatewayThemeOption(
                new GatewayThemeDescriptor("crt", "CRT", "", GatewayThemeSource.Builtin, [ThemeColorMode.Light, ThemeColorMode.Dark], null),
                null, "gw-1"),
        };
        var applied = 0;
        using var follower = new GatewayThemeFollower(catalog,
            () => new AppearanceSettings { FollowGatewayTheme = true },
            _ => throw new IOException("settings.json is locked"),
            () => applied++);
        var laterSubscriber = 0;
        catalog.Changed += (_, _) => laterSubscriber++;

        catalog.Raise();

        Assert.Equal(1, laterSubscriber);
        Assert.Equal(0, applied);
    }

    private sealed class StaticCatalog : IGatewayThemeCatalog
    {
        public IReadOnlyList<GatewayThemeOption> Options { get; init; } = [];
        public GatewayThemeOption? GatewayCurrent { get; init; }
        public event EventHandler? Changed;
        public Task RefreshAsync() => Task.CompletedTask;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    // Mica belongs to Windows default; a Gateway theme is opaque like the dashboard, except under High Contrast.
    [Theory]
    [InlineData(true, false, false, "System")]
    [InlineData(true, true, false, "Opaque")]
    [InlineData(true, true, true, "System")]
    [InlineData(false, false, false, "Opaque")]
    [InlineData(false, true, false, "Opaque")]
    [InlineData(false, false, true, "Opaque")]
    public void MicaShowsOnlyForWindowsDefaultWithTheSettingOn(
        bool useMica, bool themeActive, bool highContrast, string expected) =>
        Assert.Equal(expected, ThemeBackdropPolicy.Choose(useMica, themeActive, highContrast).ToString());

    // The applicator moves only these app brushes onto the theme's accent ramp. Matching by color would also capture
    // fixed brushes that equal a Windows accent shade, such as the #0078D4 session tint.
    [Fact]
    public void AccentBoundAppKeysAreExactlyTheXamlBrushesBoundToAnAccentShade()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tray = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var files = Directory.GetFiles(Path.Combine(tray, "Themes"), "*.xaml").Append(Path.Combine(tray, "App.xaml"));
        var bound = files
            .SelectMany(file => XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
            .SelectMany(dictionaries => dictionaries.Elements().Where(d => (string?)d.Attribute(x + "Key") is "Default" or "Light"))
            .SelectMany(dictionary => dictionary.Elements())
            .Where(resource => resource.DescendantsAndSelf().Attributes().Any(a => a.Value.Contains("SystemAccentColor", StringComparison.Ordinal)))
            .Select(resource => (string)resource.Attribute(x + "Key")!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(bound.Order(StringComparer.Ordinal), ThemeResourceProjection.AccentBoundAppKeys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FontMapperKeepsBundledFacesAndEndsInInstalledWindowsFonts()
    {
        var mono = ThemeFontMapper.ToXamlSource(ThemeFontStack.Parse(
            "\"JetBrains Mono\", ui-monospace, SFMono-Regular, \"SF Mono\", Menlo, Monaco, Consolas, monospace"),
            ThemeFontMapper.Role.Code);
        var serif = ThemeFontMapper.ToXamlSource(ThemeFontStack.Parse("\"Lora\", Georgia, \"Times New Roman\", serif"),
            ThemeFontMapper.Role.Ui);
        var unknown = ThemeFontMapper.ToXamlSource(ThemeFontStack.Parse("\"Inter\", -apple-system, BlinkMacSystemFont"),
            ThemeFontMapper.Role.Ui);

        Assert.Equal("ms-appx:///Assets/Fonts/JetBrainsMono.ttf#JetBrains Mono, Consolas, Cascadia Mono", mono);
        Assert.Equal("ms-appx:///Assets/Fonts/Lora.ttf#Lora, Georgia, Times New Roman, Segoe UI Variable Text", serif);
        Assert.Equal("Inter, Segoe UI Variable Text", unknown);
    }

    [Fact]
    public void EveryBuiltinThemeTypefaceIsBundledWithTheCompanion()
    {
        var fonts = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Assets", "Fonts");
        var families = BuiltinGatewayThemes.All
            .SelectMany(theme => new[] { theme.Light!, theme.Dark! })
            .SelectMany(palette => new[] { palette.BodyFont.Primary, palette.ChatFont.Primary, palette.MonoFont.Primary })
            .Distinct();

        foreach (var family in families)
        {
            Assert.True(ThemeFontMapper.BundledFamilies.TryGetValue(family, out var file), $"{family} is not bundled.");
            Assert.True(File.Exists(Path.Combine(fonts, file + ".ttf")), $"{file}.ttf is missing.");
        }
    }

    // Windows selects a variable font's weights from its named instances. Without them only the default instance
    // remains, which is Black for Fraunces, so each bundled family must name a Regular weight.
    [Fact]
    public void EveryBundledTypefaceNamesARegularWeight()
    {
        var fonts = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Assets", "Fonts");
        foreach (var file in ThemeFontMapper.BundledFamilies.Values.Distinct())
        {
            var weights = NamedWeights(File.ReadAllBytes(Path.Combine(fonts, file + ".ttf")));
            Assert.True(weights.Contains(400), $"{file}.ttf names weights [{string.Join(", ", weights)}], not 400.");
        }
    }

    // Reads the wght coordinate of each OpenType fvar named instance.
    private static List<double> NamedWeights(byte[] font)
    {
        static int U16(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        static int I32(byte[] data, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        var weights = new List<double>();
        for (var table = 0; table < U16(font, 4); table++)
        {
            var record = 12 + 16 * table;
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) != "fvar") continue;
            var fvar = I32(font, record + 8);
            int axes = fvar + U16(font, fvar + 4), axisCount = U16(font, fvar + 8), axisSize = U16(font, fvar + 10);
            int instanceCount = U16(font, fvar + 12), instanceSize = U16(font, fvar + 14);
            var wght = Enumerable.Range(0, axisCount)
                .Single(axis => System.Text.Encoding.ASCII.GetString(font, axes + axis * axisSize, 4) == "wght");
            for (var instance = 0; instance < instanceCount; instance++)
                weights.Add(I32(font, axes + axisCount * axisSize + instance * instanceSize + 4 + 4 * wght) / 65536.0);
        }
        return weights;
    }

    [Fact]
    public void PickerPersistsCompanionThemeAndMicaWithoutTouchingTheOtherChoice()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands());
        vm.Activate(null);
        Assert.Equal("", vm.SelectedThemeKey);
        Assert.Equal("", vm.ThemeChoices[0].Key);

        vm.SelectedThemeKey = "tide";
        vm.UseMica = false;

        Assert.Equal("tide", settings.Appearance.Theme?.Id);
        Assert.Equal("builtin", settings.Appearance.Theme?.Source);
        Assert.False(settings.Appearance.UseMica);
        Assert.Equal("tide", AppearanceResolver.ResolveTheme(new SettingsManager(temp.Path).Appearance)?.Id);
        Assert.False(new SettingsManager(temp.Path).Appearance.UseMica);

        vm.SelectedThemeKey = "";
        Assert.Null(settings.Appearance.Theme);
        Assert.False(settings.Appearance.UseMica);
    }

    [Fact]
    public void FollowingTheGatewayLocksThePickerToTheFollowedThemeAndKeepsTheManualChoice()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var store = new SettingsStore(settings, new RecordingUiDispatcher());
        var vm = new SettingsPageViewModel(store, new FakeAppCommands());
        vm.Activate(null);
        vm.SelectedThemeKey = "tide";

        vm.FollowGatewayTheme = true;
        Assert.True(settings.Appearance.FollowGatewayTheme);
        Assert.False(vm.IsManualThemeEnabled);
        // Nothing followed yet, so the manual choice is what the Companion shows.
        Assert.Equal("tide", vm.SelectedThemeKey);

        // App records the Gateway's selection in the stored appearance, outside this page.
        store.Update(null, edit => edit.Appearance = settings.Appearance with
        {
            FollowedTheme = new ThemeSelection { Id = "crt", Name = "CRT" },
        });
        Assert.Equal("crt", vm.SelectedThemeKey);
        vm.SelectedThemeKey = "rose";
        vm.UseMica = false;
        Assert.Equal("crt", settings.Appearance.FollowedTheme?.Id);
        Assert.Equal("tide", settings.Appearance.Theme?.Id);
        Assert.False(settings.Appearance.UseMica);

        vm.FollowGatewayTheme = false;
        Assert.True(vm.IsManualThemeEnabled);
        Assert.Equal("tide", vm.SelectedThemeKey);
        Assert.Equal("tide", AppearanceResolver.ResolveTheme(new SettingsManager(temp.Path).Appearance)?.Id);
    }

    [Fact]
    public void PickerKeepsItsItemsAcrossAnUnchangedRefreshAndCanPickAFollowedSnapshot()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path)
        {
            Appearance = new AppearanceSettings
            {
                FollowedTheme = new ThemeSelection { Id = "acme/neon", Name = "Neon", Source = "plugin", Definition = Json(NeonDefinition) },
            },
        };
        settings.Save();
        var catalog = new StaticCatalog();
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands(), catalog);
        vm.Activate(null);
        var items = vm.ThemeChoices;
        Assert.Contains(items, choice => choice.Key == "acme/neon");

        // Replacing the items would close a picker the user has open.
        catalog.Raise();
        Assert.Same(items, vm.ThemeChoices);

        // Following was turned off while the Gateway is offline: the listed snapshot is still a real choice.
        vm.SelectedThemeKey = "acme/neon";
        Assert.Equal("acme/neon", settings.Appearance.Theme?.Id);
        Assert.Equal("acme/neon", AppearanceResolver.ResolveTheme(settings.Appearance)?.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedThemeThatCanNoLongerBeAppliedShowsTheWindowsDefault(bool following)
    {
        using var temp = new TempDir();
        var unreadable = NeonDefinition.Replace("\"foreground\": \"#f5e9ff\"", "\"foreground\": \"#05010a\"");
        var settings = new SettingsManager(temp.Path)
        {
            Appearance = new AppearanceSettings
            {
                Theme = new ThemeSelection { Id = "acme/neon", Name = "Neon", Source = "plugin", Definition = Json(unreadable) },
                FollowGatewayTheme = following,
            },
        };
        settings.Save();
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands(),
            new GatewayThemeCatalog(new RecordingUiDispatcher()));

        vm.Activate(null);

        Assert.Null(AppearanceResolver.ResolveTheme(settings.Appearance));
        Assert.Equal("", vm.SelectedThemeKey);
        Assert.DoesNotContain(vm.ThemeChoices, choice => choice.Key == "acme/neon");
    }

    [Fact]
    public void ReplacingPickerItemsKeepsTheSavedTheme()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands());
        vm.Activate(null);
        vm.SelectedThemeKey = "tide";
        // A WinUI ComboBox clears its selection, writing null, when its ItemsSource is replaced.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.ThemeChoices))
                vm.SelectedThemeKey = null!;
        };

        vm.DefaultThemeLabel = "Windows default (relabelled)";

        Assert.Equal("tide", vm.SelectedThemeKey);
        Assert.Equal("tide", settings.Appearance.Theme?.Id);
        vm.SelectedThemeKey = "";
        Assert.Null(settings.Appearance.Theme);
    }

    [Fact]
    public void SavedGatewayThemeStaysSelectedWhileItsGatewayIsAbsent()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path)
        {
            Appearance = new AppearanceSettings
            {
                Theme = new ThemeSelection { Id = "acme/neon", Name = "Neon", Source = "plugin", Definition = Json(NeonDefinition) },
            },
        };
        settings.Save();
        var vm = new SettingsPageViewModel(
            new SettingsStore(new SettingsManager(temp.Path), new RecordingUiDispatcher()),
            new FakeAppCommands(),
            new GatewayThemeCatalog(new RecordingUiDispatcher()));

        vm.Activate(null);

        Assert.Equal("acme/neon", vm.SelectedThemeKey);
        Assert.Contains(vm.ThemeChoices, choice => choice.Key == "acme/neon" && choice.Name == "Neon");
        var theme = AppearanceResolver.ResolveTheme(new SettingsManager(temp.Path).Appearance);
        Assert.Equal(ThemeColorMode.Dark, theme?.PinnedMode);
        Assert.Equal("Space Grotesk", theme!.Dark!.BodyFont.Primary);
    }

    [Fact]
    public void UnlistedSavedThemeSurvivesThePickerWritingNull()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        using var json = System.Text.Json.JsonDocument.Parse("""
            { "name": "Glass", "dark": {
              "background": "#101010", "foreground": "#ffffff", "card": "#181818", "card-foreground": "#ffffff",
              "popover": "#181818", "popover-foreground": "#ffffff", "primary": "#5ab6d8", "primary-foreground": "#000000",
              "secondary": "#202020", "secondary-foreground": "#ffffff", "muted": "#202020", "muted-foreground": "#cccccc",
              "accent": "#5ab6d8", "accent-foreground": "#000000", "destructive": "#f87171", "destructive-foreground": "#000000",
              "border": "#ffffff33", "input": "#ffffff33", "ring": "#5ab6d8" } }
            """);
        var saved = new ThemeSelection { Id = "user/glass", Name = "Glass", Source = "user", Definition = json.RootElement.Clone() };
        settings.Appearance = new AppearanceSettings { Theme = saved };
        var vm = new SettingsPageViewModel(new SettingsStore(settings, new RecordingUiDispatcher()), new FakeAppCommands());
        vm.Activate(null);
        Assert.Equal("user/glass", vm.SelectedThemeKey);

        vm.SelectedThemeKey = null!;

        Assert.Equal("user/glass", vm.SelectedThemeKey);
        Assert.Equal(saved, settings.Appearance.Theme);
    }
}
