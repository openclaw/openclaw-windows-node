using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenClaw.Shared.Tests.Protocol;

/// <summary>
/// Protocol drift guard for the OpenClaw gateway <c>sessions</c> / <c>files</c> /
/// <c>commands</c> / compaction surface.
///
/// The Windows companion can silently drift from the upstream
/// <c>openclaw/openclaw</c> gateway protocol
/// (<c>packages/gateway-protocol/src/schema/{sessions,commands}.ts</c>): because
/// the gateway tolerates unknown/extra fields, a client sending stale shapes fails
/// no existing test. This test pins a hand-maintained mirror of that schema in
/// <c>Protocol/gateway-protocol-snapshot.json</c> and cross-checks it against what
/// the typed client (<c>OpenClawGatewayClient</c> + its <c>.Protocol</c> partial,
/// and the <c>SessionPatch</c>/DTO builders in <c>GatewayProtocolModels.cs</c>)
/// actually <em>sends</em>, <em>handles</em>, and constructs — so any future
/// divergence fails loudly in CI instead of shipping a drifted client.
///
/// This complements (does not duplicate) the behavioural parser tests in
/// <c>GatewayProtocolModelsTests</c>: those parse sample JSON and assert DTO
/// mapping; this guard statically pins the method / request-field / response-
/// envelope <em>surface</em>, so an upstream rename or a dropped API is caught even
/// when the parser unit tests still pass on their fixed samples.
///
/// The guard is deterministic and fully repo-contained: it does NOT require a
/// clone of the upstream OpenClaw repository or any network access. When upstream
/// changes, refresh the snapshot per <c>docs/gateway-protocol-drift-guard.md</c>.
///
/// Extraction is intent-specific and resilient. Two views of each source are kept,
/// index-aligned: a <c>Code</c> view with comments blanked but string/char literals
/// preserved (used for literal/identifier extraction, so commented-out code never
/// counts), and a <c>Masked</c> view with comments AND literals blanked (used for
/// brace structure, so literal/comment braces never desync the matcher).
/// </summary>
public class GatewayProtocolDriftTests
{
    // Identifier characters for wire field/method names. Wide enough to cover
    // camelCase, digits, and underscores so a future field like `contextV2` or
    // `started_at` is representable on both sides of the comparison.
    private const string Ident = "[A-Za-z_][A-Za-z0-9_]*";

    // Dispatcher-call identifiers that are logging/diagnostic sinks, not RPC
    // dispatch. A method literal passed to one of these does not count as the
    // client "sending" that method (avoids a false "used").
    private static readonly HashSet<string> LoggingSinks = new(StringComparer.Ordinal)
    {
        "Warn", "Warning", "Info", "Information", "Error", "Debug", "Trace", "Log",
        "LogWarning", "LogError", "LogInformation", "LogDebug", "LogTrace", "WriteLine",
    };

    /// <summary>
    /// Guard A — method surface, intent-specific. Every in-scope method the client
    /// actually wires (request methods it dispatches with a method literal, plus
    /// notifications it handles via a <c>case</c> label) must be pinned in the
    /// snapshot. Every method pinned <c>used</c> must still be wired the way its
    /// <c>kind</c> implies. A <c>planned</c> method must not already be wired.
    /// </summary>
    [Fact]
    public void ScopedMethodSurface_matches_snapshot()
    {
        var snapshot = LoadSnapshot();
        var client = LoadClientSource();

        var sent = ExtractDispatchedRequestMethods(client, snapshot.ScopePrefixes).Keys.ToHashSet(StringComparer.Ordinal);
        var handled = ExtractHandledMethods(client.Code, snapshot.ScopePrefixes);
        var wired = sent.Union(handled).ToHashSet(StringComparer.Ordinal);

        var pinned = snapshot.Methods.Select(m => m.Method).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var method in wired.Except(pinned).OrderBy(s => s, StringComparer.Ordinal))
            failures.Add($"client wires '{method}' but it is NOT pinned in the snapshot (refresh the snapshot or fix a typo)");

        foreach (var method in snapshot.Methods)
        {
            var isWired = wired.Contains(method.Method);
            if (method.WindowsUsage == "used")
            {
                var ok = method.Kind == "request" ? sent.Contains(method.Method) : handled.Contains(method.Method);
                if (!ok)
                {
                    var how = method.Kind == "request" ? "dispatched with a method literal" : "handled via a 'case' label";
                    failures.Add($"'{method.Method}' is pinned windowsUsage=\"used\" ({method.Kind}) but is no longer {how} by the client (the client stopped using a method it should still send)");
                }
            }
            else if (method.WindowsUsage == "planned" && isWired)
            {
                failures.Add($"'{method.Method}' is pinned windowsUsage=\"planned\" but the client now wires it — flip it to \"used\" and verify/clear its response-field shape against upstream");
            }
        }

