#!/usr/bin/env node

const {
  getPlatformSupport,
  getAvailableToolsPolicy,
  getTemporaryFilesPolicy,
  spawnSandboxAsync,
} = require('@microsoft/mxc-sdk');

async function main() {
  const support = getPlatformSupport();
  if (!support.isSupported) {
    console.error(`MXC is not supported: ${support.reason || 'unknown reason'}`);
    process.exit(2);
    return;
  }

  const tools = getAvailableToolsPolicy(process.env);
  const temp = getTemporaryFilesPolicy(process.env);

  // Based on MXC SDK README examples: define a minimal policy and run one command.
  const policy = {
    version: '0.4.0-alpha',
    filesystem: {
      readonlyPaths: tools.readonlyPaths,
      readwritePaths: temp.readwritePaths,
    },
    network: {
      allowOutbound: false,
      allowLocalNetwork: false,
    },
  };

  const script = 'cmd.exe /d /c "cd /d %SystemRoot% && echo Waiting 20 seconds so you can inspect Task Manager... && timeout /t 20 /nobreak >nul && echo Hello from MXC sandbox && ver && echo COMPUTERNAME=%COMPUTERNAME% && echo PROCESSOR_ARCHITECTURE=%PROCESSOR_ARCHITECTURE%"';

  const result = await spawnSandboxAsync(script, policy, { debug: false });

  const payload = {
    ranInsideMxc: true,
    runner: 'spawnSandboxAsync',
    executedCommandInSandbox: script,
    exitCode: result.exitCode,
    stdout: result.stdout,
    stderr: result.stderr,
  };

  process.stdout.write(`${JSON.stringify(payload, null, 2)}\n`);
  process.exit(result.exitCode === 0 ? 0 : 1);
}

main().catch((err) => {
  console.error(err && err.message ? err.message : String(err));
  process.exit(1);
});
