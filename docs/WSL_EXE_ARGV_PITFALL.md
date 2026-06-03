# WSL.exe argv variable-expansion pitfall

## Summary

`wsl.exe -- bash -c <script>` expands shell-variable references in argv before invoking `bash`, so Bash receives an already-mutated script string; any `$var` or `${var}` not defined in the Windows process environment at `wsl.exe` invocation time is dropped to an empty string.

## Reproduction

```powershell
function Invoke-Wsl([string[]]$arr) {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = "wsl.exe"
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  foreach ($a in $arr) { [void]$psi.ArgumentList.Add($a) }
  $p = [System.Diagnostics.Process]::Start($psi)
  $out = $p.StandardOutput.ReadToEnd()
  $err = $p.StandardError.ReadToEnd()
  $p.WaitForExit()
  "EXIT=$($p.ExitCode) STDOUT=[$out] STDERR=[$err]"
}

# BROKEN: argv path — $x is dropped before bash sees it
Invoke-Wsl @("-d","Ubuntu-26.04","--","bash","-c","x=abc; echo VAL=`$x")
# → EXIT=0 STDOUT=[VAL=]  ← assignment ran, but $x was already expanded to empty

# WORKING: stdin path — script bytes arrive at bash intact
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "wsl.exe"
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardInput = $true
$psi.UseShellExecute = $false
foreach ($a in @("-d","Ubuntu-26.04","--","bash","-s")) { [void]$psi.ArgumentList.Add($a) }
$p = [System.Diagnostics.Process]::Start($psi)
$p.StandardInput.WriteLine("x=abc; echo VAL=`$x")
$p.StandardInput.Close()
$p.StandardOutput.ReadToEnd()
# → VAL=abc
```

## What bash actually receives

Empirical results from dumping `/proc/$$/cmdline` inside Bash on a fresh Ubuntu-26.04 WSL distro:

| Pattern in script string | What bash actually receives |
|---|---|
| `$PATH` (defined in Windows env at `wsl.exe` invocation time) | the full Windows `PATH` expansion |
| `$workspace` (not defined in Windows env) | **removed (empty)** |
| `${workspace}` braced (not defined in Windows env) | **removed (empty)** |
| `$$` | `wsl.exe` parent process's PID, not the Bash PID |
| `\$workspace` backslash-escaped | preserved as `$workspace`; Bash then expands it normally |
| `$(echo hi)` command substitution | preserved; Bash expands it |
| Single-quoted `'$workspace'` | still expanded; single quotes do not help because `wsl.exe` runs before Bash |
| Double-quoted `"$workspace"` | still expanded |
| Subshell `( workspace=x; echo $workspace )` | the inner `$workspace` is still dropped |
| Prefix assignment `x=abc echo $x` | `$x` is still dropped |

Concrete failure mode:

```bash
workspace='/home/openclaw/.openclaw/workspace'
mkdir -p "$workspace"   # → mkdir: cannot create directory '': No such file or directory
```

The assignment runs because it has no `$var` reference, but `$workspace` on the next line is removed during `wsl.exe` argv translation.

## Why

`wsl.exe`'s argv translation layer treats argv strings as command-line text and performs shell metacharacter expansion before launching the target process. By the time `bash -c` runs, the original `$var` syntax is gone and Bash cannot recover it. This behavior is consistent across single quotes, double quotes, braces, subshells, and prefix assignment because they are all interpreted by `wsl.exe` before Bash sees the script.

## Fixes (in order of preference)

### 1. Pipe the script over stdin via `RunInWslAsync(..., inputViaStdin: true)`

`wsl.exe` does not rewrite stdin. Prefer this for any multi-line script that uses Bash variables, `${...}`, or `$$`.

```csharp
var script = """
workspace='/home/openclaw/.openclaw/workspace'
mkdir -p "$workspace"
printf 'bash pid=%s\n' "$$"
""";

await commandRunner.RunInWslAsync(
    distroName,
    script,
    cancellationToken,
    inputViaStdin: true);
```

### 2. C#-interpolate every value into the script string

Do not store values in Bash variables; bake the values into the script literally. This is the workaround used by `src/OpenClaw.SetupEngine/SetupSteps.cs:936-945` in `ValidateWslLockdownStep`. It is acceptable for short scripts with a small fixed value set and no spaces in values.

```csharp
var workspace = "/home/openclaw/.openclaw/workspace";
var script = $"mkdir -p {workspace} && test -d {workspace}";

await commandRunner.RunInWslAsync(distroName, script, cancellationToken);
```

### 3. Backslash-escape `\$var`

Escaping the dollar sign preserves it through `wsl.exe` and lets Bash expand it later.

```powershell
wsl.exe -d Ubuntu-26.04 -- bash -c "x=abc; echo VAL=\`$x"
```

This works for single isolated references, but it is fragile and easy to miss in any non-trivial script. Treat it as a last resort.

## What does NOT work

All of these failed workarounds were verified empirically:

- **Single quotes** — `'$workspace'` is still rewritten before Bash sees the quotes.
- **Double quotes** — `"$workspace"` is also rewritten before Bash sees the quotes.
- **Braces** — `${workspace}` is removed just like `$workspace`.
- **Subshells** — `( workspace=x; echo $workspace )` still loses the inner `$workspace`.
- **Prefix assignment** — `x=abc echo $x` still expands `$x` before the assignment can matter.
- **`-e VAR=val` flag forwarding** — forwarded environment values do not prevent argv rewriting before Bash receives the script.
- **Switching to `/bin/sh`** — the mutation happens in `wsl.exe`, before any shell starts.

## Where this matters in the codebase

- `src/OpenClaw.SetupEngine/CommandRunner.cs` — `RunInWslAsync` exposes the opt-in `inputViaStdin` parameter.
- `src/OpenClaw.SetupEngine/SetupSteps.cs:936-945` — `ValidateWslLockdownStep` uses workaround #2, C# interpolation.
- `src/OpenClaw.SetupEngine/SetupSteps.cs` `WindowsNodeBootstrapContextStep` — uses workaround #1, stdin.

## Related

- `docs/XAML_COMPILER_BUG.md` — sibling footgun doc for XAML compiler crashes.
