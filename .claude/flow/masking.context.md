---
target: masking
last_session: masking.3.session.md
---

## What

`GaussianSplatMask` — a ScriptableObject storing an ordered list of named brush selections, each a snapshot of `m_GpuEditSelected` (sparse `int[] splatIndices`) paired with a float `weight` in [0,1]. A per-splat reveal system driven by a single `m_MaskT` parameter on the renderer. Not `GaussianCutout` (baked layer-range cutouts at import time). As of this session, `GaussianSplatMask` is a **sub-asset of its target `GaussianSplatAsset`** rather than a standalone `.asset` file — one mask per splat asset.

## Why

The existing cutout system can't express free painted selections. The mask system enables animated progressive reveals: paint a selection, store it as a keyframe at a given weight, repeat. Timeline animates `m_MaskT`; the reveal curve emerges from all entries together. Vertex-stage application: mask weight multiplies `o.col.a`, NaN-culls below threshold — zero GPU fragment cost for masked-out splats.

A mask's `splatIndices` are meaningless without the specific `GaussianSplatAsset` they were painted against — coupling the mask as a sub-asset (rather than an independently-creatable standalone asset) prevents orphaned/mismatched masks and removes a manual two-step (create mask asset, then assign it) in favor of one button. The `[CreateAssetMenu]` standalone-creation path was removed entirely so there's no way to create a mask that isn't attached to an asset.

`splatIndices` is stored as raw bytes in a hidden `GaussianSplatMaskData` sub-asset rather than inline in the ScriptableObject, to avoid a full AssetDatabase reimport stall on every entry mutation at large splat counts. Lazy-loaded via a property reading `dataAsset.bytes` on first access after `OnAfterDeserialize` invalidates the cache (eager decode caused a load-order race).

`UpdateMaskBuffer` runs only when dirty; `SetMaskDirty` is deferred via `EditorApplication.delayCall` so it fires after `AssetDatabase.SaveAssets()` settles.

## Where

| File | Role |
|---|---|
| `Runtime/GaussianSplatting/GaussianSplatMask.cs` | ScriptableObject: `List<Entry>`, `Entry = weight + dataAsset(GaussianSplatMaskData) + lazy splatIndices property`. No longer has `[CreateAssetMenu]`. |
| `Runtime/GaussianSplatting/GaussianSplatMaskData.cs` | Hidden sub-asset: `byte[] bytes` — raw little-endian int[] encoding |
| `Runtime/GaussianSplatting/GaussianSplatRenderer.cs` | `m_Mask` (explicit serialized reference field — not auto-looked-up), `m_MaskT`, `m_GpuMaskWeights`, dirty-flagged `UpdateMaskBuffer()`, `SetMaskDirty()` |
| `Shaders/RenderGaussianSplats.shader` | Pass 0 + Pass 3 vert: `_SplatMaskWeights[instID]` × `o.col.a`, NaN cull if below threshold |
| `Editor/GaussianSplatting/GaussianSplatRendererEditor.cs` | Mask section: `m_Mask` field, "Create Mask" button (creates a sub-asset of the renderer's `GaussianSplatAsset`, via `CreateMaskSubAsset` + `CreateMaskNamePrompt` modal window), keyframe dots on MaskT slider, click-to-snap + load selection, Shift+click unions, Save/Replace dialog, `WriteEntryData` writes `GaussianSplatMaskData` sub-assets. The old "Save Selection as Mask Entry" auto-create-on-null fallback now also routes through `CreateMaskSubAsset` (previously created a standalone asset via `AssetDatabase.CreateAsset`, bypassing the sub-asset model). |
| `Editor/GaussianSplatting/GaussianSplatAssetEditor.cs` | Reverted to its pre-mask state — mask creation lives entirely in the renderer's editor now, not here. |

## When

- **Session 1 (2026-06-16):** POC built end-to-end — ScriptableObject, renderer evaluation, editor save button, shader vertex-stage apply.
- **Session 2 (2026-06-16):** Inspector UX — keyframe dots, click-to-snap + load, Shift-union. Binary sub-asset storage (`GaussianSplatMaskData`) to eliminate reimport stall. Lazy `splatIndices` + deferred `SetMaskDirty` to fix load-order race. Boundary behavior: invisible before first entry, fully visible after last.
- **Session 3 (2026-06-19):** Made `GaussianSplatMask` a sub-asset of `GaussianSplatAsset` instead of a standalone asset — removed `[CreateAssetMenu]`, added a "Create Mask" button (with name prompt) to the renderer's existing Mask inspector section, and fixed a dormant fallback path that still created standalone masks. One mask per asset; no automated migration for pre-existing standalone mask assets (manual re-creation expected). Commit: `e7af560`.

Open threads:
- `[>]` **Mask `splatIndices` validity after splat-count-changing operations** (`GaussianSplatCommitter`'s "Commit to Disk", `GaussianSplatSeparator`'s "Separate Selection to New Asset") — both compact the splat buffer by removing deleted/extracted splats, shifting subsequent indices down. Neither tool is aware of `GaussianSplatMask`; a mask's stored indices are not remapped, so they may silently point at the wrong splats afterward. Plausible from code reading, not confirmed by repro — an attempted repro this session was blocked first by an unrelated pre-existing crash (see below), then invalidated when a manual test altered the mask state before a clean after-snapshot could be taken. Needs: fresh before-snapshot, controlled delete with a known set of indices below the mask's selection range, "Commit to Disk", after-snapshot, diff.
- `[>]` Refactor `GaussianSplatMask`/renderer into a more decoupled MonoBehaviour-driven design (older parked thread, untouched this session)
- `[>]` Sparse encoding: `bool inverted` on Entry (inclusive vs exclusive, pick shorter list at save time) (older parked thread, untouched this session)
- `[X]` (learning, not a masking bug) `GaussianSplatRenderer.ComputeRawSelectionBounds` (`GaussianSplatRenderer.cs:1773-1775`) hardcodes a 12-byte stride assumption when reading `m_GpuPosData` as raw bytes, but the buffer's actual `GraphicsBuffer.stride` is 4 — causes an `ArgumentOutOfRangeException` crashing `EditDeleteSelectedWithParams` (the splat-delete tool) on some assets (reproduced on "bonsai", where `posBuf.count` wasn't a clean 3× multiple of `splatCount`; did not reproduce on "Piranesi_500k", where it was). Blocks any future repro of the mask-index-drift question above until splat deletion is reliable. Separate bug from the masking target — not fixed this session.
