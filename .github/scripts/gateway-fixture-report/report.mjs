import { copyFile, mkdtemp, readdir, readFile, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { XMLParser } from "fast-xml-parser";

const runIdPattern = /^[a-f0-9]{32}$/;
const parser = new XMLParser({
  ignoreAttributes: false,
  attributeNamePrefix: "",
  removeNSPrefix: true,
  isArray: (name) => name === "UnitTestResult",
});

function text(value) {
  return String(value).replace(/[\r\n]/g, " ").replace(/[\\`*_[\]<>|]/g, "\\$&");
}

function slug(value) {
  return value.replace(/[^a-zA-Z0-9_-]+/g, "-").slice(0, 110);
}

async function testResults(directory, results = new Map()) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      await testResults(file, results);
    } else if (entry.isFile() && entry.name.endsWith(".trx")) {
      const report = parser.parse(await readFile(file, "utf8"), true);
      for (const result of report.TestRun?.Results?.UnitTestResult ?? []) {
        const stdout = result.Output?.StdOut;
        if (typeof stdout !== "string") continue;
        for (const match of stdout.matchAll(/Fixture run ([a-f0-9]{32}),/g)) {
          results.set(match[1], { name: result.testName, outcome: result.outcome });
        }
      }
    }
  }
  return results;
}

export async function collectScreenshots(root) {
  // Only test-owned run directories are eligible. Do not follow links or upload profiles/logs.
  const entries = await readdir(root, { withFileTypes: true });
  const results = await testResults(root);
  const screenshots = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || !runIdPattern.test(entry.name)) continue;
    const directory = path.join(root, entry.name);
    const files = await readdir(directory, { withFileTypes: true });
    const images = files.filter((file) => file.isFile() && file.name.endsWith(".png"));
    if (images.length === 0) continue;
    const receipt = files.some((file) => file.isFile() && file.name === "run.json")
      ? JSON.parse(await readFile(path.join(directory, "run.json"), "utf8"))
      : null;
    const result = results.get(entry.name);
    for (const image of images) {
      screenshots.push({
        file: path.join(directory, image.name),
        runId: entry.name,
        test: result?.name ?? `Unreported test (${entry.name})`,
        outcome: result?.outcome ?? receipt?.outcome ?? "Not reported",
        scenario: receipt?.scenario ?? "Not reported",
        capture: image.name,
      });
    }
  }
  return screenshots.sort((a, b) =>
    Number(b.outcome.toLowerCase() === "failed") - Number(a.outcome.toLowerCase() === "failed")
    || a.test.localeCompare(b.test) || a.runId.localeCompare(b.runId)
    || Number(b.capture === "failure.png") - Number(a.capture === "failure.png")
    || a.capture.localeCompare(b.capture));
}

export async function publishScreenshots({ root, artifactClient, runUrl, summary }) {
  const lines = [
    "## Mock Gateway screenshots",
    "",
    "Only failed-test captures are previewed. Successful captures remain in **test-results-ui**.",
    "Click a capture to view the PNG in your browser (GitHub sign-in required).",
    "These show the real Companion app connected to the synthetic mock Gateway, not a production Gateway.",
    "The complete TRX, logs and receipts remain in **test-results-ui** under Artifacts.",
    "",
  ];
  // A failed build may leave no fixture directory. Other filesystem errors remain failures.
  let screenshots;
  try {
    screenshots = await collectScreenshots(root);
  } catch (error) {
    if (error.code !== "ENOENT" || error.path !== root) throw error;
    screenshots = [];
  }
  const failedRuns = new Set(screenshots.filter((image) =>
    image.outcome.toLowerCase() === "failed"
    || (image.outcome === "Not reported" && image.capture === "failure.png"))
    .map((image) => image.runId));
  if (screenshots.length > 0 && failedRuns.size === 0) return;
  screenshots = screenshots.filter((image) => failedRuns.has(image.runId));
  if (screenshots.length === 0) {
    await summary.addRaw(`${lines.join("\n")}\nNo screenshots were captured for failed tests. Check the mock Gateway test step for startup/build failures.\n`).write();
    return;
  }
  // This job also uploads diagnostic artifacts. Never silently omit images at GitHub's 500-artifact limit.
  if (screenshots.length > 450) throw new Error(`Too many screenshots for one job: ${screenshots.length} (limit 450).`);
  const staging = await mkdtemp(path.join(os.tmpdir(), "gateway-screenshots-"));
  let group;
  try {
    for (const [index, screenshot] of screenshots.entries()) {
      if (group !== screenshot.runId) {
        group = screenshot.runId;
        lines.push(`### ${text(screenshot.test)}`, "",
          `**${text(screenshot.outcome)}** | Scenario: ${text(screenshot.scenario)} | Run: \`${group}\``, "");
      }
      // Raw uploads use the file's basename as the artifact name, regardless of the supplied name.
      const name = `${String(index + 1).padStart(3, "0")}-${slug(screenshot.test.split(".").at(-1))}-${screenshot.runId}-${slug(path.parse(screenshot.capture).name).slice(0, 70)}.png`;
      const stagedFile = path.join(staging, name);
      await copyFile(screenshot.file, stagedFile);
      const uploaded = await artifactClient.uploadArtifact(name, [stagedFile], staging, { skipArchive: true });
      if (!Number.isSafeInteger(uploaded.id) || uploaded.id <= 0) throw new Error(`Missing artifact ID for ${name}.`);
      lines.push(`- [${text(screenshot.capture)}](${runUrl}/artifacts/${uploaded.id})`);
    }
  } finally {
    try {
      await summary.addRaw(`${lines.join("\n")}\n`).write();
    } finally {
      await rm(staging, { recursive: true, force: true });
    }
  }
}

export async function run({ core, context }) {
  const { DefaultArtifactClient } = await import("@actions/artifact");
  await publishScreenshots({
    root: path.join(process.env.GITHUB_WORKSPACE, "TestResults", "GatewayFixture"),
    artifactClient: new DefaultArtifactClient(),
    runUrl: `${process.env.GITHUB_SERVER_URL}/${context.repo.owner}/${context.repo.repo}/actions/runs/${context.runId}`,
    summary: core.summary,
  });
}
