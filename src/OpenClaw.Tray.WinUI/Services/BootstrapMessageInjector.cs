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
/// invoked from any chat-window first-show site (onboarding chat overlay, post-
/// wizard <c>App.ShowChatWindow()</c>, etc.).
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

            function findInput(root) {
                const inputs = root.querySelectorAll('textarea, input[type="text"]');
                for (const input of inputs) {
                    if (input.offsetParent !== null || input.offsetHeight > 0) return input;
                }
                const elements = root.querySelectorAll('*');
                for (const el of elements) {
                    if (el.shadowRoot) {
                        const found = findInput(el.shadowRoot);
                        if (found) return found;
                    }
                }
                return null;
            }

            function findButton(root) {
                const buttons = root.querySelectorAll('button');
                for (const btn of buttons) {
                    const text = (btn.textContent || '').toLowerCase();
                    const label = (btn.getAttribute('aria-label') || '').toLowerCase();
                    if (text.includes('send') || label.includes('send') ||
                        btn.querySelector('svg') && btn.closest('form')) {
                        return btn;
                    }
                }
                const elements = root.querySelectorAll('*');
                for (const el of elements) {
                    if (el.shadowRoot) {
                        const found = findButton(el.shadowRoot);
                        if (found) return found;
                    }
                }
                return null;
            }

            const input = findInput(document);
            if (input) {
                input.value = msg;
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));

                setTimeout(() => {
                    const btn = findButton(document);
                    if (btn) {
                        btn.click();
                        console.log('[OpenClaw] Bootstrap message sent via button click');
                    } else {
                        input.dispatchEvent(new KeyboardEvent('keydown', {
                            key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true
                        }));
                        console.log('[OpenClaw] Bootstrap message sent via Enter key');
                    }
                }, 200);
                return 'sent';
            } else {
                console.warn('[OpenClaw] Could not find chat input for bootstrap');
                return 'no-input';
            }
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
            await executor(js).ConfigureAwait(true);
            MarkInjected(settings);
            Logger.Info("[BootstrapMessageInjector] Bootstrap message injection executed");
            return true;
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
}
