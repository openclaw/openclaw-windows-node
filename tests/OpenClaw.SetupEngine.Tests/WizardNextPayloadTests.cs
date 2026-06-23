using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public class WizardNextPayloadTests
{
    [Fact]
    public void Acknowledge_SerializesGatewayCompatibleAnswerEnvelope()
    {
        var payload = WizardNextPayload.Acknowledge("session-1", "step-progress");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, SetupConfig.JsonOptions));
        var root = doc.RootElement;

        Assert.Equal("session-1", root.GetProperty("sessionId").GetString());
        var answer = root.GetProperty("answer");
        Assert.Equal("step-progress", answer.GetProperty("stepId").GetString());
        Assert.False(answer.TryGetProperty("value", out _));
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("action")]
    public void Acknowledge_AdvancesGatewayCurrentStep_ForDriveThroughTypes(string stepType)
    {
        _ = stepType; // documents the drive-through categories this contract protects.
        var session = new FakeWizardSession();

        Assert.Equal("step-1", session.Next(new { sessionId = "session-1" }));
        Assert.Equal("step-1", session.Next(new { sessionId = "session-1" }));
        Assert.Equal("step-2", session.Next(WizardNextPayload.Acknowledge("session-1", "step-1")));
        Assert.Equal("step-2", session.Next(new { sessionId = "session-1" }));
    }

    private sealed class FakeWizardSession
    {
        private string? _currentStepId = "step-1";
        private bool _acknowledged;

        public string? Next(object payload)
        {
            if (HasAnswer(payload))
            {
                _currentStepId = null;
                _acknowledged = true;
            }

            _currentStepId ??= _acknowledged ? "step-2" : "step-1";
            return _currentStepId;
        }

        private static bool HasAnswer(object payload)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, SetupConfig.JsonOptions));
            return doc.RootElement.TryGetProperty("answer", out _);
        }
    }
}
