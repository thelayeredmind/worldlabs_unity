---
target: masking
last_session: masking.2.session.md
---

## What

`GaussianSplatMask` — a ScriptableObject that stores an ordered list of named brush selections, each a snapshot of `m_GpuEditSelected` (sparse `int[] splatIndices`) paired with a float `weight` in [0,1]. Not a geometry cutout — a per-splat reveal system driven by a single `m_MaskT` parameter on the renderer.

Not to be confused with `GaussianCutout`, which operates on baked layer index ranges at import time.

## Why

The existing cutout system can't express free painted selections. The mask system enables animated progressive reveals: paint a selection, store it as a keyframe at a given weight, repeat. Timeline animates `m_MaskT`; the reveal curve emerges from all entries together.

Vertex-stage application: mask weight multiplies `o.col.a`, NaN-culls below threshold — zero GPU fragment cost for masked-out splats.

Interpolation: bracket `m_MaskT` between two entries, lerp per-splat float bitmaps. Boundary convention: no entry before MaskT → all invisible (all weights 0); no entry after MaskT → all visible (all weights 1). This lets a single entry with no bookends act as a clean reveal from nothing to everything.

`splatIndices` is stored as raw bytes in a hidden `GaussianSplatMaskData` sub-asset rather than inline in the ScriptableObject. This prevents Unity's AssetDatabase from triggering a full reimport on every entry mutation — which would stall the editor for seconds on large splat counts.

`splatIndices` is lazy-loaded via a property that reads from `dataAsset.bytes` on first access after `OnAfterDeserialize` invalidates the cache. Eager decode in `OnAfterDeserialize` caused a load-order race where sub-asset bytes weren't yet populated.

`UpdateMaskBuffer` runs only when dirty (mask reference, MaskT, or entry count changed). `SetMaskDirty` is deferred via `EditorApplication.delayCall` after `AssetDatabase.SaveAssets()` so it fires after reimport settles — calling it synchronously caused `UpdateMaskBuffer` to run before new sub-asset bytes were flushed.

## Where

| File | Role |
|---|---|
| `Runtime/GaussianSplatting/GaussianSplatMask.cs` | ScriptableObject: `List<Entry>`, `Entry = weight + dataAsset(GaussianSplatMaskData) + lazy splatIndices property` |
| `Runtime/GaussianSplatting/GaussianSplatMaskData.cs` | Hidden sub-asset: `byte[] bytes` — raw little-endian int[] encoding |
| `Runtime/GaussianSplatting/GaussianSplatRenderer.cs` | `m_Mask`, `m_MaskT`, `m_GpuMaskWeights`, dirty-flagged `UpdateMaskBuffer()`, `SetMaskDirty()` |
| `Shaders/RenderGaussianSplats.shader` | Pass 0 + Pass 3 vert: `_SplatMaskWeights[instID]` × `o.col.a`, NaN cull if below threshold |
| `Editor/GaussianSplatting/GaussianSplatRendererEditor.cs` | Mask section: keyframe dots on MaskT slider, click-to-snap + load selection, Shift+click unions, Save/Replace dialog, `WriteEntryData` writes sub-assets |

## When

- **Session 1 (2026-06-16):** POC built end-to-end — ScriptableObject, renderer evaluation, editor save button, shader vertex-stage apply. Working: empty mask = global fade, entries = keyframed reveal with smooth interpolation.
- **Session 2 (2026-06-16):** Inspector UX — keyframe dots on MaskT slider (round, hover feedback), click-to-snap + load selection into GPU edit buffer, Shift+click to union selections. Replace dialog when saving over existing weight. Binary sub-asset storage (GaussianSplatMaskData) to eliminate reimport stall. Lazy splatIndices + deferred SetMaskDirty to fix load-order race. Boundary behavior: invisible before first entry, fully visible after last.

**Open threads:**
- `[>]` Refactor into MonoBehaviour component (like GaussianSplatMorpher)
- `[>]` GaussianSplatMask stored as sub-asset of GaussianSplatAsset — coupled, shouldn't be independent
- `[>]` Sparse encoding: `bool inverted` on Entry (inclusive vs exclusive, pick shorter list at save time)
