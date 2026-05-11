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
    /// Builds the JS payload that locates the chat input (traversing shadow DOMs
    /// so it works against the Lit-based gateway chat UI), injects the message,
    /// and tries to send it via the input's own form/composer controls. The
    /// message is encoded via JsonSerializer to prevent JS template/string injection.
    /// </summary>
    public static string BuildInjectionScript(string message)
    {
        var safeMsg = JsonSerializer.Serialize(message);
        return $$"""
        (async function() {
            const msg = {{safeMsg}};
            const seen = new Set();
            const attempts = [0, 1500];
            const pollCount = 5;
            const pollDelayMs = 200;

            function walk(root, visit) {
                if (!root || seen.has(root)) return null;
                seen.add(root);
                const found = visit(root);
                if (found) return found;
                const elements = root.querySelectorAll ? root.querySelectorAll('*') : [];
                for (const el of elements) {
                    if (el.shadowRoot) {
                        const nested = walk(el.shadowRoot, visit);
                        if (nested) return nested;
                    }
                }
                return null;
            }

            function isVisible(el) {
                return !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
            }

            function isUsableInput(el) {
                return isVisible(el) && !el.disabled && !el.readOnly;
            }

            function findInput(root) {
                seen.clear();
                return walk(root, r => {
                    const inputs = r.querySelectorAll(
                        'textarea, input[type="text"], input:not([type]), [contenteditable="true"], [role="textbox"]');
                    return Array.from(inputs).find(isUsableInput) || null;
                });
            }

            function getRoot(el) {
                return (el && el.getRootNode && el.getRootNode()) || document;
            }

            function findForm(input) {
                if (!input) return null;
                if (input.form) return input.form;
                if (input.closest) {
                    const direct = input.closest('form');
                    if (direct) return direct;
                }

                const root = getRoot(input);
                const host = root && root.host;
                return host && host.closest ? host.closest('form') : null;
            }

            function isSendButton(btn) {
                if (!btn || !isVisible(btn) || btn.disabled || btn.getAttribute('aria-disabled') === 'true') return false;
                const text = (btn.textContent || '').trim().toLowerCase();
                const label = (btn.getAttribute('aria-label') || '').trim().toLowerCase();
                const title = (btn.getAttribute('title') || '').trim().toLowerCase();
                const type = (btn.getAttribute('type') || '').trim().toLowerCase();

                return type === 'submit' ||
                    label === 'send' || label === 'send message' || label.includes('send message') ||
                    title === 'send' || title === 'send message' || title.includes('send message') ||
                    text === 'send' || text === '➤' || text === '↑';
            }

            function findComposerContainer(input) {
                const selectors = [
                    'form',
                    '[role="form"]',
                    '[data-composer]',
                    '[data-testid*="composer" i]',
                    '[class*="composer" i]',
                    '[class*="chat-input" i]',
                    '[class*="message-input" i]'
                ];

                for (const selector of selectors) {
                    if (input.closest) {
                        const container = input.closest(selector);
                        if (container) return container;
                    }
                }

                return input.parentElement || getRoot(input);
            }

            function findSendButton(input) {
                const form = findForm(input);
                if (form) {
                    const formButton = Array.from(form.querySelectorAll('button:not([disabled]), [role="button"]:not([aria-disabled="true"])'))
                        .find(isSendButton);
                    if (formButton) return formButton;
                }

                const container = findComposerContainer(input);
                const roots = [container, getRoot(input)].filter(Boolean);
                for (const root of roots) {
                    const buttons = root.querySelectorAll
                        ? Array.from(root.querySelectorAll('button:not([disabled]), [role="button"]:not([aria-disabled="true"])'))
                        : [];
                    const button = buttons.find(isSendButton);
                    if (button) return button;
                }

                return null;
            }

            function setNativeValue(el, value) {
                if (el.isContentEditable) {
                    el.textContent = value;
                    return;
                }
                const proto = el.tagName === 'TEXTAREA'
                    ? window.HTMLTextAreaElement.prototype
                    : window.HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                setter ? setter.call(el, value) : (el.value = value);
            }

            function getInputValue(el) {
                if (!el) return '';
                if (el.isContentEditable) return el.textContent || '';
                return el.value || el.textContent || '';
            }

            function normalize(value) {
                return (value || '').replace(/\s+/g, ' ').trim();
            }

            function collectVisibleText(root) {
                if (!root || seen.has(root)) return '';
                seen.add(root);

                let text = '';
                if (root.nodeType === Node.TEXT_NODE) {
                    const parent = root.parentElement;
                    if (parent && isVisible(parent)) text += root.nodeValue || '';
                }

                const elements = root.querySelectorAll ? root.querySelectorAll('*') : [];
                for (const el of elements) {
                    if (!isVisible(el)) continue;
                    if (el.matches && el.matches('textarea,input,[contenteditable="true"],[role="textbox"]')) continue;
                    text += ' ' + (el.childNodes && Array.from(el.childNodes)
                        .filter(n => n.nodeType === Node.TEXT_NODE)
                        .map(n => n.nodeValue || '')
                        .join(' ') || '');
                    if (el.shadowRoot) text += ' ' + collectVisibleText(el.shadowRoot);
                }

                return text;
            }

            function messageAppearsInTranscript() {
                seen.clear();
                return normalize(collectVisibleText(document)).includes(normalize(msg));
            }

            function inputWasAccepted(input) {
                const value = normalize(getInputValue(input));
                return value.length === 0 || value !== normalize(msg);
            }

            function sleep(ms) {
                return new Promise(resolve => setTimeout(resolve, ms));
            }

            async function confirmAccepted(input) {
                for (let i = 0; i < pollCount; i++) {
                    if (inputWasAccepted(input) || messageAppearsInTranscript()) {
                        return true;
                    }
                    await sleep(pollDelayMs);
                }

                return false;
            }

            async function tryInjectOnce() {
                seen.clear();
                const input = findInput(document);
                if (!input) {
                    console.warn('[OpenClaw] Could not find chat input for bootstrap');
                    return 'no-input';
                }

                input.focus();
                setNativeValue(input, msg);
                input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: msg }));
                input.dispatchEvent(new Event('change', { bubbles: true }));

                const form = findForm(input);
                if (form?.requestSubmit) {
                    form.requestSubmit();
                    console.log('[OpenClaw] Bootstrap message submitted via composer form');
                    return await confirmAccepted(input) ? 'sent' : 'sent-unverified';
                }

                const btn = findSendButton(input);
                if (btn) {
                    btn.click();
                    console.log('[OpenClaw] Bootstrap message submitted via composer send button');
                    return await confirmAccepted(input) ? 'sent' : 'sent-unverified';
                }

                input.dispatchEvent(new KeyboardEvent('keydown', {
                    key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true
                }));

                if (await confirmAccepted(input)) {
                    console.log('[OpenClaw] Bootstrap message submitted via Enter key');
                    return 'sent';
                }

                console.warn('[OpenClaw] Bootstrap message not accepted by composer');
                return 'no-send-button';
            }

            const inputCount = document.querySelectorAll('input,textarea,button,[contenteditable="true"],[role="textbox"]').length;
            console.log('[OpenClaw] Bootstrap probe controls=' + inputCount);

            let lastStatus = 'no-input';
            for (const delay of attempts) {
                if (delay > 0) await sleep(delay);
                lastStatus = await tryInjectOnce();
                if (lastStatus !== 'no-input') return lastStatus;
            }

            return lastStatus;
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

        try
        {
            if (initialDelayMs > 0)
            {
                await Task.Delay(initialDelayMs, cancellationToken).ConfigureAwait(true);
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
