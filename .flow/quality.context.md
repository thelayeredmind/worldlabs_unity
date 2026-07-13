# quality — Context

## What
The quality system controls how Gaussian splat data is compressed at import time and how it is decoded at runtime. It spans four orthogonal format axes — position, scale, color, SH — each with its own enum and byte footprint. Chunking is a derived property, not a quality level: present whenever any format is not Float32. The HUD (`GaussianQualityHUD`) is one feature built on top of this system for dev-time inspection on device.

Not a performance optimization target in itself — quality is a storage/fidelity tradeoff decided at import time and fixed for the lifetime of the asset.

## Why
The renderer was designed for desktop (RTX 3080 Ti) and makes bandwidth assumptions invalid on Adreno 740. Quality/compression settings are the primary lever for reducing the data volume that hits the GPU each frame. Understanding the full format pipeline — from import preset through asset fields to GPU buffer strides — is prerequisite for any bandwidth optimization work.

The HUD exists because there is no runtime visibility into what compression preset a given splat was built with. On a Quest 3 standalone build, there's no other way to confirm what quality each asset is running at.

`GaussianSplatBuildProcessor`'s swap/restore was bandwidth-safe by design but had a lifecycle gap: `SwapToSibling()` calls `AssetDatabase.SaveAssets()` itself during `OnPreprocessBuild`, writing the mobile-swapped state to disk *before* the actual build runs. The only revert path was `OnPostprocessBuild`, which Unity does not guarantee to fire on every build failure mode (crash, force-quit). If it doesn't fire, the source VeryHigh `.asset` is left saved on disk in its swapped state — this reproduced as a git-dirty `.asset` after a real build. Fixed via a transient `QualityBackupStorage` sibling asset (`MyAsset_backup.asset`) written at swap time, holding only the quality-aspect fields (formats/splatCount/bounds/layer data refs) — not a full asset copy; masks/layer-activation state are untouched. A single shared function, `RestoreFromQualityBackupStorage`, restores from this backup and deletes it — used both by the automatic `OnPostprocessBuild` path and a manual "Restore from Quality Backup" right-click recovery tool for when the automatic path never ran. Confirmed working via both a real Quest build (successful path) and a right-click manual recovery run (crash-recovery path).

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

**Mobile recompression (separate from import-time DataQuality):**
- `Editor/GaussianSplatting/GaussianSplatRecompressor.cs` — editor-only, offline pipeline. Decodes a VeryHigh (Float32, unchunked, single-layer) source asset's `.bytes` buffers back to `InputSplatData[]` (`DecodeVeryHighToDisk`), re-encodes via `RuntimeSplatProcessing.Process()` at a target `MobileQuality`, writes a new sibling `.asset` (e.g. `MyAsset_low.asset`). Only supports VeryHigh/unchunked sources — throws if `chunkDataSize > 0`. One-way decode-and-reencode from highest fidelity, not incremental recompression of an already-compressed asset. Two entry points: menu item for selected renderer, and project-wide "Recompress All Mobile Assets" batch (honors `GaussianSplatBuildSettings.excludePatterns`).
- `Runtime/GaussianSplatting/GaussianSplatAsset.cs` — `MobileQuality` enum (`None, Low, Medium, High`), separate from the four-axis `DataQuality` import enum. `GetFormatsForMobileQuality()` maps it to the same four format axes: Low→(Norm11,Norm6,Norm8x4,Cluster16k), Medium→(Norm11,Norm11,Norm8x4,Norm6), High→(Norm16,Norm16,Float16x4,Norm11).
- `Editor/GaussianSplatting/GaussianSplatBuildProcessor.cs` — `IPreprocessBuildWithReport`/`IPostprocessBuildWithReport`. Does NOT recompress at build time — swaps a VeryHigh source asset's TextAsset references to its pre-baked mobile sibling before build (`SwapAllMobileAssets`/`SwapToSibling`), restores originals after (`RestoreAll`). Warns and ships uncompressed if no sibling exists or `mobileQuality == None`.
  - `GetQualityBackupStoragePath`/`WriteQualityBackupStorage` — writes a transient `MyAsset_backup.asset` sibling at swap time, holding only quality-aspect fields (formats/splatCount/bounds/layer data refs) copied from the source. Not a full asset copy.
  - `RestoreFromQualityBackupStorage(source, assetPath)` — shared restore primitive: loads the backup asset, copies its fields onto source, deletes the backup, marks source dirty. Used by both `AssetSnapshot.Restore()` (automatic, `OnPostprocessBuild`) and the manual recovery tool below — `AssetSnapshot` itself only carries an asset ref + path, no duplicated field state.
  - `MenuItem("Assets/Gaussian Splats/Restore from Quality Backup")` — manual right-click recovery tool for a selected `GaussianSplatAsset`; validate function only enables when a lingering backup exists for it. Covers the case where `OnPostprocessBuild` never ran (build crash/force-quit) and a backup was left on disk.

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
- Session 2 (2026-07-11): surveyed the mobile recompression pipeline (GaussianSplatRecompressor, MobileQuality enum, GaussianSplatBuildProcessor). Fixed a real bug found via git-dirty-asset symptom: build-time swap wasn't guaranteed to revert on build failure/crash — added transient QualityBackupStorage + shared restore function + manual recovery tool. Confirmed via real Quest build and manual right-click recovery.

Open threads:
- [>] Occlusion: hide dot/label when splat centre is occluded by scene geometry (needs colliders or depth buffer approach)
- [>] Runtime splat coverage: expose format fields publicly from `GaussianSplatRenderer` so HUD covers the WorldLabs runtime load path
