#!/usr/bin/env node
/**
 * tools/mxc/run-command.cjs — productized runner for OpenClaw MxcCommandRunner.
 *
 * Reads a single JSON request from stdin describing a system.run invocation
 * plus the SandboxPolicy to apply. Spawns wxc-exec via @microsoft/mxc-sdk's
 * spawnSandboxFromConfig({ usePty: false }) so stdout / stderr stay separate
 * and the exit code is reliable. Writes a single JSON envelope to stdout on
 * completion; node-side errors go to stderr.
 *
 * Wire request (matches BridgeRequest in OneShotAppContainerExecutor.cs):
 *   {
 *     "capabilityCommand": "system.run",
 *     "args": { command: "...", shell: "powershell"|"cmd"|"pwsh", args?: [], ... },
 *     "policy": { version, filesystem, network, ui, timeoutMs },
 *     "cwd": "...", "env": {...}, "timeoutMs": 30000,
 *     "wxcExecPath": "...optional override..."
 *   }
 *
 * Wire response (matches BridgeResponse):
 *   { exitCode, stdout, stderr, timedOut, durationMs, containmentTag }
 *
 * Currently handles system.run only. Other capabilities follow the same envelope
 * shape with capabilityCommand set appropriately and structuredResult populated.
 */

const {
  createConfigFromPolicy,
  spawnSandboxFromConfig,
  getAvailableToolsPolicy,
  getTemporaryFilesPolicy,
} = require('@microsoft/mxc-sdk');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const DEFAULT_MAX_OUTPUT_BYTES = 4 * 1024 * 1024; // mirrors C# DefaultMaxOutputBytes
const HARD_MAX_OUTPUT_BYTES = 256 * 1024 * 1024;  // safety ceiling regardless of caller
const DIRECT_DEBUG_LOG_PATH = path.join(
  process.env.OPENCLAW_TRAY_DATA_DIR ||
    process.env.LOCALAPPDATA && path.join(process.env.LOCALAPPDATA, 'OpenClawTray') ||
    os.tmpdir(),
  'openclaw-mxc-debug.log');
const DIRECT_DEBUG_DIR = path.dirname(DIRECT_DEBUG_LOG_PATH);
const PREFLIGHT_ONLY = true;

/**
 * Match a drive root like "C:\", "D:", "c:\\", etc. The MXC SDK's
 * getAvailableToolsPolicy adds the system drive root as readonly when pwsh.exe
 * is on PATH. That's WAY more access than any preset claims to give the agent
 * (e.g., Locked Down promises "no standard user folders"). We strip drive
 * roots from the merged policy; PATH-specific tool dirs and PSReadLine paths
 * stay, so commands still run.
 */
function isDriveRoot(p) {
  if (!p) return false;
  const norm = path.normalize(p).replace(/[\\/]+$/, '');
  return /^[A-Za-z]:$/.test(norm);
}

/**
 * Match the user's real %TEMP% / %TMP% / os.tmpdir() roots so we can strip
 * them from the merged policy. The bridge substitutes a fresh per-invocation
 * scratch directory in their place (see createScratchDir below).
 */
function isUserTempRoot(p) {
  if (!p) return false;
  const norm = path.normalize(p).toLowerCase().replace(/[\\/]+$/, '');
  const candidates = [
    process.env.TEMP,
    process.env.TMP,
    os.tmpdir(),
  ].filter(Boolean).map(c => path.normalize(c).toLowerCase().replace(/[\\/]+$/, ''));
  return candidates.some(c => c === norm);
}

/**
 * Remove any allow-list entry that overlaps a denied path.
 * Mirrors C# MxcPolicyBuilder.FilterOutDenied. Case-insensitive (NTFS
 * semantics) and tolerant of trailing slashes / mixed separators. Returns
 * a new array; doesn't mutate the input.
 */
function filterOutDenied(allowed, denied) {
  return filterOutDeniedWithReasons(allowed, denied).allowed;
}

