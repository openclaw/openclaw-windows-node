using System.Text.Json;

namespace OpenClawTray.Windows;

internal static class WebChatVoiceDomBridge
{
    public const string DocumentCreatedScript = """
(() => {
  const isVisible = (el) => !!el && !(el.disabled === true) && el.getClientRects().length > 0;
  let desiredDraft = '';

  const findComposer = () => {
    const candidates = Array.from(document.querySelectorAll('textarea, input[type="text"], [contenteditable="true"], [contenteditable="plaintext-only"]'));
    return candidates.find(isVisible) || null;
  };

  const setElementValue = (el, value) => {
    const text = typeof value === 'string' ? value : '';
    if ('value' in el) {
      const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
      const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
      if (descriptor && descriptor.set) {
        descriptor.set.call(el, text);
      } else {
        el.value = text;
      }
      el.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return;
    }

    if (el.isContentEditable) {
      el.textContent = text;
      el.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
    }
  };

  const applyDraftIfPossible = () => {
    const composer = findComposer();
    if (!composer) return false;
    setElementValue(composer, desiredDraft);
    return true;
  };

  const clearLegacyTurnsHost = () => {
    const host = document.getElementById('openclaw-tray-voice-turns');
    if (host) {
      host.remove();
    }
  };

  const observer = new MutationObserver(() => applyDraftIfPossible());
  const start = () => {
    if (!document.body) return;
    observer.observe(document.body, { childList: true, subtree: true });
    applyDraftIfPossible();
    clearLegacyTurnsHost();
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }

  window.__openClawTrayVoice = {
    setDraft(text) {
      desiredDraft = text || '';
      return applyDraftIfPossible();
    },
    clearDraft() {
      desiredDraft = '';
      return applyDraftIfPossible();
    },
    setTurns() {
      clearLegacyTurnsHost();
      return true;
    }
    };
})();
""";

    public static string BuildSetDraftScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "window.__openClawTrayVoice?.clearDraft?.();";
        }

        return $"window.__openClawTrayVoice?.setDraft?.({JsonSerializer.Serialize(text)});";
    }

    public const string ClearLegacyTurnsScript = "window.__openClawTrayVoice?.setTurns?.([]);";
}
