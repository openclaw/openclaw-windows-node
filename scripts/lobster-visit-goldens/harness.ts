// Executes the pinned upstream Control UI lobster modules under DOM-free stubs and fake timers, and prints
// tests/OpenClaw.Tray.Tests/LobsterVisitGoldens.g.cs. Run it through scripts/Update-LobsterVisitGoldens.ps1,
// which stages the upstream sources under ./tree and bundles this file with esbuild.
//
// Owner traces neutralize ledge traffic (passers, bottles) and geometry measurement, which the Hub port does not
// include, and use seeds with no molt, twin or Elder load. Step strings are shared verbatim with the C# replay.
import { writeFileSync } from "node:fs";
import { createLobsterPetLook, lobsterLookStyle, lobsterPetName, mulberry32 } from "./tree/ui/src/components/lobster-pet-identity.ts";
import { LobsterPetOwner } from "./tree/ui/src/components/lobster-pet-owner.ts";
import { pickLobsterEntrance } from "./tree/ui/src/components/lobster-pet-plans.ts";
import {
  LobsterComposerGeometry,
  lobsterTravelDuration,
  resolveLobsterComposerScene,
} from "./tree/ui/src/components/lobster-pet-scene.ts";

const upstreamRef = process.argv[2];
const outputPath = process.argv[3];
if (!/^[0-9a-f]{40}$/.test(upstreamRef ?? "") || !outputPath) throw new Error("usage: harness.mjs <upstream-ref> <output-path>");

const RealDate = Date;
const START = new RealDate(2026, 6, 15, 12, 0, 0).getTime();
let now = START;
class FakeDate extends RealDate {
  constructor(...args: any[]) {
    if (args.length === 0) super(now);
    else super(...(args as [any]));
  }
  static now() {
    return now;
  }
}
(globalThis as any).Date = FakeDate;

type Timer = { id: number; due: number; seq: number; fn: () => void };
let timers: Timer[] = [];
let seq = 0;
let nextId = 1;
let reducedMotion = false;
const doc = { hidden: false };
(globalThis as any).document = doc;
(globalThis as any).window = {
  setTimeout(fn: () => void, ms?: number) {
    const id = nextId++;
    timers.push({ id, due: now + Math.max(0, ms ?? 0), seq: seq++, fn });
    return id;
  },
  clearTimeout(id: number) {
    timers = timers.filter((timer) => timer.id !== id);
  },
  matchMedia: () => ({ matches: reducedMotion }),
  dispatchEvent() {},
};

function makeStorage(): Storage {
  const map = new Map<string, string>();
  return {
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => void map.set(key, value),
    removeItem: (key: string) => void map.delete(key),
    clear: () => map.clear(),
    key: () => null,
    length: 0,
  } as unknown as Storage;
}

// Timers fire in due order, then scheduling order, as browsers do for equal deadlines.
function advance(ms: number) {
  const end = now + ms;
  for (;;) {
    timers.sort((a, b) => a.due - b.due || a.seq - b.seq);
    const timer = timers[0];
    if (!timer || timer.due > end) break;
    timers.shift();
    now = timer.due;
    timer.fn();
  }
  now = end;
}

const fl = (value: number) => Math.floor(value * 100);
function project(scene: any): string {
  if (!scene) return "none";
  const travel = scene.travel
    ? `${scene.travel.hop ? "h" : "w"}:${fl(scene.travel.from.x)},${fl(scene.travel.from.y)}>${fl(scene.travel.to.x)},${fl(scene.travel.to.y)}`
    : "-";
  return `${scene.presence} act=${scene.act ?? "-"} spot=${fl(scene.spotPct)} face=${scene.facing} ${scene.anchor} enter=${scene.entering ? scene.entrance : "-"} vigil=${+scene.vigil} grumpy=${+scene.grumpy} travel=${travel} mode=${scene.mode}`;
}

const HUB = { top: { start: 296, end: 649, y: 0 }, floor: null, passage: null };
const ROOMY = { top: { start: 66, end: 734, y: 0 }, floor: { start: 150, end: 394, y: 107 }, passage: [366, 394] };

type Setup = {
  seed: number;
  mode?: "idle" | "busy" | "offline";
  floorEnabled?: boolean;
  scene?: "roomy";
  visits?: number;
  shoos?: number;
  reduced?: boolean;
};

