# Design principles — OpenClaw Windows Hub

> This is the prose companion to `design.json`. Designers write here; developers
> and Copilot read here. Keep it short enough that people actually read it.

## The one-line brief
A calm native Windows companion for your AI assistant. It should feel like a
quiet, trustworthy extension of Windows — Fluent surfaces, the user's own
accent color — not a web dashboard wearing a desktop window.

## Voice
- Clear, respectful, and native to Windows. Say what will happen and why.
- Explain pairing, permissions, and security plainly — never alarmist, never hype.
- No hype words ("supercharge", "seamless", "revolutionary", "effortless").

## Information hierarchy
1. **One subject per screen.** Decide what the screen is *for* and let that element win.
2. **Size and weight before color.** Establish the hierarchy in grayscale first; color is the last 10%.
3. **One accent.** `accent` = "act here" (selection, focus, primary action). If everything is accented, nothing is.
4. **Generous vertical rhythm.** Prefer the `space-5`/`space-6` steps between groups; let content breathe.

## Do
- Defer to Windows: theme off `SystemAccentColor` and Fluent resources so the app
  follows the user's accent, light/dark, and high-contrast settings.
- Use Segoe UI Variable for text and Cascadia Mono for commands, endpoints, tokens, and logs.
- Make connection state legible: connected/pairing/error read via `positive`/`warning`/`critical`.
- Keep motion to 120–200ms ease-out. Animate to explain, not to impress.

## Don't
- Don't hard-code hex in UI — it breaks in dark and high-contrast modes. Use tokens/theme resources.
- Don't put gray text on a colored background.
- Don't nest cards inside cards, or wrap every block in a card.
- Don't reach for bounce/elastic easing; it reads as dated and AI-generated.
- Don't fight the OS theme with custom-skinned controls or glassmorphism overload.

## Anti-references
If a mock could be mistaken for a neon-gradient SaaS landing page, or ignores the
user's system accent and light/dark preference — start over.
