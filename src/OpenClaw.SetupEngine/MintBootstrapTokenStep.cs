using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class MintBootstrapTokenStep : SetupStep
{
    public override string Id => "mint-token";
    public override string DisplayName => "Mint bootstrap token";

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        // Token was already set by ConfigureGatewayStep
        if (string.IsNullOrWhiteSpace(ctx.SharedGatewayToken))
            return StepResult.Fail("No shared gateway token set by previous step");

        // Mint a bootstrap/QR token
        var env = new Dictionary<string, string>
        {
            ["OPENCLAW_GATEWAY_TOKEN"] = ctx.SharedGatewayToken
        };

        var mint = await ctx.Commands.RunInWslAsync(
            distro, $"{ctx.WslPathPrefix} && openclaw qr --json", TimeSpan.FromSeconds(30), env, ct);

        if (mint.ExitCode == 0 && !string.IsNullOrWhiteSpace(mint.Stdout))
        {
            // Parse bootstrap token from JSON output
            try
            {
                if (TryReadBootstrapToken(mint.Stdout.Trim(), out var bootstrapToken, out var source))
                {
                    ctx.BootstrapToken = bootstrapToken;
                    ctx.Logger.StateChange("bootstrap_token", null, "[SET]");
                    return StepResult.Ok($"Bootstrap token minted from {source}");
                }
            }
            catch (JsonException ex)
            {
                ctx.Logger.Warn($"Failed to parse QR JSON: {ex.Message}");
            }
        }

        ctx.Logger.Warn("QR/bootstrap token mint failed or did not return a bootstrapToken/setupCode");
        return StepResult.Fail("Could not mint bootstrap token; refusing to use the shared gateway token as bootstrap.");
    }

    internal static bool TryReadBootstrapToken(string json, out string? token, out string? source)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var propertyName in new[] { "bootstrapToken", "setupCode" })
        {
            if (doc.RootElement.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
            {
                token = property.GetString();
                source = propertyName;
                return true;
            }
        }

        token = null;
        source = null;
        return false;
    }
}