type Api = {
  advance: (ms: number) => void;
  update: (patch: Record<string, unknown>) => void;
  poke: () => void;
  holdStart: () => void;
  holdEnd: () => void;
  holdCancel: () => void;
  dismiss: (permanently: boolean) => void;
  hidden: (hidden: boolean) => void;
  gaze: (pointerX: number, centerX: number) => void;
};

function run(setup: Setup, steps: string[], presenceOnly: boolean): string[] {
  now = START;
  timers = [];
  seq = 0;
  nextId = 1;
  reducedMotion = setup.reduced ?? false;
  doc.hidden = false;
  (globalThis as any).__listeners = [];
  const storage = makeStorage();
  storage.setItem(
    "openclaw.control.lobsterpet.familiarity.v1",
    JSON.stringify({ visits: setup.visits ?? 0, shoos: setup.shoos ?? 0 }),
  );
  (globalThis as any).__storage = storage;
  let props: any = {
    composerElements: undefined,
    seed: setup.seed,
    mode: setup.mode ?? "idle",
    visitsEnabled: true,
    residentEnabled: true,
    critters: [],
    critterArtwork: undefined,
    floorEnabled: setup.floorEnabled ?? false,
    runOutcome: "ok",
    soundsEnabled: false,
    gatewayVersion: null,
    onVisitsDisabled: () => {},
  };
  const host: any = { composerElements: () => null, setAttribute() {}, toggleAttribute() {}, isConnected: true };
  const owner: any = new LobsterPetOwner(host, () => { props = { ...props, visitsEnabled: false }; }, props);
  const geometry = owner.geometry;
  geometry.connect = () => {};
  geometry.dispose = () => {};
  geometry.scheduleMeasure = () => {};
  geometry.scene = setup.scene === "roomy" ? ROOMY : HUB;
  const traffic = owner.traffic;
  for (const name of ["connect", "reset", "update", "replanPasser", "dispose"]) traffic[name] = () => {};
  const lines: string[] = [];
  let last = "";
  let lastPresence = "";
  owner.subscribe(() => {
    const scene = owner.snapshot().scene;
    const line = project(scene);
    if (presenceOnly) {
      if (scene && scene.presence !== lastPresence) {
        lastPresence = scene.presence;
        lines.push(`${Math.floor(now - START)} ${scene.presence}`);
      }
      return;
    }
    if (line !== last) {
      last = line;
      lines.push(`${Math.floor(now - START)} ${line}`);
    }
  });
  owner.connect();
  owner.update({ ...props });
  const api: Api = {
    advance,
    update(patch) {
      props = { ...props, ...patch };
      owner.update({ ...props });
    },
    poke() {
      owner.interactions.handleHoldStart({ button: 0 });
      owner.interactions.handleHoldEnd({ button: 0 });
    },
    holdStart: () => owner.interactions.handleHoldStart({ button: 0 }),
    holdEnd: () => owner.interactions.handleHoldEnd({ button: 0 }),
    holdCancel: () => owner.interactions.handleHoldCancel(),
    dismiss: (permanently) => owner.dismiss(permanently),
    hidden(hidden) {
      doc.hidden = hidden;
      for (const entry of (globalThis as any).__listeners) if (entry.type === "visibilitychange") entry.handler({});
    },
    gaze(pointerX, centerX) {
      owner.interactions.bindSprite({ isConnected: true, getBoundingClientRect: () => ({ left: centerX - 10, width: 20 }) });
      for (const entry of (globalThis as any).__listeners) if (entry.type === "pointermove") entry.handler({ clientX: pointerX });
    },
  };
  for (const step of steps) interpret(api, step);
  owner.dispose();
  return lines;
}

function interpret(api: Api, step: string) {
  const [verb, ...args] = step.split(" ");
  switch (verb) {
    case "advance": return api.advance(Number(args[0]));
    case "poke": return api.poke();
    case "holdStart": return api.holdStart();
    case "holdEnd": return api.holdEnd();
    case "holdCancel": return api.holdCancel();
    case "dismiss": return api.dismiss(args[0] === "permanently");
    case "hidden": return api.hidden(args[0] === "1");
    case "gaze": return api.gaze(Number(args[0]), Number(args[1]));
    case "update": {
      const patch: Record<string, unknown> = {};
      for (const pair of args) {
        const [key, value] = pair.split("=");
        if (key === "mode") patch.mode = value;
        else if (key === "outcome") patch.runOutcome = value;
        else if (key === "floor") patch.floorEnabled = value === "1";
        else if (key === "visits") patch.visitsEnabled = value === "1";
        else throw new Error(`unknown update ${key}`);
      }
      return api.update(patch);
    }
    default: throw new Error(`unknown step ${step}`);
  }
}

