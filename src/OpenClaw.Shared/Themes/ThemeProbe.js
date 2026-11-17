// Resolves the Control UI's built-in theme tokens to concrete colors, radii and font stacks.
//
// The Gateway's built-in palettes are a CSS cascade (core styles plus themes/<id>.css, with color-mix()
// derivations), so only a CSS engine can produce their values. This probe is the single resolver for both
// consumers: scripts/Update-GatewayThemeTable.ps1 runs it in headless Edge against pinned upstream sources to
// generate the bundled table, and the Companion runs it in an offscreen WebView2 against the stylesheets the
// connected Gateway serves. The host document must already contain the stylesheets, with each theme file marked
// data-openclaw-theme-sheet; this script only reads them.
//
// Usage:
//   const { result, failures } = openclawThemeProbe.resolve(['claw', 'tide']);
//   // result.tide.dark.Canvas === '#10151b'; failures names every token that did not resolve.
(() => {
  // [palette field, CSS variable, fallback variable]. Field names are ResolvedThemePalette properties.
  // Fallbacks mirror how the Control UI's own consumers read variables a release may not define:
  // var(--inline-code-bg, var(--bg-muted)) in chat text, var(--bg-content, var(--bg)) for the shell, and
  // var(--chat-composer-surface, var(--popover)) for the composer.
  const COLORS = [
    ['Canvas', 'bg', null], ['Content', 'bg-content', 'bg'], ['Surface', 'bg-accent', null],
    ['Card', 'card', null], ['Popover', 'popover', null], ['Hover', 'bg-hover', null],
    ['Text', 'text', null], ['TextStrong', 'text-strong', null], ['Muted', 'muted', null],
    ['Border', 'border', null], ['BorderStrong', 'border-strong', null], ['Accent', 'accent', null],
    ['AccentHover', 'accent-hover', null], ['AccentForeground', 'accent-foreground', null],
    ['Primary', 'primary', null], ['PrimaryForeground', 'primary-foreground', null], ['Ring', 'ring', null],
    ['Destructive', 'destructive', null], ['Danger', 'danger', null], ['Ok', 'ok', null], ['Warn', 'warn', null],
    ['Info', 'info', null], ['ComposerSurface', 'chat-composer-surface', 'popover'],
    ['InlineCode', 'inline-code-bg', 'bg-muted'], ['Elevated', 'bg-elevated', null], ['Panel', 'panel', null],
    ['AccentSubtle', 'accent-subtle', null],
  ];
  const RADII = [['RadiusSm', 'radius-sm'], ['RadiusMd', 'radius-md'], ['CornerScale', 'openclaw-corner-radius-scale']];
  const FONTS = [['Body', 'font-body'], ['Chat', 'font-chat'], ['Mono', 'mono']];

  // Engines that draw superellipse corners scale radii by 1.25 to look like the circular ones WinUI draws, so the
  // probe reads the circular geometry. It sits after the core styles and before the theme files, so a theme's own
  // scale (CRT's near-square 0.25) still wins.
  const CIRCULAR_GEOMETRY = '@supports (corner-shape: superellipse(1.5)) { :root { --openclaw-corner-radius-scale: 1; } }';

  // Theme ids map to data-theme aliases exactly as ui/src/app/theme.ts resolveTheme() does.
  function dataTheme(id, mode) {
    const family = id === 'claw' ? null : id === 'knot' ? 'openknot' : id;
    return family === null ? mode : mode === 'light' ? family + '-light' : family;
  }

  const hex = (n) => Math.round(Math.min(1, Math.max(0, n)) * 255).toString(16).padStart(2, '0');
  function toHex(value) {
    let m = value.match(/^rgba?\(\s*([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\s*\)$/);
    let r, g, b, a;
    if (m) { r = +m[1] / 255; g = +m[2] / 255; b = +m[3] / 255; a = m[4] === undefined ? 1 : +m[4]; }
    else {
      m = value.match(/^color\(srgb\s+([-\d.e]+)\s+([-\d.e]+)\s+([-\d.e]+)(?:\s*\/\s*([\d.e]+))?\)$/);
      if (!m) return null;
      r = +m[1]; g = +m[2]; b = +m[3]; a = m[4] === undefined ? 1 : +m[4];
    }
    return '#' + hex(r) + hex(g) + hex(b) + (a >= 1 ? '' : hex(a));
  }

  // True when some rule targets this data-theme value. Without one every token would silently be the base
  // (claw) palette, so an id no stylesheet styles fails instead.
  function isStyled(alias) {
    const needles = ['data-theme="' + alias + '"]', "data-theme='" + alias + "']", 'data-theme=' + alias + ']'];
    const visit = (rules) => {
      for (const rule of rules) {
        if (rule.selectorText && needles.some((needle) => rule.selectorText.includes(needle))) return true;
        if (rule.cssRules && visit(rule.cssRules)) return true;
      }
      return false;
    };
    for (const sheet of document.styleSheets) {
      try { if (visit(sheet.cssRules)) return true; } catch { /* a sheet the document cannot read */ }
    }
    return false;
  }

  function resolve(themeIds) {
    const root = document.documentElement;
    const previous = [root.getAttribute('data-theme'), root.getAttribute('data-theme-mode')];
    const geometry = document.createElement('style');
    geometry.textContent = CIRCULAR_GEOMETRY;
    document.head.insertBefore(geometry, document.head.querySelector('[data-openclaw-theme-sheet]'));
    const probe = document.createElement('span');
    (document.body || root).appendChild(probe);
    const result = {};
    const failures = [];
    try {
      for (const id of themeIds) {
        result[id] = {};
        for (const mode of ['light', 'dark']) {
          const alias = dataTheme(id, mode);
          root.setAttribute('data-theme', alias);
          root.setAttribute('data-theme-mode', mode);
          // claw is the base palette (data-theme light or dark), which no rule needs to name.
          if (id !== 'claw' && !isStyled(alias)) failures.push(id + '/' + mode + '/unstyled');
          const style = getComputedStyle(root);
          const palette = {};
          for (const [name, primary, fallback] of COLORS) {
            const variable = style.getPropertyValue('--' + primary).trim() || !fallback ? primary : fallback;
            const raw = style.getPropertyValue('--' + variable).trim();
            probe.style.color = '';
            // Custom properties keep color-mix() unevaluated; a real color property resolves it.
            probe.style.color = 'rgb(from var(--' + variable + ') r g b / alpha)';
            const value = raw ? toHex(getComputedStyle(probe).color) : null;
            if (!value) failures.push(id + '/' + mode + '/' + variable + '=' + raw);
            palette[name] = value;
          }
          for (const [name, variable] of RADII) {
            const value = parseFloat(style.getPropertyValue('--' + variable));
            if (!Number.isFinite(value) || value < 0) failures.push(id + '/' + mode + '/' + variable);
            palette[name] = value;
          }
          for (const [name, variable] of FONTS) {
            palette[name] = style.getPropertyValue('--' + variable).trim().replace(/\s+/g, ' ');
            if (!palette[name]) failures.push(id + '/' + mode + '/' + variable);
          }
          result[id][mode] = palette;
        }
      }
    } finally {
      probe.remove();
      geometry.remove();
      for (const [attribute, value] of [['data-theme', previous[0]], ['data-theme-mode', previous[1]]]) {
        if (value === null) root.removeAttribute(attribute); else root.setAttribute(attribute, value);
      }
    }
    return { result, failures };
  }

  globalThis.openclawThemeProbe = { dataTheme, toHex, resolve };
})();
