// components.jsx — Pseudocode React that documents your UI patterns.
//
// These render live in the Design System canvas. Keep them small and honest:
// one component = one decision your team has already made. Style with the
// `ds-*` classes and CSS variables the design system generates from design.json
// (e.g. var(--color-accent), var(--space-4), var(--radius-md), var(--font-display)).
//
// Copilot reads this file to match the house style when it builds new UI.

// Primary action. One per view — it's where the eye should land.
export function Button({ children = "Save changes", variant = "primary" }) {
  return <button className={`ds-btn ds-btn-${variant}`}>{children}</button>;
}

// Text input with a label sitting above it. Labels are quiet; the field is the subject.
export function Field({ label = "Gateway address", placeholder = "127.0.0.1:18789" }) {
  return (
    <label className="ds-field">
      <span className="ds-field-label">{label}</span>
      <input className="ds-input" placeholder={placeholder} />
    </label>
  );
}

// Content card. Flat by default — a hairline border, not a drop shadow.
// Never nest a card inside another card.
export function Card({ title = "Windows node", body = "Paired · 6 capabilities exposed over MCP." }) {
  return (
    <article className="ds-card">
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
