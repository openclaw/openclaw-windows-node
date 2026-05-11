using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Injects the first-run "BOOTSTRAP.md" kickoff message into the chat WebView2
/// exactly once after onboarding completes.
///
/// Originally lived inline on <see cref="OpenClawTray.Onboarding.OnboardingWindow"/> and
/// was wired to the (now-removed) in-wizard chat preview page. PR #274 dropped that
/// page from the page order, leaving the kickoff dead. This shared service can be
/// invoked from any chat first-show site (onboarding chat overlay, HubWindow chat
/// page, etc.).
///
/// Gating: <see cref="SettingsManager.HasInjectedFirstRunBootstrap"/> is the
/// persistent one-shot flag. Once set, no further injection is performed.
///
/// This service deliberately does NOT depend on <c>Microsoft.Web.WebView2</c>
/// directly — callers pass in a <see cref="ScriptExecutor"/> delegate that
/// wraps <c>CoreWebView2.ExecuteScriptAsync</c>. This keeps the gate logic
/// testable without bringing WebView2 into the test project.
/// </summary>
public static class BootstrapMessageInjector
{
    private static int s_inFlight;

    /// <summary>
    /// Delegate matching <c>CoreWebView2.ExecuteScriptAsync(string)</c>.
    /// Returns the JSON-serialized result of the script (unused here).
    /// </summary>
    public delegate Task<string> ScriptExecutor(string script);

    /// <summary>
    /// The kickoff message sent to the agent on first run. Mirrors the macOS
    /// behavior (maybeKickoffOnboardingChat) and references BOOTSTRAP.md.
    /// </summary>
    public const string Message =
        "Hi! I just installed OpenClaw and you're my brand-new agent. " +
        "Please start the first-run ritual from BOOTSTRAP.md, ask one question at a time, " +
        "and before we talk about WhatsApp/Telegram, visit soul.md with me to craft SOUL.md: " +
        "ask what matters to me and how you should be. Then guide me through choosing " +
        "how we should talk (web-only, WhatsApp, or Telegram).";

    /// <summary>
    /// Builds the JS payload that locates the chat input using broad composer
    /// primitives (including open shadow roots), injects the message, and tries
    /// to send it via the input's own form/composer controls. The message is
    /// encoded via JsonSerializer to prevent JS template/string injection.
    /// </summary>
    public static string BuildInjectionScript(string message)
    {
        var safeMsg = JsonSerializer.Serialize(message);
        return $$"""
        (function() {
            const msg = {{safeMsg}};

            function isVisible(el) {
                return !!(el && (el.offsetWidth || el.offsetHeight || el.getClientRects().length));
            }

            function allCandidateElements(selectors) {
                const found = [];
                const roots = [document];
                const seen = new Set();
                while (roots.length) {
                    const root = roots.shift();
                    if (!root || seen.has(root)) continue;
                    seen.add(root);
                    for (const selector of selectors) {
                        try {
                            found.push(...Array.from(root.querySelectorAll(selector)));
                        } catch { }
                    }
                    for (const el of Array.from(root.querySelectorAll('*'))) {
                        if (el.shadowRoot) roots.push(el.shadowRoot);
                    }
                }
                return found;
            }

            function isEditable(el) {
                return !!(el && isVisible(el) && !el.disabled && !el.readOnly &&
                    el.getAttribute('aria-disabled') !== 'true');
            }

            function findInput() {
                const selectors = [
                    'textarea:not([disabled])',
                    'input[type="text"]:not([disabled])',
                    'input:not([type]):not([disabled])',
                    '[contenteditable="true"]',
                    '[role="textbox"]'
                ];
                return allCandidateElements(selectors).find(isEditable) || null;
            }

            function findSendButton() {
                const selectors = [
                    'button.chat-send-btn[aria-label="Send message"]',
                    'button.chat-send-btn[title="Send"]',
                    'button[aria-label="Send message"]',
                    'button[title="Send"]',
                    'button[aria-label*="Send" i]',
                    'button[title*="Send" i]',
                    'button[type="submit"]'
                ];
                return allCandidateElements(selectors)
                    .find(el => isVisible(el) && !el.disabled && el.getAttribute('aria-disabled') !== 'true') || null;
            }

            function setNativeValue(el, value) {
                if (el.isContentEditable || el.getAttribute('contenteditable') === 'true') {
                    el.textContent = value;
                    return;
                }
                const proto = el.tagName === 'TEXTAREA'
                    ? window.HTMLTextAreaElement.prototype
                    : window.HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                setter ? setter.call(el, value) : (el.value = value);
            }

            function getRenderedValue(el) {
                if (el.isContentEditable || el.getAttribute('contenteditable') === 'true') {
                    return el.innerText || el.textContent || '';
                }
                return el.value || '';
            }

            const input = findInput();
            if (!input) {
                console.warn('[OpenClaw] Could not find chat input for bootstrap');
                return 'no-input';
            }

            input.focus();
            setNativeValue(input, msg);
            try {
                input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: msg }));
            } catch {
                input.dispatchEvent(new Event('input', { bubbles: true }));
            }
            input.dispatchEvent(new Event('change', { bubbles: true }));

            if (getRenderedValue(input) !== msg) {
                console.warn('[OpenClaw] Bootstrap message did not render in composer');
                return 'not-rendered';
            }

            const form = input.closest ? input.closest('form') : null;
            if (form && typeof form.requestSubmit === 'function') {
                try {
                    form.requestSubmit();
                    console.log('[OpenClaw] Bootstrap message submitted via composer form');
                    return 'sent';
                } catch { }
            }

            const button = findSendButton();
            if (!button) {
                console.warn('[OpenClaw] Bootstrap message rendered, but send button was not found');
                return 'rendered';
            }

            button.click();
            console.log('[OpenClaw] Bootstrap message submitted via chat send button');
            return 'sent';
        })();
        """;
    }

