---
target: masking
last_session: masking.1.session.md
---

## What

`GaussianSplatMask` — a ScriptableObject that stores an ordered list of named brush selections, each a snapshot of `m_GpuEditSelected` (sparse `int[] splatIndices`) paired with a float `weight` in [0,1]. Not a geometry cutout — a per-splat reveal system driven by a single `m_MaskT` parameter on the renderer.

Not to be confused with `GaussianCutout`, which operates on baked layer index ranges at import time.

## Why

The existing cutout system can't express free painted selections — it only cuts by geometry shape or layer range. The mask system enables animated progressive reveals: paint a selection, store it as a keyframe at a given weight position, repeat. Timeline animates `m_MaskT` over the shared 0→1 axis; the reveal curve emerges from all entries together. Entry weights are positions on this axis, not individual blend amounts.

Vertex-stage application: mask weight multiplies `o.col.a` in the vertex shader, then NaN-culls the primitive if below `_AlphaDiscardThreshold`. Zero GPU fragment cost for masked-out splats — no rasterization at all.

Interpolation: find the two entries bracketing `m_MaskT` by weight, build per-splat float bitmaps for each, lerp between them. Splats exclusive to one entry fade in/out correctly across the gap.

## Where

| File | Role |
|---|---|
| `Runtime/GaussianSplatting/GaussianSplatMask.cs` | ScriptableObject: `List<Entry>`, `Entry = Selection(int[] splatIndices) + float weight` |
| `Runtime/GaussianSplatting/GaussianSplatRenderer.cs` | `m_Mask`, `m_MaskT`, `m_GpuMaskWeights`, `UpdateMaskBuffer()` in `Update()`, `Props.SplatMaskWeights/Valid` |
| `Shaders/RenderGaussianSplats.shader` | Pass 0 + Pass 3 vert: `_SplatMaskWeights[instID]` × `o.col.a`, NaN cull if below threshold |
| `Editor/GaussianSplatting/GaussianSplatRendererEditor.cs` | Mask section: m_Mask field, m_MaskT slider, Save Selection as Mask Entry button |

## When

- **Session 1 (2026-06-16):** POC built end-to-end — ScriptableObject, renderer evaluation, editor save button, shader vertex-stage apply. Working: empty mask = global fade, entries = keyframed reveal with smooth interpolation.

**Open threads:**
- `[>]` Refactor into MonoBehaviour component (like GaussianSplatMorpher)
- `[>]` GaussianSplatMask stored as sub-asset of GaussianSplatAsset — coupled, shouldn't be independent
- `[>]` Inspector UX: show active keyframe when scrubbing MaskT, allow editing that entry's selection inline
- `[>]` Store index lists as binary TextAsset files (like GaussianSplatAsset data chunks) — lean serialization for large splat counts
- `[>]` Sparse encoding: `bool inverted` on Selection (inclusive vs exclusive, pick shorter list at save time)
