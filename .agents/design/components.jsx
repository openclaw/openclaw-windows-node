// components.jsx — Pseudocode React that documents your UI patterns.
//
// These render live in the Design System canvas. Keep them small and honest:
// one component = one decision your team has already made. Style with the
// `ds-*` classes and CSS variables the design system generates from design.json
// (e.g. var(--color-accent), var(--space-4), var(--radius-md), var(--font-display)).
//
// Copilot reads this file to match the house style when it builds new UI.

// Primary action. One per view — it's where the eye should land.
// Padding mirrors WinUI's default ButtonPadding (Thickness 11,5,11,6 → CSS 5px 11px 6px);
// MinWidth 100px keeps short labels (OK / Connect) from looking cramped and gives
// side-by-side buttons a consistent footprint.
export function Button({ children = "Save changes", variant = "primary" }) {
  return (
    <button
      className={`ds-btn ds-btn-${variant}`}
      style={{ padding: "5px 11px 6px", minWidth: "100px" }}
    >
      {children}
    </button>
  );
}

// Text input with a label sitting above it. Labels are quiet; the field is the subject.
// Input padding mirrors WinUI's TextControlThemePadding (Thickness 10,5,6,6 → CSS 5px 6px 6px 10px)
// with TextControlThemeMinHeight 32px, so a field lines up with a Button on the same row.
export function Field({ label = "Gateway address", placeholder = "127.0.0.1:18789" }) {
  return (
    <label className="ds-field">
      <span className="ds-field-label">{label}</span>
      <input
        className="ds-input"
        placeholder={placeholder}
        style={{ padding: "5px 6px 6px 10px", minHeight: "32px" }}
      />
    </label>
  );
}

// Content card. Flat by default — a 1px hairline border, not a drop shadow.
// Padding mirrors the WinUI Gallery card (Padding 16,12 → CSS 12px 16px) with the
// `md` radius (8px) and a `line`-colored border. Never nest a card inside a card.
export function Card({ title = "Windows node", body = "Paired · 6 capabilities exposed over MCP." }) {
  return (
    <article className="ds-card" style={{ padding: "12px 16px" }}>
      <h3 className="ds-card-title">{title}</h3>
      <p className="ds-card-body">{body}</p>
      <a className="ds-link" href="#">Open report →</a>
    </article>
  );
}

// Status pill. Uses semantic color tokens, never a raw hex.
export function Badge({ children = "Connected", tone = "positive" }) {
  return <span className={`ds-badge ds-badge-${tone}`}>{children}</span>;
}

// Chat bubble. User messages sit on the right on the accent (white text); assistant
// messages sit on the left on a subtle surface with a hairline border. The `bubble`
// radius (16px) is intentionally friendlier than the 8/12px control radii, padding is 12/16, and the
// bubble caps at 720px. Mirrors the native timeline (OpenClawChatTimeline:
// CornerRadius 16, Thickness 16,12; user = AccentFillColorSecondary + TextOnAccent,
// assistant = SubtleFillColorSecondary + ControlStroke). One accent, and it still
// means "you" — the user's own words ride the system accent.
//
// Every bubble carries a muted timestamp footer. Assistant bubbles add two things the
// app already shows, inline in that footer: the context-usage readout — the same
// `used/context (pct%)` string ChatUsageFormatter produces (e.g. "12.5K/200K (6%)") —
// followed by a subtle Copy affordance (low-emphasis until hover). This mirrors the
// native timeline, where the footer reads timestamp · usage · actions. State/metadata
// stay muted so the message itself, not its chrome, holds the hierarchy.
function CopyGlyph() {
  // Minimal clipboard mark — font-independent stand-in for Fluent glyph \uE8C8.
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" aria-hidden="true"
      stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <rect x="9" y="9" width="11" height="11" rx="2" />
      <path d="M6 15H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v1" />
    </svg>
  );
}

// Assistant avatar. Native chat shows a 36×36 circle carrying the OpenClaw app icon on
// a subtle fill with a 1px hairline border (OpenClawChatTimeline: AssistantAvatar, bg
// SubtleFillColorTertiary, border ControlStrokeColorDefault, top-aligned, shown on the
// first entry of an agent turn). Here we stand in a font-independent sparkle mark in the
// muted foreground — swap for the real logo asset in-app.
function AssistantAvatar() {
  return (
    <div
      aria-hidden="true"
      style={{
        flex: "0 0 auto",
        width: "36px",
        height: "36px",
        borderRadius: "var(--radius-pill)",
        background: "var(--color-surface)",
        border: "1px solid var(--color-line)",
        color: "var(--color-muted)",
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "center",
      }}
    >
      <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M12 2.5l1.7 5.1a4 4 0 0 0 2.7 2.7l5.1 1.7-5.1 1.7a4 4 0 0 0-2.7 2.7L12 21.5l-1.7-5.1a4 4 0 0 0-2.7-2.7L2.5 12l5.1-1.7a4 4 0 0 0 2.7-2.7L12 2.5z" />
      </svg>
    </div>
  );
}

