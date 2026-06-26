# morphing — Session 5

- session opened, seeded by a target switch from KitchenOfMemories' gaussian-morphing.2.session.md
- context: KitchenOfMemories' Timeline morph-pair clip (GaussianSplatTrack/Clip/MixerBehaviour) is fully wired and smoke tested -- SetAssets, t-driving via authorable curve, and enabled-toggling all shipped and confirmed working there. New bug surfaced during that smoke testing: the clip's left/right asset assignment can come out swapped relative to the GaussianMorphMap's built orientation, and the existing fix (a swap button on GaussianSplatMorpher, presumably SwapAssets()) doesn't hold -- it resets on every clip restart even if manually corrected in between.

[F] Read GaussianMorphMap.cs: stores ONLY splatCountLeft/splatCountRight (ints) -- no asset references or GUIDs at all.
[F] Read GaussianSplatMorpher.cs MapIsSwapped() (line 513-525): compares m_AssetLeft.splatCount/m_AssetRight.splatCount against m_MorphMap.splatCountLeft/splatCountRight -- a count-based heuristic, no real identity check. Falls back to a warning + assumes not-swapped if neither count pairing matches. This is the root cause: whenever both assets have equal/similar splat counts, this heuristic can't reliably distinguish orientation, and recomputes (potentially wrong) on every Setup() call -- independent of any swap button state, which only mutates m_AssetLeft/m_AssetRight in place and doesn't persist as data MapIsSwapped() would respect on the next rebuild.
[F] Read GaussianMorphMapBuilderWindow.cs SaveAsset() (line 293-311): exact construction site -- `var map = CreateInstance<GaussianMorphMap>(); map.splatCountLeft = left.splatCount; map.splatCountRight = right.splatCount; ...` then AssetDatabase.CreateAsset. This is where left/right identity must be captured at build time.
[D] User confirmed: fix at the source. Store real asset identity (GUIDs) on GaussianMorphMap, set at build time, so MapIsSwapped() can compare by identity instead of splat count.
[D] GaussianSplatAsset has no GUID property of its own -- use AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) at build time (SaveAsset, editor-only call) and store as a string field. MapIsSwapped() compares the morpher's current assetLeft/assetRight GUIDs (looked up the same way, also editor-only) against the map's stored GUIDs by exact identity. Falls back to the existing splat-count heuristic only when the map predates this fix (stored GUID fields empty) -- backward compatible with already-built maps, no forced re-bake required immediately.

[Task] Add leftAssetGuid/rightAssetGuid string fields to GaussianMorphMap. Set them in GaussianMorphMapBuilderWindow.SaveAsset() via AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(left/right)). Rewrite GaussianSplatMorpher.MapIsSwapped() to compare the currently-assigned assetLeft/assetRight (resolved to GUIDs the same way) against the map's stored leftAssetGuid/rightAssetGuid by exact identity when both are non-empty; fall back to the existing splat-count comparison only when the map's GUID fields are empty (pre-fix maps), preserving today's behavior for those until they're rebuilt. This makes orientation detection deterministic and immune to splat-count collisions, fixing the "swap resets on clip restart" bug reported from KitchenOfMemories' Timeline morph-pair clip.

Task: GaussianMorphMap stores asset GUIDs at build time; MapIsSwapped() compares by identity with count-based fallback for legacy maps.

Ready to wind up.
[^] Continue morphing. Last: Task opened, all gaps closed. Next: wind up and execute. Confirm: none.

[S] GaussianMorphMap GUID identity + MapIsSwapped() rewrite
- reviewable surface: rebuild a MorphMap in KOM_TechTests editor, inspect resulting GUID fields, smoke test orientation
Attempt 1
Added leftAssetGuid/rightAssetGuid (string) to GaussianMorphMap. GaussianMorphMapBuilderWindow.SaveAsset() now sets them via AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(left/right)) alongside the existing splatCountLeft/Right assignment. Rewrote GaussianSplatMorpher.MapIsSwapped(): when the map carries a non-empty GUID (built after this fix), resolves the morpher's current assetLeft/assetRight to GUIDs the same way and compares by exact identity -- unambiguous regardless of splat-count collisions. Falls back to the original splat-count heuristic only when both map GUID fields are empty (legacy pre-fix maps). GUID resolution wrapped in #if UNITY_EDITOR (AssetDatabase is editor-only) -- matches the existing FindMorphMap editor-only guard already in this file; at runtime in a build this always falls through to the count heuristic, same as today's behavior there.
Compile check (port 7890, KOM_TechTests): 0 errors, isCompiling false.
✓ Clean (compile)
[/S]
[^] Continue morphing. Last: GUID fields + MapIsSwapped rewrite written, compiles clean. Next: user's turn -- rebuild a MorphMap in the editor (ideally the pair that showed the swap bug), inspect leftAssetGuid/rightAssetGuid landed correctly, confirm orientation holds across clip restart. Confirm: none.

