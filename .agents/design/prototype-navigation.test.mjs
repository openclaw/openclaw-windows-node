import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const html = readFileSync(new URL("./prototype-export/index.html", import.meta.url), "utf8");
const source = html.match(/<script>(\/\* proto-render\.js[\s\S]*?)<\/script>/)?.[1];
assert(source, "The export must include the prototype runtime.");
const doc = {
  screens: [{ id: "hub" }, { id: "settings" }],
  state: { settingsTab: "settings", footerMenuOpen: true, dashboardPage: "agents" },
};
function player() {
  const context = vm.createContext({ window: {} });
  vm.runInContext(source, context);
  return context.ProtoRender.createRuntime({ doc: structuredClone(doc) });
}
test("Settings navigation applies the requested subpage before notifying the renderer", () => {
  const runtime = player();
  let observed;
  runtime.onNavigate(() => { observed = runtime.state.settingsTab; });
  runtime.dispatch({ navigate: "settings", setState: { settingsTab: "usage", footerMenuOpen: false } });
  assert.equal(runtime.currentId, "settings");
  assert.equal(observed, "usage");
  assert.equal(runtime.state.footerMenuOpen, false);
  assert.equal(runtime.state.dashboardPage, "agents");
  runtime.dispatch({ back: true });
  assert.equal(runtime.currentId, "hub");
  assert.equal(runtime.state.dashboardPage, "agents");
});
test("a later deep link replaces the previously selected Settings subpage", () => {
  const runtime = player();
  runtime.dispatch({ navigate: "settings", setState: { settingsTab: "usage" } });
  runtime.dispatch({ back: true });
  runtime.dispatch({ navigate: "settings", setState: { settingsTab: "channels" } });
  assert.equal(runtime.currentId, "settings");
  assert.equal(runtime.state.settingsTab, "channels");
});
test("ordinary navigation and state-only actions retain their behavior", () => {
  const runtime = player();
  runtime.dispatch({ setState: { dashboardPage: "systems" } });
  assert.equal(runtime.currentId, "hub");
  runtime.dispatch({ navigate: "settings" });
  assert.equal(runtime.state.dashboardPage, "systems");
  runtime.dispatch({ back: true });
  assert.equal(runtime.currentId, "hub");
  assert.equal(source.includes("action.openWindow"), false);
  assert.equal(source.includes("window.open("), false);
});
