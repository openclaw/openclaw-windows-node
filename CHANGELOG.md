# Changelog

## Unreleased

- Change the Local AI model directly on the Local AI page instead of rerunning setup: pick a catalog model that fits this PC or paste a public Hugging Face GGUF (`owner/repo`, `owner/repo:Q4_K_M`, a file path including a repository subfolder, or a link). Custom models are pinned to a commit and SHA-256, sized from their GGUF header, and confirmed before switching when they may not fit. A nested GGUF installs into the same subfolder as the hub cache. A failed start restores the previous model. Settings gains a Model section with a new Default model page for choosing the Gateway's default model, including Local AI.
- Select a compatible managed Local AI runtime/model profile and explain hardware fallbacks. Add an experimental, pinned Bonsai 2 27B PTQ1_0 profile for Windows x64 NVIDIA GPUs with 8 GiB VRAM, 16 GiB system RAM, and 16 GiB free on a shared runtime/model-cache volume (8 GiB each when separate). Disk shortages can be rechecked after freeing space. Thanks @alexandre-leng.
