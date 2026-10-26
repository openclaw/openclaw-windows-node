using System.Text.Json;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>Synthetic pinned-source sign-in protocol. No provider, browser or credential storage is involved.</summary>
public sealed class ModelSignInFixture
{
    public const string Name = "model-sign-in";
    public const string Choice = "fixture-provider:fixture-login";
    public const string Answer = "synthetic-answer-not-a-credential";
    public static readonly string[] Methods = ["models.authStatus", "models.authLogin", "wizard.next", "wizard.cancel"];
    private readonly object _gate = new();
    private readonly List<(string Method, JsonElement Parameters)> _requests = [];
    private string? _session;
    private bool _progressRead;
    private bool _saved;
    public bool RejectLogin { get; init; }
    public bool StartWithProgress { get; init; }
    public string TerminalStatus { get; init; } = "done";
    public bool CancellationHeld { get; init; }
    public bool OmitWizardStatus { get; init; }
    public IReadOnlyList<(string Method, JsonElement Parameters)> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    internal object Respond(string method, JsonElement parameters)
    {
        lock (_gate)
        {
            _requests.Add((method, parameters.Clone()));
            if (method == "models.authStatus")
            {
                GatewayScenario.ValidateProperties(parameters, "agentId");
                RequireAgent(parameters);
                return new
                {
                    ts = 1792790000000L,
                    providers = new[] { new { provider = "fixture-provider", displayName = "Fixture provider", status = _saved ? "ok" : "missing" } },
                    providerCapabilities = new[] { new { provider = "fixture-provider",
                        loginOptions = new[] { new { id = Choice, label = "Fixture sign-in", hint = "Synthetic provider. No real account." } } } }
                };
            }
            if (method == "models.authLogin")
            {
                GatewayScenario.ValidateProperties(parameters, "sessionId", "agentId", "authChoice");
                RequireAgent(parameters);
                if (RejectLogin || parameters.GetProperty("authChoice").GetString() != Choice)
                    throw new FixtureRequestException("INVALID_REQUEST", "Synthetic provider admission denied.",
                        detailCode: "SYNTHETIC_ADMISSION_DENIED", retryable: true, retryAfterMs: 250);
                var id = parameters.GetProperty("sessionId").GetString();
                if (!Guid.TryParse(id, out _) || _session is not null)
                    throw new FixtureRequestException("INVALID_PARAMS", "Invalid synthetic session.");
                _session = id;
                return WizardResult(false, "running", Step(StartWithProgress), id);
            }
            if (parameters.GetProperty("sessionId").GetString() != _session)
                throw new FixtureRequestException("INVALID_REQUEST", "Synthetic wizard not found.", detailCode: "WIZARD_NOT_FOUND");
            if (method == "wizard.cancel")
            {
                GatewayScenario.ValidateProperties(parameters, "sessionId", "closeInput");
                if (parameters.GetProperty("closeInput").ValueKind != JsonValueKind.True)
                    throw new FixtureRequestException("INVALID_PARAMS", "Close input is required.");
                if (!CancellationHeld) _session = null;
                return new { status = CancellationHeld ? "running" : "cancelled" };
            }
            GatewayScenario.ValidateProperties(parameters, "sessionId", "answer");
            if (StartWithProgress && !_progressRead)
            {
                if (parameters.TryGetProperty("answer", out _))
                    throw new FixtureRequestException("INVALID_PARAMS", "Gateway progress does not accept an answer.");
                _progressRead = true;
                return WizardResult(false, "running", Step(false));
            }
            var answer = parameters.GetProperty("answer");
            GatewayScenario.ValidateProperties(answer, "stepId", "value");
            if (answer.GetProperty("stepId").GetString() != "fixture-question" ||
                answer.GetProperty("value").GetString() != Answer)
                throw new FixtureRequestException("INVALID_PARAMS", "Synthetic answer rejected.");
            _saved = true;
            _session = null;
            return WizardResult(true, TerminalStatus);
        }
    }

    private object WizardResult(bool done, string status, object? step = null, string? sessionId = null)
    {
        var result = new Dictionary<string, object> { ["done"] = done };
        if (!OmitWizardStatus) result["status"] = status;
        if (step is not null) result["step"] = step;
        if (sessionId is not null) result["sessionId"] = sessionId;
        if (status == "error") result["error"] = "Synthetic failure after saving.";
        return result;
    }

    private static void RequireAgent(JsonElement parameters)
    {
        if (parameters.GetProperty("agentId").GetString() is not ("main" or "research"))
            throw new FixtureRequestException("INVALID_PARAMS", "Unknown synthetic agent.");
    }
    private static object Step(bool progress) => progress
        ? new { id = "fixture-progress", type = "progress", executor = "gateway",
            title = "Fixture provider preparation", message = "Synthetic provider work only." }
        : new { id = "fixture-question", type = "text", executor = "client", sensitive = true,
            title = "Fixture sign-in question", message = "Enter the synthetic answer from the fixture instructions." };
}
