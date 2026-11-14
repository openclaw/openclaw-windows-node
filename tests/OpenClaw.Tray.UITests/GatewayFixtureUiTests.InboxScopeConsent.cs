// Real mounted Inbox consent through supported plugin scope events and the existing decision RPC.
// Run GatewayFixtureUiTests.InboxScopeConsent_* in the ordinary fixture UI lane.
using System.Text.Json;
using System.Windows.Automation;
using ComUia = global::Interop.UIAutomationClient;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string InboxScopePluginDescription = "Review the complete fixture scope before deciding.";
    private const string InboxScopePluginDetail = "This fixture operation has a separate scoped command or audience.";
    private const string InboxScopeCommandTail = "SCOPE COMMAND END";
    private const string InboxScopeStandingSummary =
        "Always allow runs this exact command for \"fixture nightly\" without asking, for 3 days (revocable)";

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxScopeConsent_PageAndFlyoutExposeScopedCommandAndBothAudiencesBeforeAllow()
    {
        await WithAppAsync(async run =>
        {
            // Keep the positive sample inside the independent upstream scope.command 256-char
            // bound. Plain ASCII survives the real producer sanitizer unchanged; wrapping must
            // reveal its tail, rather than relying on raw newlines the producer would escape.
            var scopedCommand = string.Join(" && ", Enumerable.Range(1, 7).Select(index => $"echo fixture scope line {index:00}")) +
                " && echo " + InboxScopeCommandTail;
            Assert.InRange(scopedCommand.Length, 1, 256);
            Assert.DoesNotContain(InboxScopeCommandTail, InboxScopePluginDescription);
            Assert.DoesNotContain(InboxScopeCommandTail, InboxScopePluginDetail);
            var cases = new[]
            {
                (Name: "standing", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "standing-grant", automation = "fixture nightly", command = scopedCommand, expiresInDays = 3 }),
                    Disclosure: InboxScopeStandingSummary + "\nCommand:\n" + scopedCommand, Critical: InboxScopeCommandTail),
                (Name: "public", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "external-post", target = "fixture newsroom", visibility = "public" }),
                    Disclosure: "Posts externally to fixture newsroom\nVisibility: public", Critical: "Visibility: public"),
                (Name: "restricted", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "external-post", target = "fixture newsroom", visibility = "restricted" }),
                    Disclosure: "Posts externally to fixture newsroom\nVisibility: restricted", Critical: "Visibility: restricted"),
                (Name: "message-internal", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "message-send", target = "fixture mailroom", recipientCount = 2,
                        recipients = new[] { "first-reader@example.test", "last-reader@example.test" }, audience = "internal" }),
                    Disclosure: "Sends to 2 recipient(s) via fixture mailroom\nListed recipients:\nfirst-reader@example.test\nlast-reader@example.test\nAudience: internal",
                    Critical: "Audience: internal"),
                (Name: "message-external", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "message-send", target = "fixture mailroom", recipientCount = 2,
                        recipients = new[] { "first-reader@example.test", "last-reader@example.test" }, audience = "external" }),
                    Disclosure: "Sends to 2 recipient(s) via fixture mailroom\nListed recipients:\nfirst-reader@example.test\nlast-reader@example.test\nAudience: external",
                    Critical: "Audience: external"),
            };
            foreach (var context in new[] { "page", "flyout" })
            {
                await OpenInboxReviewAsync(run, flyout: context == "flyout");
                foreach (var item in cases)
                {
                    var id = $"fixture-inbox-plugin-scope-{context}-{item.Name}";
                    var title = $"Fixture scope {context} {item.Name}";
                    Assert.DoesNotContain(InboxScopeCommandTail, title);
                    var expectedDetails = title + "\n\n" + InboxScopePluginDescription + "\n\n" + InboxScopePluginDetail;
                    await PublishInboxScopeAsync(run, id, title, item.Scope);
                    await ExpandInboxReviewAsync(run, title);
                    await WaitUiAsync(run, () => FindInboxDisclosure(run, item.Disclosure) is not null &&
                        FindInboxDisclosure(run, expectedDetails) is not null,
                        $"complete mounted {context} scope and distinct plugin title/description/detail");
                    // COM GetText equality, selection endpoint equality, ScrollIntoView, visible
                    // ranges and viewport-contained rectangles. No row-model/MCP disclosure oracle.
                    await RevealInboxTextRangeAsync(run, expectedDetails, InboxScopePluginDetail);
                    if (item.Name.StartsWith("message-", StringComparison.Ordinal))
                    {
                        await RevealInboxTextRangeAsync(run, item.Disclosure, "last-reader@example.test");
                        await CaptureIfRequestedAsync(run, $"inbox-scope-{context}-{item.Name}-recipients.png");
                    }
                    await RevealInboxTextRangeAsync(run, item.Disclosure, item.Critical);
                    Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowAlways"));
                    Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "plugin.approval.resolve" && request.ApprovalId == id);
                    await CaptureIfRequestedAsync(run, $"inbox-scope-{context}-{item.Name}-disclosure.png");

                    Invoke(await RevealInboxDecisionAsync(run, "InboxApprovalAllowOnce"));
                    await AssertInboxDecisionAsync(run, id, "allow-once", title);
                }
            }
            var decisions = run.Gateway.Requests.Where(request => request.Method == "plugin.approval.resolve").ToArray();
            Assert.Equal(10, decisions.Length);
            Assert.All(decisions, request =>
            {
                Assert.Equal("allow-once", request.Decision);
                Assert.Equal("ok", request.Outcome);
            });
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "exec.approval.resolve" or "approval.resolve");
        }, scenario: GatewayScenario.CreateApprovalReview());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxScopeConsent_SecretOversizedAndMalformedScopeStayDenyOnlyWithoutDisclosureLeaks()
    {
        await WithAppAsync(async run =>
        {
            const string concealed = "s3cret42"; // Synthetic short credential: redaction must mask the whole value.
            const string malformedSecret = "m4lform42";
            const string suppressedTail = "OVERSIZED SCOPE TAIL";
            const string oversizedMarker = "[exec approval command exceeds display size limit; full text suppressed]";
            // Defense-in-depth wire inputs, not a claim that the sanitized/schema-checked
            // plugin producer emits raw credentials, oversized commands or extra scope fields.
            var cases = new[]
            {
                (Name: "secret", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "standing-grant", automation = "fixture nightly", command = "Authorization: Bearer " + concealed, expiresInDays = 3 }),
                    Disclosure: InboxScopeStandingSummary + "\nCommand:\nAuthorization: Bearer ***", Critical: "***"),
                (Name: "oversized", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "standing-grant", automation = "fixture nightly", command = new string('x', 256 * 1024 + 1) + suppressedTail, expiresInDays = 3 }),
                    Disclosure: InboxScopeStandingSummary + "\nCommand:\n" + oversizedMarker, Critical: "full text suppressed]"),
                (Name: "malformed", Scope: JsonSerializer.SerializeToElement(new
                    { kind = "external-post", target = "fixture newsroom", visibility = "public", undisclosed = "Authorization: Bearer " + malformedSecret }),
                    Disclosure: InboxDenyOnlyNote, Critical: "only Deny is available."),
            };
            foreach (var context in new[] { "page", "flyout" })
            {
                await OpenInboxReviewAsync(run, flyout: context == "flyout");
                foreach (var item in cases)
                {
                    var id = $"fixture-inbox-plugin-scope-{context}-{item.Name}";
                    var title = $"Fixture unsafe scope {context} {item.Name}";
                    // The fixture always sends allow-once + deny, even for these inputs. It cannot
                    // provide the UI's security result or reject Allow on the product's behalf.
                    await PublishInboxScopeAsync(run, id, title, item.Scope);
                    await ExpandInboxReviewAsync(run, title);
                    await WaitUiAsync(run, () => InboxReviewTextElements(run).Any(text => ReadInboxReviewText(text) == InboxDenyOnlyNote),
                        "mounted scope deny-only explanation");
                    Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowOnce"));
                    Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowAlways"));
                    if (item.Name == "malformed")
                    {
                        // Scope and decision note deliberately have the same explanation. Inspect
                        // both, rather than using the unique-text lookup intended for ordinary rows.
                        var explanations = InboxReviewTextElements(run).Where(text => ReadInboxReviewText(text) == InboxDenyOnlyNote).ToArray();
                        Assert.Equal(2, explanations.Length);
                        var bindings = explanations.Where(text => text.TryGetCurrentPattern(TextPattern.Pattern, out _))
                            .Select(text => BindNativeInboxText(run, text.GetRuntimeId())).ToArray();
                        var selectable = Assert.Single(bindings, binding => binding.Pattern.SupportedTextSelection !=
                            ComUia.SupportedTextSelection.SupportedTextSelection_None);
                        // Prove every meaningful character, excluding only provider-added document
                        // positions outside the independently checked explanation. Also read back
                        // actual selection endpoints, just as for ordinary disclosure ranges.
                        await RevealInboxTextRangeAsync(run, selectable, InboxDenyOnlyNote, InboxDenyOnlyNote);
                        GC.KeepAlive(bindings);
                    }
                    else
                    {
                        await WaitUiAsync(run, () => FindInboxDisclosure(run, item.Disclosure) is not null, "complete redacted/suppressed scope disclosure");
                        await RevealInboxTextRangeAsync(run, item.Disclosure, item.Critical);
                    }
                    await AssertInboxScopeHasNoAccessibleSecretsAsync(run, concealed, malformedSecret, suppressedTail);
                    Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "plugin.approval.resolve" && request.ApprovalId == id);
                    await CaptureIfRequestedAsync(run, $"inbox-scope-{context}-{item.Name}-safe-disclosure.png");
                    var deny = await RevealInboxDecisionAsync(run, "InboxApprovalDeny");
                    Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowOnce"));
                    Assert.Null(FindInboxReviewAction(run, "InboxApprovalAllowAlways"));
                    await AssertInboxScopeHasNoAccessibleSecretsAsync(run, concealed, malformedSecret, suppressedTail);
                    await CaptureIfRequestedAsync(run, $"inbox-scope-{context}-{item.Name}-deny-only.png");
                    Invoke(deny);
                    await AssertInboxDecisionAsync(run, id, "deny", title);
                }
            }
            var decisions = run.Gateway.Requests.Where(request => request.Method == "plugin.approval.resolve").ToArray();
            Assert.Equal(6, decisions.Length);
            Assert.All(decisions, request =>
            {
                Assert.Equal("deny", request.Decision);
                Assert.Equal("ok", request.Outcome);
            });
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "exec.approval.resolve" or "approval.resolve");
        }, scenario: GatewayScenario.CreateApprovalReview());
    }

    private static Task PublishInboxScopeAsync(GatewayFixtureRun run, string id, string title, JsonElement scope) =>
        run.Gateway.PublishPluginApprovalRequestedAsync(id, title, InboxScopePluginDetail, scope: scope);

    private static async Task AssertInboxScopeHasNoAccessibleSecretsAsync(GatewayFixtureRun run, params string[] forbidden)
    {
        AutomationElement? list = null;
        await WaitUiAsync(run, () => (list = FindInboxReviewList(run)) is not null, "mounted Inbox review list");
        var elements = list!.FindAll(TreeScope.Subtree, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
        Assert.InRange(elements.Length, 1, InboxAutomationNodeLimit);
        foreach (var element in elements)
        {
            // Check both rendered TextPattern documents and accessible metadata, including the
            // collapsed-header/expander names. Offscreen descendants are not exempt from redaction.
            var text = element.Current.Name + "\n" + element.Current.HelpText + "\n" + element.Current.ItemStatus;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                text += "\n" + ((TextPattern)pattern).DocumentRange.GetText(-1);
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                text += "\n" + ((ValuePattern)value).Current.Value;
            foreach (var secret in forbidden) Assert.DoesNotContain(secret, text);
        }
    }
}
