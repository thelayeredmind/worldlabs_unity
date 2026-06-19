- session opened
- target: morphing — feature already built, exploring next steps
- regression to verify on GaussianMorpher, but wants UX improvement done first
- UX idea: GaussianMorpher needs a MorphTarget; currently only creatable via menu. Want second path: button in inspector next to target selection fields, auto-populates the creation window, converges into the existing main creation path.
[>] Regression verification on GaussianMorpher — parked until UX improvement lands.
[F] "MorphTarget" = GaussianMorphMap asset. "GaussianMorpher" = GaussianSplatMorpher (Editor/GaussianSplatting/Morph/GaussianSplatMorpherEditor.cs). Menu creation path = Tools/Gaussian Splats/Build Morph Map, opens GaussianMorphMapBuilderWindow (Editor/GaussianSplatting/Morph/GaussianMorphMapBuilderWindow.cs), static Open() with no params, fields m_AssetLeft/m_AssetRight/m_OutputFolder/m_ColorWeight are private SerializedField, no public API to pre-populate.
[D] Button always visible next to Asset Left/Right fields (not conditional on missing-map state) — separate, permanent second creation path rather than augmenting the existing warning HelpBox.
[D] Button disabled until both Asset Left and Asset Right are set — mirrors GaussianMorphMapBuilderWindow's own DisabledScope rule (line 113). Once both set, button enables and opens window pre-filled with both.
[D] Button label: "Build Morph Map…" (matches menu item / window's internal button wording, not the asset type name).
[Task] In GaussianSplatMorpherEditor.cs, add a button labelled "Build Morph Map…" placed near the Asset Left/Right fields (after the Swap button block, ~line 57-59, before the MorphMap section). Disabled (via EditorGUI.DisabledScope) unless both m_AssetLeft and m_AssetRight are set, mirroring GaussianMorphMapBuilderWindow's own build-button gating (line 113). On click, open GaussianMorphMapBuilderWindow pre-populated with the morpher's assetLeft/assetRight. This requires adding an overload to GaussianMorphMapBuilderWindow.Open() (currently parameterless, line 45-51) that accepts left/right GaussianSplatAsset and pre-fills m_AssetLeft/m_AssetRight before showing. The window's existing StartBuild()/SaveAsset() path is reused unchanged — this is purely a second entry point converging into the same creation flow. Goal: from the GaussianSplatMorpher inspector, with both assets already chosen, one click opens the builder window ready to build, instead of requiring the user to navigate Tools → Gaussian Splats → Build Morph Map and re-pick the same two assets.

Task: Add inspector button "Build Morph Map…" next to GaussianSplatMorpher's asset fields, opening a pre-populated Morph Map Builder window.

Ready to wind up.
[S] Step 1: Open() overload on GaussianMorphMapBuilderWindow + inspector button on GaussianSplatMorpherEditor — full feature in one step. Surface: code diff.
[C] GaussianMorphMapBuilderWindow.Open() — was a single parameterless static method with inline window-creation code; split into Open()/Open(left,right) overloads delegating to a shared private OpenWindow(left,right), per user's inline review comment flagging duplicated hard-coded Rect/minSize values.
[/S]
[/Task]
[^] Continue morphing. Last: review clean — user confirmed "Works" via smoke test (button opens pre-filled window, disabled when assets missing). Next: drift or wrap-up. Confirm: none.
