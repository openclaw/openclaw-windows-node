---
version: alpha
name: Windows Fluent UI for OpenClaw
description: Windows Fluent UI for OpenClaw is the design system for OpenClaw's native Windows companion suite, a WinUI 3 system-tray app that connects your PC to OpenClaw, the AI-powered personal assistant. The app pairs your PC to an OpenClaw gateway, exposes allowlisted Windows node capabilities over MCP, and guides first-run pairing through a setup wizard. Its interface follows Windows Fluent design conventions (Mica surfaces, the user's system accent, and Segoe UI Variable type) so it feels like a quiet, native extension of the OS rather than a bolted-on dashboard.
colors:
  ink: "#1b1b1b"
  paper: "#f3f3f3"
  surface: "#fbfbfb"
  muted: "#5d5d5d"
  line: "#e5e5e5"
  accent: "#0067c0"
  accentInk: "#ffffff"
  positive: "#0f7b0f"
  warning: "#9d5d00"
  critical: "#c42b1c"
  subtleHover: rgba(0,0,0,0.037)
  subtlePressed: rgba(0,0,0,0.024)
  control: "#ffffff"
  controlLine: "#ececec"
  accentSubtle: "#1a75c7"
typography:
  display:
    fontFamily: '"Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: 40px
    fontWeight: 600
    lineHeight: 50px
    letterSpacing: -0.01em
  title:
    fontFamily: '"Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: 28px
    fontWeight: 600
    lineHeight: 36px
  heading:
    fontFamily: '"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: 20px
    fontWeight: 600
    lineHeight: 28px
  body:
    fontFamily: '"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: 14px
    fontWeight: 400
    lineHeight: 20px
  small:
    fontFamily: '"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: 13px
    fontWeight: 400
    lineHeight: 18px
  caption:
    fontFamily: '"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: 12px
    fontWeight: 400
    lineHeight: 16px
spacing:
  "1": 4px
  "2": 8px
  "3": 12px
  "4": 16px
  "5": 24px
  "6": 32px
  "7": 48px
  "8": 64px
rounded:
  sm: 4px
  md: 8px
  lg: 12px
  bubble: 16px
  pill: 999px
