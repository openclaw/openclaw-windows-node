using System.Runtime.InteropServices;
using System.Text.Json;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Pages;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Tray.UITests;

/// <summary>Synthetic inputs only. Unknown commands fail rather than falling back to a live service.</summary>
internal static class OnboardingSetupGalleryData
{
    internal sealed record Scene(string Id, string Family, string State);
    internal sealed record HeaderContract(Type PageType, string Text, string? ControlName = null, string? ContainerName = null);

    internal static HeaderContract HeaderFor(Scene scene) => scene.Family switch
    {
        "security" => new(typeof(SecurityNoticePage), "Welcome to OpenClaw"),
        "welcome" => new(typeof(WelcomePage), "Set up OpenClaw"),
        "advanced" => new(typeof(AdvancedSetupPage), "Choose another setup route"),
        "connection" => new(typeof(SetupNativeConnectionPage), "Connect to a gateway", "TitleText"),
        "capabilities" or "branch" => new(typeof(CapabilitiesPage), "PC capabilities"),
        "review" => new(typeof(GatewaySetupPage), "WSL Gateway setup"),
        "tailscale" => new(typeof(GatewaySetupDetailPage), "Tailscale Serve", "DetailTitle"),
        "local-review" => new(typeof(GatewaySetupDetailPage), "Local AI", "DetailTitle"),
        "networking" => new(typeof(GatewaySetupDetailPage), "WSL networking consent", "DetailTitle"),
        "ai" or "ai-return" or "dialog" => new(typeof(AiSetupPage), "Connect your AI", "TitleText"),
        "preview" when scene.State.StartsWith("wizard", StringComparison.Ordinal) =>
            new(typeof(WizardPage), "OpenClaw onboard"),
        "preview" when scene.State == "milestone" =>
            new(typeof(ProgressPage), "Gateway installed", ContainerName: "MilestonePanel"),
        "preview" => new(typeof(ProgressPage), "Making room for your agent", "TitleText"),
        "complete" => new(typeof(CompletePage), scene.State switch
        {
            "success" or "local-ai" => "All set!",
            "restart" => "Restart required",
            _ => "Setup failed",
        }, "TitleText"),
        _ => throw new InvalidOperationException($"Gallery scene has no semantic header contract: {scene.Id}"),
    };