type Scenario = { setup: Setup; steps: string[]; presenceOnly?: boolean };
const SCENARIOS: Record<string, Scenario> = {
  scheduleTwoHours: { setup: { seed: 42 }, steps: ["advance 7200000"], presenceOnly: true },
  firstVisitActs: { setup: { seed: 42 }, steps: ["advance 60000"] },
  friendGreets: { setup: { seed: 42, visits: 30 }, steps: ["advance 12000"] },
  shyAndWary: { setup: { seed: 5, visits: 1, shoos: 5 }, steps: ["advance 10800000"], presenceOnly: true },
  shyOpeningStaysAway: { setup: { seed: 7 }, steps: ["advance 7200000"], presenceOnly: true },
  offlineSummon: { setup: { seed: 7, mode: "offline" }, steps: ["advance 15000", "update mode=idle", "advance 2000"] },
  busyThenCheer: { setup: { seed: 42 }, steps: ["advance 8000", "update mode=busy", "advance 10000", "update mode=idle outcome=ok", "advance 3000"] },
  vigilError: { setup: { seed: 0, mode: "busy" }, steps: ["advance 600500", "advance 30000", "update mode=idle outcome=error", "advance 10000"] },
  finishWithoutVigil: { setup: { seed: 42 }, steps: ["advance 100000", "update mode=busy", "advance 66000", "update mode=idle outcome=ok", "advance 3000"] },
  vigilAborted: { setup: { seed: 0, mode: "busy" }, steps: ["advance 600500", "update mode=idle outcome=aborted", "advance 3000"] },
  pokes: { setup: { seed: 42 }, steps: ["advance 8000", "poke", "poke", "poke", "advance 61000", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "advance 1000"] },
  huffReturns: { setup: { seed: 42 }, steps: ["advance 8000", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "poke", "advance 7200000"], presenceOnly: true },
  petHold: { setup: { seed: 42 }, steps: ["advance 8000", "holdStart", "advance 600", "holdEnd", "advance 1600", "holdStart", "advance 300", "holdCancel", "advance 400"] },
  dismissTemporary: { setup: { seed: 42 }, steps: ["advance 8000", "dismiss temporary", "advance 1000", "update mode=offline", "advance 3700000"] },
  hiddenHost: { setup: { seed: 42 }, steps: ["advance 8000", "hidden 1", "advance 30000", "hidden 0", "advance 15000"] },
  reducedMotion: { setup: { seed: 42, reduced: true }, steps: ["advance 60000", "update mode=busy", "advance 10000"] },
  gaze: { setup: { seed: 42 }, steps: ["advance 5700", "gaze -400 100", "advance 50", "gaze 400 100", "advance 200", "gaze 400 100"] },
  visitsToggled: { setup: { seed: 42 }, steps: ["advance 8000", "update visits=0", "advance 1000", "update visits=1", "advance 3700000"], presenceOnly: true },
  dismissPermanently: { setup: { seed: 42 }, steps: ["advance 8000", "dismiss permanently", "advance 1000", "update mode=offline", "advance 3700000"] },
  floorLost: { setup: { seed: 4, scene: "roomy", floorEnabled: true }, steps: ["advance 60000", "update floor=0", "advance 8000"] },
};

// C# emission. Strings escape everything outside printable ASCII so the file stays encoding-neutral.
function str(value: string): string {
  let out = '"';
  for (const ch of value) {
    const code = ch.codePointAt(0)!;
    if (ch === "\\" || ch === '"') out += `\\${ch}`;
    else if (code >= 0x20 && code <= 0x7e) out += ch;
    else if (code <= 0xffff) out += `\\u${code.toString(16).padStart(4, "0")}`;
    else out += `\\U${code.toString(16).padStart(8, "0")}`;
  }
  return `${out}"`;
}
function num(value: number): string {
  if (!Number.isFinite(value)) throw new Error(`non-finite ${value}`);
  return String(value);
}
const bool = (value: boolean) => (value ? "true" : "false");

