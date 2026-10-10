// Run on native Windows with Node >=24 after npm ci. No child is launched.
// Regenerate the policy fixtures through the pinned SDK, retaining only the
// normalization performed by the companion's golden-test comparison.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

async function main() {
  assert.equal(process.platform, 'win32', 'Generate Windows policies on Windows');
  assert.equal(require('@microsoft/mxc-sdk/package.json').version, '0.9.0');
  const { createConfigFromPolicy } = await import('@microsoft/mxc-sdk');
  const root = path.resolve(__dirname, '../..');
  const p = name => `C:\\Golden\\${name}`;
  const folders = ['Documents', 'Downloads', 'Desktop'].map(p);
  const presets = {
    'locked-down': { ro: [], rw: [], outbound: false, clipboard: 'none' },
    balanced: { ro: folders, rw: [], outbound: true, clipboard: 'read' },
    permissive: { ro: [], rw: folders, outbound: true, clipboard: 'all' },
    custom: { ro: [p('Documents')], rw: [p('Custom')], outbound: true, clipboard: 'all' },
  };
  for (const [name, preset] of Object.entries(presets)) {
    const config = createConfigFromPolicy({
      version: '0.9.0-alpha',
      filesystem: {
        readonlyPaths: preset.ro,
        readwritePaths: [...preset.rw, p('Scratch')],
        clearPolicyOnExit: true,
      },
      network: {
        egress: { default: preset.outbound ? 'allow' : 'deny' },
        ingress: { default: 'deny', hostLoopback: 'deny' },
      },
      ui: { allowWindows: false, clipboard: preset.clipboard, allowInputInjection: false },
    }, 'process', `golden-${name}`);
    // Omission with a processContainer block selects that same native backend.
    assert.equal(config.containment, 'process');
    delete config.containment;
    config.process = {};
    delete config.filesystem.deniedPaths;
    // The JS convenience builder still adds redundant network capabilities.
    // The 0.9 native directional-policy owner discards/rederives these itself.
    assert.deepEqual(config.processContainer.capabilities, preset.outbound ? ['internetClient'] : []);
    config.processContainer.capabilities = [];
    const target = path.join(root, 'tests/OpenClaw.Shared.Tests/Mxc/Golden', `sdk-config-${name}.json`);
    const json = `${JSON.stringify(config, null, 2)}\n`;
    if (process.argv.includes('--check')) assert.equal(fs.readFileSync(target, 'utf8').replace(/\r\n/g, '\n'), json, target);
    else fs.writeFileSync(target, json);
    console.log(`${name}: SDK 0.9 policy ${process.argv.includes('--check') ? 'verified' : 'generated'}`);
  }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