function filterOutDeniedWithReasons(allowed, denied) {
  const source = Array.isArray(allowed) ? allowed : [];
  if (source.length === 0) return { allowed: [], removed: [] };
  if (!Array.isArray(denied) || denied.length === 0) return { allowed: source, removed: [] };
  const normalizedDenied = denied
    .map(d => ({ original: d, normalized: normalizePath(d) }))
    .filter(d => d.normalized);
  if (normalizedDenied.length === 0) return { allowed: source, removed: [] };

  const kept = [];
  const removed = [];
  for (const candidate of source) {
    const normalizedCandidate = normalizePath(candidate);
    if (!normalizedCandidate) {
      removed.push({ path: candidate, reason: 'invalid-path' });
      continue;
    }

    const matchedDeny = normalizedDenied.find(d => pathsOverlap(normalizedCandidate, d.normalized));
    if (matchedDeny) {
      removed.push({ path: candidate, reason: 'overlaps-denied-path', deniedPath: matchedDeny.original });
      continue;
    }

    kept.push(candidate);
  }

  return { allowed: kept, removed };
}

function buildPathAccounting(callerPolicy, tools, temp, mergedPolicy) {
  const callerFs = callerPolicy?.filesystem ?? {};
  const toolReadonly = tools?.readonlyPaths ?? [];
  const tempReadwrite = temp?.readwritePaths ?? [];
  return {
    caller: {
      readonlyPaths: callerFs.readonlyPaths ?? [],
      readwritePaths: callerFs.readwritePaths ?? [],
      deniedPaths: callerFs.deniedPaths ?? [],
    },
    sdkAdds: {
      toolReadonlyPaths: toolReadonly,
      tempReadwritePaths: tempReadwrite,
    },
    mergedBeforeScopeFilters: {
      readonlyPaths: mergedPolicy?.filesystem?.readonlyPaths ?? [],
      readwritePaths: mergedPolicy?.filesystem?.readwritePaths ?? [],
      deniedPaths: mergedPolicy?.filesystem?.deniedPaths ?? [],
    },
    network: mergedPolicy?.network ?? null,
    ui: mergedPolicy?.ui ?? null,
  };
}

function pathsOverlap(left, right) {
  return isSameOrNested(left, right) || isSameOrNested(right, left);
}

function isSameOrNested(child, parent) {
  return child === parent || child.startsWith(parent + '\\') || child.startsWith(parent + '/');
}

function normalizePath(p) {
  if (!p) return '';
  try {
    return path.resolve(p).replace(/[\\/]+$/, '').toLowerCase();
  } catch {
    return String(p).toLowerCase();
  }
}