export function ChatBubble({
  role = "assistant",
  time = "2:14 PM",
  usage = "12.5K/200K (6%)",
  children = "Paired to the gateway — 6 Windows node capabilities are live.",
}) {
  const isUser = role === "user";
  const content = (
    <div style={{ display: "flex", flexDirection: "column", alignItems: isUser ? "flex-end" : "flex-start", gap: "var(--space-1)" }}>
      <div
        style={{
          maxWidth: "min(80%, 520px)",   // caps at 720px in the app; tighter here to read as a bubble
          padding: "var(--space-3) var(--space-4)",
          borderRadius: "var(--radius-bubble)",
          fontFamily: "var(--font-body)",
          fontSize: "14px",
          lineHeight: "20px",
          background: isUser ? "var(--color-accent)" : "var(--color-surface)",
          color: isUser ? "var(--color-accentInk)" : "var(--color-ink)",
          border: isUser ? "none" : "1px solid var(--color-line)",
        }}
      >
        {children}
      </div>
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: "var(--space-2)",
          padding: "0 var(--space-2)",
          fontFamily: "var(--font-body)",
          fontSize: "12px",
          lineHeight: "16px",
          color: "var(--color-muted)",
        }}
      >
        <span>{time}</span>
        {!isUser && usage && (
          <>
            <span aria-hidden="true">·</span>
            <span>{usage}</span>
            <button
              type="button"
              title="Copy"
              aria-label="Copy message"
              style={{
                display: "inline-flex",
                alignItems: "center",
                justifyContent: "center",
                width: "20px",
                height: "20px",
                padding: 0,
                border: "none",
                borderRadius: "var(--radius-sm)",
                background: "transparent",
                color: "var(--color-muted)",
                opacity: 0.65,   // subtle at rest; the app raises it on hover
                cursor: "pointer",
                transform: "translateY(2px)",   // nudge down to sit on the caption baseline
              }}
            >
              <CopyGlyph />
            </button>
          </>
        )}
      </div>
    </div>
  );

  if (isUser) {
    return <div style={{ display: "flex", justifyContent: "flex-end" }}>{content}</div>;
  }

  // Assistant: avatar sits to the LEFT of the bubble, top-aligned (matches native's
  // 8px bubbleSideMargin gap).
  return (
    <div style={{ display: "flex", alignItems: "flex-start", gap: "var(--space-2)" }}>
      <AssistantAvatar />
      {content}
    </div>
  );
}

// Chat input. A rounded surface (md radius / 8px) with a 1px hairline border wrapping a
// borderless, transparent, auto-growing textarea (min-height 56 → max-height 200, then it
// scrolls) over a toolbar row. Mirrors OpenClawComposer chrome (CornerRadius 8,
// transparent TextBox, MinHeight 56 / MaxHeight 200) and the GitHub Copilot composer
// layout: left cluster = Add, model picker, reasoning-effort picker; right cluster = mic
// + Send. Only Send spends the accent ("act here"); every other control stays subtle so
// the toolbar reads as quiet chrome, not a row of competing buttons.
function ComposerIconButton({ label, primary = false, children }) {
  const [hover, setHover] = React.useState(false);
  const [pressed, setPressed] = React.useState(false);
  // WinUI SubtleButtonStyle: transparent at rest, SubtleFillColorSecondary on hover,
  // SubtleFillColorTertiary on press, and the glyph darkens to primary text on hover.
  const bg = primary
    ? "var(--color-accent)"
    : pressed
    ? "var(--color-subtlePressed)"
    : hover
    ? "var(--color-subtleHover)"
    : "transparent";
  const fg = primary ? "var(--color-accentInk)" : hover ? "var(--color-ink)" : "var(--color-muted)";
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => { setHover(false); setPressed(false); }}
      onMouseDown={() => setPressed(true)}
      onMouseUp={() => setPressed(false)}
      style={{
        display: "inline-flex",
        alignItems: "center",
        justifyContent: "center",
        width: "32px",
        height: "32px",
        padding: 0,
        borderRadius: "var(--radius-sm)",
        cursor: "pointer",
        border: primary ? "none" : "1px solid transparent",
        background: bg,
        color: fg,
        transition: "background 120ms ease-out, color 120ms ease-out",
      }}
    >
      {children}
    </button>
  );
}

