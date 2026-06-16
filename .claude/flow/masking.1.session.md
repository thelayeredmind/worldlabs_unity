# masking — Session 1

- session opened; target: Masking in worldlabs_gaussian
- user wants brush-based splat reveal: paint a selection, store it as a named cutout mask, animate reveal on Timeline
- brush tool already exists and paints into m_GpuEditSelected (per-splat uint bitmask)

[F] m_GpuEditSelected is a per-splat bitmask. Brush selection → int[] splatIndices (sparse, not dense float[]).

[D] GaussianSplatMask holds List<Entry>. Each Entry = Selection (int[] splatIndices) + float weight [0,1]. Weight is the entry's position on a shared 0→1 timeline — renderer samples entries in weight order to shape the reveal curve.

[D] Masks created programmatically from renderer editor. [CreateAssetMenu] kept for now.

[D] Interpolation: find two bracketing entries by weight, lerp between their full splat bitmaps per-splat. Splats exclusive to one entry fade in/out correctly across the gap.

[D] Mask applied in vertex shader — o.col.a *= maskWeight, then NaN-cull if below threshold. Zero GPU fragment cost for masked-out splats.

[D] UpdateMaskBuffer runs in Update() unconditionally so the buffer is always current before SetAssetDataOnMaterial.

[Task] Build GaussianSplatMask selection cutout system
[S] Full POC implementation
- GaussianSplatMask ScriptableObject: List<Entry>, Entry = Selection(int[] splatIndices) + float weight
- GaussianSplatRenderer: m_Mask, m_MaskT, m_GpuMaskWeights, UpdateMaskBuffer()
- Props.SplatMaskWeights + SplatMaskValid wired into CS and material paths
- RenderGaussianSplats.shader: mask applied in vertex stage (Pass 0 + Pass 3), NaN cull for zero-weight splats
- GaussianSplatRendererEditor: Mask section, Save Selection as Mask Entry button
- Base case (no entries): uniform m_MaskT across all splats
- Interpolation: bracket lo/hi entries, lerp per-splat bitmaps
- Freeze-on-delete fix: null-guard entries before processing
[R] Empty mask made splats disappear → fixed: no entries = uniform MaskT
[R] No interpolation between entries → fixed: bracket + lerp between full bitmaps
[R] Deleting entry froze editor → fixed: null-guard + removed O(n²) IndexOf
[/S]
[/Task]

[>] Refactor mask into a separate MonoBehaviour component (like GaussianSplatMorpher) — after POC validated
[>] Sparse encoding: bool inverted on Selection — inclusive vs exclusive, pick shorter list at save time
[>] Store Selection index lists as binary TextAsset files (same pattern as GaussianSplatAsset data chunks) — keeps ScriptableObject lean for large splat counts
[>] GaussianSplatMask should be stored as a sub-asset of GaussianSplatAsset — mask and splat are coupled, wrong to have them as independent assets
[>] Inspector UX: when scrubbing MaskT, show which entry (keyframe) is active and allow editing that entry's selection directly from the slider position

--- CLOSED 2026-06-16 — GaussianSplatMask POC: brush selection → named entries with weight → GPU reveal curve, working end-to-end ---