async function main() {
  const startTime = Date.now();
  const req = await readJsonFromStdin();

  // Args specific to system.run. Other capabilities will have their own shapes.
  const args = req.args ?? {};
  const command = typeof args.command === 'string' ? args.command : '';
  const shell = typeof args.shell === 'string' ? args.shell : 'powershell';
  const argv = Array.isArray(args.args) ? args.args : [];

  if (!command) {
    return emit(failResponse(-1, 'Missing required arg: command', startTime));
  }

  // Honor the caller-supplied maxOutputBytes (from C# bridge). Clamp to a
  // hard ceiling so a misconfigured caller can't OOM the bridge process.
  const callerMaxOutput = Number.isFinite(req.maxOutputBytes) && req.maxOutputBytes > 0
    ? Math.min(req.maxOutputBytes, HARD_MAX_OUTPUT_BYTES)
    : DEFAULT_MAX_OUTPUT_BYTES;

  // Compose host-discovered tool/temp paths into the policy supplied by C#.
  const tools = getAvailableToolsPolicy(process.env, { containerType: 'appcontainer' });
  const temp = getTemporaryFilesPolicy(process.env);
  diag('input policy', summarizePolicy(req.policy));
  diag('sdk tools policy', summarizePolicy({ filesystem: tools }));
  diag('sdk temp policy', summarizePolicy({ filesystem: temp }));

  const policy = mergePolicy(req.policy, tools, temp);
  diag('policy merge path accounting', buildPathAccounting(req.policy, tools, temp, policy));

  // SCOPE the merged policy: strip the SDK's "convenience" grants that
  // bypass the user's explicit choices in the Sandbox UI.
  //   - Drive root (C:\) — SDK adds this when pwsh.exe is on PATH. Strip it;
  //     PATH-specific tool dirs (git, node, python, etc.) stay because they
  //     remain in the filtered list.
  //   - User's real %TEMP% — wholesale temp access leaks other apps' files.
  //     We substitute a fresh per-invocation scratch dir as the only writable
  //     temp area, and override TEMP/TMP/TMPDIR in the spawned process's env
  //     so commands that write to %TEMP% land in our scratch dir.
  const beforeScopeReadonly = policy.filesystem.readonlyPaths || [];
  const beforeScopeReadwrite = policy.filesystem.readwritePaths || [];
  const driveRootRemovals = beforeScopeReadonly.filter(isDriveRoot);
  const tempRootRemovals = beforeScopeReadwrite.filter(isUserTempRoot);
  policy.filesystem.readonlyPaths = beforeScopeReadonly.filter(p => !isDriveRoot(p));
  policy.filesystem.readwritePaths = beforeScopeReadwrite.filter(p => !isUserTempRoot(p));
  if (driveRootRemovals.length || tempRootRemovals.length) {
    diag('policy scope filtered sdk convenience grants', {
      removedReadonlyDriveRoots: driveRootRemovals,
      removedReadwriteTempRoots: tempRootRemovals,
    });
  }

  // Mirror the C# MxcPolicyBuilder.FilterOutDenied logic on the JS side after
  // merging the SDK's tools/temp policies. The C# side already stripped any
  // allow-list entry that overlapped a denied path, but the SDK merge re-adds
  // its own allow grants (PATH dirs, PSReadLine history, etc.) that the C#
  // filter never saw. Without this pass, the SDK could grant access to a
  // parent of a denied path — e.g., %LOCALAPPDATA% which contains the browser
  // profile dirs we deny in MxcPolicyBuilder. Belt-and-suspenders deny precedence
  // independent of the @microsoft/mxc-sdk's (undocumented, alpha) deny semantics.
  const readonlyDeniedFilter = filterOutDeniedWithReasons(policy.filesystem.readonlyPaths, policy.filesystem.deniedPaths);
  const readwriteDeniedFilter = filterOutDeniedWithReasons(policy.filesystem.readwritePaths, policy.filesystem.deniedPaths);
  policy.filesystem.readonlyPaths = readonlyDeniedFilter.allowed;
  policy.filesystem.readwritePaths = readwriteDeniedFilter.allowed;
  if (readonlyDeniedFilter.removed.length || readwriteDeniedFilter.removed.length) {
    diag('policy denied-overlap filtered allow grants', {
      removedReadonlyPaths: readonlyDeniedFilter.removed,
      removedReadwritePaths: readwriteDeniedFilter.removed,
    });
  }

  let scratchDir = null;
  try {
    scratchDir = fs.mkdtempSync(path.join(os.tmpdir(), 'openclaw-mxc-'));
  } catch (e) {
    return emit(failResponse(-1, `Failed to create scratch dir: ${e.message}`, startTime));
  }
  policy.filesystem.readwritePaths.push(scratchDir);
  diag('policy scratch grant added', { scratchDir });
  diag('effective policy before config', summarizePolicy(policy));

  try {
    let config;
    try {
      config = createConfigFromPolicy(policy, 'process');
      diag('sandbox config after policy conversion', summarizeConfig(config));
    } catch (e) {
      diag('policy conversion failed', { error: e.message, effectivePolicy: summarizePolicy(policy) });
      return emit(failResponse(-1, `Policy invalid: ${e.message}`, startTime));
    }

    // Build the shell command line. Quote the inner command for the chosen shell.
    config.process.commandLine = buildShellCommandLine(shell, command, argv);
    if (req.cwd) config.process.cwd = req.cwd;
    diag('sandbox process command prepared', {
      shell,
      commandLength: command.length,
      argCount: argv.length,
      cwd: config.process.cwd || null,
      timeoutMs: req.timeoutMs,
    });

    // Override TEMP/TMP/TMPDIR so commands inside the sandbox write to our
    // scratch dir, not the user's real %TEMP% (which we stripped above).
    // New-TemporaryFile, mkdtemp(), etc. all respect these.
    config.process.env = buildSandboxEnv(req.env, scratchDir);
    const sdkTimeoutMs = req.timeoutMs > 0 ? req.timeoutMs : 30000;
    config.process.timeout = sdkTimeoutMs;
    diag('sandbox process env prepared', {
      envKeys: config.process.env.map(e => String(e).split('=')[0]).sort(),
      scratchDir,
      sdkTimeoutMs,
    });

    // CRITICAL: usePty:false — the @microsoft/mxc-sdk default uses node-pty which
    // conflates stdout/stderr and rounds exit codes through PTY signals. We want
    // LocalCommandRunner-equivalent semantics here (separate streams, reliable
    // exit code).
    const spawnOptions = {
      usePty: false,
      debug: false,
    };
    if (req.wxcExecPath) {
      spawnOptions.executablePath = req.wxcExecPath;
    }
    diag('spawnSandboxFromConfig before', {
      spawnOptions,
      config: summarizeConfig(config),
      effectivePolicy: summarizePolicy(policy),
    });

    const artifactPaths = writePreflightArtifacts(policy, config, spawnOptions);
    diag('preflight artifacts written', artifactPaths);

    if (PREFLIGHT_ONLY) {
      diag('spawnSandboxFromConfig skipped', {
        reason: 'preflight-only mode; sandbox execution intentionally disabled',
        artifactPaths,
      });
      return emit({
        exitCode: 0,
        stdout:
          "MXC preflight completed; sandbox execution intentionally skipped.\n" +
          `Effective policy: ${artifactPaths.effectivePolicyPath}\n` +
          `Container config: ${artifactPaths.configPath}\n` +
          `Spawn options: ${artifactPaths.spawnOptionsPath}\n`,
        stderr: '',
        timedOut: false,
        durationMs: Math.max(0, Date.now() - startTime),
        containmentTag: 'mxc-preflight',
      });
    }

    let child;
    try {
      child = spawnSandboxFromConfig(config, spawnOptions);
      diag('spawnSandboxFromConfig after', {
        pid: child && typeof child.pid === 'number' ? child.pid : null,
        stdout: Boolean(child?.stdout),
        stderr: Boolean(child?.stderr),
      });
    } catch (e) {
      diag('spawnSandboxFromConfig failed', { error: e.message, config: summarizeConfig(config) });
      return emit(failResponse(-1, `spawnSandboxFromConfig failed: ${e.message}`, startTime));
    }

    let stdout = '';
    let stderr = '';
    let stdoutBytes = 0;
    let stderrBytes = 0;
    let truncated = false;

    child.stdout?.on('data', (chunk) => {
      const text = chunk.toString();
      if (stdoutBytes + text.length > callerMaxOutput) {
        stdout += text.substring(0, callerMaxOutput - stdoutBytes);
        stdoutBytes = callerMaxOutput;
        truncated = true;
      } else {
        stdout += text;
        stdoutBytes += text.length;
      }
    });
    child.stderr?.on('data', (chunk) => {
      const text = chunk.toString();
      if (stderrBytes + text.length > callerMaxOutput) {
        stderr += text.substring(0, callerMaxOutput - stderrBytes);
        stderrBytes = callerMaxOutput;
        truncated = true;
      } else {
        stderr += text;
        stderrBytes += text.length;
      }
    });

    const exitCode = await new Promise((resolve) => {
      child.on('close', (code) => resolve(code ?? -1));
      child.on('error', (err) => {
        stderr += `\n[bridge] spawn error: ${err.message}`;
        resolve(-1);
      });
    });

    if (truncated) {
      stderr += `\n[bridge] output truncated at ${callerMaxOutput} bytes`;
    }

    // Heuristic for SDK-level timeout: if elapsed >= the timeout we passed to the
    // SDK and the child exited non-zero, we treat it as a timeout. The SDK kills
    // the child on timeout but doesn't surface a distinct exit code, so this is
    // the cleanest signal available without poking SDK internals.
    const durationMs = Date.now() - startTime;
    const timedOut = exitCode !== 0 && durationMs >= sdkTimeoutMs;
    diag('sandbox child closed', {
      exitCode,
      durationMs,
      timedOut,
      stdoutBytes,
      stderrBytes,
      truncated,
    });

    emit({
      exitCode,
      stdout,
      stderr,
      timedOut,
      durationMs,
      containmentTag: 'mxc',
    });
  } finally {
    // Best-effort scratch cleanup. If the user's command spawned a detached
    // process that's still using the dir we may fail here — that's fine, the
    // OS will reap it eventually since these live under %TEMP%.
    if (scratchDir) {
      try { fs.rmSync(scratchDir, { recursive: true, force: true }); } catch { /* ignore */ }
    }
  }
}

