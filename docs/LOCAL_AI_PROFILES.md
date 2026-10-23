# Managed Local AI profiles

The Local AI page and setup wizard use the shared inference catalog. Selection
tries models in recommendation order, then context and KV-cache profiles in
quality order. The existing Qwen profile stays preferred when it fits. A smaller
selection is explained in both surfaces; a failed preferred profile does not
make Local AI unavailable when another supported profile fits.

`LocalManagedInferenceProfile` binds a model, its runtime family, launch recipe,
GPU vendor and memory requirement, system RAM floor, and free-disk floor. NVIDIA
CUDA facts determine GPU capacity. Free VRAM indicates a busy GPU and never
causes an automatic model downgrade. Unknown required facts remain retryable.
Existing installed receipts do not require download space again. Explicit model
requests and installed receipts are not silently switched to another model.
RTX Spark keeps its existing per-adapter SKU recommendations.

## CUDA runtime routing

The stock llama.cpp family pins two Windows x64 builds from ggml-org release
`b11320`: CUDA 13.4 (`b11320-cuda13-x64`) and CUDA 12.4 (`b11320-cuda12-x64`).
The hardware probe reads each GPU's compute capability through
`cuDeviceGetAttribute`. Selection prefers CUDA 13 and uses CUDA 12 only when no
NVIDIA GPU can run the CUDA 13 build:

- CUDA 13 needs compute capability 7.5 or newer (Turing and later) and a driver
  that exposes CUDA 13.
- CUDA 12 covers compute capability 5.0 through 9.x (Maxwell through Hopper) on a
  CUDA 12 or newer driver, for example a TITAN V (7.0) on an R580 driver.
  Blackwell is not routed to it.

A missing compute capability is reported as retryable incomplete facts. The CUDA
12 build installs under `engines\llama-server\b11320\win-x64-cuda12`; CUDA 13
keeps `win-x64`. ARM64, Bonsai, and Nemotron Bolt stay CUDA 13 only. A receipt
whose runtime no longer matches the host is re-acquired by the existing runtime
upgrade path.

## Low-memory models

Two low-VRAM recipes are part of the catalog by default: **Qwen3.5 9B (Q4_K_M)**
and **DeepSeek-R1-Distill-Qwen-14B (Q3_K_M)**, ranked below every production
model. `OPENCLAW_LOCAL_AI_DEV_LOW_MEMORY_MODELS=0` is the explicit opt-out:
with it, neither is offered and both resolve only through installed receipts
with the native-context F16 profile a pre-profile install could have recorded,
and a receipt recorded under a reduced profile then fails launch validation.
Set the opt-out at user level (`setx`) so the tray, setup UI, and setup engine
all see it. The legacy value "1" is still accepted and keeps the models on.

The 9B uses the shared profile ladder (262,144 down to 65,536 tokens). The 14B
is a dense Qwen2-architecture distill: 48 full-attention layers, 8 KV heads,
head dimension 128, so 196,608 bytes/token of F16 KV, roughly six times the 9B.
It gets its own ladder of 65,536, 32,768, and 8,192 tokens in F16 and Q8_0.
At 8,192 tokens Q8_0 it needs about 11.6 GiB, so a 12 GiB card such as the
TITAN V qualifies; 16 GiB cards reach 32,768 tokens. It has no MTP head, so no
speculative decoding; llama-server keeps its `reasoning-format deepseek`
handling for the distilled R1 chain-of-thought format.

Weights are pinned to
[bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF](https://huggingface.co/bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF)
revision `9f5d77d401799416e0702290a691038b44012e0c`, file
`DeepSeek-R1-Distill-Qwen-14B-Q3_K_M.gguf`, SHA-256
`faf2e6f4d4462619a9352419b751f140be67ca9a4cb343b6796e37adafd8a050`
(7,339,204,000 bytes). The upstream model is MIT-licensed
([deepseek-ai](https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-14B)).

## Experimental Bonsai 2 profile

The Windows x64 profile uses **Bonsai 2 27B PTQ1_0**, derived from Qwen3.8, with
8,192 context tokens, F16 KV, 512 batch tokens, 128 microbatch tokens, and no
speculative decoding or vision projector. Its output limit is 2,048 tokens in
both the local server preset and Gateway provider definition. Other profiles
retain their existing 8,192 output limit.

The admission policy requires 16 GiB system RAM and 16 GiB free when the runtime
and model cache share a volume, or 8 GiB on each separate volume. The two reserves
cover the cached weights, a second compatibility copy, and runtime download/extraction.
Disk shortages offer Recheck in setup and the Local AI page after freeing space.
Existing installation repair does not require these download reserves. GPU accounting includes the 5,946,648,928-byte
weights, 512 MiB KV cache, and a provisional 1.5 GiB workspace allowance, about
7.54 GiB total. These are conservative product admission settings, not an
upstream performance guarantee. Real RTX 4060 peak-memory, long-prompt, and
complete tool-call round-trip proof remains required before removing the
experimental designation. The 8K context limits conversation and tool history.
There is no CPU, AMD, Vulkan, or Windows ARM64 Bonsai profile in this release.

### Verified sources (2026-10-12)

- [Official model and license](https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf/tree/b072e1d3b35a0a630cece372c2127528e0994386): Apache-2.0. File `Ternary-Bonsai-2-27B-PTQ1_0.gguf`, SHA-256 `53107f530aa52eb00912263ab1ee29bd199261c87cd7b4ad4ca1318c1fe33ee3`. The immutable download served a GGUF byte range successfully.
- [PrismML runtime release](https://github.com/PrismML-Eng/llama.cpp/releases/tag/prism-b10772-e8fd209), commit `e8fd209c975c80aa7fb36a8423571ff3b1b7e955`: [MIT license](https://github.com/PrismML-Eng/llama.cpp/blob/e8fd209c975c80aa7fb36a8423571ff3b1b7e955/LICENSE). The CUDA 13.3 x64 runtime and dependency ZIPs were downloaded and their SHA-256 digests verified. `BonsaiRuntimeCatalog` also pins the extracted executable and DLL files.
- [Official setup reference](https://github.com/PrismML-Eng/Bonsai-demo): documents the custom ternary kernels and native Windows setup.

Stock llama.cpp cannot run these ternary weights. Catalog selection, installed
receipt validation, and setup reconciliation preserve the runtime-family
boundary. The existing artifact installer, lifecycle, health checks, and
Gateway connection owner are reused. No Gateway source port is required.
