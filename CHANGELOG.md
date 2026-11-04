# Changelog

## Unreleased

- CI: batch main CodeQL scans every six hours while preserving PR, master, tag, and manual scans; include attribute-only changes in PR and master scanning, and restore fork PR scans by retiring the completed setup-migration gate.
- Select a compatible managed Local AI runtime/model profile and explain hardware fallbacks. Add an experimental, pinned Bonsai 2 27B PTQ1_0 profile for Windows x64 NVIDIA GPUs with 8 GiB VRAM, 16 GiB system RAM, and 16 GiB free on a shared runtime/model-cache volume (8 GiB each when separate). Disk shortages can be rechecked after freeing space. Thanks @alexandre-leng.