// Identity: Mulberry32 sequences.
const randomRows = [12345, 0xdeadbeef].map((seed) => {
  const rng = mulberry32(seed);
  const values = [rng(), rng(), rng(), rng(), rng()];
  return `        { ${seed >>> 0}u, [${values.map(num).join(", ")}] },`;
});

// Identity: looks, paint and names. The seeds after 0xdeadbeef cover a clawtron, a left crusher, a mighty glint,
// freckles, a right crusher, golden retro, invisible, mood, shiny, two chimeras and a zombie.
const NEUTRAL = new Date(2026, 6, 15, 12);
const LOOK_SEEDS = [1, 42, 7, 99, 123456789, 0xdeadbeef, 2, 5, 10, 18, 35, 43, 111, 204, 235, 315, 349, 352];
const RENDER_TIMING = /^--lob-(blink|breathe)-delay:/;
const lookRows = LOOK_SEEDS.map((raw) => {
  const seed = raw >>> 0;
  const look: any = createLobsterPetLook(seed, NEUTRAL);
  // Blink and breathe delays are renderer timing, so the style column keeps only paint and claw scale.
  const style = lobsterLookStyle(look).split(";").filter((part: string) => !RENDER_TIMING.test(part)).join(";");
  const chimera = look.chimeraParts
    ? [look.chimeraParts.body, look.chimeraParts.clawLeft, look.chimeraParts.clawRight, look.chimeraParts.antennae].join("/")
    : "-";
  const row = [
    seed, look.palette.id, look.scale, look.accessory, look.antennae, look.spotPct, look.facing, look.personality,
    look.blinkDelayS, look.clawSize, +look.tailFan, +look.shiny, look.crusherSide ?? "-", +look.freckles, look.glint ?? "-",
    chimera, style, lobsterPetName(look, seed),
  ].join(" | ");
  return `        ${str(row)},`;
});

// Identity: seasonal wardrobe over seeds 0..399 per local date, and seed 7's dress.
const WARDROBE_DATES: Array<[number, number, number]> = [
  [2026, 12, 3], [2026, 10, 25], [2026, 10, 19], [2026, 9, 25], [2026, 11, 24], [2026, 11, 25],
];
const wardrobeRows = WARDROBE_DATES.map(([year, month, day]) => {
  const date = new Date(year, month - 1, day, 12);
  const accessories = new Set<string>();
  for (let seed = 0; seed < 400; seed++) accessories.add((createLobsterPetLook(seed, date) as any).accessory);
  const seven: any = createLobsterPetLook(7, date);
  if (seven.chimeraParts) throw new Error("seed 7 became a chimera");
  return `        { ${year}, ${month}, ${day}, ${str([...accessories].sort().join(","))}, ${str(`${seven.palette.id} ${seven.accessory}`)} },`;
});

// Scene: lanes for a Hub-like dense composer. Boxes are left, top, right, bottom in the input's coordinates.
const box = (left: number, top: number, right: number, bottom: number) =>
  ({ left, top, right, bottom, width: right - left, height: bottom - top });
const HUB_CONTROLS = [box(116, 384, 148, 408), box(152, 384, 184, 408), box(560, 384, 760, 408), box(800, 384, 832, 408), box(840, 382, 876, 410)];
const lane = (value: any) => (value ? `x${num(value.start)}-${num(value.end)}/y${num(value.y)}` : "none");
const laneRows = ([[360, false, true], [330, false, true], [330, true, false]] as Array<[number, boolean, boolean]>).map(
  ([editorBottom, twins, pickersAbove]) => {
    const scene = resolveLobsterComposerScene({
      composer: box(100, 300, 900, 420),
      footer: box(108, 380, 892, 412),
      controls: HUB_CONTROLS,
      topObstacles: pickersAbove ? [box(100, 240, 580, 272)] : [],
      editor: box(116, 312, 884, editorBottom),
      placeholderRight: 400,
      twins,
    });
    const passage = scene.passage ? `${num(scene.passage[0])}-${num(scene.passage[1])}` : "none";
    return `        { ${editorBottom}, ${bool(twins)}, ${bool(pickersAbove)}, ${str(lane(scene.top))}, ${str(lane(scene.floor))}, ${str(passage)} },`;
  },
);

