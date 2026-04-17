# CLAUDE.md — worldlabs_unity Gaussian Splat Fork

## Project Context

This is a Unity package that wraps Aras Pranckevičius's [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) with a WorldLabs API client for text-to-3D generation and runtime splat loading. The Gaussian Splat renderer is the performance-critical component.

**Current problem:** 25fps on Quest 3 (target: 70fps). Root cause is bandwidth exhaustion on Adreno 740 — the renderer was designed for desktop (RTX 3080 Ti class) and makes assumptions about memory bandwidth, cache size, and pipeline architecture that are invalid on TBDR mobile GPUs.

**Reference branch for the optimization work:**  
`d:\Dev\playground\unity\arghyasur1991-UnityGaussianSplatting` (feature/quest-stereo-perf)

**Merge strategy and analysis documents:**  
`d:\Dev\playground\unity\GaussianSplatTest\GaussianSplat_Quest3_MergeStrategy.md`

---

## Role Division

### Seb (human) owns:
- All C# orchestration code — `GaussianSplatRenderer.cs`, `GaussianSplatURPFeature.cs`
- Buffer lifecycle (creation, destruction, resize)
- Render scheduling logic (when things run, how many times, in what order)
- Sort scheduling (`ShouldSort`, `OnSorted`, frame counter logic)
- Stereo detection and camera matrix extraction
- Inspector properties and Unity editor integration
- WorldLabs-specific code: `RuntimeSplatData`, layer system, API client
- Project settings changes (URP renderer asset, stencil buffer, render features)
- Build and device testing

Seb is strong at CPU-level C# architecture, software patterns, and human-facing API design. Metal/GPU-facing code is his stated weak area — frame explanations and reviews in C# terms where possible.

### Claude owns:
- All HLSL/ShaderLab code — compute shaders, vertex shaders, fragment shaders
- GPU algorithm design (sort strategy, culling kernels, covariance math)
- Shader constant layout and buffer struct definitions
- Explaining what GPU code does and why it matters for Adreno specifically

Claude writes GPU code, explains it clearly enough for Seb to integrate it correctly, and reviews that the C# → shader handoff (buffer handles, dispatch counts, shader constants) is wired up right.

---

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
