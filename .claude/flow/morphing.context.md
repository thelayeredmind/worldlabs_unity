**What**
`GaussianSplatMorpher` (Runtime) + `GaussianMorphMapBuilder`/`GaussianMorphMapBuilderWindow` (Editor) — blends two `GaussianSplatAsset`s via a precomputed `GaussianMorphMap` correspondence. Not the renderer itself; sits alongside `GaussianSplatRenderer` via external buffer binding.

**Why**
The correspondence search (nearest-neighbor matching between two splat sets) is computationally expensive — not feasible to run at runtime on Quest. It must be precomputed offline in the editor and baked into a `GaussianMorphMap` asset; runtime only does a cheap per-frame lerp between already-matched pairs. Strict 1:1 uniqueness in that correspondence isn't required; convergence (every splat has a destination to move toward) is the real constraint, so duplicate matches are acceptable when a clean 1:1 match can't be found within the round cap.

**Where**
- `Runtime/GaussianSplatting/Morph/GaussianSplatMorpher.cs` — runtime blend driver, GPU buffer upload/output, `t` interpolation
- `Editor/GaussianSplatting/Morph/GaussianSplatMorpherEditor.cs` — inspector; has a "Build Morph Map…" button next to asset fields, disabled until both assets are set
- `Editor/GaussianSplatting/Morph/GaussianMorphMapBuilder.cs` — correspondence search (round-based one-to-one matching + GPU-dispatched remainder resolution), CPU decode helpers
- `Editor/GaussianSplatting/Morph/GaussianMorphMapBuilderWindow.cs` — editor window UI; `Open(left, right)` overload pre-populates fields
- `Shaders/SplatCorrespondence.compute` — GPU nearest-neighbor distance search kernel (`FindBestMatch`), reused for both the main round loop and remainder resolution
- `Shaders/SplatMorph.compute` — per-pair lerp kernel, tolerant of duplicate indices in `matchedPairs`

**When**
- 2026-06-19: Added inspector button to open Morph Map Builder pre-filled with morpher's assets (second creation path, converges into existing build flow). Fixed an invariant violation where morph output splat count could exceed `max(left,right)` splat count due to unresolved correspondence collisions after the round cap — leftover splats are now resolved via GPU-dispatched nearest-neighbor lookup against the full opposite set (reusing the existing `ICorrespondenceDispatcher`/`FindBestMatch` kernel), accepting duplicate matches rather than enforcing strict 1:1 uniqueness. A CPU-side version of this remainder fix was tried first and rejected — same O(n·m) distance search as the GPU kernel but serial, so it stalled at real asset scale (100k–2M+ splats).

Open threads: optimizing `GaussianMorphMap` correspondence quality to minimize unmatched-splat count in the first place (parked, not started) — currently `kMaxMatchRounds = 8` round cap with GPU-dispatched leftover resolution as the safety net.
