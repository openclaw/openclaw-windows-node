# Native Gateway error messages

Read this after a failure from
[the native Gateway skill](../SKILL.md). Safety rules are in its
[Pitfalls](../SKILL.md#pitfalls).

| Message | Cause | Action |
|---|---|---|
| `The development Gateway package ...-<patch> is not registered ...` (new setup) | Variable set, patch not registered | Run the build, or clear the variable to use the Store Gateway |
| `This Gateway profile uses the development Gateway package ...-<patch>. Set OPENCLAW_NATIVE_GATEWAY_DEV_PATCH=<patch> ...` | Saved profile bound to a patch, variable unset or different | Restart Companion with that variable |
| `This Gateway profile uses ... which is not registered ...` | Saved profile bound to a patch that was unregistered | Re-register the patch, or remove the gateway in Connection settings and set up again |
| `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH must be 1 to 15 letters ...` | Invalid variable value | Use lowercase letters, digits, hyphens |
| `-WorkRoot '...' is inside your user profile ...` | Profile path passed | Use a path such as `C:\OpenClawDev` |
| `The package-qualified aliases were not found ...` | App execution aliases disabled | Enable them in Settings > Apps > Advanced app settings > App execution aliases |
| `Another native Gateway source build or unregister is running ...` | Shared build/registration resources are locked | Wait for that operation to finish, then retry |
| `Native Gateway build path ... grants write or replacement access ...` | Another account can modify code or replace a parent | Use a fresh root under a trusted parent, or have an administrator verify and repair the existing tree |
| `Prebuilt package metadata ... requires ...` | Missing, malformed or truncated provenance | Recreate `source.json` with the package producer; do not invent hashes |
| `Could not acquire ... dist-artifacts.lock` | A cancelled source build left `openclaw\.artifacts\dist-artifacts.lock` | After confirming no build process is still running, remove that directory and rerun |
