# MSIX update endpoint options

OpenClaw's MSIX/AppInstaller update feed needs a stable HTTPS URL for each architecture-specific `.appinstaller` file. The URL should outlive any one maintainer account, release host, or CI implementation because Windows stores it as the update source for installed packages.

## Recommendation

Use a project-owned custom domain such as `https://updates.openclaw.dev/windows/msix/openclaw-x64.appinstaller`, backed by Azure Static Web Apps or Azure Blob Storage plus CDN/Front Door. This keeps the public contract independent from the hosting backend and avoids tying an unofficial community project to Microsoft-owned `aka.ms` infrastructure.

## Options

| Option | Example endpoint | Pros | Cons / risks | Best fit | Recommendation |
|---|---|---|---|---|---|
| Project-owned custom domain backed by Azure Static Web Apps | `https://updates.openclaw.dev/windows/msix/openclaw-x64.appinstaller` | Stable public contract; easy static hosting; GitHub Actions deploy works well; can serve required MIME, `Content-Length`, and range headers; backend can change later behind DNS | Requires domain/DNS ownership and a small Azure resource; maintainers must manage deployment credentials | Community-owned project that wants a durable updater URL without taking on much infra | **Preferred** |
| Project-owned custom domain backed by Azure Blob Storage + CDN or Front Door | `https://updates.openclaw.dev/windows/msix/openclaw-x64.appinstaller` | Very durable object storage; strong header control; CDN/range support; easy to keep historical MSIX assets available | More Azure configuration than Static Web Apps; CDN caching needs careful invalidation for stable `.appinstaller` filenames | Higher-scale or more operations-friendly version of the preferred model | **Preferred if maintainers are comfortable with Azure ops** |
| GitHub Pages from the main repo, optionally behind a custom domain | `https://openclaw.github.io/openclaw-windows-node/openclaw-x64.appinstaller` or custom-domain equivalent | Simple; close to repo/release workflow; no separate cloud account if Pages is acceptable; good interim path | Requires Pages on the main repo; stable feed publishing is separate from GitHub Release attachment unless automated; direct `github.io` URL couples installed clients to GitHub Pages | Interim endpoint or long-term endpoint only if maintainers want GitHub Pages as project infrastructure | **Acceptable interim; better behind custom domain** |
| GitHub Pages from a dev/fork branch | `https://indierawk2k2.github.io/openclaw-windows-node/openclaw-x64.appinstaller` | Fast for testing; no main-repo Pages decision required | Not durable; tied to an individual fork/account; wrong trust boundary for production installs | Manual pre-merge update testing | **Testing only** |
| Direct GitHub Release asset URL | `https://github.com/openclaw/openclaw-windows-node/releases/download/vX.Y.Z/...` | Releases already contain signed artifacts; immutable tag URLs are good for package payloads | Not a stable feed URL by itself; "latest" redirects and release asset URLs are not ideal as Windows' stored `.appinstaller` source; harder to guarantee AppInstaller-friendly headers on redirects | Payload downloads referenced by a hosted `.appinstaller` | **Use for MSIX payloads, not as the stable feed** |
| `aka.ms` short link | `https://aka.ms/openclaw-msix-x64` | Very stable Microsoft-operated short URL; can redirect to any backend | This is not an official Microsoft project; ownership/approval may be inappropriate or unavailable; redirect adds another operational dependency | Official Microsoft-owned projects or Microsoft-sponsored distribution | **Do not use as canonical for this project** |
| Third-party object storage/static hosting with custom domain | `https://updates.openclaw.dev/windows/msix/openclaw-x64.appinstaller` backed by S3, Cloudflare R2, Netlify, etc. | Can be cheap and durable; custom domain keeps backend replaceable; many providers support correct headers | Provider-specific header/range behavior must be validated; maintainers need provider access and deployment secrets | Maintainers prefer non-Azure hosting | **Viable if header validation passes** |
| Self-hosted server | `https://updates.openclaw.dev/windows/msix/openclaw-x64.appinstaller` | Full control over headers, logs, and rollout behavior | Highest maintenance burden; uptime/TLS/security patching become project responsibilities | Projects with existing reliable infra | **Avoid unless existing infra already exists** |

## Assumptions

- OpenClaw remains distributed outside the Microsoft Store for this path.
- AppInstaller update metadata stays architecture-specific for now: `openclaw-x64.appinstaller` and `openclaw-arm64.appinstaller`.
- The stable `.appinstaller` URL is a long-lived contract stored by Windows for installed clients.
- MSIX package payloads may still live on GitHub Releases as long as the hosted `.appinstaller` points to versioned, signed assets and header validation passes.
- The update feed host must serve `.appinstaller` as `application/appinstaller`, MSIX payloads as `application/msix`, provide `Content-Length`, and support range requests for MSIX payloads.