    internal static IReadOnlyList<Scene> RequiredScenes { get; } =
    [
        new("01-welcome-trust", "security", "default"),
        new("02-gateway-install", "welcome", "install"),
        new("02-gateway-existing", "welcome", "existing"),
        new("02-gateway-checking", "welcome", "checking"),
        new("02-gateway-blocked", "welcome", "blocked"),
        new("02-gateway-inspection-failed", "welcome", "failure"),
        new("03-alternatives", "advanced", "default"),
        new("03-alternatives-host-unavailable", "advanced", "unavailable"),
        new("04-existing-empty", "connection", "existing-empty"),
        new("04-existing-check-failed", "connection", "existing-check"),
        new("04-existing-next-failed", "connection", "existing-next"),
        new("04-remote-empty", "connection", "remote-empty"),
        new("04-remote-check-failed", "connection", "remote-check"),
        new("04-remote-next-failed", "connection", "remote-next"),
        new("04-remote-ssh-draft", "connection", "remote-ssh"),
        new("05-capabilities-read-only", "capabilities", "ReadOnly"),
        new("05-capabilities-standard", "capabilities", "Standard"),
        new("05-capabilities-full", "capabilities", "Full"),
        new("05-capabilities-read-only-fine-tune", "capabilities", "ReadOnly-fine-tune"),
        new("05-capabilities-standard-fine-tune", "capabilities", "Standard-fine-tune"),
        new("05-capabilities-full-fine-tune", "capabilities", "Full-fine-tune"),
        new("05-capabilities-custom", "capabilities", "Custom"),
        new("05-capabilities-transports-off", "capabilities", "off"),
        new("05-capabilities-browser-prerequisite", "capabilities", "browser"),
        new("07-review-fresh", "review", "fresh"),
        new("07-review-exact-commands", "review", "commands"),
        new("07-review-replacement-unchecked", "review", "replacement"),
        new("07-review-replacement-checked", "review", "confirmed"),
        new("07-review-inspection-required", "review", "inspection"),
        new("07-review-local-ai-required", "review", "local-ai"),
        new("07-review-networking-required", "review", "networking"),
        new("07-review-tailscale-required", "review", "tailscale"),
        new("08-tailscale-off", "tailscale", "off"),
        new("09-progress-gateway", "preview", "progress"),
        new("09-progress-local-ai", "preview", "progress-local-ai"),
        new("09-gateway-installed-milestone", "preview", "milestone"),
        new("10-ai-local-checking", "ai", "Checking"),
        new("10-ai-local-set-up", "ai", "SetUp"),
        new("10-ai-local-start-and-use", "ai", "StartAndUse"),
        new("10-ai-local-use", "ai", "Use"),
        new("10-ai-local-repair", "ai", "Repair"),
        new("10-ai-local-busy", "ai", "BusyGpu"),
        new("10-ai-local-unsupported", "ai", "Unsupported"),
        new("10-ai-local-unsupported-hidden", "ai", "unsupported-hidden"),
        new("10-ai-local-unknown", "ai", "Unknown"),
        new("10-ai-local-remote", "ai", "UnsupportedGateway"),
        new("10-ai-local-working", "ai", "Working"),
        new("12-ai-providers", "ai", "providers"),
        new("12-ai-more-sign-in", "ai", "more"),
        new("12-ai-api-key", "ai", "api-key"),
        new("12-ai-other-local-services", "ai", "other-local"),
        new("12-ai-empty-discovery", "ai", "empty"),
        new("12-ai-discovery-failed", "ai", "discovery-failed"),
        new("12-ai-classic-fallback", "ai", "classic"),
        new("12-ai-catalog-consent", "ai", "catalog"),
        new("13-provider-text", "dialog", "text"),
        new("13-provider-password", "dialog", "password"),
        new("13-provider-select", "dialog", "select"),
        new("13-provider-multiselect", "dialog", "multiselect"),
        new("13-provider-confirm", "dialog", "confirm"),
        new("13-provider-note", "dialog", "note"),
        new("13-provider-progress", "dialog", "progress"),
        new("13-provider-error", "dialog", "error"),
        new("13-provider-cancelling", "dialog", "cancelling"),
        new("14-local-ai-review-model", "local-review", "eligible"),
        new("14-local-ai-review-model-list", "local-review", "models"),
        new("14-local-ai-review-busy", "local-review", "busy"),
        new("14-local-ai-review-unsupported", "local-review", "unsupported"),
        new("14-local-ai-review-unknown", "local-review", "unknown"),
        new("14-local-ai-review-checking", "local-review", "checking"),
        new("14-local-ai-review-off", "local-review", "off"),
        new("15-network-consent-unchecked", "networking", "unchecked"),
        new("15-network-consent-checked", "networking", "checked"),
        new("16-ai-return-to-choices", "ai-return", "choices"),
        new("16-ai-exact-model-verification", "ai-return", "verification"),
        new("17-compatibility-wizard", "preview", "wizard"),
        new("17-compatibility-wizard-error", "preview", "wizard-error"),
        new("18-complete", "complete", "success"),
        new("18-complete-local-ai", "complete", "local-ai"),
        new("18-complete-error", "complete", "error"),
        new("18-complete-restart", "complete", "restart"),
        new("18-complete-fallback", "complete", "fallback"),
        new("19-mcp-capabilities", "branch", "mcp-capabilities"),
        new("19-mcp-last-setup-page", "branch", "mcp-finish"),
        new("19-deferred-capabilities", "branch", "deferred-capabilities"),
        new("19-deferred-last-setup-page", "branch", "deferred-finish"),
        new("20-mascot-static-halo", "security", "halo"),
    ];

    internal static HostHardwareInfo Hardware(string state) => state switch
    {
        "unknown" => new(Architecture.X64, null, null,
            [new(GpuVendor.Nvidia, "Synthetic GPU: facts unavailable")], false),
        "unsupported" => new(Architecture.X64, 64L << 30, 48L << 30, [], false),
        _ => new(Architecture.X64, 128L << 30, 96L << 30,
            [new(GpuVendor.Nvidia, "Synthetic NVIDIA GPU", 48L << 30,
                state == "busy" ? 1L << 30 : 44L << 30,
                DriverVersion: "999.0", CudaMajorVersion: 13, StableId: "synthetic-gallery-gpu")], false),
    };

