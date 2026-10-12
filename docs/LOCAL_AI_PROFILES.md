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
