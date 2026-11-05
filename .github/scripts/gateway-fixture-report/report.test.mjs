import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { collectScreenshots, publishScreenshots } from "./report.mjs";

const passedRun = "a".repeat(32);
const failedRun = "b".repeat(32);
const runUrl = "https://github.com/openclaw/openclaw-windows-node/actions/runs/123";
const png = Buffer.from("89504e470d0a1a0a000102", "hex");

async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), "gateway-report-test-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  return root;
}

async function capture(root, id, names, outcome = "passed") {
  const directory = path.join(root, id);
  await mkdir(directory, { recursive: true });
  await writeFile(path.join(directory, "run.json"), JSON.stringify({
    scenario: "multi-session-browse", outcome, gatewayToken: "must-not-be-published",
  }));
  for (const name of names) await writeFile(path.join(directory, name), png);
}

async function trx(root) {
  const directory = path.join(root, "trx", "invocation");
  await mkdir(directory, { recursive: true });
  await writeFile(path.join(directory, "fixture-ui.trx"), `<?xml version="1.0"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Fixture.Success" outcome="Passed"><Output><StdOut>Fixture run ${passedRun}, PID 12, artifacts: C:\\test</StdOut></Output></UnitTestResult>
    <UnitTestResult testName="Fixture.Failed&lt;case&gt;" outcome="Failed"><Output><StdOut>Fixture run ${failedRun}, PID 34, artifacts: C:\\test</StdOut></Output></UnitTestResult>
  </Results>
</TestRun>`);
}

function summary() {
  return { body: "", written: false,
    addRaw(value) { this.body += value; return this; },
    async write() { this.written = true; },
  };
}

test("indexes actual test names, failed runs first, then failure capture; excludes non-run files", async (t) => {
  const root = await fixture(t);
  await capture(root, passedRun, ["connected.png"]);
  await capture(root, failedRun, ["narrow.png", "failure.png"], "failed");
  await capture(root, "profile", ["private.png"]);
  await writeFile(path.join(root, "unrelated.png"), png);
  await trx(root);
  const images = await collectScreenshots(root);
  assert.deepEqual(images.map(({ test, outcome, capture }) => [test, outcome, capture]), [
    ["Fixture.Failed<case>", "Failed", "failure.png"],
    ["Fixture.Failed<case>", "Failed", "narrow.png"],
    ["Fixture.Success", "Passed", "connected.png"],
  ]);
});

test("publishes only failed-test images, leaving passed captures in the diagnostic archive", async (t) => {
  const root = await fixture(t);
  await capture(root, passedRun, ["failure.png"]);
  await capture(root, failedRun, ["connected.png", "failure.png"], "failed");
  await trx(root);
  const report = summary();
  const uploads = [];
  await publishScreenshots({ root, runUrl, summary: report, artifactClient: {
    async uploadArtifact(name, files, directory, options) {
      uploads.push({ name, files, directory, options });
      assert.equal(files.length, 1);
      assert.equal(name, path.basename(files[0]));
      assert.deepEqual(await readFile(files[0]), png);
      assert.deepEqual(options, { skipArchive: true });
      return { id: 100 + uploads.length };
    },
  } });
  assert.equal(uploads.length, 2);
  assert.notEqual(uploads[0].name, uploads[1].name);
  assert.match(uploads[0].name, /^001-Failed-case-.*-failure\.png$/);
  assert.match(report.body, /### Fixture\.Failed\\<case\\>/);
  assert.ok(report.body.includes(`[failure.png](${runUrl}/artifacts/101)`));
  assert.ok(report.body.includes(`[connected.png](${runUrl}/artifacts/102)`));
  assert.ok(!report.body.includes("Fixture.Success"));
  assert.deepEqual(await readFile(path.join(root, passedRun, "failure.png")), png);
  assert.ok(report.body.includes("test-results-ui"));
  assert.ok(!report.body.includes("must-not-be-published"));
  assert.equal(report.written, true);
  await assert.rejects(readdir(uploads[0].directory), { code: "ENOENT" });
});

test("successful runs produce no preview summary or uploads, even above the preview budget", async (t) => {
  const root = await fixture(t);
  await capture(root, passedRun, Array.from({ length: 451 }, (_, index) => `${index}.png`));
  const report = summary();
  await publishScreenshots({ root, runUrl, summary: report, artifactClient: {
    async uploadArtifact() { assert.fail("Successful screenshots belong only in test-results-ui"); },
  } });
  assert.equal(report.body, "");
  assert.equal(report.written, false);
  assert.equal((await readdir(path.join(root, passedRun))).filter((name) => name.endsWith(".png")).length, 451);
});

test("retains successfully published navigation and propagates upload failures", async (t) => {
  const root = await fixture(t);
  await capture(root, failedRun, ["a.png", "b.png"], "failed");
  const report = summary();
  let count = 0;
  let staging;
  await assert.rejects(publishScreenshots({ root, runUrl, summary: report, artifactClient: {
    async uploadArtifact(_name, _files, directory) {
      staging = directory;
      if (++count === 2) throw new Error("upload unavailable");
      return { id: 201 };
    },
  } }), /upload unavailable/);
  assert.equal(report.written, true);
  assert.ok(report.body.includes(`${runUrl}/artifacts/201`));
  await assert.rejects(readdir(staging), { code: "ENOENT" });
});

test("missing or empty fixture output explicitly reports no screenshots without uploading", async (t) => {
  const directory = await fixture(t);
  for (const root of [directory, path.join(directory, "missing")]) {
    const report = summary();
    await publishScreenshots({ root, runUrl, summary: report, artifactClient: {
      async uploadArtifact() { assert.fail("No screenshots to upload"); },
    } });
    assert.match(report.body, /No screenshots were captured/);
    assert.equal(report.written, true);
  }
});

test("an interrupted test without a receipt or TRX still publishes its failure capture", async (t) => {
  const root = await fixture(t);
  await mkdir(path.join(root, failedRun));
  await writeFile(path.join(root, failedRun, "failure.png"), png);
  const [image] = await collectScreenshots(root);
  assert.equal(image.runId, failedRun);
  assert.equal(image.capture, "failure.png");
  assert.equal(image.outcome, "Not reported");
  const report = summary();
  let uploaded = false;
  await publishScreenshots({ root, runUrl, summary: report, artifactClient: {
    async uploadArtifact() { uploaded = true; return { id: 301 }; },
  } });
  assert.equal(uploaded, true);
  assert.ok(report.body.includes(`${runUrl}/artifacts/301`));
});

test("malformed evidence is not silently treated as a successful empty report", async (t) => {
  const root = await fixture(t);
  await writeFile(path.join(root, "bad.trx"), "<TestRun><Results></TestRun>");
  await assert.rejects(collectScreenshots(root), /closing tag|Invalid/i);
});

test("fails explicitly before uploading when the per-job image budget is exceeded", async (t) => {
  const root = await fixture(t);
  await capture(root, failedRun, Array.from({ length: 451 }, (_, index) => `${index}.png`), "failed");
  await assert.rejects(publishScreenshots({ root, runUrl, summary: summary(), artifactClient: {
    async uploadArtifact() { assert.fail("Budget must be checked before upload"); },
  } }), /Too many screenshots.*451/);
});