x-colophon:
  version: 1
  tokens:
    $schema: https://agents.design/schema/v1
    meta:
      version: 1
      updatedBy: xaml+manual
      note: These tokens are the source of truth for DESIGN (brand, color, type, spacing, principles) and are framework-agnostic, and components.jsx shows design intent for the canvas preview, not shipping code. They were captured by manual inspection of the repo's WinUI 3 / XAML and C# surfaces (the CSS/JSX scanner finds no files in a XAML repo) and map to Windows Fluent system resources (SystemAccentColor, Mica, Segoe UI Variable). To ship, port the design into native code via the port targets in `authority`. Edit in the Design System canvas or directly.
    authority:
      designSource: self
      owner: OpenClaw Windows (openclaw/openclaw-windows-node)
      syncProcess: Tokens are captured by manual inspection of the app's WinUI 3 XAML/C# Fluent resources; re-inspect and update this file when the app's ThemeResource mappings or SystemAccentColor usage change.
      tooling:
        name: winapp (WinAppCli)
        source: https://github.com/microsoft/WinAppCli
        role: The Windows App Development CLI is the single build/packaging tool for both port targets below. Both the WinUI 3 (XAML) shell and the Reactor chat surface now build, package, and generate app identity, manifests, and certificates through winapp, so treat it as the port authority's build toolchain.
      port:
        authoritySource: Native WinUI 3 / C# (Windows Fluent)
        syncSource: https://github.com/microsoft/win-dev-skills
        toolingSource: https://github.com/microsoft/WinAppCli
        helperAgent: win-dev-skills
      portOverrides:
        - area: chat
          components:
            - ChatBubble
            - ChatComposer
            - ChatThread
          authoritySource: Reactor (Microsoft.UI.Reactor)
          syncSource: https://github.com/microsoft/microsoft-ui-reactor
          toolingSource: https://github.com/microsoft/WinAppCli
          helperAgent: ""
    shadows:
      - name: sm
        value: 0 1px 2px rgba(0,0,0,0.10)
      - name: md
        value: 0 4px 16px rgba(0,0,0,0.12)
      - name: lg
        value: 0 12px 40px rgba(0,0,0,0.16)
    principles:
      - Defer to Windows. Theme off SystemAccentColor and Fluent resources so the app follows the user's accent, light/dark, and high-contrast choices.
      - One accent, and it means "act here". Selection, focus, and primary actions share the system accent; don't spend it on decoration.
      - Hierarchy over decoration. Size, weight, and space do the work before color does.
      - "State must be legible: connected, pairing, and error states read clearly via positive/warning/critical, never color alone."
      - No hard-coded hex in UI. Use tokens/theme resources so dark and high-contrast modes stay correct.
      - "Motion is a cue, not a garnish: 120-200ms ease-out, no bounce."
    brand:
      tagline: A calm native Windows companion for your AI assistant.
      surface: product
      voice: Clear, respectful, and native to Windows. Explain pairing, permissions, and security plainly. State what will happen and why; never alarmist, never hype.
      personality:
        - native
        - trustworthy
        - calm
        - precise
      antiReferences:
        - custom-skinned controls that fight the OS theme
        - neon gradients and glassmorphism overload
        - ignoring the user's system accent or light/dark preference
        - cramming a web dashboard into a desktop window
        - hard-coded hex that breaks in dark or high-contrast mode
    colors:
      ink:
        resource: TextFillColorPrimaryBrush
        themes:
          dark: "#ffffff"
          highContrast: "#ffffff"
        usage: Primary text, headings. Fluent TextFillColorPrimary on light.
      paper:
        resource: ApplicationPageBackgroundThemeBrush
        themes:
          dark: "#202020"
          highContrast: "#000000"
        usage: App / page background. Mica base / ApplicationPageBackground (light).
      surface:
        resource: CardBackgroundFillColorDefaultBrush
        themes:
          dark: "#2b2b2b"
          highContrast: "#000000"
        usage: Cards, flyouts, inputs. Fluent CardBackgroundFillColorDefault.
      muted:
        resource: TextFillColorSecondaryBrush
        themes:
          dark: "#c8c8c8"
          highContrast: "#ffffff"
        usage: Secondary text, captions. Fluent TextFillColorSecondary/Tertiary.
      line:
        resource: CardStrokeColorDefaultBrush
        themes:
          dark: "#303030"
          highContrast: "#ffffff"
        usage: Borders, dividers. Fluent CardStrokeColorDefault / DividerStroke.
      accent:
        resource: AccentFillColorDefaultBrush
        themes:
          dark: "#4cc2ff"
          highContrast: "#ffff00"
        usage: Primary actions, links, selection, focus. Follows SystemAccentColor (default AccentDark1 on light).
      accentInk:
        resource: TextOnAccentFillColorPrimaryBrush
        themes:
          dark: "#000000"
          highContrast: "#000000"
        usage: Text/glyphs on accent fills.
      positive:
        resource: SystemFillColorSuccessBrush
        themes:
          dark: "#6ccb5f"
          highContrast: "#3ff23f"
        usage: Success / connected states. Fluent SystemFillColorSuccess.
      warning:
        resource: SystemFillColorCautionBrush
        themes:
          dark: "#fcd116"
          highContrast: "#ffb000"
        usage: Warnings / attention. Fluent SystemFillColorCaution.
      critical:
        resource: SystemFillColorCriticalBrush
        themes:
          dark: "#ff99a4"
          highContrast: "#ff5449"
        usage: Errors, disconnected, destructive. Fluent SystemFillColorCritical.
      subtleHover:
        resource: SubtleFillColorSecondaryBrush
        themes:
          dark: rgba(255,255,255,0.0605)
          highContrast: transparent
        usage: "Hover fill for subtle/transparent controls (icon buttons, inline pickers). Fluent SubtleFillColorSecondary (light #09000000)."
      subtlePressed:
        resource: SubtleFillColorTertiaryBrush
        themes:
          dark: rgba(255,255,255,0.0419)
          highContrast: transparent
        usage: "Pressed fill for subtle/transparent controls. Fluent SubtleFillColorTertiary (light #06000000)."
      control:
        resource: ControlFillColorDefaultBrush
        themes:
          dark: "#2d2d2d"
          highContrast: "#000000"
        usage: Fill for input/entry controls (combo boxes, text fields, the chat composer). Fluent ControlFillColorDefault (distinct from the Card surface used for cards/flyouts).
      controlLine:
        resource: ControlStrokeColorDefaultBrush
        themes:
          dark: "#353535"
          highContrast: "#ffffff"
        usage: Border for input/entry controls (combo boxes, text fields, composer, avatars). Fluent ControlStrokeColorDefault (distinct from the Card/divider stroke used for cards).
      accentSubtle:
        resource: AccentFillColorSecondaryBrush
        themes:
          dark: "#47b6ef"
          highContrast: "#ffff00"
        usage: Softer accent fill for the user's own chat bubble. Fluent AccentFillColorSecondary (SystemAccent at ~90%), quieter than the full accent reserved for primary actions.
    typography:
      display:
        weights:
          - 400
          - 600
        usage: Page and section titles. Segoe UI Variable Display optical size.
      body:
        weights:
          - 400
          - 600
        usage: Body copy, UI labels, buttons. Segoe UI Variable Text optical size.
      mono:
        family: '"Cascadia Mono", "Cascadia Code", Consolas, "SFMono-Regular", monospace'
        weights:
          - 400
        usage: Commands, endpoints, tokens, logs, timestamps. Used heavily across setup and diagnostics surfaces.
      icon:
        family: '"Segoe Fluent Icons", "Segoe MDL2 Assets", sans-serif'
        weights:
          - 400
        usage: Fluent icon glyphs (checkmarks, chevrons, action glyphs). Ships as WinUI FontIcon/SymbolIcon on SymbolThemeFontFamily (Segoe Fluent Icons); e.g. the selection CheckMark is glyph U+E73E. Not for body text.
      scale:
        display:
          role: display
        title:
          role: display
        heading:
          role: body
        body:
          role: body
        small:
          role: body
        caption:
          role: body
    spacing:
      unit: 4
