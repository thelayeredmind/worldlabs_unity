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
