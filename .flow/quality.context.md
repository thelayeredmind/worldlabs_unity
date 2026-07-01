# quality — Context

## What
The quality system controls how Gaussian splat data is compressed at import time and how it is decoded at runtime. It spans four orthogonal format axes — position, scale, color, SH — each with its own enum and byte footprint. Chunking is a derived property, not a quality level: present whenever any format is not Float32. The HUD (`GaussianQualityHUD`) is one feature built on top of this system for dev-time inspection on device.

Not a performance optimization target in itself — quality is a storage/fidelity tradeoff decided at import time and fixed for the lifetime of the asset.

## Why
The renderer was designed for desktop (RTX 3080 Ti) and makes bandwidth assumptions invalid on Adreno 740. Quality/compression settings are the primary lever for reducing the data volume that hits the GPU each frame. Understanding the full format pipeline — from import preset through asset fields to GPU buffer strides — is prerequisite for any bandwidth optimization work.

The HUD exists because there is no runtime visibility into what compression preset a given splat was built with. On a Quest 3 standalone build, there's no other way to confirm what quality each asset is running at.

## Where
**Import/Editor:**
- `Editor/GaussianSplatting/GaussianSplatAssetCreator.cs` — `DataQuality` enum (VeryHigh→VeryLow+Custom), `ApplyQualityLevel()` maps preset → four format fields. `isUsingChunks` derived from formats.
- `Editor/GaussianSplatting/GaussianSplatAssetCreator.cs` — `CalcChunkDataJob`: builds `ChunkInfo` per 256-splat chunk (min/max bounds for pos/scale/color/SH), normalises splat data to [0,1] relative to chunk.

**Runtime asset:**
- `Runtime/GaussianSplatting/GaussianSplatAsset.cs` — `VectorFormat`, `ColorFormat`, `SHFormat` enums + byte-size helpers. `ChunkInfo` struct (64 bytes: pos bounds, scale bounds, color bounds, SH bounds). `kChunkSize = 256`. Public accessors: `posFormat`, `scaleFormat`, `colorFormat`, `shFormat`.
- `Runtime/WorldLabs/RuntimeSplatData.cs` — same four format fields for runtime-loaded splats (private path, not yet publicly exposed).

**GPU:**
- `Shaders/GaussianSplatting.hlsl` — `LoadSplatData()` reads format descriptor (`_SplatFormat`), dechunks via `LoadSplatChunk()`, reconstructs full-precision values. Scale dechunked as `x^8` (three squarings).
- `Runtime/GaussianSplatting/GaussianSplatRenderer.cs` — uploads chunk buffer, sets `_SplatChunkCount`, `_SplatFormat`. `OnEnable`/`OnDisable` call `GaussianQualityHUD.Register/Unregister`.

**HUD:**
- `Runtime/GaussianSplatting/GaussianQualityHUD.cs` — `OnGUI`-based dev tool. Tier reverse-mapped from four format fields via `GetTierName()`. Color-coded dot at splat centre + connector line to legend column. Renderers self-register into static `s_Registered` list — no per-frame scene search.

**Known quality presets (from `ApplyQualityLevel`):**

| Tier | pos | scale | color | SH | ~compression |
|---|---|---|---|---|---|
| VeryHigh | Float32 | Float32 | Float32x4 | Float32 | 1× |
| High | Norm16 | Norm16 | Float16x4 | Norm11 | 2.9× |
| Medium | Norm11 | Norm11 | Norm8x4 | Norm6 | 5.1× |
| Low | Norm6 | Norm6 | Norm8x4 | Cluster16k | 14× |
| VeryLow | Norm6 | Norm6 | BC7 | Cluster4k | 18.6× |

## When
- Session 1 (2026-07-01): orientation survey (full format/chunk pipeline). GaussianQualityHUD shipped: tier name + format fields + color-coded dot + connector line + legend column. Renderers self-register via static list.

Open threads:
- [>] Occlusion: hide dot/label when splat centre is occluded by scene geometry (needs colliders or depth buffer approach)
- [>] Runtime splat coverage: expose format fields publicly from `GaussianSplatRenderer` so HUD covers the WorldLabs runtime load path