// Scene: walks and hops across the roomy scene, and the lanes each needs.
const geometry: any = new (LobsterComposerGeometry as any)({}, () => null, () => false, () => {});
const MOVES: Array<[string, string, string, number, number]> = [
  ["roomy", "walk", "top", 50, 0.2], ["roomy", "walk", "top", 50, 0.51], ["roomy", "walk", "top", 30, 0.285],
  ["roomy", "walk", "floor", 10, 0.9], ["roomy", "walk", "floor", 60, 0.004], ["roomy", "hop", "top", 10, 0],
  ["roomy", "hop", "top", 47, 0], ["roomy", "hop", "floor", 95, 0], ["roomy", "hop", "floor", 99, 0],
  ["topOnly", "walk", "floor", 50, 0.5], ["topOnly", "hop", "top", 47, 0],
];
const moveRows = MOVES.map(([sceneName, kind, anchor, spot, roll]) => {
  geometry.scene = sceneName === "roomy" ? ROOMY : { top: ROOMY.top, floor: null, passage: null };
  const move = kind === "walk" ? geometry.planWalk(anchor, spot, roll) : geometry.planHop(anchor, spot);
  const expected = move
    ? `${move.anchor} ${num(move.spotPct)} ${move.facing} ${num(move.travel.from.x)},${num(move.travel.from.y)} ${num(move.travel.to.x)},${num(move.travel.to.y)} ${move.travel.hop ? "True" : "False"} ${num(lobsterTravelDuration(move.travel))}`
    : "null";
  return `        { ${str(sceneName)}, ${str(kind)}, ${str(anchor)}, ${num(spot)}, ${num(roll)}, ${str(expected)} },`;
});

const entranceRows = [0, 0.059999, 0.06, 0.129999, 0.13, 0.9].map(
  (roll) => `        { ${num(roll)}, ${str(pickLobsterEntrance(roll))} },`,
);

// Owner traces.
const traceRows: string[] = [];
for (const [name, scenario] of Object.entries(SCENARIOS)) {
  const setup = scenario.setup;
  const args = [`Seed: ${setup.seed}`];
  if (setup.mode && setup.mode !== "idle") args.push(`Mode: LobsterVisitMode.${setup.mode[0].toUpperCase()}${setup.mode.slice(1)}`);
  if (setup.floorEnabled) args.push("FloorEnabled: true");
  if (setup.scene === "roomy") args.push("Roomy: true");
  if (setup.visits) args.push(`Visits: ${setup.visits}`);
  if (setup.shoos) args.push(`Shoos: ${setup.shoos}`);
  if (setup.reduced) args.push("ReducedMotion: true");
  if (scenario.presenceOnly) args.push("PresenceOnly: true");
  const lines = run(setup, scenario.steps, scenario.presenceOnly ?? false);
  traceRows.push(`        [${str(name)}] = new(new(${args.join(", ")}),`);
  traceRows.push(`            [${scenario.steps.map(str).join(", ")}],`);
  traceRows.push('            """');
  for (const line of lines) traceRows.push(`            ${line}`);
  traceRows.push('            """),');
}

const out: string[] = [];
const table = (declaration: string, rows: string[]) => {
  out.push(`    ${declaration} => new()`, "    {", ...rows, "    };");
};
out.push(
  "// <auto-generated>",
  "// Generated by scripts/Update-LobsterVisitGoldens.ps1 from the Control UI lobster modules at",
  `// openclaw/openclaw@${upstreamRef}. Do not edit by hand; rerun the script.`,
  "// </auto-generated>",
  "using OpenClawTray.Chat.LobsterVisits;",
  "",
  "namespace OpenClaw.Tray.Tests;",
  "",
  "public sealed partial class LobsterVisitLogicTests",
  "{",
);
table("public static TheoryData<uint, double[]> UpstreamRandom", randomRows);
out.push("");
table("public static TheoryData<string> UpstreamLooks", lookRows);
out.push("");
table("public static TheoryData<int, int, int, string, string> UpstreamWardrobe", wardrobeRows);
out.push("");
table("public static TheoryData<double, bool, bool, string, string, string> UpstreamLanes", laneRows);
out.push("");
table("public static TheoryData<string, string, string, double, double, string> UpstreamMoves", moveRows);
out.push("");
table("public static TheoryData<double, string> UpstreamEntrances", entranceRows);
out.push(
  "}",
  "",
  "public sealed partial class LobsterVisitOwnerTests",
  "{",
  "    private static readonly Dictionary<string, Golden> Goldens = new()",
  "    {",
  ...traceRows,
  "    };",
  "}",
);
writeFileSync(outputPath, `${out.join("\n")}\n`, "utf8");