    /// <summary>
    /// Should the gate allow an injection right now? Returns true only if the
    /// settings flag has not yet been set.
    /// </summary>
    public static bool ShouldInject(SettingsManager settings)
    {
        if (settings is null) return false;
        return !settings.HasInjectedFirstRunBootstrap;
    }

    /// <summary>
    /// Marks the gate as consumed and persists. Public so the rare external
    /// caller (e.g. a test, or a code path that injects via a different
    /// mechanism) can flip the gate without going through <see cref="InjectAsync"/>.
    /// </summary>
    public static void MarkInjected(SettingsManager settings)
    {
        if (settings is null) return;
        if (settings.HasInjectedFirstRunBootstrap) return;
        settings.HasInjectedFirstRunBootstrap = true;
        settings.Save();
    }

    /// <summary>
    /// Attempts to inject the bootstrap kickoff via <paramref name="executor"/>
    /// (typically a delegate wrapping <c>CoreWebView2.ExecuteScriptAsync</c>).
    /// Returns true only if the script confirmed that the message was accepted
    /// by the chat UI. The gate flag is not flipped for unverified sends so the
    /// next chat-tab visit can retry.
    /// </summary>
    public static async Task<bool> InjectAsync(
        ScriptExecutor? executor,
        SettingsManager settings,
        int initialDelayMs = 3000,
        CancellationToken cancellationToken = default)
    {
        if (settings is null) return false;
        if (!ShouldInject(settings)) return false;
        if (executor is null) return false;
        if (Interlocked.CompareExchange(ref s_inFlight, 1, 0) != 0)
        {
            Logger.Info("[BootstrapMessageInjector] Bootstrap injection skipped because another injection is in flight");
            return false;
        }

        try
        {
            if (initialDelayMs > 0)
            {
                await Task.Delay(initialDelayMs, cancellationToken).ConfigureAwait(true);
            }

            if (!ShouldInject(settings))
            {
                Logger.Info("[BootstrapMessageInjector] Bootstrap injection skipped because gate was consumed during initial delay");
                return false;
            }

            var js = BuildInjectionScript(Message);
            var result = await executor(js).ConfigureAwait(true);
            var status = TryParseScriptResult(result);
            if (string.Equals(status, "sent", StringComparison.Ordinal))
            {
                MarkInjected(settings);
                Logger.Info("[BootstrapMessageInjector] Bootstrap message injection sent");
                return true;
            }

            if (string.Equals(status, "rendered", StringComparison.Ordinal))
            {
                Logger.Warn("[BootstrapMessageInjector] Bootstrap message rendered in composer but was not confirmed sent; gate remains open");
                return false;
            }

            Logger.Warn($"[BootstrapMessageInjector] Bootstrap injection did not send; status={status ?? result ?? "<null>"}");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[BootstrapMessageInjector] Bootstrap injection failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref s_inFlight, 0);
        }
    }

    private static string? TryParseScriptResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return result;
        try
        {
            return JsonSerializer.Deserialize<string>(result);
        }
        catch (JsonException)
        {
            return result;
        }
    }
}
