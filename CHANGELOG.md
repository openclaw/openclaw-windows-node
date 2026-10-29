# Changelog

## Unreleased

- Add Reset Local AI to Settings > Local AI and to setup for unusable managed installs. An install whose model or runtime is no longer in the catalog (for example a pre-rename Nemotron Bolt receipt) now shows as an error with Reset Local AI, instead of as stopped and verified, including after Stop, a failed reset, or a restart. Reset Local AI in setup no longer fails with "Close Local AI setup before releasing Gateway ownership." or because setup holds the setup run lock, and a reset whose last step failed can be run again.
- Select a compatible managed Local AI runtime/model profile and explain hardware fallbacks. Add an experimental, pinned Bonsai 2 27B PTQ1_0 profile for Windows x64 NVIDIA GPUs with 8 GiB VRAM, 16 GiB system RAM, and 16 GiB free on a shared runtime/model-cache volume (8 GiB each when separate). Disk shortages can be rechecked after freeing space. Thanks @alexandre-leng.
