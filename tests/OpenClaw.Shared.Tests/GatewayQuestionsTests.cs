using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayQuestionsTests
{
    private const string Request = """
        {"id":"request-1","sessionKey":"agent:main:main","agentId":"main","runId":"run-1",
         "createdAtMs":1000,"expiresAtMs":9999999999999,"status":"pending",
         "questions":[{"questionId":"choice","header":"Platform","question":"Choose a platform",
          "options":[{"label":"Windows","value":"win","description":"Native desktop"},
                     {"label":"Linux","value":"linux"}],"isOther":false}]}
        """;

    [Fact]
    public void Requested_PreservesRoutingAndOpaqueOptionValues()
    {
        var evt = GatewayQuestions.ParseEvent("question.requested", JsonDocument.Parse(Request).RootElement);
        Assert.Equal("request-1", evt.Id);
        Assert.Equal("agent:main:main", evt.Request!.SessionKey);
        Assert.Equal("run-1", evt.Request.RunId);
        var question = Assert.Single(evt.Request.Questions);
        Assert.Equal("win", question.Options[0].Value);
        Assert.False(question.AllowOther);
    }

    [Theory]
    [InlineData("answered")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public void Resolved_DoesNotRequireSessionOrRetainAnswers(string status)
    {
        var evt = GatewayQuestions.ParseEvent("question.resolved",
            JsonSerializer.SerializeToElement(new { id = "request-1", status, answers = new { answers = new { secret = new[] { "not retained" } } } }));
        Assert.Equal(status, evt.Status);
        Assert.Null(evt.Request);
        Assert.DoesNotContain("not retained", JsonSerializer.Serialize(evt));
    }

    [Theory]
    [InlineData("\"multiSelect\":3,")]
    [InlineData("\"url\":\"file:///C:/secret\",")]
    [InlineData("\"url\":\"https://user:password@example.com\",")]
    [InlineData("\"presentation\":\"unknown\",")]
    public void InvalidFields_AreRejectedOrReportedWithoutInventingAnAnswer(string field)
    {
        var json = Request.Replace("\"questionId\":\"choice\",", field + "\"questionId\":\"choice\",");
        if (field.StartsWith("\"url\":", StringComparison.Ordinal))
        {
            var question = Assert.Single(GatewayQuestions.ParseRequest(JsonDocument.Parse(json).RootElement).Questions);
            Assert.Null(question.Url);
            Assert.True(question.HasUnavailableLink);
            return;
        }
        Assert.Throws<JsonException>(() => GatewayQuestions.ParseRequest(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void OmittedOtherFlag_RestrictsAnswersToOptions_AndProtocolAllowsSixtyFourOptions()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Request)!;
        var question = node["questions"]![0]!.AsObject();
        question.Remove("isOther");
        question["options"] = JsonSerializer.SerializeToNode(
            Enumerable.Range(0, 64).Select(index => new { label = $"Choice {index}" }));
        var parsed = Assert.Single(GatewayQuestions.ParseRequest(JsonSerializer.SerializeToElement(node)).Questions);
        Assert.False(parsed.AllowOther);
        Assert.Equal(64, parsed.Options.Count);
    }

    [Fact]
    public void SecretDefaults_AreNotExposedAndSecretStoreRequiresDedicatedControls()
    {
        var json = Request.Replace("\"isOther\":false", """
            "isOther":true,"isSecret":true,"defaultAnswers":["never display"],
            "secretStore":{"name":"API_KEY","kind":"secret"}
            """);
        var question = Assert.Single(GatewayQuestions.ParseRequest(JsonDocument.Parse(json).RootElement).Questions);
        Assert.True(question.IsSecret);
        Assert.True(question.RequiresWebControl);
        Assert.Empty(question.DefaultAnswers);
    }

    [Fact]
    public void FormOptionsAndMultilineDefaults_AreSupported()
    {
        var json = Request.Replace("\"isOther\":false",
            "\"presentation\":\"form\",\"answerFormat\":\"lines\",\"allowEmpty\":true,\"defaultAnswers\":[\"win\"]");
        var question = Assert.Single(GatewayQuestions.ParseRequest(JsonDocument.Parse(json).RootElement).Questions);
        Assert.True(question.IsForm);
        Assert.True(question.AnswerAsLines);
        Assert.True(question.AllowEmpty);
        Assert.Equal("win", Assert.Single(question.DefaultAnswers));
    }
}