    internal sealed class LocalHost(LocalAiOnboardingState state, bool freshUnsupported = false) : ISetupLocalAiHost
    {
        internal LocalAiOnboardingSnapshot Snapshot { get; } = freshUnsupported || state == LocalAiOnboardingState.Unsupported
            ? LocalAiOnboardingSnapshot.Project(new("gallery", "Gallery-NoRealDistro", 18789, null, null),
                LocalInferenceEligibility.Evaluate(Hardware("unsupported")), null, false, false, null,
                installationKnown: !freshUnsupported)
            : new(state, new("gallery", "Gallery-NoRealDistro", 18789, "synthetic-target", 18803),
                "openai/gallery-local-model", "synthetic-receipt", "Synthetic NVIDIA GPU", "Synthetic local model",
                HasInstallationEvidence: true);
        public Task<LocalAiOnboardingSnapshot> ObserveAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public Task<SetupLocalAiTarget> RevalidateReviewAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct) =>
            Task.FromResult(selected.Target!);
        public Task<SetupLocalAiUseResult> UseAsync(LocalAiOnboardingSnapshot selected, CancellationToken ct) =>
            throw new InvalidOperationException("Gallery cannot start or publish Local AI.");
        public OpenClaw.Connection.GatewayRegistrySnapshot BeginGatewaySetup() => throw new InvalidOperationException("Gallery cannot install a Gateway.");
        public Task ReconcileGatewaySetupAsync(OpenClaw.Connection.GatewayRegistrySnapshot expectedOutput, string? completedGatewayId) =>
            throw new InvalidOperationException("Gallery cannot reconcile a Gateway.");
    }

    internal sealed class NativeHost : ISetupNativeConnectionHost
    {
        internal List<string> Calls { get; } = [];
        public Task<SetupNativeConnectionResult> CheckAsync(SetupNativeConnectionRequest request, CancellationToken ct)
        {
            Calls.Add("check");
            return Task.FromResult(new SetupNativeConnectionResult(false,
                Error: "Synthetic check failure. No Gateway was contacted."));
        }
        public Task<SetupNativeConnectionResult> ConnectAsync(SetupNativeConnectionRequest request, CancellationToken ct)
        {
            Calls.Add("connect");
            return Task.FromResult(new SetupNativeConnectionResult(false,
                Error: "Synthetic connection failure. Nothing was committed."));
        }
        public Task DiscardCheckAsync() => Task.CompletedTask;
    }

    internal sealed class Transport(string state) : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route { get; } = new("gallery", "main", "synthetic-authority");
        public long Generation => 1;
        public bool IsConnected => true;
        public IReadOnlyCollection<string> OperatorScopes => ["operator.admin"];
        public IReadOnlyCollection<string> Methods => state == "classic"
            ? ["wizard.start", "wizard.next", "wizard.cancel"]
            : ["openclaw.setup.detect", "openclaw.setup.verify", "openclaw.setup.activate.start",
                "openclaw.setup.auth.start", "openclaw.setup.prepare.start", "wizard.next", "wizard.cancel", "wizard.status"];
        internal List<string> Calls { get; } = [];

        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(method);
            if (method == "openclaw.setup.verify")
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    ok = false, status = "unavailable",
                    error = "Synthetic exact-model verification failure. No Gateway was contacted."
                }));
            if (method != "openclaw.setup.detect")
                throw new InvalidOperationException($"Gallery forbids provider operations: {method}");
            if (state == "discovery-failed")
                throw new IOException("Synthetic discovery failure. No Gateway was contacted.");
            var empty = state == "empty";
            var detection = new GatewayAiSetupDetection
            {
                Candidates = empty ? [] :
                [
                    new("existing-model", "Existing Gateway model", "Synthetic configured Gateway choice",
                        "openai/gallery-cloud-model", true, true, "codex"),
                    new("saved-auth:claude", "Claude on your Gateway", "Synthetic saved provider",
                        "anthropic/gallery-model", false, true, "claude"),
                ],
                ManualProviders = [new("openai-api-key", "OpenAI API key", "Synthetic provider", BrandId: "codex"),
                    new("anthropic-api-key", "Anthropic API key", "Synthetic provider", BrandId: "claude")],
                AuthOptions = empty ? [] :
                [
                    new("codex-login", "OpenAI sign-in", "Synthetic account", BrandId: "codex", Featured: true),
                    new("gemini-login", "Gemini sign-in", "Synthetic account", BrandId: "gemini", Featured: true),
                    new("kimi-login", "Kimi sign-in", "Synthetic device code", BrandId: "kimi", Kind: "device-code"),
                    new("grok-login", "Grok sign-in", "Synthetic account", BrandId: "grok"),
                    new("pi-login", "Pi sign-in", "Synthetic account", BrandId: "pi"),
                    new("opencode-login", "OpenCode sign-in", "Synthetic account", BrandId: "opencode"),
                ],
                PrepareOptions = empty ? [] :
                [
                    new("ollama", "Ollama", "Synthetic local service", BrandId: "ollama", Kind: "install"),
                    new("lmstudio", "LM Studio", "Synthetic local service", BrandId: "lmstudio", Kind: "custom"),
                ],
                UnavailableCandidates = [new("unavailable", "Unavailable Gateway model",
                    "Synthetic credential unavailable", "auth", BrandId: "claude")],
                // These addresses are displayed by production controls, never opened or fetched.
                RecommendedInstalls = [new("codex", "Install Codex", "Synthetic recommendation",
                    BrandId: "codex", Website: "https://gallery.invalid/install")],
                NativeSessionCatalogPreferenceRequired = state == "catalog",
                NativeSessionCatalogs = [new("synthetic-catalog", "Synthetic conversation history")],
                Workspace = "/synthetic-gallery",
                ConfiguredModel = "openai/gallery-cloud-model",
                SetupComplete = true,
            };
            Assert.All(detection.AuthOptions.Concat(detection.ManualProviders).Concat(detection.PrepareOptions),
                provider => Assert.Null(provider.Icon));
            return Task.FromResult(JsonSerializer.SerializeToElement(detection, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }
}
