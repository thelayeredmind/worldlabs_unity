# CLAUDE.md — worldlabs_unity Gaussian Splat Fork

## Project Context

This is a work of WorldLabs Integration for Unity (the last original commit is at d573119ed976823fe481f21cb4fcdeedb7b2ff49)
WorldLabs Integration for Unity is a Unity package that wraps Aras Pranckevičius's [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) with a WorldLabs API client for text-to-3D generation and runtime splat loading, and weak Meta Quest 3 optimization as after-thought. The Gaussian Splat renderer is the performance-critical component. It is a package, so only source code lives here. The assets and test scenes can be found at D:\Dev\work\arkanum\Kitchen_of_Memories\KOM_TechTests. The goal of this personal repository is to address the real Architecture/Algorithm limitations of the Quest 3 and to extend the package to allow for production ready workflows: like correct occlusion behavior, great editor tooling, right automatisms for efficiency.

**Current problem:** 25fps on Quest 3 (target: 70fps). Root cause is bandwidth exhaustion on Adreno 740 — the renderer was designed for desktop (RTX 3080 Ti class) and makes assumptions about memory bandwidth, cache size, and pipeline architecture that are invalid on TBDR mobile GPUs.

**Reference branch for the optimization work:**  
`d:\Dev\playground\unity\arghyasur1991-UnityGaussianSplatting` (Has been surpassed however by now)

## WorldLabs-Specific Code — Do Not Remove

The following exist in this fork but not in upstream Aras or the reference branch. They are WorldLabs features and must be preserved through all refactoring:

- `RuntimeSplatData` class and `LoadFromRuntimeData()` — runtime loading from the WorldLabs API without AssetDatabase
- Layer system: `m_LayerActivationState`, `IsSplatCutAtLayer()`, `m_GpuLayerData` — multi-layer splat support
- FidelityFX sort path: `m_CSSplatUtilities_fidelityFX`, `GpuSorting.SortType` enum — alternative sort backend

---

## Code Conventions

- Match the existing Aras coding style (no unnecessary abstraction, direct buffer manipulation is normal here)
- GPU code comments should explain *why* a choice was made for Adreno, not just *what* it does
- Quest-specific settings should be documented with `// Quest: <value>` in tooltips as the reference branch does
- Do not add editor-only code paths (`#if UNITY_EDITOR`) to the GPU hot path
- Keep WorldLabs package includes using the full package path: `Packages/com.worldlabs.gaussian-splatting/Shaders/...`
- When adding a per-splat distance/correspondence search (editor tooling, not the runtime hot path), dispatch it through the existing GPU compute path (e.g. `ICorrespondenceDispatcher`/`SplatCorrespondence.compute`) rather than a CPU loop — the same O(n·m) search done serially on CPU is orders of magnitude slower at real asset scale (100k–2M+ splats) and can look indistinguishable from a hang
- When verifying GPU buffer state, compile errors, or runtime asset state in this package, use the Unity MCP tools (`unity_execute_code`, `unity_get_compilation_errors`) against the live editor instance instead of relying on visual judgment alone — has caught stride/buffer-size mismatches and ambiguous-reference compile errors that weren't visible from the editor UI
- Every shader/compute-shader resource a kernel declares must have something bound, even when the kernel's runtime logic never reads it for a given code path (e.g. a chunk buffer that's only used when `chunkCount > 0`). A conditional `if (buf != null) SetBuffer(...)` that skips binding when the real buffer is absent will fail Unity's shader validation ("Property ... is not set") even though the kernel branch never touches it — bind a dummy/fallback buffer (any already-valid buffer of a compatible type works) instead of skipping the bind. This caused three separate bugs in the morph path when uncompressed (chunk-less) assets were introduced — see `GaussianSplatMorpher.cs` (`DispatchMorph`'s `_ChunksA`/`_ChunksB` binds) and `GaussianSplatRenderer.cs` (`SetExternalBuffers`'s chunk dummy-buffer guard)

---

## Hardware Target

| | Quest 3 (target) | RTX 3080 Ti (original design) |
|---|---|---|
| GPU | Adreno 740 (TBDR) | Ampere (IMR) |
| Memory bandwidth | ~50 GB/s shared | ~912 GB/s dedicated |
| L2 cache | ~3 MB | ~6 MB |
| FP32 throughput | ~3.6 TFLOPS | ~34 TFLOPS |
| Frame budget | 14.3ms (70fps) | 6.8ms (147fps) |

The renderer is currently bandwidth-bound, not compute-bound. Optimizations that reduce bytes moved (culling, compression, lower-res RT) outweigh optimizations that reduce ALU ops.

---

## Key Files

| File | Role |
|---|---|
| `Runtime/GaussianSplatting/GaussianSplatRenderer.cs` | Main renderer class + render system. Seb's primary file. |
| `Runtime/GaussianSplatting/GaussianSplatURPFeature.cs` | URP render feature, stereo path. Seb's file. |
| `Runtime/GaussianSplatting/GpuSorting.cs` | GPU sort wrapper (DeviceRadixSort + FidelityFX). Seb's file. |
| `Shaders/SplatUtilities_DeviceRadixSort.compute` | All compute kernels: view data, sort, culling. Claude's file. |
| `Shaders/RenderGaussianSplats.shader` | Splat vertex + fragment shader. Claude's file. |
| `Shaders/GaussianSplatting.hlsl` | Shared HLSL library: structs, SH, covariance math. Claude's file. |
| `Runtime/WorldLabs/` | WorldLabs API client. Out of scope for optimization work. |
