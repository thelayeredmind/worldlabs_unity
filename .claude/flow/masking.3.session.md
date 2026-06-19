# masking — Session 3

- session resumed from session 2 (closed 2026-06-16)
- open threads available: MonoBehaviour refactor, sub-asset coupling to GaussianSplatAsset, sparse inversion encoding

- picked up parked thread: GaussianSplatMask coupling to GaussianSplatAsset — currently independent SO, not sub-asset

[D] GaussianSplatMask becomes a sub-asset of GaussianSplatAsset (not a standalone asset with a reference) — mirrors the GaussianSplatMaskData byte-blob sub-asset pattern already in use.

[F] Current chain: GaussianSplatAsset --references--> GaussianSplatMask (own .asset file) --entries--> GaussianSplatMaskData sub-asset (attached to the MASK's file, via WriteEntryData/AddObjectToAsset at GaussianSplatRendererEditor.cs:168). Mask becoming a sub-asset of GaussianSplatAsset moves the whole chain one level down: mask gets AddObjectToAsset'd onto the GaussianSplatAsset file; m_Mask reference becomes implicit lookup instead of manual drag-assign.
[D] Exactly one mask per GaussianSplatAsset (not multiple/named) — matches today's single m_Mask field, simplest migration.

[D] Renderer keeps m_Mask as an explicit serialized reference field (no auto-lookup/GetMask() helper) — the field's target object just becomes a sub-asset of GaussianSplatAsset instead of a standalone .asset file. Manual assignment in the inspector stays as-is.

[D] Creation flow: "Create Mask" button added to GaussianSplatAsset's own inspector (mirrors morpher's inspector-button pattern from prior session). Click creates GaussianSplatMask instance, AddObjectToAsset's it onto the splat asset file, and assigns it to m_Mask on the renderer(s) currently using that asset. Existing CreateAssetMenu on GaussianSplatMask likely removed/deprecated since standalone creation no longer fits the model — needs explicit decision.

[D] Remove CreateAssetMenu attribute from GaussianSplatMask — inspector button on GaussianSplatAsset becomes the only creation path. Prevents orphan masks not attached to any splat asset.

[D] No automated migration tool — existing standalone mask assets are manually re-created via the new inspector button. Acceptable given POC-stage usage (per session 1/2 history, this is still early).

[Task] Make GaussianSplatMask a sub-asset of GaussianSplatAsset instead of a standalone .asset file. Specifically: (1) remove [CreateAssetMenu] from GaussianSplatMask.cs so it can no longer be created as a free-floating asset; (2) add a "Create Mask" button to GaussianSplatAsset's inspector editor that creates a GaussianSplatMask instance via ScriptableObject.CreateInstance, AddObjectToAsset's it onto the GaussianSplatAsset's own asset file (mirroring the AddObjectToAsset pattern already used for GaussianSplatMaskData sub-assets at GaussianSplatRendererEditor.cs:168, but targeting the splat asset's path instead of the mask's), and assigns the result to m_Mask on any GaussianSplatRenderer component(s) in the open scene currently referencing that GaussianSplatAsset; (3) GaussianSplatRenderer.m_Mask stays an explicit serialized reference field (no auto-lookup) — only its target object's storage location changes; (4) WriteEntryData's GaussianSplatMaskData sub-asset attachment (GaussianSplatRendererEditor.cs:153-168) needs its AssetDatabase.GetAssetPath(mask) call to still resolve correctly now that the mask itself is a sub-asset rather than a root asset — verify AddObjectToAsset for the data blob still lands on the correct underlying file; (5) one mask per GaussianSplatAsset, no multi-mask support; (6) no automated migration for existing standalone mask assets — manual re-creation via the new button is acceptable.

Task: Convert GaussianSplatMask from standalone asset to a sub-asset of GaussianSplatAsset, with inspector-button creation and no auto-migration.

