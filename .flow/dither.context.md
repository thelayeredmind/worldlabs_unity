# dither

## What

Dithered/probabilistic splat selection and deletion tooling for the Editor brush and box-select workflows: density-based partial delete, position-weighted Hardness intensity, and HSL color-based brush selection. Lives in `GaussianSplatRenderer.cs` (public `Edit*` API), `GaussianSplatRendererEditor.cs` (Inspector UI), `GaussianBrushSelectTool.cs` (brush tool state/interaction), and `SplatUtilities_DeviceRadixSort.compute` (GPU kernels: `CSDeleteSelectedWithParams`, `CSBrushSelect`, `CSBrushSelectWorld`, `CSPickSplatColor`).

## Why

User wanted delete/selection tools that don't just do binary all-or-nothing operations — Density and Hardness let a stroke/delete partially affect the edge of a selection or brush radius, and Color Mode lets selection be driven by visual similarity instead of pure spatial proximity. Built and validated in this test-lab repo per the wrapper project's CLAUDE.md before porting to KitchenOfMemories.

## Where

**Delete (Density/Hardness):**
- `GaussianSplatRenderer.cs`: `EditDeleteSelectedWithParams(density, hardness, usePositionMode)` — density decides candidacy (position falloff), Hardness decides intensity.
- `SplatUtilities_DeviceRadixSort.compute`: `CSDeleteSelectedWithParams`. At Hardness>=1, candidates get the existing hard delete (deleted-bit). Below that, `opacityMult = saturate(1.0 - _DeleteHardness * t)` is written to `_SplatOpacityMultRW` (a `RWStructuredBuffer<float>`, separate Props ID `SplatOpacityMultRW` from the read-only material-bound `SplatOpacityMult` — these must never be conflated, see Bug 1 below). `t` is a radial ellipsoid distance (`saturate(length(localOffset))`) from the selection's local-space extents, not a bounding-box max — matches the selection's actual shape.
- `GaussianSplatRendererEditor.cs`: Delete Selected button captures `selectedBefore = gs.editSelectedSplats` before the delete call and gates the undo-stack push on `selectedBefore > 0` (not `editDeletedSplats > 0`, which is false whenever Hardness<1 produces a pure opacity blend with zero hard deletes). Undo round-trip confirmed working live.
- The old "Position Mode" (opacity-vs-position-gate toggle, per-splat deterministic index-hash roll) that preceded the Density/Hardness redesign is now dead/unused code in `GaussianSplatRendererEditor.cs` and `CSDeleteSelectedWithParams` — confirmed by user not worth stripping right now, deprioritized.

**Select-by-color:**
- `GaussianSplatRenderer.cs`: `EditPickSplatColor(screenPos, pickRadiusPx, cam, out Color)` — screen-space GPU nearest-splat color sample via `CSPickSplatColor`. `EditBrushSelect`/`EditBrushSelectWorld` gained `colorMode`/`refColor`/`tolHsl` params; C#-side `RgbToHsl` helper matches the shader's exactly (needed for correct C#/GPU HSL comparison).
- `SplatUtilities_DeviceRadixSort.compute`: `RgbToHsl`/`ColorMatchesHsl` helpers; `_BrushColorModeActive`/`_BrushRefColorHsl`/`_BrushColorTolHsl` uniforms. In `CSBrushSelect`/`CSBrushSelectWorld`, color match is a hard gate checked first (when active), and density thinning always applies afterward on top of whatever passed — an AND gate, not a replacement. Density dithers *within* the color-matched candidates.
- `GaussianBrushSelectTool.cs`: `ReferenceColor`, `PickModeActive`, `ColorModeActive`, `ToleranceHue`/`ToleranceSaturation`/`ToleranceLightness` (all clamped 0-1) static state. `HandleBrushGUI`'s MouseDown branches to a pick dispatch when `PickModeActive` instead of brushing.
- `GaussianSplatRendererEditor.cs`: Color Mode toggle, Pick Color button + color swatch, three tolerance sliders (shown only while Color Mode active) below the Density slider. Density is NOT disabled by Color Mode (that was the initial design, superseded by the AND-gate decision).
- Pick precision (Alt+Click, 20px radius) explicitly parked as good-enough — not a priority.

## When

### Future
- (none currently open — dead Position-Mode code cleanup deprioritized indefinitely, not tracked as a live thread)

### Past
- Position/Opacity delete-mode toggle → superseded by the Density=shape/Hardness=intensity redesign; old toggle code left in place but unused, confirmed low priority to strip.
- Hardness redesign: fixed an inverted-lerp bug where Hardness=0 fully hid every candidate (should mean ~no effect) — corrected to `saturate(1.0 - _DeleteHardness * t)`. Fixed box-relative falloff (`t`) falsely treating bounding-box corners as deep-in-selection — replaced with radial ellipsoid distance.
- Delete-undo-stack push silently broken by the Hardness redesign (gated on hard-delete count, now zero for Hardness<1) — fixed, confirmed working live.
- Select-by-color: built pick kernel, Inspector UI, and HSL brush gating. Initially designed as "Color Mode replaces Density entirely" — revised to an AND gate (color gates candidacy, density dithers within matches) per explicit user request after seeing it live.
- **Bug 1** (recurring pattern, hit twice this feature): a new RW compute-buffer/uniform binding can pass `unity_get_compilation_errors` (C#-only) and even a shader ForceUpdate reimport, yet still fail at runtime — either a GPU property-not-set validation error (`_SplatOpacityMultRW` bound under the wrong Props ID) or a whole-file HLSL compile failure invisible to the C# checker (`InterlockedMin` called on `RWStructuredBuffer` instead of `RWByteAddressBuffer` broke every kernel in the file). Both were only caught via `unity_console_log` (type: all), not `unity_get_compilation_errors`. Any new RW buffer/uniform binding in this shader needs a live console check, not just a compile-error check, before trusting "clean."
- **Bug 2**: a stray reference to a locally-scoped `bool colorMode` variable survived after its declaration was edited out during the Density-disabled-scope removal — CS0103, fixed by referencing `GaussianBrushSelectTool.ColorModeActive` directly. Also: Unity's compile-error cache can report a stale error after a fix is applied — force with `CompilationPipeline.RequestScriptCompilation()` if `unity_get_compilation_errors` looks stale.
