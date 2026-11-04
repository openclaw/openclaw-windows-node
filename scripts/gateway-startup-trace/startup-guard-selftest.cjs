"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
if (process.env.OPENCLAW_COMPARISON_ENTRY) {
  fs.accessSync(process.env.OPENCLAW_COMPARISON_ENTRY, fs.constants.R_OK);
}
if (process.env.OPENCLAW_COMPARISON_GUEST_USER) {
  const expected = process.env.OPENCLAW_COMPARISON_GUEST_USER.split("\\").pop().toLowerCase();
  assert.equal(require("node:os").userInfo().username.toLowerCase(), expected);
}
if (process.env.OPENCLAW_COMPARISON_NODE_SHA256) {
  const actual = require("node:crypto").createHash("sha256").update(fs.readFileSync(process.execPath)).digest("hex");
  assert.equal(actual, process.env.OPENCLAW_COMPARISON_NODE_SHA256);
}
if (process.env.OPENCLAW_COMPARISON_ASSERT_SANITIZED) {
  assert.equal(process.env.OPENCLAW_COMPARISON_PARENT_ONLY, undefined);
  assert.equal(process.env.OPENCLAW_COMPARISON_LAUNCH, undefined);
  assert.equal(process.env.OPENCLAW_COMPARISON_CODE, undefined);
}
assert.throws(() => require("node:child_process").spawn("not-executed"), {
  code: "OPENCLAW_COMPARISON_BLOCKED",
});
assert.throws(() => require("node:net").createConnection(1), {
  code: "OPENCLAW_COMPARISON_BLOCKED",
});
assert.throws(() => new (require("node:dns").Resolver)().resolve4("example.invalid"), {
  code: "OPENCLAW_COMPARISON_BLOCKED",
});
assert.throws(() => new (require("node:dns").promises.Resolver)().resolve4("example.invalid"), {
  code: "OPENCLAW_COMPARISON_BLOCKED",
});
process.stderr.write("[gateway] startup trace: cli.bootstrap.admission.session-runtime-import 12.5ms total=30.0ms start=17.5ms\n");
process.stderr.write("[gateway] startup phase: cli.bootstrap.admission.agent-inventory starting total=30.2ms\n");
process.stderr.write("[gateway] startup trace: cli.bootstrap.admission.plugin-config 25.0ms total=60.0ms start=35.0ms\n");
process.stderr.write("[gateway] startup phase: cli.bootstrap.admission.config-recovery starting total=60.2ms\n");
throw new Error("The comparison guard did not stop at the required boundary");
