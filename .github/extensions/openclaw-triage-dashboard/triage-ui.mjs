function escapeScriptValue(value) {
    return JSON.stringify(value).replaceAll("<", "\\u003c");
}

export function renderDashboardHtml(actionToken) {
    return `<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>OpenClaw triage</title>
  <style>
    :root {
      color-scheme: light dark;
      --github-bg: var(--bgColor-default, var(--background-color-default, Canvas));
      --github-inset: var(--bgColor-inset, color-mix(in srgb, CanvasText 4%, Canvas));
      --github-muted: var(--fgColor-muted, var(--text-color-muted, GrayText));
      --github-border: var(--borderColor-default, var(--border-color-default, ButtonBorder));
      --github-border-muted: var(--borderColor-muted, var(--border-color-default, ButtonBorder));
      --github-accent: var(--fgColor-accent, var(--true-color-blue, #0969da));
      --github-open: var(--fgColor-open, var(--true-color-green, #1a7f37));
      --github-danger: var(--fgColor-danger, var(--true-color-red, #cf222e));
      --github-attention: var(--fgColor-attention, var(--true-color-yellow, #9a6700));
      --github-button: var(--button-default-bgColor-rest, var(--background-color-default, ButtonFace));
      --github-button-hover: var(--button-default-bgColor-hover, color-mix(in srgb, CanvasText 8%, Canvas));
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--github-bg);
      color: var(--fgColor-default, var(--text-color-default, CanvasText));
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      font-size: var(--text-body-medium, 14px);
      line-height: var(--leading-body-medium, 20px);
    }
    button, input, select { font: inherit; }
    button:focus-visible, input:focus-visible, select:focus-visible, a:focus-visible {
      outline: 2px solid var(--color-focus-outline, Highlight);
      outline-offset: 2px;
    }
    main { max-width: 1012px; margin: 0 auto; padding: 24px 16px 48px; }
    header { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; }
    .header-status { display: flex; align-items: center; gap: 8px; flex-shrink: 0; }
    h1 {
      margin: 0 0 2px;
      font-family: var(--font-sans-display, var(--font-sans, "Segoe UI", sans-serif));
      font-size: var(--text-title-medium, 20px);
      line-height: var(--leading-title-medium, 26px);
      font-weight: var(--font-weight-semibold, 600);
    }
    h2 {
      margin: 20px 0 8px;
      font-size: var(--text-body-medium, 14px);
      line-height: var(--leading-body-medium, 20px);
      font-weight: var(--font-weight-semibold, 600);
    }
    p { margin: 0; }
    .muted { color: var(--github-muted); }
    .tabs {
      display: flex;
      gap: 8px;
      margin-top: 16px;
      overflow-x: auto;
      border-bottom: 1px solid var(--github-border);
    }
    .tab {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      min-height: 40px;
      margin-bottom: -1px;
      padding: 8px 10px;
      border: 0;
      border-bottom: 2px solid transparent;
      background: transparent;
      color: var(--github-muted);
      font: inherit;
      white-space: nowrap;
      cursor: pointer;
    }
    .tab:hover { color: inherit; border-bottom-color: var(--github-border); }
    .tab[aria-selected="true"] {
      color: inherit;
      border-bottom-color: var(--underlineNav-borderColor-active, #fd8c73);
      font-weight: 600;
    }
    .tab-count {
      min-width: 20px;
      padding: 0 6px;
      border-radius: 999px;
      background: color-mix(in srgb, CanvasText 10%, transparent);
      font-size: 12px;
      font-weight: 500;
      line-height: 20px;
      text-align: center;
    }
    .tab-panel { padding-top: 16px; }
    .metrics {
      display: block;
      margin-bottom: 10px;
      color: var(--github-muted);
      font-size: 12px;
    }
    .toolbar {
      display: flex;
      align-items: center;
      margin-bottom: 16px;
    }
    .control {
      min-height: 32px;
      border: 1px solid var(--github-border);
      border-radius: 6px;
      background: var(--github-button);
      color: inherit;
      padding: 5px 12px;
      font-weight: 500;
      box-shadow: 0 1px 0 color-mix(in srgb, CanvasText 4%, transparent);
    }
    .search-wrap {
      position: relative;
      display: flex;
      flex: 1;
      align-items: center;
    }
    .search-wrap svg {
      position: absolute;
      left: 12px;
      width: 16px;
      height: 16px;
      color: var(--github-muted);
      pointer-events: none;
    }
    input.control {
      width: 100%;
      min-width: 220px;
      background: var(--github-bg);
      padding-left: 36px;
      font-weight: 400;
      box-shadow: inset 0 1px 0 color-mix(in srgb, CanvasText 4%, transparent);
    }
    .select-wrap { position: relative; display: inline-flex; align-items: center; }
    .select-wrap select { appearance: none; padding-right: 30px; cursor: pointer; }
    .select-wrap svg {
      position: absolute;
      right: 10px;
      width: 12px;
      height: 12px;
      color: var(--github-muted);
      pointer-events: none;
    }
    .list-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      min-height: 49px;
      padding: 8px 16px;
      border: 1px solid var(--github-border);
      border-radius: 6px 6px 0 0;
      background: var(--github-inset);
    }
    .type-toggle { display: flex; align-items: center; gap: 18px; }
    .type-toggle button {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      border: 0;
      background: transparent;
      color: var(--github-muted);
      font: inherit;
      cursor: pointer;
    }
    .type-toggle button:hover { color: inherit; }
    .type-toggle button[aria-pressed="true"] { color: inherit; font-weight: 600; }
    .type-toggle svg { width: 16px; height: 16px; }
    .type-count { color: var(--github-muted); font-weight: 400; }
    .filter-controls { display: flex; align-items: center; margin-right: -8px; }
    .filter-controls .control {
      min-height: 32px;
      border: 0;
      background: transparent;
      padding-left: 10px;
      box-shadow: none;
      font-weight: 400;
    }
    .filter-controls .control:hover { color: var(--github-accent); }
    button.control { cursor: pointer; }
    button.control:hover:not(:disabled) {
      background: var(--github-button-hover);
    }
    button.primary {
      border-color: var(--color-focus-outline, Highlight);
      background: var(--color-focus-outline, Highlight);
      color: var(--color-white, HighlightText);
    }
    button:disabled { cursor: not-allowed; opacity: 0.55; }
    .icon-control {
      display: inline-grid;
      place-items: center;
      width: 28px;
      min-height: 28px;
      padding: 0;
    }
    .icon-control svg { width: 16px; height: 16px; }
    .icon-control:disabled svg { animation: refresh-spin 800ms linear infinite; }
    @keyframes refresh-spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) {
      .icon-control:disabled svg { animation: none; }
    }
    .plan, .items {
      border: 1px solid var(--github-border);
      border-radius: 6px;
      overflow: hidden;
    }
    .items { border-top: 0; border-radius: 0 0 6px 6px; }
    .plan-row, .item {
      border-bottom: 1px solid var(--github-border-muted);
      padding: 12px 16px;
    }
    .plan-row:last-child, .item:last-child { border-bottom: 0; }
    .plan-row { display: grid; grid-template-columns: 84px 1fr; align-items: start; gap: 10px; }
    .plan-row p { margin-top: 1px; }
    .report-box {
      border: 1px solid var(--github-border);
      border-radius: 6px;
      overflow: hidden;
    }
    .report-row {
      display: grid;
      grid-template-columns: minmax(140px, 24%) 1fr;
      gap: 16px;
      padding: 12px 16px;
      border-bottom: 1px solid var(--github-border-muted);
    }
    .report-row:last-child { border-bottom: 0; }
    .report-row strong { font-weight: 600; }
    .report-fields { display: grid; gap: 3px; }
    .report-field { color: var(--github-muted); }
    .report-field b { color: inherit; font-weight: 600; }
    .numbered-list { margin: 0; padding: 8px 16px 8px 44px; }
    .numbered-list li { padding: 5px 0 5px 4px; }
    .empty-state { padding: 32px 16px; color: var(--github-muted); text-align: center; }
    .item:hover { background: var(--github-inset); }
    .item-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-width: 0; }
    .item-heading-main { display: flex; align-items: center; gap: 6px; min-width: 0; }
    .item-icon { width: 16px; height: 16px; flex: 0 0 16px; color: var(--github-open); }
    .item-title { color: inherit; font-weight: 600; text-decoration: none; }
    .item-title:hover { color: var(--github-accent); }
    .item-decision {
      flex-shrink: 0;
      color: var(--github-muted);
      font-size: var(--text-body-medium, 14px);
      line-height: var(--leading-body-medium, 20px);
      white-space: nowrap;
    }
    .badges, .stages, .actions { display: flex; flex-wrap: wrap; gap: 5px; }
    .badge, .stage {
      display: inline-flex;
      align-items: center;
      min-height: 20px;
      border: 1px solid transparent;
      border-radius: 999px;
      padding: 1px 7px;
      font-size: var(--text-caption, 12px);
      line-height: 16px;
      white-space: nowrap;
    }
    .label-pr { display: none; }
    .label-success {
      border-color: color-mix(in srgb, var(--github-open) 30%, transparent);
      background: color-mix(in srgb, var(--github-open) 12%, transparent);
      color: var(--github-open);
    }
    .label-attention {
      border-color: color-mix(in srgb, var(--github-attention) 35%, transparent);
      background: color-mix(in srgb, var(--github-attention) 13%, transparent);
      color: var(--github-attention);
    }
    .label-danger {
      border-color: color-mix(in srgb, var(--github-danger) 30%, transparent);
      background: color-mix(in srgb, var(--github-danger) 10%, transparent);
      color: var(--github-danger);
    }
    .status-done { color: var(--true-color-green, #1a7f37); }
    .status-in_progress, .status-pending { color: var(--true-color-blue, #0969da); }
    .status-blocked { color: var(--true-color-red, #cf222e); }
    .item-meta { display: flex; flex-wrap: wrap; gap: 4px 12px; margin-top: 4px; font-size: var(--text-caption, 12px); }
    .item-body { margin-top: 4px; max-width: 100ch; }
    .item-footer { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-top: 6px; }
    .stage { border: 0; padding: 0; }
    .stage::before { content: "●"; margin-right: 4px; font-size: 8px; }
    .actions { flex-shrink: 0; }
    .actions button { min-height: 26px; padding: 2px 8px; font-size: var(--text-caption, 12px); }
    .gate { margin-top: 5px; color: var(--github-muted); font-size: var(--text-caption, 12px); }
    .gate summary { cursor: pointer; }
    .gate p { margin-top: 3px; }
    .error {
      margin-top: 16px;
      border-left: 3px solid var(--true-color-red, #cf222e);
      padding: 8px 12px;
    }
    .notice {
      position: fixed;
      right: 20px;
      bottom: 20px;
      max-width: 420px;
      border: 1px solid var(--border-color-default, ButtonBorder);
      border-radius: 8px;
      background: var(--background-color-default, Canvas);
      box-shadow: 0 8px 24px color-mix(in srgb, CanvasText 15%, transparent);
      padding: 12px 14px;
    }
    .hidden { display: none; }
    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      padding: 0;
      margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }
    @media (max-width: 700px) {
      main { padding: 14px 12px 40px; }
      header { display: block; }
      .item-head, .item-heading-main { flex-wrap: wrap; }
      .item-decision { margin-left: auto; }
      .plan-row { grid-template-columns: 1fr; gap: 6px; }
      .report-row { grid-template-columns: 1fr; gap: 4px; }
      .item-footer { align-items: flex-start; flex-direction: column; }
      .list-header { align-items: flex-start; flex-direction: column; gap: 6px; }
      .filter-controls { width: 100%; margin: 0; overflow-x: auto; }
    }
  </style>
</head>
<body>
  <main>
    <header>
      <div>
        <h1 id="title">OpenClaw triage</h1>
        <p id="scope" class="muted">Loading current triage state...</p>
      </div>
      <div class="header-status">
        <p id="updated" class="muted"></p>
        <button id="refresh" class="control icon-control" type="button" aria-label="Refresh GitHub status" title="Refresh GitHub status">
          <svg viewBox="0 0 16 16" aria-hidden="true">
            <path fill="currentColor" d="M8 2.5a5.5 5.5 0 0 0-5.19 3.67.75.75 0 1 1-1.42-.5A7 7 0 0 1 12.6 2.74V1.5a.75.75 0 0 1 1.5 0v3.25a.75.75 0 0 1-.75.75H10.1a.75.75 0 0 1 0-1.5h1.48A5.48 5.48 0 0 0 8 2.5Zm6.13 7.08a.75.75 0 0 1 .48.95A7 7 0 0 1 3.4 13.26v1.24a.75.75 0 0 1-1.5 0v-3.25a.75.75 0 0 1 .75-.75H5.9a.75.75 0 0 1 0 1.5H4.42A5.5 5.5 0 0 0 13.18 10a.75.75 0 0 1 .95-.42Z"/>
          </svg>
        </button>
      </div>
    </header>
    <div id="error" class="error hidden" role="alert"></div>
    <nav class="tabs" role="tablist" aria-label="Triage report sections">
      <button class="tab" role="tab" aria-selected="true" aria-controls="tab-items" data-tab="items">Items <span id="count-items" class="tab-count">0</span></button>
      <button class="tab" role="tab" aria-selected="false" aria-controls="tab-landing" data-tab="landing">Landing plan <span id="count-landing" class="tab-count">0</span></button>
      <button class="tab" role="tab" aria-selected="false" aria-controls="tab-changes" data-tab="changes">Changes <span id="count-changes" class="tab-count">0</span></button>
      <button class="tab" role="tab" aria-selected="false" aria-controls="tab-queue" data-tab="queue">Queue <span id="count-queue" class="tab-count">0</span></button>
      <button class="tab" role="tab" aria-selected="false" aria-controls="tab-ownership" data-tab="ownership">Ownership <span id="count-ownership" class="tab-count">0</span></button>
      <button class="tab" role="tab" aria-selected="false" aria-controls="tab-reviews" data-tab="reviews">Reviews <span id="count-reviews" class="tab-count">0</span></button>
      <button class="tab" role="tab" aria-selected="false" aria-controls="tab-day-plan" data-tab="day-plan">Day plan <span id="count-day-plan" class="tab-count">0</span></button>
      <button class="tab" role="tab" aria-selected="false" aria-controls="tab-automation" data-tab="automation">Automation <span id="count-automation" class="tab-count">0</span></button>
    </nav>
    <section id="tab-items" class="tab-panel" role="tabpanel">
      <h2 class="sr-only">Items</h2>
      <section id="metrics" class="metrics" aria-label="Triage summary"></section>
      <div class="toolbar">
        <label class="search-wrap">
          <span class="sr-only">Search triage items</span>
          <svg viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M10.5 6.5a4 4 0 1 0-8 0 4 4 0 0 0 8 0Zm-.73 4.33a5.5 5.5 0 1 1 1.06-1.06l3.95 3.95a.75.75 0 1 1-1.06 1.06l-3.95-3.95Z"/></svg>
          <input id="search" class="control" type="search" placeholder="Search all triage items">
        </label>
      </div>
      <div class="list-header">
        <div class="type-toggle" role="group" aria-label="Item type">
          <button id="type-pr" type="button" aria-pressed="true" aria-label="Show pull requests">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M1.75 3.5a1.75 1.75 0 1 1 2.5 1.58v5.84a1.75 1.75 0 1 1-1.5 0V5.08A1.75 1.75 0 0 1 1.75 3.5Zm8.5-1.75a.75.75 0 0 1 .75.75v1.75h.25A2.75 2.75 0 0 1 14 7v3.92a1.75 1.75 0 1 1-1.5 0V7c0-.69-.56-1.25-1.25-1.25H11V7.5a.75.75 0 0 1-1.28.53l-2.5-2.5a.75.75 0 0 1 0-1.06l2.5-2.5a.75.75 0 0 1 .53-.22Z"/></svg>
            Pull requests <span id="type-pr-count" class="type-count">0</span>
          </button>
          <button id="type-issue" type="button" aria-pressed="false" aria-label="Show issues">
            <svg viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M8 1.25a6.75 6.75 0 1 1 0 13.5 6.75 6.75 0 0 1 0-13.5Zm0 1.5a5.25 5.25 0 1 0 0 10.5 5.25 5.25 0 0 0 0-10.5Zm0 7.5a1 1 0 1 1 0 2 1 1 0 0 1 0-2Zm.75-5.5v4h-1.5v-4h1.5Z"/></svg>
            Issues <span id="type-issue-count" class="type-count">0</span>
          </button>
        </div>
        <div class="filter-controls">
          <span class="select-wrap"><select id="verdict-filter" class="control" aria-label="Filter by verdict">
            <option value="all">Verdict</option>
          </select><svg viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M4.4 6.2a.75.75 0 0 1 1.05 0L8 8.75l2.55-2.55a.75.75 0 0 1 1.05 1.06l-3.08 3.08a.75.75 0 0 1-1.04 0L4.4 7.26a.75.75 0 0 1 0-1.06Z"/></svg></span>
          <span class="select-wrap"><select id="readiness-filter" class="control" aria-label="Filter by readiness">
            <option value="all">Status</option>
            <option value="ready">Merge ready</option>
            <option value="checks">Checks running</option>
            <option value="proof">Needs proof</option>
            <option value="blocked">Blocked</option>
          </select><svg viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M4.4 6.2a.75.75 0 0 1 1.05 0L8 8.75l2.55-2.55a.75.75 0 0 1 1.05 1.06l-3.08 3.08a.75.75 0 0 1-1.04 0L4.4 7.26a.75.75 0 0 1 0-1.06Z"/></svg></span>
          <span class="select-wrap"><select id="sort" class="control" aria-label="Sort items">
            <option value="confidence-desc">Sort: confidence</option>
            <option value="confidence-asc">Lowest confidence</option>
            <option value="number-desc">Newest</option>
          </select><svg viewBox="0 0 16 16" aria-hidden="true"><path fill="currentColor" d="M4.4 6.2a.75.75 0 0 1 1.05 0L8 8.75l2.55-2.55a.75.75 0 0 1 1.05 1.06l-3.08 3.08a.75.75 0 0 1-1.04 0L4.4 7.26a.75.75 0 0 1 0-1.06Z"/></svg></span>
        </div>
      </div>
      <div id="items" class="items"></div>
    </section>
    <section id="tab-landing" class="tab-panel hidden" role="tabpanel">
      <div id="plan" class="plan"></div>
    </section>
    <section id="tab-changes" class="tab-panel hidden" role="tabpanel"><div id="changes" class="report-box"></div></section>
    <section id="tab-queue" class="tab-panel hidden" role="tabpanel"><div id="queue" class="report-box"></div></section>
    <section id="tab-ownership" class="tab-panel hidden" role="tabpanel"><div id="ownership" class="report-box"></div></section>
    <section id="tab-reviews" class="tab-panel hidden" role="tabpanel"><div id="reviews" class="report-box"></div></section>
    <section id="tab-day-plan" class="tab-panel hidden" role="tabpanel"><div id="day-plan" class="report-box"></div></section>
    <section id="tab-automation" class="tab-panel hidden" role="tabpanel"><div id="automation" class="report-box"></div></section>
  </main>
  <div id="notice" class="notice hidden" role="status"></div>
  <script>
    const actionToken = ${escapeScriptValue(actionToken)};
    let state = null;
    let selectedType = "pr";

    function element(tag, className, text) {
      const node = document.createElement(tag);
      if (className) node.className = className;
      if (text != null) node.textContent = text;
      return node;
    }

    function itemIcon(type) {
      const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      svg.setAttribute("class", "item-icon");
      svg.setAttribute("viewBox", "0 0 16 16");
      svg.setAttribute("aria-hidden", "true");
      const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
      path.setAttribute("fill", "currentColor");
      path.setAttribute("d", type === "pr"
        ? "M1.75 3.5a1.75 1.75 0 1 1 2.5 1.58v5.84a1.75 1.75 0 1 1-1.5 0V5.08A1.75 1.75 0 0 1 1.75 3.5Zm8.5-1.75a.75.75 0 0 1 .75.75v1.75h.25A2.75 2.75 0 0 1 14 7v3.92a1.75 1.75 0 1 1-1.5 0V7c0-.69-.56-1.25-1.25-1.25H11V7.5a.75.75 0 0 1-1.28.53l-2.5-2.5a.75.75 0 0 1 0-1.06l2.5-2.5a.75.75 0 0 1 .53-.22Z"
        : "M8 1.25a6.75 6.75 0 1 1 0 13.5 6.75 6.75 0 0 1 0-13.5Zm0 1.5a5.25 5.25 0 1 0 0 10.5 5.25 5.25 0 0 0 0-10.5Zm0 7.5a1 1 0 1 1 0 2 1 1 0 0 1 0-2Zm.75-5.5v4h-1.5v-4h1.5Z");
      svg.append(path);
      return svg;
    }

    function showNotice(message) {
      const notice = document.getElementById("notice");
      notice.textContent = message;
      notice.classList.remove("hidden");
      window.clearTimeout(showNotice.timer);
      showNotice.timer = window.setTimeout(() => notice.classList.add("hidden"), 5000);
    }

    function statusLabel(value) {
      return String(value || "pending").replace("_", " ");
    }

    function readiness(item) {
      if (item.mergeRequest.eligible) return { label: "Ready", variant: "success" };
      if (item.decision === "NEEDS_INFO") return { label: "Decision needed", variant: "attention" };
      if (item.decision === "HOLD_FOR_AUTHOR" || item.decision === "DECLINE") {
        return { label: "Code blocked", variant: "danger" };
      }
      if (item.proofStatus === "required" || item.proofStatus === "blocked") {
        return { label: "Needs proof", variant: "attention" };
      }
      if (item.checks && item.checks.pending > 0) return { label: "CI running", variant: "attention" };
      return { label: "Maintainer review", variant: "attention" };
    }

    function renderMetrics(summary) {
      const root = document.getElementById("metrics");
      root.textContent = summary.total + " items · " +
        summary.ready + " merge ready · " +
        summary.checksRunning + " checks running · " +
        summary.needsProof + " need proof · " +
        summary.blocked + " blocked";
    }

    function renderPlan(plan) {
      const root = document.getElementById("plan");
      root.replaceChildren();
      if (!plan.length) {
        root.append(element("p", "plan-row muted", "No landing plan was supplied."));
        return;
      }
      for (const step of plan) {
        const row = element("div", "plan-row");
        row.append(element("span", "badge status-" + step.liveStatus, statusLabel(step.liveStatus)));
        const body = element("div");
        body.append(element("strong", "", step.title));
        if (step.detail) body.append(element("p", "muted", step.detail));
        row.append(body);
        root.append(row);
      }
    }

    function renderEmpty(root, message) {
      root.replaceChildren(element("p", "empty-state", message));
    }

    function renderNumbered(rootId, entries, emptyMessage) {
      const root = document.getElementById(rootId);
      root.replaceChildren();
      if (!entries.length) {
        renderEmpty(root, emptyMessage);
        return;
      }
      const list = element("ol", "numbered-list");
      for (const entry of entries) list.append(element("li", "", entry));
      root.append(list);
    }

    function renderRecords(rootId, entries, primaryKey, fields, emptyMessage) {
      const root = document.getElementById(rootId);
      root.replaceChildren();
      if (!entries.length) {
        renderEmpty(root, emptyMessage);
        return;
      }
      for (const entry of entries) {
        const row = element("div", "report-row");
        row.append(element("strong", "", entry[primaryKey] || "—"));
        const details = element("div", "report-fields");
        for (const [key, label] of fields) {
          if (!entry[key]) continue;
          const field = element("div", "report-field");
          field.append(element("b", "", label + ": "), document.createTextNode(entry[key]));
          details.append(field);
        }
        row.append(details);
        root.append(row);
      }
    }

    function renderReport(report) {
      renderRecords("changes", report.changes, "change", [["items", "Items"]], "No change summary was supplied.");
      renderNumbered("queue", report.executiveQueue, "No executive queue was supplied.");
      renderRecords("ownership", report.ownership, "item", [["assessment", "Assessment"]], "No ownership audit was supplied.");
      renderRecords(
        "reviews",
        report.reviews,
        "item",
        [["title", "Title"], ["decision", "Decision"], ["summary", "Assessment"]],
        "No adversarial review summary was supplied.",
      );
      renderNumbered("day-plan", report.dayPlan, "No day plan was supplied.");
      renderRecords(
        "automation",
        report.automation,
        "opportunity",
        [["why", "Why"], ["owner", "Owner"], ["effort", "Effort"], ["notes", "Notes"]],
        "No automation opportunities were supplied.",
      );
      const counts = {
        items: state.items.length,
        landing: state.plan.length,
        changes: report.changes.length,
        queue: report.executiveQueue.length,
        ownership: report.ownership.length,
        reviews: report.reviews.length,
        "day-plan": report.dayPlan.length,
        automation: report.automation.length,
      };
      for (const [name, count] of Object.entries(counts)) {
        document.getElementById("count-" + name).textContent = String(count);
      }
    }

    function selectTab(name) {
      for (const tab of document.querySelectorAll("[data-tab]")) {
        const selected = tab.dataset.tab === name;
        tab.setAttribute("aria-selected", String(selected));
        tab.tabIndex = selected ? 0 : -1;
        document.getElementById("tab-" + tab.dataset.tab).classList.toggle("hidden", !selected);
      }
    }

    function itemMatches(item) {
      const query = document.getElementById("search").value.trim().toLowerCase();
      const verdictFilter = document.getElementById("verdict-filter").value;
      const readinessFilter = document.getElementById("readiness-filter").value;
      const haystack = [item.number, item.title, item.owner, item.decision, item.nextAction].join(" ").toLowerCase();
      if (query && !haystack.includes(query)) return false;
      if (item.type !== selectedType) return false;
      if (verdictFilter !== "all" && item.decision !== verdictFilter) return false;
      if (readinessFilter === "ready") return item.mergeRequest.eligible;
      if (readinessFilter === "checks") return item.checks && item.checks.pending > 0;
      if (readinessFilter === "proof") return item.proofStatus === "required" || item.proofStatus === "blocked";
      if (readinessFilter === "blocked") return Object.values(item.stages).includes("blocked");
      return true;
    }

    function sortItems(items) {
      const sort = document.getElementById("sort").value;
      return [...items].sort((left, right) => {
        if (sort === "confidence-asc") {
          return left.takeConfidence - right.takeConfidence || right.number - left.number;
        }
        if (sort === "number-desc") return right.number - left.number;
        return right.takeConfidence - left.takeConfidence || right.number - left.number;
      });
    }

    function renderVerdictOptions(items) {
      const select = document.getElementById("verdict-filter");
      const selected = select.value;
      const verdicts = [...new Set(items.map((item) => item.decision))].sort();
      select.replaceChildren(new Option("Verdict", "all"));
      for (const verdict of verdicts) {
        select.append(new Option(verdict, verdict));
      }
      select.value = verdicts.includes(selected) ? selected : "all";
    }

    async function post(path, body) {
      const response = await fetch(path, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Triage-Token": actionToken,
        },
        body: JSON.stringify(body),
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || "Request failed");
      return result;
    }

    function renderItems(items) {
      const root = document.getElementById("items");
      root.replaceChildren();
      const visible = sortItems(items.filter(itemMatches));
      if (!visible.length) {
        root.append(element("p", "item muted", "No items match this filter."));
        return;
      }

      for (const item of visible) {
        const itemReadiness = readiness(item);
        const row = element("article", "item status-" + itemReadiness.variant);
        const head = element("div", "item-head");
        const headingMain = element("div", "item-heading-main");
        const link = element("a", "item-title", "#" + item.number + " " + item.title);
        link.href = item.url;
        link.target = "_blank";
        link.rel = "noreferrer";
        headingMain.append(
          itemIcon(item.type),
          element("span", "badge label-" + itemReadiness.variant, itemReadiness.label),
          link,
        );
        head.append(
          headingMain,
          element("span", "item-decision", item.decision + " · " + item.takeConfidence + "% take"),
        );
        row.append(head);

        const meta = element("div", "item-meta muted");
        meta.append(
          element("span", "", item.owner),
          element("span", "", (item.risk || "Unknown") + " risk"),
          element("span", "", item.live ? String(item.live.mergeStateStatus || item.live.state) : "Live unavailable"),
        );
        if (item.checks) {
          meta.append(element("span", "", item.checks.passed + " passed · " +
            item.checks.failed + " failed · " + item.checks.pending + " pending"));
        }
        row.append(meta, element("p", "item-body", item.nextAction));

        const footer = element("div", "item-footer");
        const stages = element("div", "stages");
        for (const [name, value] of Object.entries(item.stages)) {
          stages.append(element("span", "stage status-" + value, name + " " + statusLabel(value)));
        }

        const actions = element("div", "actions");
        const next = element("button", "control", "Request next step");
        next.type = "button";
        next.addEventListener("click", async () => {
          next.disabled = true;
          try {
            await post("/action", { action: "request_next_action", number: item.number });
            showNotice("Next-step request sent to this Copilot session.");
          } catch (error) {
            showNotice(error.message);
          } finally {
            next.disabled = false;
          }
        });
        const merge = element("button", "control primary", "Prepare merge");
        merge.type = "button";
        merge.disabled = !item.mergeRequest.eligible;
        merge.title = item.mergeRequest.eligible ? "Request fresh merge verification in chat" : item.mergeRequest.reasons.join("; ");
        merge.addEventListener("click", async () => {
          merge.disabled = true;
          try {
            await post("/action", {
              action: "request_merge",
              number: item.number,
              headSha: item.live.headRefOid,
            });
            showNotice("Guarded merge request sent to this Copilot session.");
          } catch (error) {
            showNotice(error.message);
            merge.disabled = false;
          }
        });
        actions.append(next, merge);
        footer.append(stages, actions);
        row.append(footer);
        if (!item.mergeRequest.eligible && item.mergeRequest.reasons.length) {
          const gate = element("details", "gate");
          gate.append(
            element("summary", "", "Why merge is blocked"),
            element("p", "", item.mergeRequest.reasons.join("; ")),
          );
          row.append(gate);
        }
        root.append(row);
      }
    }

    function render(nextState) {
      state = nextState;
      document.getElementById("title").textContent = state.title;
      document.getElementById("scope").textContent = state.scope;
      document.getElementById("updated").textContent = "Live " +
        new Date(state.liveUpdatedAt).toLocaleTimeString();
      const error = document.getElementById("error");
      error.textContent = state.refreshError || "";
      error.classList.toggle("hidden", !state.refreshError);
      renderMetrics(state.summary);
      document.getElementById("type-pr-count").textContent =
        String(state.items.filter((item) => item.type === "pr").length);
      document.getElementById("type-issue-count").textContent =
        String(state.items.filter((item) => item.type === "issue").length);
      renderPlan(state.plan);
      renderReport(state.report);
      renderVerdictOptions(state.items);
      renderItems(state.items);
    }

    async function loadState() {
      const response = await fetch("/state", { cache: "no-store" });
      if (!response.ok) throw new Error("Unable to load triage state");
      render(await response.json());
    }

    document.getElementById("search").addEventListener("input", () => state && renderItems(state.items));
    for (const id of ["verdict-filter", "readiness-filter", "sort"]) {
      document.getElementById(id).addEventListener("change", () => state && renderItems(state.items));
    }
    for (const type of ["pr", "issue"]) {
      document.getElementById("type-" + type).addEventListener("click", () => {
        selectedType = type;
        document.getElementById("type-pr").setAttribute("aria-pressed", String(type === "pr"));
        document.getElementById("type-issue").setAttribute("aria-pressed", String(type === "issue"));
        if (state) renderItems(state.items);
      });
    }
    for (const tab of document.querySelectorAll("[data-tab]")) {
      tab.addEventListener("click", () => selectTab(tab.dataset.tab));
      tab.addEventListener("keydown", (event) => {
        if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
        const tabs = [...document.querySelectorAll("[data-tab]")];
        const offset = event.key === "ArrowRight" ? 1 : -1;
        const next = tabs[(tabs.indexOf(tab) + offset + tabs.length) % tabs.length];
        selectTab(next.dataset.tab);
        next.focus();
      });
    }
    document.getElementById("refresh").addEventListener("click", async (event) => {
      event.currentTarget.disabled = true;
      try {
        const result = await post("/refresh", {});
        render(result.state);
        showNotice("GitHub status refreshed.");
      } catch (error) {
        showNotice(error.message);
      } finally {
        event.currentTarget.disabled = false;
      }
    });

    const events = new EventSource("/events");
    events.addEventListener("state", (event) => render(JSON.parse(event.data)));
    events.addEventListener("error", () => showNotice("Live updates disconnected. Use Refresh now to retry."));
    loadState().catch((error) => showNotice(error.message));
  </script>
</body>
</html>`;
}
