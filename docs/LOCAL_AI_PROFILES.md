# Managed Local AI profiles

This guide covers Companion's managed Local AI installer, which downloads and
supervises a verified native llama-server on this PC. Its requirements do not
apply to every local model provider you can configure on a Gateway.

## Managed setup requirements

| Component | Managed support |
| --- | --- |
| Windows architecture | A cataloged native runtime for x64 or ARM64, with a model profile offered for that architecture. Some profiles, including experimental Bonsai 2, are x64-only. |
| GPU and driver | A compatible NVIDIA GPU with a working driver that exposes the required CUDA capability. Having an NVIDIA GPU alone does not guarantee that a model fits. |
| Intel Arc, AMD and other non-NVIDIA GPUs | Not supported by this managed setup. Installing an NVIDIA driver does not add support for these GPUs. |
| CPU-only or Vulkan execution | No managed fallback is provided. |
| Memory and disk | Enough GPU memory, system RAM and installation space for the selected model and context profile. These requirements vary by profile; the Bonsai figures below are not universal Local AI minimums. |

### Understanding an unavailable result

Open **See why** on the Local AI page or in setup for the reason. The hardware
check reads the NVIDIA CUDA driver, not a complete inventory of graphics cards.
No NVIDIA GPU reported by that check does not mean it identified Intel Arc,
or that installing a driver will make a non-NVIDIA GPU compatible.

If the PC only has Intel Arc or other non-NVIDIA graphics, managed setup is
unavailable by design. If it also has an NVIDIA GPU, check that GPU's driver
and try setup again. A failed or incomplete probe is different from unsupported
hardware: when Companion cannot read required GPU, memory or disk facts, it
keeps **Recheck** available rather than treating the result as definitive.
Resolve the reported problem and recheck; memory or disk shortages need the
specific resources named by the explanation.

## Installing managed Local AI

1. During Companion setup, use the managed **Local AI** choice on the AI setup
   page when it is offered. Review the model, downloads and any required consent
   before installing. An unavailable choice does not block using a separately
   configured provider.
2. After setup, open **Settings > Local AI**. When setup is available and no
   model is installed, **Retry setup or download** opens the appropriate setup
   flow for the app-managed Gateway. If no suitable managed Gateway can be
   identified, Companion opens full setup or asks you to review Connection
   settings instead of configuring an unrelated Gateway.
3. After installation, the Local AI page shows the runtime, model and Gateway
   connection state. Use its existing Start, Stop and repair actions as needed;
   follow any **Use Local AI** guidance to select it for a native Gateway.
   **Requirements and setup** stays available even when managed setup is not.

The installer supplies its pinned runtime dependencies. This guide does not
require a separate CUDA Toolkit installation.

## Separately configured local providers

LM Studio or Ollama can be configured as providers on the Gateway independently
of Companion's managed installer, using the setup options exposed by that
Gateway. Install and configure the provider separately, load a model supported
by that provider, and follow its Gateway connection instructions. Each provider
has its own operating-system, hardware, driver and model requirements. Check
those requirements for your exact GPU; neither provider nor a manually launched
llama-server is a guaranteed Intel Arc fix. This guide does not qualify those
combinations or change managed Local AI eligibility.

Gateway provider configuration is also distinct from **Share Windows Ollama**,
the optional node capability on the Permissions page. Enabling sharing does not
install managed Local AI or configure a Gateway model provider. See
[Local AI provider versus shared Windows Ollama](OPERATOR_NODE_CONCEPTS.md#local-ai-provider-versus-shared-windows-ollama).

## How managed profiles are selected

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

## Changing the model

The Local AI page changes the installed model in place, without the setup
wizard. Its list shows the installed model and every catalog model this PC can
be offered. The optional custom field accepts a public, non-gated, single-file
GGUF from Hugging Face: `owner/repo`, `owner/repo:Q4_K_M`,
`owner/repo/path/file.gguf`, or a `huggingface.co` / `hf.co` link.

A custom reference is pinned to the commit revision and its Git LFS SHA-256 and
size. Its block count, KV heads, head size, and trained context are read from
the GGUF header, without downloading the weights. The same capacity rules then
select catalog context tiers up to the trained context, with F16 before Q8_0. A
custom model runs on the generic llama.cpp runtime with no speculative draft. It
is stored as a schema-6 receipt that carries the recipe. When a model looks too
large for GPU memory, the page asks before switching.

The new weights are downloaded and verified while the current model keeps
serving. The runtime then withdraws the old Gateway route, saves the new
receipt, and restarts if it was running. If the new model does not start,
whether the start fails or throws, the previous receipt is restored and
restarted. Earlier weights stay in the shared Hugging Face cache, so switching
back only verifies them.

With a native Gateway, the ownership receipt (`gateway-binding.json`) records
the previous model, the installation receipt is saved, and the record is
cleared, all under one ownership-receipt lock. A record left on disk means the
switch was interrupted; the runtime's next load of the installation receipt
settles it, under that lock, to whichever model the receipt names. Setup's
ownership check never settles it: it reports the ownership as unavailable
until the runtime does.

The Models page changes Local AI's fallback only on the Gateway Local AI
is bound to. On any other Gateway it writes that Gateway's default and leaves
Local AI alone. Models whose provider still needs sign-in are listed but cannot
be made the default.

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