        if (failures.Count > 0)
            Assert.Fail("Gateway protocol method-surface drift detected:\n  - " + string.Join("\n  - ", failures) + "\nSee docs/gateway-protocol-drift-guard.md.");
    }

    /// <summary>
    /// Guard B — <c>sessions.list</c> response shape. The session fields the legacy
    /// session parser reads off the wire (scoped to the two session-parsing
    /// methods) must exactly equal the snapshot's pinned <c>responseFields</c>.
    /// </summary>
    [Fact]
    public void SessionsList_responseShape_matches_snapshot()
    {
        var snapshot = LoadSnapshot();
        var client = LoadClientSource();

        var pinned = snapshot.Methods.Single(m => m.Method == "sessions.list").ResponseFields.ToHashSet(StringComparer.Ordinal);

        var signatures = new[] { "void PopulateSessionFromObject(", "string? ParseSessionItem(" };
        var readByClient = new HashSet<string>(StringComparer.Ordinal);
        var missingSignatures = new List<string>();

        foreach (var sig in signatures)
        {
            var region = ExtractMethodBody(client, sig);
            if (region is null)
            {
                missingSignatures.Add(sig);
                continue;
            }

            foreach (Match m in Regex.Matches(region.Code, $"\\.TryGetProperty\\(\\s*\"({Ident})\""))
                readByClient.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(region.Code, $"(?:GetLong|GetString|GetBool|ParseUnixTimestampMs)\\(\\s*item\\s*,\\s*\"({Ident})\""))
                readByClient.Add(m.Groups[1].Value);
        }

        Assert.True(
            missingSignatures.Count == 0,
            $"Could not locate session-parsing method(s): [{string.Join(", ", missingSignatures)}]. " +
            "The extraction signatures in GatewayProtocolDriftTests likely need updating after a refactor (a test-maintenance issue, not protocol drift).");

        var clientNotPinned = readByClient.Except(pinned).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var pinnedNotRead = pinned.Except(readByClient).OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (clientNotPinned.Count > 0 || pinnedNotRead.Count > 0)
        {
            Assert.Fail(
                "sessions.list response shape drift detected.\n" +
                $"  Fields parsed by the client but NOT in the snapshot (upstream renamed/removed, or refresh the snapshot): [{string.Join(", ", clientNotPinned)}]\n" +
                $"  Fields pinned but no longer parsed by the client: [{string.Join(", ", pinnedNotRead)}]\n" +
                "See docs/gateway-protocol-drift-guard.md.");
        }
    }

    /// <summary>
    /// Guard C — request shapes, scoped and <b>bidirectional</b>. For each
    /// <c>used</c> request method, the set of wire keys the client actually
    /// constructs in the request-construction region (the explicit
    /// <c>requestFieldsSource</c> builder body when set, otherwise the enclosing
    /// block of an actual dispatch call) must exactly equal the pinned
    /// <c>requestFields</c>, modulo a per-method <c>allowedExtraRequestFields</c>
    /// allowlist:
    /// <list type="bullet">
    /// <item><b>missing</b> = pinned − constructed: the client no longer sends a
    /// field the schema expects (upstream rename/removal not reconciled, or stale
    /// snapshot).</item>
    /// <item><b>unexpected</b> = constructed − pinned − allowed: the client still
    /// sends a stale/extra key after upstream removed or renamed it — strict
    /// gateways (<c>additionalProperties:false</c>) reject the whole request.</item>
    /// </list>
    /// A constructed key is an anonymous-object member or dictionary string key in
    /// the region (see <see cref="ExtractConstructedKeys"/>); the comparison is over
    /// constructed keys, not token presence, so a rename where the old name survives
    /// as a local/parameter is still caught.
    /// </summary>
    [Fact]
    public void RequestParameterNames_arePresentInConstructionRegion()
    {
        var snapshot = LoadSnapshot();
        var client = LoadClientSource();
        var protocol = LoadProtocolSource();

        var sentIndices = ExtractDispatchedRequestMethods(client, snapshot.ScopePrefixes);
        var failures = new List<string>();

        foreach (var method in snapshot.Methods.Where(m => m.WindowsUsage == "used" && m.Kind == "request" && m.RequestFields.Count > 0))
        {
            var regions = new List<Region>();

            if (!string.IsNullOrEmpty(method.RequestFieldsSource))
            {
                var fragment = method.RequestFieldsSource!;
                var occurrences = CountOccurrences(protocol.Masked, fragment);
                if (occurrences == 0)
                {
                    failures.Add($"{method.Method}: requestFieldsSource '{fragment}' not found (refactor? update the snapshot/guard)");
                    continue;
                }
                if (occurrences > 1)
                {
                    failures.Add($"{method.Method}: requestFieldsSource '{fragment}' is ambiguous (matches {occurrences} sites) — make it unique");
                    continue;
                }
                var region = ExtractMethodBody(protocol, fragment);
                if (region is null)
                {
                    failures.Add($"{method.Method}: requestFieldsSource '{fragment}' did not resolve to a body");
                    continue;
                }
                if (ExtractConstructedKeys(region).Count == 0)
                {
                    failures.Add($"{method.Method}: requestFieldsSource '{fragment}' body constructs no request keys (new {{ … }} or [\"…\"]) — it is not a request builder");
                    continue;
                }
                regions.Add(region);
            }
            else
            {
                if (!sentIndices.TryGetValue(method.Method, out var indices) || indices.Count == 0)
                {
                    failures.Add($"{method.Method}: no dispatch call site found to validate request fields");
                    continue;
                }

                var degenerate = false;
                foreach (var idx in indices)
                {
                    var (region, isTypeBody) = ExtractEnclosingBlock(client, idx);
                    if (region is null) continue;
                    if (isTypeBody)
                    {
                        degenerate = true;
                        break;
                    }
                    regions.Add(region);
                }

                if (degenerate)
                {
                    failures.Add($"{method.Method}: dispatched from an expression-bodied member, so the construction region degrades to the whole type — add a 'requestFieldsSource' pointing at the real payload builder");
                    continue;
                }
                if (regions.Count == 0)
                {
                    failures.Add($"{method.Method}: could not resolve the enclosing block of any dispatch call site");
                    continue;
                }
            }

            var constructedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in regions)
                constructedKeys.UnionWith(ExtractConstructedKeys(r));

            // Bidirectional comparison (both directions are real drift):
            //   missing    = pinned fields the client no longer constructs (upstream
            //                rename/removal not reconciled, or stale snapshot).
            //   unexpected = wire keys the client still constructs that are NOT pinned
            //                and NOT an explicitly-allowed extra — i.e. the client
            //                sends a stale/extra key after upstream removed/renamed it.
            // allowedExtraRequestFields lets a method declare intentional extras (e.g.
            // a client-only param the strict gateway tolerates) without weakening the
            // check for everything else.
            var allowed = new HashSet<string>(method.AllowedExtraRequestFields, StringComparer.Ordinal);

            var missing = method.RequestFields
                .Where(f => !constructedKeys.Contains(f))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            var unexpected = constructedKeys
                .Where(k => !method.RequestFields.Contains(k) && !allowed.Contains(k))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            foreach (var field in missing)
                failures.Add($"{method.Method}.{field}: pinned but NOT constructed as a request wire key in the construction region (upstream rename not reconciled, or refresh the snapshot). Constructed keys: [{string.Join(", ", constructedKeys.OrderBy(s => s, StringComparer.Ordinal))}]");
            foreach (var key in unexpected)
                failures.Add($"{method.Method}.{key}: constructed as a request wire key but NOT pinned in the snapshot (client sends a stale/extra key after an upstream change — remove it, or add it to requestFields / allowedExtraRequestFields). Pinned: [{string.Join(", ", method.RequestFields.OrderBy(s => s, StringComparer.Ordinal))}]");
        }

        if (failures.Count > 0)
            Assert.Fail("Gateway request-parameter drift detected:\n  - " + string.Join("\n  - ", failures.OrderBy(s => s, StringComparer.Ordinal)) + "\nSee docs/gateway-protocol-drift-guard.md.");
    }

    /// <summary>
    /// Guard D — response envelope. For each <c>used</c> method that pins a
    /// <c>responseEnvelope</c> property, the client must read that property
    /// (<c>TryGetProperty("…")</c> or <c>TryGetArray(payload, "…")</c>) inside the
    /// method's own parser body (pinned via <c>responseEnvelopeSource</c>), so one
    /// parser reading the same property name cannot satisfy another method's
    /// envelope, and an upstream envelope rename forces client reconciliation. Runs
    /// over the comment-stripped view so commented-out reads don't count.
    /// </summary>
    [Fact]
    public void ResponseEnvelopes_areReadByClient()
    {
        var snapshot = LoadSnapshot();
        var client = LoadClientSource();

        var failures = new List<string>();
        foreach (var method in snapshot.Methods.Where(m => m.WindowsUsage == "used" && m.ResponseEnvelope.Count > 0))
        {
            // Symmetric with requestFieldsSource: a pinned envelope must name the
            // specific parser body, and that fragment must resolve uniquely — no
            // whole-client fallback (which would let any parser reading the same
            // property name satisfy a different method's envelope).
            if (string.IsNullOrEmpty(method.ResponseEnvelopeSource))
            {
                failures.Add($"{method.Method}: responseEnvelope is pinned but responseEnvelopeSource is missing — add the parser-body fragment so the envelope check is method-scoped");
                continue;
            }
            var occurrences = CountOccurrences(client.Masked, method.ResponseEnvelopeSource!);
            if (occurrences == 0)
            {
                failures.Add($"{method.Method}: responseEnvelopeSource '{method.ResponseEnvelopeSource}' not found (refactor? update the snapshot)");
                continue;
            }
            if (occurrences > 1)
            {
                failures.Add($"{method.Method}: responseEnvelopeSource '{method.ResponseEnvelopeSource}' is ambiguous (matches {occurrences} sites) — make it unique");
                continue;
            }
            var region = ExtractMethodBody(client, method.ResponseEnvelopeSource!);
            if (region is null)
            {
                failures.Add($"{method.Method}: responseEnvelopeSource '{method.ResponseEnvelopeSource}' did not resolve to a body");
                continue;
            }

            foreach (var envelope in method.ResponseEnvelope)
            {
                var pattern = $"TryGet(?:Property|Array)\\(\\s*(?:{Ident}\\s*,\\s*)?\"{Regex.Escape(envelope)}\"";
                if (!Regex.IsMatch(region.Code, pattern))
                    failures.Add($"{method.Method}: response envelope property \"{envelope}\" is never read (TryGetProperty/TryGetArray) by its parser");
            }
        }

        if (failures.Count > 0)
            Assert.Fail("Gateway response-envelope drift detected:\n  - " + string.Join("\n  - ", failures) + "\nSee docs/gateway-protocol-drift-guard.md.");
    }

    /// <summary>
    /// Guard E — snapshot integrity. The pinned snapshot itself must stay
    /// well-formed, keep covering the protocol-foundation shapes, and keep its
    /// honesty invariant (provisional response fields only on <c>planned</c>
    /// methods).
    /// </summary>
    [Fact]
    public void Snapshot_isWellFormed_andCoversFoundationShapes()
    {
        var snapshot = LoadSnapshot();

        Assert.NotEmpty(snapshot.Methods);
        Assert.NotEmpty(snapshot.ScopePrefixes);

        var validUsage = new HashSet<string>(StringComparer.Ordinal) { "used", "planned" };
        var validKind = new HashSet<string>(StringComparer.Ordinal) { "request", "response", "notification" };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in snapshot.Methods)
        {
            Assert.False(string.IsNullOrWhiteSpace(method.Method), "Snapshot method name must not be blank.");
            Assert.True(seen.Add(method.Method), $"Duplicate snapshot method entry: {method.Method}");
            Assert.True(validUsage.Contains(method.WindowsUsage),
                $"{method.Method}: invalid windowsUsage '{method.WindowsUsage}' (expected used|planned).");
            Assert.True(validKind.Contains(method.Kind),
                $"{method.Method}: invalid kind '{method.Kind}' (expected request|response|notification).");
            Assert.True(snapshot.ScopePrefixes.Any(p => method.Method.StartsWith(p, StringComparison.Ordinal)),
                $"{method.Method}: outside declared scopePrefixes [{string.Join(", ", snapshot.ScopePrefixes)}].");

            if (method.ProvisionalResponseFields)
                Assert.True(method.WindowsUsage == "planned",
                    $"{method.Method}: provisionalResponseFields=true is only allowed while windowsUsage=\"planned\". " +
                    "Verify the response fields against upstream and clear the flag before marking it used.");

            // Tri-state contract honesty: the semantics tag and the contract object
            // must co-exist, and a present contract must carry markers + states.
            var isTriStateNullable = method.RequestFieldSemantics == "tristate-nullable";
            Assert.True(isTriStateNullable == (method.TriState is not null),
                $"{method.Method}: requestFieldSemantics=\"tristate-nullable\" and a 'tristateContract' object must be present together (one without the other is a snapshot mistake).");
            if (method.TriState is not null)
            {
                Assert.NotEmpty(method.TriState.Markers);
                Assert.NotEmpty(method.TriState.StateMembers);
                Assert.False(string.IsNullOrWhiteSpace(method.TriState.BuilderSource), $"{method.Method}: tristateContract.builderSource must be set.");
                Assert.False(string.IsNullOrWhiteSpace(method.TriState.StateTypeSource), $"{method.Method}: tristateContract.stateTypeSource must be set.");
            }
        }

        foreach (var required in new[]
                 {
                     "commands.list",
                     "sessions.list", "sessions.patch", "sessions.compact",
                     "agents.files.list", "agents.files.get",
                     "sessions.files.list", "sessions.files.get",
                     "sessions.compaction.list", "sessions.compaction.get",
                     "sessions.compaction.branch", "sessions.compaction.restore",
                 })
            Assert.True(seen.Contains(required), $"Snapshot is missing required foundation method: {required}");

        Assert.NotEmpty(snapshot.Methods.Single(m => m.Method == "sessions.list").ResponseFields);
        Assert.NotEmpty(snapshot.Methods.Single(m => m.Method == "commands.list").ResponseEnvelope);
    }

    /// <summary>
    /// Guard F — tri-state clear contract (structural presence). Upstream
    /// <c>SessionsPatchParamsSchema</c> types every field as
    /// <c>Union([&lt;value&gt;, Null])</c>, where an explicit null removes a session
    /// override; the client models this with a tri-state
    /// <see cref="OpenClaw.Shared.PatchField{T}"/> (unset → omitted, value → sent
    /// with blank omitted, <c>SessionPatch.Clear</c> → explicit JSON null).
    ///
    /// This guard pins the <em>structural presence</em> of that machinery for any
    /// method declaring <c>requestFieldSemantics: "tristate-nullable"</c>: each
    /// snapshot marker (a regex over a specific, uniquely-resolving source body)
    /// must be present, and the <c>PatchField&lt;T&gt;</c> type must still publicly
    /// expose its state members. It deliberately does NOT prove routing
    /// <em>semantics</em> — a static presence check cannot see, e.g., a value sent
    /// when blank, nor the <c>unset → omit</c> behaviour (the absence of an else
    /// branch). Those, and the exact JSON emission for each state, are owned by the
    /// behavioural tests in <c>GatewayProtocolModelsTests</c>. Guard F is the
    /// complement that catches the machinery being removed/renamed/gutted.
    /// </summary>
    [Fact]
    public void TriStateClearContract_isWiredInClient()
    {
        var snapshot = LoadSnapshot();
        var protocol = LoadProtocolSource();

        var failures = new List<string>();
        foreach (var method in snapshot.Methods.Where(m => m.TriState is not null))
        {
            var contract = method.TriState!;

            // Always resolve builderSource (and reuse it for markers that don't pin
            // their own source) so it can never go stale/ambiguous unchecked, even
            // if every marker specifies an explicit source.
            if (!TryResolveUniqueBody(protocol, contract.BuilderSource, $"{method.Method}: builderSource", failures, out var builderRegion))
                builderRegion = null;

            // Each marker is checked inside its own uniquely-resolving source body
            // (defaulting to builderSource) — so e.g. AddString regressing is caught
            // even if AddEncoded is still correct.
            foreach (var marker in contract.Markers)
            {
                Region? region;
                if (string.IsNullOrEmpty(marker.Source))
                {
                    region = builderRegion;
                    if (region is null) continue; // builderSource already reported as failed
                }
                else if (!TryResolveUniqueBody(protocol, marker.Source!, $"{method.Method}: marker '{marker.Name}' source", failures, out region))
                {
                    continue;
                }
                if (!Regex.IsMatch(region!.Code, marker.Pattern))
                    failures.Add($"{method.Method}: tri-state contract marker '{marker.Name}' (/{marker.Pattern}/) is missing from '{(string.IsNullOrEmpty(marker.Source) ? contract.BuilderSource : marker.Source)}' — the tri-state routing drifted");
            }

            // The tri-state value type must still publicly expose all three states.
            if (TryResolveUniqueBody(protocol, contract.StateTypeSource, $"{method.Method}: stateTypeSource", failures, out var stateType))
            {
                foreach (var state in contract.StateMembers)
                {
                    // Require a public member declaration (not a bare token), tolerating
                    // common modifiers before the type so a future `public required T x`
                    // / `public static …` doesn't false-fail.
                    if (!Regex.IsMatch(stateType!.Code, $"public\\s+(?:(?:static|readonly|required|virtual|override|sealed|new)\\s+)*[A-Za-z_][\\w<>?]*\\s+{Regex.Escape(state)}\\b"))
                        failures.Add($"{method.Method}: tri-state value type '{contract.StateTypeSource}' no longer publicly exposes state '{state}'");
                }
            }
        }

        if (failures.Count > 0)
            Assert.Fail("Gateway tri-state clear-contract drift detected:\n  - " + string.Join("\n  - ", failures) + "\nSee docs/gateway-protocol-drift-guard.md.");
    }

    /// <summary>
    /// Resolves a snapshot source fragment to a unique method/type body, mirroring
    /// the uniqueness discipline of Guard C/D: a 0-match or &gt;1-match fragment is a
    /// loud failure (recorded in <paramref name="failures"/>) rather than a silent
    /// first-match bind.
    /// </summary>
    private static bool TryResolveUniqueBody(Source source, string fragment, string label, List<string> failures, out Region? region)
    {
        region = null;
        var occurrences = CountOccurrences(source.Masked, fragment);
        if (occurrences == 0)
        {
            failures.Add($"{label} '{fragment}' not found (refactor? update the snapshot)");
            return false;
        }
        if (occurrences > 1)
        {
            failures.Add($"{label} '{fragment}' is ambiguous (matches {occurrences} sites) — make it unique");
            return false;
        }
        region = ExtractMethodBody(source, fragment);
        if (region is null)
        {
            failures.Add($"{label} '{fragment}' did not resolve to a body");
            return false;
        }
        return true;
    }

    // --- request-key extraction ---------------------------------------------

    /// <summary>
    /// Extracts the request wire keys actually constructed in a region: the member
    /// names of anonymous-object payloads (<c>new { key, sessionKey = x }</c>), the
    /// string keys of dictionary payloads (<c>payload["key"] = …</c>,
    /// <c>new Dictionary&lt;…&gt; { ["key"] = … }</c>), and the key literal of an
    /// add-helper call whose first argument is the payload dictionary
    /// (<c>AddString(payload, "key", …)</c> / <c>AddEncoded(payload, "key", …)</c>,
    /// as the tri-state <c>SessionPatch.ToPayload</c> uses). Comparing pinned
    /// request fields against the ACTUAL constructed keys (not mere token presence)
    /// catches a client-side wire-key rename even when the old name survives as a
    /// local or parameter — e.g. <c>new { sessionKey = key }</c> yields key
    /// <c>sessionKey</c>, not <c>key</c>. Object initializers
    /// (<c>new TypeName { Prop = … }</c>) are intentionally excluded — only
    /// anonymous <c>new { … }</c> payloads count.
    /// </summary>
    private static HashSet<string> ExtractConstructedKeys(Region region)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        // Dictionary string keys: ["field"] — strings are preserved in the code view.
        foreach (Match m in Regex.Matches(region.Code, $"\\[\\s*\"({Ident})\"\\s*\\]"))
            keys.Add(m.Groups[1].Value);

        // Add-helper calls: Add*(payload, "field", …) — the key is the second arg
        // when the helper name starts with "Add" and the first arg is the payload
        // dictionary. This covers the tri-state builder (ToPayload delegates to
        // AddString/AddEncoded), while excluding unrelated 2-arg calls like
        // Validate(payload, "x") or string.Equals(x, "y") that would otherwise
        // inject spurious keys.
        foreach (Match m in Regex.Matches(region.Code, $"\\bAdd[A-Za-z0-9_]*\\(\\s*payload\\s*,\\s*\"({Ident})\""))
            keys.Add(m.Groups[1].Value);

        // Anonymous-object members: each `new {` block (no type name between `new`
        // and `{`). Parse the masked block so commas inside strings don't split.
        foreach (Match m in Regex.Matches(region.Masked, "new\\s*\\{"))
        {
            var open = region.Masked.IndexOf('{', m.Index);
            if (open < 0) continue;
            var close = MatchBrace(region.Masked, open);
            if (close < 0) continue;

            var inner = region.Masked.Substring(open + 1, close - open - 1);
            foreach (var segment in SplitTopLevel(inner))
            {
                var member = Regex.Match(segment.Trim(), $"^({Ident})");
                if (member.Success)
                    keys.Add(member.Groups[1].Value);
            }
        }

        return keys;
    }

    /// <summary>Splits on top-level commas, ignoring commas nested in (), [], or {}.</summary>
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return text.Substring(start, i - start);
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text.Substring(start);
    }

    // --- method-surface extraction -----------------------------------------

    /// <summary>
    /// Returns the in-scope request methods dispatched by the client, mapped to the
    /// char offsets of each dispatch call. A dispatch call is any non-logging method
    /// call whose first argument is a quote-bounded scoped method literal — e.g.
    /// <c>TrySendTrackedRequestAsync("sessions.patch", …)</c>,
    /// <c>TryRequestPayloadAsync("commands.list", …)</c>, or the private
    /// <c>MutateCompactionAsync("sessions.compaction.branch", …)</c> forwarder.
    /// This recognises new dispatcher wrappers automatically and excludes non-call
    /// uses (<c>case "…":</c>, <c>method is "…"</c>, log sentences) which is the
    /// false-"used" trap.
    /// </summary>
    private static Dictionary<string, List<int>> ExtractDispatchedRequestMethods(Source source, IReadOnlyList<string> scopePrefixes)
    {
        var scope = ScopeAlternation(scopePrefixes);
        var rx = new Regex($"(?<![A-Za-z0-9_])({Ident})\\s*\\(\\s*\"((?:{scope})[A-Za-z0-9_.]+)\"", RegexOptions.CultureInvariant);
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(source.Code))
        {
            var dispatcher = m.Groups[1].Value;
            if (LoggingSinks.Contains(dispatcher)) continue;
            var name = m.Groups[2].Value;
            if (!map.TryGetValue(name, out var list))
                map[name] = list = new List<int>();
            list.Add(m.Index);
        }
        return map;
    }

    /// <summary>Returns the in-scope methods handled by a <c>case "…":</c> label.</summary>
    private static HashSet<string> ExtractHandledMethods(string code, IReadOnlyList<string> scopePrefixes)
    {
        var scope = ScopeAlternation(scopePrefixes);
        var rx = new Regex($"case\\s*\"((?:{scope})[A-Za-z0-9_.]+)\"\\s*:", RegexOptions.CultureInvariant);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in rx.Matches(code))
            set.Add(m.Groups[1].Value);
        return set;
    }

    private static string ScopeAlternation(IReadOnlyList<string> scopePrefixes) =>
        string.Join("|", scopePrefixes.Select(p => Regex.Escape(p)));

    // --- block extraction ---------------------------------------------------

    /// <summary>
    /// Returns the body (including braces) of the first method/block whose
    /// declaration contains <paramref name="signatureFragment"/>, as both the
    /// comment-stripped code view and the fully-masked view (same indices). Brace
    /// matching uses the masked view. Returns null when the fragment is absent.
    /// </summary>
    private static Region? ExtractMethodBody(Source source, string signatureFragment)
    {
        var sigIndex = source.Masked.IndexOf(signatureFragment, StringComparison.Ordinal);
        if (sigIndex < 0) return null;

        var open = source.Masked.IndexOf('{', sigIndex);
        if (open < 0) return null;
        var close = MatchBrace(source.Masked, open);
        if (close < 0) return null;
        return Slice(source, open, close);
    }

    /// <summary>
    /// Returns the innermost <c>{ … }</c> block enclosing <paramref name="position"/>
    /// (both views) and a flag indicating whether that block is a type body rather
    /// than a method body — which happens when the dispatch call is in an
    /// expression-bodied member and the enclosing block degrades to the whole class.
    /// </summary>
    private static (Region? region, bool isTypeBody) ExtractEnclosingBlock(Source source, int position)
    {
        var masked = source.Masked;
        var depth = 0;
        var open = -1;
        for (var i = Math.Min(position, masked.Length) - 1; i >= 0; i--)
        {
            var c = masked[i];
            if (c == '}') depth++;
            else if (c == '{')
            {
                if (depth == 0) { open = i; break; }
                depth--;
            }
        }
        if (open < 0) return (null, false);

        var close = MatchBrace(masked, open);
        if (close < 0) return (null, false);

        var isTypeBody = IsTypeDeclarationBefore(masked, open);
        return (Slice(source, open, close), isTypeBody);
    }

    /// <summary>
    /// True when the <c>{</c> at <paramref name="openIndex"/> opens a type body —
    /// i.e. the nearest declaration keyword immediately preceding it is
    /// class/struct/record/interface <em>followed by a type name</em> (no
    /// intervening <c>;</c>/<c>{</c>/<c>}</c>). Requiring the type name avoids a
    /// false positive on a generic constraint such as <c>where T : class { … }</c>,
    /// where the keyword is not introducing a type declaration.
    /// </summary>
    private static bool IsTypeDeclarationBefore(string masked, int openIndex)
    {
        var start = Math.Max(0, openIndex - 400);
        var prefix = masked.Substring(start, openIndex - start);
        return Regex.IsMatch(prefix, $"\\b(class|struct|record|interface)\\s+{Ident}[^;{{}}]*$");
    }

    private static Region Slice(Source source, int open, int close) =>
        new(source.Code.Substring(open, close - open + 1), source.Masked.Substring(open, close - open + 1));

    private static int MatchBrace(string masked, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < masked.Length; i++)
        {
            if (masked[i] == '{') depth++;
            else if (masked[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // --- source preprocessing ----------------------------------------------

    /// <summary>
    /// Produces two index-aligned, equal-length views of <paramref name="source"/>:
    /// <c>Code</c> blanks comments only (string/char literals preserved), and
    /// <c>Masked</c> blanks comments AND string/char/verbatim/interpolated literals.
    /// Newlines are preserved in both. A single lexer pass guarantees the two views
    /// stay aligned. Precondition: interpolated-string holes in scanned regions do
    /// not contain a closing quote inside the hole; the scanned OpenClaw methods
    /// satisfy this.
    /// </summary>
    private static (string Code, string Masked) Preprocess(string source)
    {
        var code = new StringBuilder(source.Length);
        var masked = new StringBuilder(source.Length);
        var i = 0;

        void Emit(char c) { code.Append(c); masked.Append(c); }
        void Blank(int n) { code.Append(' ', n); masked.Append(' ', n); }            // comment: blank both
        void Keep(char c) { code.Append(c); masked.Append(' '); }                    // literal char: keep in Code, blank in Masked
        void NL() { code.Append('\n'); masked.Append('\n'); }

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') { if (source[i] == '\n') NL(); else Blank(1); i++; }
                continue;
            }
            if (c == '/' && next == '*')
            {
                Blank(2); i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                { if (source[i] == '\n') NL(); else Blank(1); i++; }
                if (i < source.Length) { Blank(2); i += 2; }
                continue;
            }
            if (c == '@' && next == '"')
            {
                Keep('@'); Keep('"'); i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { Keep('"'); Keep('"'); i += 2; continue; }
                        Keep('"'); i++; break;
                    }
                    if (source[i] == '\n') NL(); else Keep(source[i]);
                    i++;
                }
                continue;
            }
            if (c == '"' || (c == '$' && next == '"'))
            {
                if (c == '$') { Keep('$'); i++; }
                Keep('"'); i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\') { Keep('\\'); if (i + 1 < source.Length) Keep(source[i + 1]); i += 2; continue; }
                    if (source[i] == '"') { Keep('"'); i++; break; }
                    if (source[i] == '\n') NL(); else Keep(source[i]);
                    i++;
                }
                continue;
            }
            if (c == '\'')
            {
                Keep('\''); i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\') { Keep('\\'); if (i + 1 < source.Length) Keep(source[i + 1]); i += 2; continue; }
                    if (source[i] == '\'') { Keep('\''); i++; break; }
                    Keep(source[i]); i++;
                }
                continue;
            }

            if (c == '\n') NL(); else Emit(c);
            i++;
        }

        return (code.ToString(), masked.ToString());
    }

    // --- loading ------------------------------------------------------------

    /// <summary>The client dispatch surface: the OpenClawGatewayClient partial class files.</summary>
    private static Source LoadClientSource() =>
        ConcatSources(f => FileNameMatches(f.Path, "OpenClawGatewayClient", ".cs"),
            "Could not locate OpenClawGatewayClient*.cs under src/.");

    /// <summary>The client surface plus the DTO/payload builders (GatewayProtocolModels.cs).</summary>
    private static Source LoadProtocolSource() =>
        ConcatSources(
            f => FileNameMatches(f.Path, "OpenClawGatewayClient", ".cs") || EndsWith(f.Path, "GatewayProtocolModels.cs"),
            "Could not locate the gateway protocol source under src/.");

    private static Source ConcatSources(Func<SourceFileSnapshot, bool> predicate, string notFound)
    {
        var files = ProductionSourceFiles.All
            .Where(predicate)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        Assert.True(files.Count > 0, notFound);
        // Join with a newline so per-file brace balance is preserved across the
        // concatenation (each .cs is itself balanced, so blocks never span files).
        var text = string.Join("\n", files.Select(f => f.Text));
        var (code, masked) = Preprocess(text);

        // Self-check: a desynced masker (e.g. an unhandled string/raw-string form
        // leaving a brace "live") would make brace matching unreliable and could
        // produce false passes. The masked view must keep brace balance.
        var opens = masked.Count(ch => ch == '{');
        var closes = masked.Count(ch => ch == '}');
        Assert.True(opens == closes,
            $"Literal/comment masker desynced on the gateway protocol source (masked braces {opens} open vs {closes} close). " +
            "A new C# string form (e.g. a raw string literal) likely needs handling in Preprocess.");

        return new Source(code, masked);
    }

    private static bool FileNameMatches(string path, string prefix, string suffix)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static bool EndsWith(string path, string fileName) =>
        string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal);

    private static ProtocolSnapshot LoadSnapshot()
    {
        var path = Path.Combine(ProductionSourceFiles.FindRepoRoot(), "tests", "OpenClaw.Shared.Tests", "Protocol", "gateway-protocol-snapshot.json");
        Assert.True(File.Exists(path), $"Protocol snapshot fixture not found at {path}.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var scopePrefixes = root.GetProperty("scopePrefixes").EnumerateArray().Select(e => e.GetString()!).ToList();

        var methods = new List<ProtocolMethod>();
        foreach (var m in root.GetProperty("methods").EnumerateArray())
        {
            methods.Add(new ProtocolMethod(
                Method: m.GetProperty("method").GetString()!,
                Kind: m.GetProperty("kind").GetString()!,
                WindowsUsage: m.GetProperty("windowsUsage").GetString()!,
                RequestFields: ReadStringArray(m, "requestFields"),
                AllowedExtraRequestFields: ReadStringArray(m, "allowedExtraRequestFields"),
                ResponseFields: ReadStringArray(m, "responseFields"),
                ResponseEnvelope: ReadStringArray(m, "responseEnvelope"),
                RequestFieldsSource: m.TryGetProperty("requestFieldsSource", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                ResponseEnvelopeSource: m.TryGetProperty("responseEnvelopeSource", out var es) && es.ValueKind == JsonValueKind.String ? es.GetString() : null,
                RequestFieldSemantics: m.TryGetProperty("requestFieldSemantics", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null,
                TriState: ReadTriStateContract(m),
                ProvisionalResponseFields: m.TryGetProperty("provisionalResponseFields", out var p) && p.ValueKind == JsonValueKind.True));
        }

        return new ProtocolSnapshot(scopePrefixes, methods);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private static TriStateContract? ReadTriStateContract(JsonElement method)
    {
        if (!method.TryGetProperty("tristateContract", out var c) || c.ValueKind != JsonValueKind.Object)
            return null;

        var markers = new List<TriStateMarker>();
        if (c.TryGetProperty("markers", out var ms) && ms.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in ms.EnumerateArray())
            {
                markers.Add(new TriStateMarker(
                    Name: m.GetProperty("name").GetString()!,
                    Pattern: m.GetProperty("pattern").GetString()!,
                    Source: m.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : null));
            }
        }

        return new TriStateContract(
            BuilderSource: c.GetProperty("builderSource").GetString()!,
            Markers: markers,
            StateTypeSource: c.GetProperty("stateTypeSource").GetString()!,
            StateMembers: ReadStringArray(c, "stateMembers"));
    }

    private sealed record Source(string Code, string Masked);

    private sealed record Region(string Code, string Masked);

    private sealed record TriStateMarker(string Name, string Pattern, string? Source);

    private sealed record TriStateContract(
        string BuilderSource,
        IReadOnlyList<TriStateMarker> Markers,
        string StateTypeSource,
        IReadOnlyList<string> StateMembers);

    private sealed record ProtocolSnapshot(IReadOnlyList<string> ScopePrefixes, IReadOnlyList<ProtocolMethod> Methods);

    private sealed record ProtocolMethod(
        string Method,
        string Kind,
        string WindowsUsage,
        IReadOnlyList<string> RequestFields,
        IReadOnlyList<string> AllowedExtraRequestFields,
        IReadOnlyList<string> ResponseFields,
        IReadOnlyList<string> ResponseEnvelope,
        string? RequestFieldsSource,
        string? ResponseEnvelopeSource,
        string? RequestFieldSemantics,
        TriStateContract? TriState,
        bool ProvisionalResponseFields);
}
