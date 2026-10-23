# Changelog

## Unreleased

- Local AI offers low-memory models by default: Qwen3.5 9B and the new DeepSeek-R1-Distill-Qwen-14B (Q3_K_M), a dense reasoning distill from the pinned bartowski GGUF repository with its own 32K/8K context tiers, fit 16 and 12 GiB development GPUs such as the TITAN V. Set `OPENCLAW_LOCAL_AI_DEV_LOW_MEMORY_MODELS=0` to hide both from new installs; existing receipts keep launching.
- Select a compatible managed Local AI runtime/model profile and explain hardware fallbacks. Add an experimental, pinned Bonsai 2 27B PTQ1_0 profile for Windows x64 NVIDIA GPUs with 8 GiB VRAM, 16 GiB system RAM, and 16 GiB free on a shared runtime/model-cache volume (8 GiB each when separate). Disk shortages can be rechecked after freeing space. Thanks @alexandre-leng.
- Local AI now reads each NVIDIA GPU's compute capability and installs the pinned llama.cpp CUDA 12.4 x64 build when the CUDA 13 build cannot run, for example on Maxwell, Pascal or Volta GPUs such as the TITAN V, or on a driver that only exposes CUDA 12. Other GPUs keep the CUDA 13 runtime and its existing install path.