Ready to wind up.
[S] Step 1: CreateAssetMenu removal + "Create Mask" inspector button on GaussianSplatAssetEditor (creates instance, AddObjectToAsset onto splat asset, assigns to scene renderers' m_Mask). Surface: code diff.
[R] Mask sub-asset hardcoded to name "Mask" — should creation prompt for a custom name?
[/S]

[S] Amend: prompt for mask name before creation, via EditorUtility.SaveFilePanelInProject-style or inline name field. Surface: code diff.
[C] CreateMask split into CreateMask (opens prompt) + CreateMaskWithName (actual creation) — name now comes from a new CreateMaskNamePrompt modal EditorWindow instead of a hardcoded literal.
[/S]
[/Task]

[X] Compile error CS0104: 'Object' ambiguous between UnityEngine.Object and System.Object after adding `using System;` for Action<string> → fixed by qualifying as UnityEngine.Object.FindObjectsByType. Verified via unity_get_compilation_errors on TechTests instance (port 7891) — 0 errors after AssetDatabase.Refresh.

[R] Mask sub-asset invisible in Project window (HideFlags.HideInHierarchy) — user wants to see/expand it under the GaussianSplatAsset like a normal multi-object asset.
[D] Drop HideFlags.HideInHierarchy on the created mask so it's visible/expandable in the Project window.
[C] CreateMaskWithName — removed mask.hideFlags assignment (was HideFlags.HideInHierarchy).
[/S]
[/Task]

[R] No "Create Mask" button visible — user was looking in GaussianSplatRendererEditor's existing Mask section (where m_Mask, MaskT slider, keyframe dots already live), not on the GaussianSplatAsset file's own inspector where it was placed during execution. Placement mismatch, not a render bug.
[D] Move "Create Mask" button into GaussianSplatRendererEditor's Mask section, next to the existing m_Mask field — matches where masking controls are actually used/looked for.
[>] New thread surfaced: what happens to a mask's splatIndices when the underlying GaussianSplatAsset's splat count changes via decimation/splitting tools — indices would point at stale/shifted splats. Not yet investigated.
[F] GaussianSplatRendererEditor.cs's "Save Selection as Mask Entry" button (line ~626) had its own dormant auto-create-mask-on-null path using AssetDatabase.CreateAsset (standalone asset) — a second creation path that would have silently bypassed the new sub-asset model. Found while relocating the button; folded into the same CreateMaskSubAsset helper.
[C] GaussianSplatAssetEditor.cs — reverted to pre-mask state (button/prompt/helper all removed, moved to GaussianSplatRendererEditor.cs).
[C] GaussianSplatRendererEditor.cs — added CreateMaskSubAsset + CreateMaskNamePrompt (moved from GaussianSplatAssetEditor.cs); "Create Mask" button inserted after m_PropMask PropertyField; old AssetDatabase.CreateAsset fallback in "Save Selection as Mask Entry" replaced with CreateMaskSubAsset call.
[/S]
[/Task]

[^] Continue masking. Last: review clean — Create Mask button working in renderer's Mask section, sub-asset visible and assigned correctly. Next: drift or wrap-up. Confirm: none.
[CKP] e7af560 — "feat: make GaussianSplatMask a sub-asset of GaussianSplatAsset" — user committed directly.

- picked up parked thread: investigate what happens to GaussianSplatMask's splatIndices when decimation/split tools change the underlying GaussianSplatAsset's splat count

[F] Two tools change a GaussianSplatAsset's splat count post-creation: GaussianSplatCommitter (Editor/GaussianSplatting/GaussianSplatCommitter.cs) — "Commit to Disk" bakes deleted splats out, compacting the buffer; GaussianSplatSeparator (Editor/GaussianSplatting/GaussianSplatSeparator.cs) — "Separate Selection to New Asset" extracts selected splats into a new asset and deletes them from the original. Both only work on VeryHigh-quality assets, both compact remaining splats by removing gaps — every splat after a removed one shifts to a lower index.
[F] GaussianSplatMask.Entry.splatIndices are raw indices into the asset's splat array at the time the selection was painted. Neither Committer nor Separator are aware of GaussianSplatMask — after either runs, a mask's stored indices point at whatever splat now occupies that shifted slot, not the originally-selected splat. Silent corruption, no validation/warning exists today.
[D~] This is a real bug risk, not yet confirmed reproduced in practice — needs a concrete repro (paint a mask selection, run Commit to Disk with some deletions before the selected indices, check if the mask selection visibly drifts) before deciding on a fix (remap indices through the deletion, or block/warn the commit when a mask exists).

[D] Repro plan: user has a SplatMask test level ready, will paint/delete/commit manually. Claude pulls before/after snapshots via unity_execute_code (mask splatIndices + asset splatCount) on the TechTests instance (port 7891) instead of relying on visual judgment alone.

[^] Continue masking. Last: about to snapshot mask/asset state before user runs Commit to Disk. Next: take "before" snapshot, wait for user to commit, take "after" snapshot, diff. Confirm: none.
[F] Before-snapshot taken on "Splat" GameObject (asset "bonsai"): splatCount=83643, mask entry "Selection 0" weight=0.384, count=30043, first5 indices=[26813,26814,26815,26816,26817].
- user hit ArgumentOutOfRangeException trying to delete splats — unrelated to masking
[F] Root cause (unrelated to masking target): GaussianSplatRenderer.ComputeRawSelectionBounds (GaussianSplatRenderer.cs:1773-1775) hardcodes 12-byte (3-float) per-splat stride when reading m_GpuPosData as raw bytes, but the actual GraphicsBuffer.stride is 4 (it's a flat float buffer, 3 floats/splat = 12 bytes total represented as element stride 4). Byte indexing `i * stride + 8` using the wrong stride overruns the byte array on the last splats — crashes EditDeleteSelectedWithParams (the splat-delete tool) unconditionally, before any decimation/mask repro can run. Confirmed via live inspection on TechTests (port 7891): posBuf.count=83644, posBuf.stride=4, splatCount=83643.
[>] Pre-existing bug: ComputeRawSelectionBounds stride mismatch crashes all splat deletion. Blocks the decimation/mask-index repro entirely until fixed — separate target from masking, parked for now.

[^] Continue masking. Last: delete-selection crashes due to unrelated pre-existing stride bug in ComputeRawSelectionBounds, parked. Next: find another way to change asset splat count for the repro, or pause repro until that bug is fixed. Confirm: none.
[F] User loaded new uncompressed asset "Piranesi_500k" (VeryHigh quality, no recompression) on a fresh GameObject. Before-snapshot: splatCount=500000, posBuf.count=1500000 (stride 4) — here posBufCount is an exact 3x multiple of splatCount, unlike bonsai's posBufCount=83644 vs splatCount=83643 (off by 1, not a clean 3x). Mask entry "Selection 0" weight=0.456, count=140519, first5=[0,1,2,3,4].
[H] bonsai's posBufCount not being an exact 3x multiple of splatCount may be why delete crashed there (buffer undersized relative to what ComputeRawSelectionBounds expects) — Piranesi_500k's clean 3x ratio suggests delete might not crash here. Unverified — next step is to retry delete on this asset.

[^] Continue masking. Last: new clean VeryHigh asset (Piranesi_500k) loaded and snapshotted, posBuf ratio looks correct unlike bonsai's. Next: user retries splat delete + Commit to Disk on this asset; re-snapshot after to check mask index drift. Confirm: [H] above.
[F] User manually tested delete + commit on Piranesi_500k — workflow itself unaffected, no crash. But the manual test changed the mask state, invalidating the clean before/after comparison needed for the repro.
[>] Parked: mask splatIndices drift after Commit to Disk / Separator — needs a fresh before-snapshot + controlled delete + after-snapshot to actually confirm/deny index corruption. Not done this session.

[^] Continue masking. Last: repro investigation parked — manual test invalidated the clean snapshot comparison. Next: drift or wrap-up. Confirm: none.

--- CLOSED 2026-06-19 — moved GaussianSplatMask to a sub-asset of GaussianSplatAsset (Create Mask button + name prompt in renderer's Mask section); surfaced unconfirmed risk of mask splatIndices drift after Commit to Disk/Separator; found and parked an unrelated pre-existing crash in ComputeRawSelectionBounds (stride mismatch) ---
