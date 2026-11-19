// Strict pending-list admission for uncertain approval reconciliation.
// Unlike display-only parsing, invalid or partial data cannot establish absence.
using System.Text.Json;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class GatewayApprovalInventoryTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"approvals\":[]}")]
    public void CompleteEmptyInventoryCanEstablishAbsence(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(GatewayPendingApprovalParser.TryParseCompleteList(GatewayApprovalKind.Exec, document.RootElement, out var rows));
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"approvals\":null}")]
    [InlineData("{\"approvals\":[],\"hasMore\":true}")]
    [InlineData("{\"approvals\":[{\"id\":\"invalid\"}]}")]
    public void MalformedOrUnknownEnvelopeCannotEstablishAbsence(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(GatewayPendingApprovalParser.TryParseCompleteList(GatewayApprovalKind.Exec, document.RootElement, out _));
    }

    [Fact]
    public void DuplicateOrMalformedRowsDoNotBecomeAnApparentlyCompleteInventory()
    {
        var valid = new { id = "same", request = new { command = "echo hi" }, createdAtMs = 1, expiresAtMs = 2 };
        foreach (var payload in new[]
        {
            JsonSerializer.SerializeToElement(new object[] { valid, valid }),
            JsonSerializer.SerializeToElement(new object[] { valid, new { id = "bad" } }),
        })
            Assert.False(GatewayPendingApprovalParser.TryParseCompleteList(GatewayApprovalKind.Exec, payload, out _));
        Assert.True(GatewayPendingApprovalParser.TryParseCompleteList(GatewayApprovalKind.Exec,
            JsonSerializer.SerializeToElement(new[] { valid }), out var rows));
        Assert.Equal("same", Assert.Single(rows).Id);
    }
}