function mergePolicy(callerPolicy, tools, temp) {
  const fs0 = callerPolicy?.filesystem ?? {};
  return {
    version: callerPolicy?.version ?? '0.4.0-alpha',
    filesystem: {
      readonlyPaths: dedupe([
        ...(fs0.readonlyPaths ?? []),
        ...(tools?.readonlyPaths ?? []),
      ]),
      readwritePaths: dedupe([
        ...(fs0.readwritePaths ?? []),
        ...(temp?.readwritePaths ?? []),
      ]),
      deniedPaths: fs0.deniedPaths ?? [],
      clearPolicyOnExit: fs0.clearPolicyOnExit ?? true,
    },
    network: callerPolicy?.network ?? { allowOutbound: false, allowLocalNetwork: false },
    ui: callerPolicy?.ui ?? { allowWindows: false, clipboard: 'none', allowInputInjection: false },
    timeoutMs: callerPolicy?.timeoutMs,
  };
}

function dedupe(arr) {
  return Array.from(new Set(arr.filter(Boolean)));
}

const BASE_ENV_ALLOWLIST = [
  'ALLUSERSPROFILE',
  'APPDATA',
  'ComSpec',
  'CommonProgramFiles',
  'CommonProgramFiles(x86)',
  'CommonProgramW6432',
  'HOMEDRIVE',
  'HOMEPATH',
  'LOCALAPPDATA',
  'NUMBER_OF_PROCESSORS',
  'OS',
  'PATH',
  'PATHEXT',
  'PROCESSOR_ARCHITECTURE',
  'PROCESSOR_IDENTIFIER',
  'PROCESSOR_LEVEL',
  'PROCESSOR_REVISION',
  'ProgramData',
  'ProgramFiles',
  'ProgramFiles(x86)',
  'ProgramW6432',
  'PUBLIC',
  'SystemDrive',
  'SystemRoot',
  'USERDOMAIN',
  'USERNAME',
  'USERPROFILE',
  'windir',
];