// Subtle inline picker — text label + chevron, no border until hover in the app. Used
// for the model and reasoning-effort selectors; reads as a quiet dropdown, not a button.
// Same SubtleButtonStyle hover/press treatment as the icon buttons.
function ComposerPicker({ label }) {
  const [hover, setHover] = React.useState(false);
  const [pressed, setPressed] = React.useState(false);
  const bg = pressed
    ? "var(--color-subtlePressed)"
    : hover
    ? "var(--color-subtleHover)"
    : "transparent";
  return (
    <button
      type="button"
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => { setHover(false); setPressed(false); }}
      onMouseDown={() => setPressed(true)}
      onMouseUp={() => setPressed(false)}
      style={{
        display: "inline-flex",
        alignItems: "center",
        gap: "var(--space-1)",
        height: "32px",
        padding: "0 var(--space-2)",
        borderRadius: "var(--radius-sm)",
        border: "1px solid transparent",
        background: bg,
        color: hover ? "var(--color-ink)" : "var(--color-muted)",
        fontFamily: "var(--font-body)",
        fontSize: "13px",
        lineHeight: "18px",
        cursor: "pointer",
        whiteSpace: "nowrap",
        transition: "background 120ms ease-out, color 120ms ease-out",
      }}
    >
      <span>{label}</span>
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" aria-hidden="true"
        stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M6 9l6 6 6-6" />
      </svg>
    </button>
  );
}

export function ChatComposer({
  placeholder = "Message OpenClaw…",
  model = "Claude Opus 4.8",
  effort = "High",
}) {
  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        gap: "var(--space-2)",
        padding: "var(--space-2)",
        borderRadius: "var(--radius-md)",
        background: "var(--color-surface)",
        border: "1px solid var(--color-line)",
      }}
    >
      <textarea
        placeholder={placeholder}
        rows={2}
        style={{
          minHeight: "56px",
          maxHeight: "200px",
          resize: "none",
          border: "none",
          outline: "none",
          background: "transparent",
          color: "var(--color-ink)",
          fontFamily: "var(--font-body)",
          fontSize: "14px",
          lineHeight: "20px",
          padding: "var(--space-2)",
        }}
      />
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: "var(--space-2)" }}>
        {/* Left cluster: add attachment, model, reasoning effort */}
        <div style={{ display: "flex", alignItems: "center", gap: "var(--space-1)", minWidth: 0 }}>
          <ComposerIconButton label="Add context">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" aria-hidden="true"
              stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M12 5v14M5 12h14" />
            </svg>
          </ComposerIconButton>
          <ComposerPicker label={model} />
          <ComposerPicker label={effort} />
        </div>
        {/* Right cluster: dictation + send */}
        <div style={{ display: "flex", alignItems: "center", gap: "var(--space-1)" }}>
          <ComposerIconButton label="Dictate">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" aria-hidden="true"
              stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="9" y="3" width="6" height="11" rx="3" />
              <path d="M5 11a7 7 0 0 0 14 0M12 18v3" />
            </svg>
          </ComposerIconButton>
          <ComposerIconButton label="Send" primary>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" aria-hidden="true"
              stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M12 19V5M5 12l7-7 7 7" />
            </svg>
          </ComposerIconButton>
        </div>
      </div>
    </div>
  );
}

// A short thread: the "does chat hang together" check — a user turn and an assistant
// turn (with its Copy affordance + context-usage readout), then the composer.
export function ChatThread() {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)", maxWidth: "560px" }}>
      <ChatBubble role="user" time="2:14 PM">Can you list the Windows node capabilities?</ChatBubble>
      <ChatBubble role="assistant" time="2:14 PM" usage="12.5K/200K (6%)">
        Paired to the gateway — 6 capabilities are live, including system.run and clipboard access.
      </ChatBubble>
      <ChatComposer />
    </div>
  );
}

// A representative screen composed from the pieces above. This is the
// "does it hang together" check for the whole system.
export function ExampleScreen() {
  return (
    <section className="ds-screen">
      <header className="ds-screen-head">
        <div>
          <p className="ds-eyebrow">Connection</p>
          <h2 className="ds-screen-title">OpenClaw Windows Hub</h2>
        </div>
        <Badge tone="positive">Connected</Badge>
      </header>

      <div className="ds-grid">
        <Card title="Gateway" body="Paired to OpenClawGateway · last seen just now." />
        <Card title="Windows node" body="6 capabilities exposed over MCP." />
      </div>

      <div className="ds-row">
        <Field label="Gateway address" placeholder="127.0.0.1:18789" />
        <Button>Connect</Button>
      </div>
    </section>
  );
}
