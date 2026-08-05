using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class SessionListProtocolTests
{
    [Fact]
    public void RequestDto_UsesExactStableWireNames_AndOmitsAbsentFields()
    {
        var request = new SessionListRequest
        {
            AgentId = "main",
            Limit = 100,
            Offset = 200,
            Search = "needle",
            ConfiguredAgentsOnly = true,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
            ["agentId", "limit", "offset", "search", "configuredAgentsOnly"],
            names);
        Assert.Equal(["limit"], JsonDocument.Parse(
            JsonSerializer.Serialize(new SessionListRequest { Limit = 100 }))
            .RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Null(new SessionListRequest { Limit = 100 }.ToLegacyParameters());
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(
            new SessionListRequest { AgentId = "main", Limit = 100 }.ToLegacyParameters()));
        Assert.Equal(["agentId"], legacy.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void ResultDto_UsesExactStableMetadataWireNames()
    {
        var result = new SessionListResult
        {
            Sessions = [new SessionInfo { Key = "agent:main:1" }],
            Count = 1,
            TotalCount = 2,
            LimitApplied = 100,
            Offset = 0,
            NextOffset = 1,
            HasMore = true,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
            ["sessions", "count", "totalCount", "limitApplied", "offset", "nextOffset", "hasMore"],
            names);
    }

    [Fact]
    public void ResultParser_ToleratesAbsentAndAdditiveMetadata()
    {
        using var identity = new TempDirectory("session-list-parser-");
        var client = new OpenClawGatewayClient(
            "ws://localhost:18789",
            "token",
            identityPath: identity.Path);
        using var document = JsonDocument.Parse("""
            {
              "sessions": [{ "key": "agent:main:1", "label": "One" }],
              "count": 1,
              "futureMetadata": { "ignored": true }
            }
            """);

        var result = client.ParseSessionListResult(document.RootElement);

        Assert.Equal(1, result.Count);
        Assert.Null(result.TotalCount);
        Assert.Null(result.NextOffset);
        Assert.Equal("One", Assert.Single(result.Sessions).Label);
        client.Dispose();
    }

    [Fact]
    public void LegacyResultParser_CapsUnboundedRowsAtTwoThousand()
    {
        using var identity = new TempDirectory("session-list-legacy-cap-");
        var client = new OpenClawGatewayClient(
            "ws://localhost:18789",
            "token",
            identityPath: identity.Path);
        var rows = Enumerable.Range(0, 2001)
            .Select(index => new { key = $"agent:main:{index}" })
            .ToArray();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { sessions = rows }));

        var result = client.ParseSessionListResult(document.RootElement, isLegacyResponse: true);

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions, result.Sessions.Count);
        client.Dispose();
    }

    [Fact]
    public void LegacyArrayResultParser_StopsBeforeMalformedRowBeyondCap()
    {
        using var identity = new TempDirectory("session-list-legacy-array-cap-");
        var client = new OpenClawGatewayClient(
            "ws://localhost:18789",
            "token",
            identityPath: identity.Path);
        var rows = Enumerable.Range(0, SessionQueryCoordinator.MaximumMaterializedSessions)
            .Select(index => (object)new { key = $"agent:main:{index}", label = $"Session {index}" })
            .ToList();
        rows.Add(new { key = "agent:main:after-cap", model = 123 });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { sessions = rows }));

        var result = client.ParseSessionListResult(document.RootElement, isLegacyResponse: true);

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions, result.Sessions.Count);
        Assert.DoesNotContain(result.Sessions, session => session.Key == "agent:main:after-cap");
        client.Dispose();
    }

    [Fact]
    public void LegacyObjectResultParser_StopsBeforeMalformedRowBeyondCap()
    {
        using var identity = new TempDirectory("session-list-legacy-object-cap-");
        var client = new OpenClawGatewayClient(
            "ws://localhost:18789",
            "token",
            identityPath: identity.Path);
        var rows = Enumerable.Range(0, SessionQueryCoordinator.MaximumMaterializedSessions)
            .ToDictionary(
                index => $"agent:main:{index}",
                index => (object)new { label = $"Session {index}" });
        rows.Add("agent:main:after-cap", new { model = 123 });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { sessions = rows }));

        var result = client.ParseSessionListResult(document.RootElement, isLegacyResponse: true);

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions, result.Sessions.Count);
        Assert.DoesNotContain(result.Sessions, session => session.Key == "agent:main:after-cap");
        client.Dispose();
    }

    [Fact]
    public void ExpandedResultParser_IsNotCappedByLegacyLimit()
    {
        using var identity = new TempDirectory("session-list-expanded-cap-");
        var client = new OpenClawGatewayClient(
            "ws://localhost:18789",
            "token",
            identityPath: identity.Path);
        var rows = Enumerable.Range(0, SessionQueryCoordinator.MaximumMaterializedSessions + 1)
            .Select(index => new { key = $"agent:main:{index}", label = $"Session {index}" });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { sessions = rows }));

        var result = client.ParseSessionListResult(document.RootElement);

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions + 1, result.Sessions.Count);
        client.Dispose();
    }

    [Theory]
    [InlineData("invalid sessions.list params: unexpected property limit")]
    [InlineData("unknown parameter: \"offset\"")]
    [InlineData("Unexpected property 'search'.")]
    [InlineData("unknown parameter `configuredAgentsOnly`")]
    public void LegacyFallbackMatcher_AcceptsExactSentExpandedField(string message)
    {
        var request = new SessionListRequest
        {
            Limit = 100,
            Offset = 0,
            Search = "needle",
            ConfiguredAgentsOnly = true,
        };

        Assert.True(OpenClawGatewayClient.IsLegacySessionListParameterError(
            new GatewayRequestException("INVALID_REQUEST", message),
            request));
    }

    [Theory]
    [InlineData("unknown parameter value for search")]
    [InlineData("unknown parameter archived")]
    [InlineData("unexpected property searchValue")]
    [InlineData("validation failed for unexpected search property")]
    public void LegacyFallbackMatcher_RejectsNonFieldDiagnostics(string message)
    {
        var request = new SessionListRequest { Search = "needle" };

        Assert.False(OpenClawGatewayClient.IsLegacySessionListParameterError(
            new GatewayRequestException("INVALID_REQUEST", message),
            request));
    }

    [Fact]
    public void LegacyFallbackMatcher_RejectsExpandedFieldThatWasNotSent()
    {
        Assert.False(OpenClawGatewayClient.IsLegacySessionListParameterError(
            new GatewayRequestException("INVALID_REQUEST", "unexpected property search"),
            new SessionListRequest { Limit = 100 }));
    }
}