function buildSandboxEnv(requestEnv, scratchDir) {
  const env = {};
  for (const name of BASE_ENV_ALLOWLIST) {
    if (Object.prototype.hasOwnProperty.call(process.env, name)) {
      env[name] = process.env[name];
    }
  }

  if (requestEnv && typeof requestEnv === 'object') {
    for (const [name, value] of Object.entries(requestEnv)) {
      if (isRequestEnvNameBlocked(name) || value == null) continue;
      env[name] = String(value);
    }
  }

  env.TEMP = scratchDir;
  env.TMP = scratchDir;
  env.TMPDIR = scratchDir;
  return Object.entries(env).map(([k, v]) => `${k}=${v}`);
}

function isRequestEnvNameBlocked(name) {
  if (!name || /[=\0\r\n\s]/.test(name)) return true;
  const upper = String(name).toUpperCase();
  if ([
    'PATH',
    'PATHEXT',
    'COMSPEC',
    'PSMODULEPATH',
    'NODE_OPTIONS',
    'NODE_PATH',
    'PYTHONPATH',
    'PYTHONSTARTUP',
    'PYTHONUSERBASE',
    'RUBYOPT',
    'RUBYLIB',
    'PERL5OPT',
    'PERL5LIB',
    'PERLIO',
    'GIT_SSH',
    'GIT_SSH_COMMAND',
    'GIT_EXEC_PATH',
    'GIT_PROXY_COMMAND',
    'GIT_ASKPASS',
    'BASH_ENV',
    'ENV',
    'CDPATH',
    'PROMPT_COMMAND',
    'ZDOTDIR',
  ].includes(upper)) return true;
  if (upper.startsWith('LD_') || upper.startsWith('DYLD_')) return true;
  return hasCredentialMarker(upper);
}

