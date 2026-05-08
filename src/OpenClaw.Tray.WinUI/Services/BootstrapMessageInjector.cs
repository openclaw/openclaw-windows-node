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
    /// and tries to send it via the visible Send button — falling back to an
    /// Enter keypress. The message is encoded via JsonSerializer to prevent
    /// JS template/string injection.
    /// </summary>
    public static string BuildInjectionScript(string message)
    {
        var safeMsg = JsonSerializer.Serialize(message);
        return $$"""
        (function() {
            const msg = {{safeMsg}};
            const seen = new Set();

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
                return walk(root, r => {
                    const inputs = r.querySelectorAll(
                        'textarea, input[type="text"], input:not([type]), [contenteditable="true"], [role="textbox"]');
                    return Array.from(inputs).find(isUsableInput) || null;
                });
            }

            function findButton(root) {
                seen.clear();
                return walk(root, r => {
                    const buttons = r.querySelectorAll('button:not([disabled]), [role="button"]:not([aria-disabled="true"])');
                    return Array.from(buttons).find(btn => {
                        if (!isVisible(btn)) return false;
                        const text = (btn.textContent || '').toLowerCase();
                        const label = (btn.getAttribute('aria-label') || '').toLowerCase();
                        const title = (btn.getAttribute('title') || '').toLowerCase();
                        return text.includes('send') || label.includes('send') || title.includes('send') ||
                            text === '➤' || text === '↑' || btn.closest('form');
                    }) || null;
                });
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

            const inputCount = document.querySelectorAll('input,textarea,button,[contenteditable="true"],[role="textbox"]').length;
            console.log('[OpenClaw] Bootstrap probe controls=' + inputCount);
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

            const btn = findButton(document);
            if (btn) {
                btn.click();
                console.log('[OpenClaw] Bootstrap message sent via button click');
                return 'sent';
            }

            const form = input.closest && input.closest('form');
            if (form?.requestSubmit) {
                form.requestSubmit();
                console.log('[OpenClaw] Bootstrap message sent via form submit');
                return 'sent';
            }

            input.dispatchEvent(new KeyboardEvent('keydown', {
                key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true
            }));
            console.log('[OpenClaw] Bootstrap message sent via Enter key');
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
    /// Returns true if the script ran successfully; the gate flag is flipped on
    /// any successful execution to prevent spam. Returns false if the gate is
    /// already consumed, the executor is null, or the call threw.
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