---

## Overview

Windows Fluent UI for OpenClaw is the design system for OpenClaw's native Windows companion suite: a WinUI 3 system-tray app that connects your PC to OpenClaw, the AI-powered personal assistant. The app pairs your PC to an OpenClaw gateway, exposes allowlisted Windows node capabilities over MCP, and guides first-run pairing through a setup wizard. Its interface follows Windows Fluent design conventions (Mica surfaces, the user's system accent, and Segoe UI Variable type) so it feels like a quiet, native extension of the OS rather than a bolted-on dashboard.

<!-- colophon:note -->
Edit and preview this design with the **Colophon** canvas in the GitHub Copilot app.
Install the optional plugin with `copilot plugin install karkarl/colophon`.
Agents without Colophon can read and edit this file directly.
[Colophon documentation](https://github.com/karkarl/colophon).
<!-- colophon:note -->

This file owns the design tokens and rationale. `x-colophon` preserves preview
themes, production resource mappings, and other Colophon-specific metadata.

**The one-line brief.** A calm native Windows companion for your AI assistant. It should feel like a quiet, trustworthy extension of Windows (Fluent surfaces, the user's own accent color), not a web dashboard wearing a desktop window.

Voice: Clear, respectful, and native to Windows. Explain pairing, permissions, and security plainly. State what will happen and why; never alarmist, never hype. No hype words ("supercharge", "seamless", "revolutionary", "effortless").

Personality: native, trustworthy, calm, precise.

## Colors

- **ink** {colors.ink}: Primary text, headings. Fluent TextFillColorPrimary on light.
- **paper** {colors.paper}: App / page background. Mica base / ApplicationPageBackground (light).
- **surface** {colors.surface}: Cards, flyouts, inputs. Fluent CardBackgroundFillColorDefault.
- **muted** {colors.muted}: Secondary text, captions. Fluent TextFillColorSecondary/Tertiary.
- **line** {colors.line}: Borders, dividers. Fluent CardStrokeColorDefault / DividerStroke.
- **accent** {colors.accent}: Primary actions, links, selection, focus. Follows SystemAccentColor (default AccentDark1 on light).
- **accentInk** {colors.accentInk}: Text/glyphs on accent fills.
- **positive** {colors.positive}: Success / connected states. Fluent SystemFillColorSuccess.
- **warning** {colors.warning}: Warnings / attention. Fluent SystemFillColorCaution.
- **critical** {colors.critical}: Errors, disconnected, destructive. Fluent SystemFillColorCritical.
- **subtleHover** {colors.subtleHover}: Hover fill for subtle/transparent controls (icon buttons, inline pickers). Fluent SubtleFillColorSecondary (light #09000000).
- **subtlePressed** {colors.subtlePressed}: Pressed fill for subtle/transparent controls. Fluent SubtleFillColorTertiary (light #06000000).
- **control** {colors.control}: Fill for input/entry controls (combo boxes, text fields, the chat composer). Fluent ControlFillColorDefault, distinct from the Card surface used for cards/flyouts.
- **controlLine** {colors.controlLine}: Border for input/entry controls (combo boxes, text fields, composer, avatars). Fluent ControlStrokeColorDefault, distinct from the Card/divider stroke used for cards.
- **accentSubtle** {colors.accentSubtle}: Softer accent fill for the user's own chat bubble. Fluent AccentFillColorSecondary (SystemAccent at ~90%), quieter than the full accent reserved for primary actions.

Colors are preview swatches only. Bind each color's mapped `resource` key (in `x-colophon.tokens.colors`) rather than the hex, so light, dark, and high-contrast themes stay correct. Never put gray text on a colored background.

## Typography

- **display** {typography.display}: Page and section titles. Segoe UI Variable Display optical size.
- **title** {typography.title}: Page and section titles. Segoe UI Variable Display optical size.
- **heading** {typography.heading}: Section headings. Segoe UI Variable Text optical size.
- **body** {typography.body}: Body copy, UI labels, buttons. Segoe UI Variable Text optical size.
- **small** {typography.small}: Secondary and dense UI text. Segoe UI Variable Text optical size.
- **caption** {typography.caption}: Captions and metadata. Segoe UI Variable Text optical size.

Use Segoe UI Variable for text and Cascadia Mono (`x-colophon.tokens.typography.mono`) for commands, endpoints, tokens, and logs. Fluent icon glyphs use Segoe Fluent Icons (`x-colophon.tokens.typography.icon`); for example, the selection checkmark is glyph U+E73E.

## Layout

Use the named spacing tokens rather than ad-hoc values. Prefer the `5` and `6` steps between groups so content can breathe. One subject per screen: decide what the screen is for, and let that element win.

## Information hierarchy

1. **One subject per screen.** Decide what the screen is for and let that element win.
2. **Size and weight before color.** Establish the hierarchy in grayscale first; color is the last 10%.
3. **One accent.** `accent` means "act here" (selection, focus, primary action). If everything is accented, nothing is.
4. **Generous vertical rhythm.** Prefer the `5` and `6` spacing steps between groups; let content breathe.

## Elevation & Depth

Use the named shadows in `x-colophon.tokens.shadows` when elevation is needed. Keep shadows soft and shallow; depth should read as Fluent layering, not drop-shadow decoration.

## Shapes

Use the `rounded` tokens for corner radii: `sm` for buttons and inputs, `md` for cards and flyouts, `lg` for grouped panels, `bubble` for chat messages (intentionally friendlier), and `pill` for status badges and toggles.

## Components

Reuse [component patterns](.agents/design/components.jsonc); these describe design intent, not shipping code.
Optional click-through flows live in `.agents/design/prototypes.jsonc`.

## Do's and Don'ts

Do:

- Defer to Windows: theme off `SystemAccentColor` and Fluent resources so the app follows the user's accent, light/dark, and high-contrast settings.
- Spend the accent sparingly. Selection, focus, and primary actions share the system accent; do not spend it on decoration.
- Let size, weight, and space carry hierarchy before color does.
- Make connection state legible: connected, pairing, and error read via `positive`/`warning`/`critical`, never by color alone.
- Keep motion to 120-200ms ease-out. Animate to explain, not to impress.

Don't:

- Don't hard-code hex in UI; it breaks in dark and high-contrast modes. Use tokens/theme resources.
- Don't put gray text on a colored background.
- Don't nest cards inside cards, or wrap every block in a card.
- Don't reach for bounce/elastic easing; it reads as dated.
- Don't fight the OS theme with custom-skinned controls or glassmorphism overload.

Anti-references: if a mock could be mistaken for a neon-gradient SaaS landing page, or ignores the user's system accent and light/dark preference, start over. Avoid: custom-skinned controls that fight the OS theme; neon gradients and glassmorphism overload; ignoring the user's system accent or light/dark preference; cramming a web dashboard into a desktop window; hard-coded hex that breaks in dark or high-contrast mode.

## Production implementation

The design files are the source of truth for design, and are framework-agnostic. `components.jsonc` shows design intent for preview, not shipping code. Follow `x-colophon.tokens.authority` for port targets, ownership, and synchronization:

- **Default:** surfaces ship as **native WinUI 3 / C#** (Windows Fluent). Port the design into XAML/C# via [win-dev-skills](https://github.com/microsoft/win-dev-skills).
- **Chat surface** (`ChatBubble`, `ChatComposer`, `ChatThread`): ships via **[Reactor](https://github.com/microsoft/microsoft-ui-reactor)**, the React-style framework for this app's chat. Reactor has no dedicated porting agent yet.
- Both port targets build and package through **[winapp (WinAppCli)](https://github.com/microsoft/WinAppCli)**, the Windows App Development CLI, which also generates app identity, manifests, and certificates.

Colors are preview swatches: bind their mapped `resource` keys rather than hard-coding hex values. Preserve the light, dark, and high-contrast mappings. The shipping implementation is canonical; component patterns are not shipping code. When you need a value the design doesn't cover, add it here first, then port it into the native/Reactor implementation via the matching reference above.