function hasCredentialMarker(name) {
  const segments = name.split(/[_\-.]/).filter(Boolean);
  const has = (segment) => segments.includes(segment);
  const hasPair = (first, second) => {
    for (let i = 0; i < segments.length - 1; i++) {
      if (segments[i] === first && segments[i + 1] === second) return true;
    }
    return false;
  };
  return has('TOKEN') ||
    has('SECRET') ||
    has('PASSWORD') ||
    has('PASSWD') ||
    has('CREDENTIAL') ||
    has('CREDENTIALS') ||
    hasPair('API', 'KEY') ||
    hasPair('ACCESS', 'KEY') ||
    hasPair('PRIVATE', 'KEY') ||
    hasPair('CLIENT', 'SECRET') ||
    hasPair('CONNECTION', 'STRING') ||
    name.includes('CONNSTR');
}

function buildShellCommandLine(shell, command, argv) {
  const sh = shell.toLowerCase();
  if (sh === 'cmd') {
    // For cmd.exe, wrap the entire command line in outer quotes via /S /C.
    // /S strips exactly the first and last `"` of the operand before parsing,
    // so the inner content (already quoteArg-escaped for args) is passed
    // through verbatim. DO NOT double-escape `"` here — quoteArg already
    // doubles inner quotes per cmd's escape convention; running .replace on
    // the concatenated string would quadruple them.
    const argsSuffix = (argv && argv.length > 0)
      ? ' ' + argv.map((a) => quoteArg(a, /*isCmd*/ true)).join(' ')
      : '';
    const inner = command + argsSuffix;
    return `cmd.exe /S /C "${inner}"`;
  }
  // PowerShell variants: use -EncodedCommand with UTF-16LE Base64. PowerShell
  // decodes it back into a single command expression that is NOT subject to
  // outer command-line metacharacter interpretation. This is the most robust
  // way to pass an agent-supplied command without leaking control characters
  // (`;`, `|`, `&`, etc.) into the outer cmdline parser.
  const argsSuffix = (argv && argv.length > 0)
    ? ' ' + argv.map((a) => quoteArg(a, /*isCmd*/ false)).join(' ')
    : '';
  const psExpression = command + argsSuffix;
  const encoded = Buffer.from(psExpression, 'utf16le').toString('base64');
  if (sh === 'pwsh') {
    return `pwsh.exe -NoProfile -NonInteractive -EncodedCommand ${encoded}`;
  }
  return `powershell.exe -NoProfile -NonInteractive -EncodedCommand ${encoded}`;
}

// Shell metacharacters whose presence forces quoting. Matches the set used by
// OpenClaw.Shared.ShellQuoting.NeedsQuoting on the C# side so the bridge has
// the same quoting behavior as LocalCommandRunner. Quoting a switch like
// `-Name` would break PowerShell parameter binding (PowerShell sees it as a
// string literal, not a parameter) — we conditional-quote like the host does.
const SHELL_METACHARS = /[ \t"'&|;<>()^%!$`*?\[\]{}~\n\r]/;

function needsQuoting(arg) {
  // Empty string needs explicit quotes so it's preserved as an argv element.
  if (arg === '' || arg == null) return true;
  return SHELL_METACHARS.test(arg);
}

function quoteArg(arg, isCmd) {
  if (!needsQuoting(arg)) return String(arg);
  // Minimal quoting; matches OpenClaw.Shared/ShellQuoting semantics for cmd
  // (double-quote with escaped inner quotes) and PowerShell (single quotes).
  if (isCmd) {
    return `"${String(arg).replace(/"/g, '""')}"`;
  }
  return `'${String(arg).replace(/'/g, "''")}'`;
}

function readJsonFromStdin() {
  return new Promise((resolve, reject) => {
    const chunks = [];
    process.stdin.on('data', (c) => chunks.push(c));
    process.stdin.on('end', () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
      } catch (e) {
        reject(e);
      }
    });
    process.stdin.on('error', reject);
  });
}