- review: user confirmed orientation fix "works" -- but flagged console errors during the smoke test. Read full console log (unity_console_log, port 7890): 7 identical NullReferenceException entries, all from GaussianSplatRendererEditor.OnGetFrameBounds() (line 913), triggered by SceneView.FrameSelected (pressing F). Confirmed by reading the source: line 913 does `bounds.SetMinMax(gs.asset.boundsMin, gs.asset.boundsMax)` with no null check on gs.asset. Line 910's earlier guard (`!gs.HasValidRenderSetup`) evidently still passes while GaussianSplatMorpher has taken over via SetExternalBuffers -- in that state gs.asset is legitimately null (renderer driven by external buffers, not its own asset), but OnGetFrameBounds doesn't account for that. Only one distinct bug in the log, not two -- the rest of the 50 entries are normal [Morpher] debug logging from Setup/BuildIndexBuffer/blend, nothing else exceptional.

[Task] Fix GaussianSplatRendererEditor.OnGetFrameBounds() (line 907-920) to null-guard gs.asset before reading boundsMin/boundsMax -- currently throws NullReferenceException whenever Frame Selected (F) is pressed on a GaussianSplatRenderer while a GaussianSplatMorpher has taken over via SetExternalBuffers (gs.asset is legitimately null in that state). Fall back to a sensible default Bounds (matching the existing !HasValidRenderSetup early-return at line 910-911) when gs.asset is null instead of dereferencing it.

Task: Null-guard OnGetFrameBounds against gs.asset being null during active morph takeover.

Ready to wind up.
[^] Continue morphing. Last: reopened with new Task for OnGetFrameBounds null-guard. Next: wind up and execute. Confirm: none.

[S] Null-guard OnGetFrameBounds against gs.asset == null
- reviewable surface: code diff; re-test by pressing F while a morph is active
Attempt 1
Added `|| !gs.asset` to the existing early-return guard at line 910 (alongside !gs / !gs.HasValidRenderSetup) -- uses Unity's overloaded null check (!gs.asset), not a raw reference comparison, consistent with this project's known "fake null"/destroyed-UnityEngine.Object footgun. Falls back to the same default Bounds(zero, one) the !HasValidRenderSetup case already used.
Compile check (port 7890): 0 errors, isCompiling false.
✓ Clean (compile)
[/S]
[^] Continue morphing. Last: null-guard written, compiles clean. Next: user's turn -- press F (Frame Selected) on the GaussianSplat object while a morph is mid-blend, confirm no NullReferenceException. Confirm: none.

- review: user confirmed "works. Good job." -- nothing raised, amend stack empty
[/Task] -- closed: OnGetFrameBounds null-guarded against gs.asset == null during active morph takeover. Smoke tested clean.

[/Task] -- closed (orientation fix, opened earlier this session): GaussianMorphMap stores leftAssetGuid/rightAssetGuid; MapIsSwapped() compares by identity with splat-count fallback for legacy maps. Smoke tested clean by user (rebuilt MorphMap, confirmed orientation holds).

--- CLOSED 2026-06-26 — Two fixes shipped this session: (1) GaussianMorphMap orientation detection now uses asset-GUID identity instead of a splat-count heuristic, fixing the "swap resets on clip restart" bug surfaced from KitchenOfMemories' Timeline morph-pair clip. (2) GaussianSplatRendererEditor.OnGetFrameBounds() null-guarded against gs.asset being null during active morph takeover (Frame Selected NRE). Both smoke tested clean by user. Not yet committed. ---

[^] Continue morphing. Last: both Tasks closed clean this session, smoke tested. Next: drift or wrap-up. Commit nudge pending -- two uncommitted fixes in this repo (GaussianMorphMap GUID + OnGetFrameBounds null-guard). Also: KitchenOfMemories has its own pending checkpoint (enabled-toggling change, user said they'd commit it) and will need its package reference bumped once this repo's fixes are pushed.