function emit(response) {
  process.stdout.write(JSON.stringify(response));
}

function diag(label, detail) {
  const line = `[${new Date().toISOString()}] [openclaw-mxc-debug] ${label}: ${JSON.stringify(detail)}\n`;
  try {
    process.stderr.write(line);
  } catch {
    // Diagnostics must never affect sandbox execution.
  }
  try {
    fs.mkdirSync(DIRECT_DEBUG_DIR, { recursive: true });
    fs.appendFileSync(DIRECT_DEBUG_LOG_PATH, line, 'utf8');
  } catch {
    // Diagnostics must never affect sandbox execution.
  }
}

function writePreflightArtifacts(policy, config, spawnOptions) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').replace(/Z$/, '');
  const prefix = path.join(DIRECT_DEBUG_DIR, `openclaw-mxc-preflight-${stamp}-${process.pid}`);
  const effectivePolicyPath = `${prefix}.effective-policy.json`;
  const configPath = `${prefix}.container-config.json`;
  const spawnOptionsPath = `${prefix}.spawn-options.json`;

  fs.mkdirSync(DIRECT_DEBUG_DIR, { recursive: true });
  fs.writeFileSync(effectivePolicyPath, JSON.stringify(policy, null, 2), 'utf8');
  fs.writeFileSync(configPath, JSON.stringify(config, null, 2), 'utf8');
  fs.writeFileSync(spawnOptionsPath, JSON.stringify(spawnOptions, null, 2), 'utf8');

  return { effectivePolicyPath, configPath, spawnOptionsPath };
}

function summarizePolicy(policy) {
  const fsPolicy = policy?.filesystem ?? {};
  return {
    version: policy?.version ?? null,
    filesystem: {
      readonlyPaths: fsPolicy.readonlyPaths ?? [],
      readwritePaths: fsPolicy.readwritePaths ?? [],
      deniedPaths: fsPolicy.deniedPaths ?? [],
      clearPolicyOnExit: fsPolicy.clearPolicyOnExit ?? null,
    },
    network: policy?.network ?? null,
    ui: policy?.ui ?? null,
    timeoutMs: policy?.timeoutMs ?? null,
  };
}

function summarizeConfig(config) {
  const processConfig = config?.process ?? {};
  return {
    keys: config && typeof config === 'object' ? Object.keys(config).sort() : [],
    policyPath: firstString(config, ['policyPath', 'policyFile', 'policyFilePath', 'configPath', 'configFilePath']),
    process: {
      commandLineLength: typeof processConfig.commandLine === 'string' ? processConfig.commandLine.length : 0,
      cwd: processConfig.cwd ?? null,
      timeout: processConfig.timeout ?? null,
      envKeys: Array.isArray(processConfig.env)
        ? processConfig.env.map(e => String(e).split('=')[0]).sort()
        : [],
    },
  };
}

function firstString(source, names) {
  if (!source || typeof source !== 'object') return null;
  for (const name of names) {
    if (typeof source[name] === 'string') return source[name];
  }
  return null;
}

function failResponse(exitCode, errorMessage, startTime = Date.now()) {
  return {
    exitCode,
    stdout: '',
    stderr: errorMessage,
    timedOut: false,
    durationMs: Math.max(0, Date.now() - startTime),
    containmentTag: 'mxc',
  };
}

main().catch((err) => {
  process.stdout.write(JSON.stringify({
    exitCode: -1,
    stdout: '',
    stderr: `[bridge] unhandled error: ${err && err.message ? err.message : String(err)}`,
    timedOut: false,
    durationMs: 0,
    containmentTag: 'mxc',
  }));
  process.exit(0); // exit 0 so the host always sees our envelope, not a Node crash
});
