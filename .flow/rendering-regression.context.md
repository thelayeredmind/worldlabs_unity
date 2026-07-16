# rendering-regression — Context

## What
Investigating a specific reported regression: Gaussian splats rendering flat with no spatial/stereo perception in HMD (Quest). Scoped narrowly to this one symptom and its root cause — not a general rendering-quality or performance target (see `performance` for that).

## Why
Root cause: the `symmmetric-greedy` branch forked from `main` at an old point (`a8f2625`) and never picked up `main`'s later commit `aa9e932 fix: Revert to User builtin Unity Matrix VP` — the stereo-correctness fix documented in this package's CLAUDE.md (`CSCalcViewData` must use `UNITY_MATRIX_VP`, not a mono-uploaded `_MatrixVP` constant, for correct per-eye clip position in stereo XR). Running the pre-fix code reproduces exactly as "flat, no spatial perception" in real HMD while looking fine in the editor (mono rendering masks the bug). This is a second confirmed real-world instance of the exact failure mode CLAUDE.md already warns about — reinforces that warning rather than adding a new one.

## Where
- `Shaders/SplatUtilities_DeviceRadixSort.compute` — `CSCalcViewData` kernel, the fix point (already documented in CLAUDE.md, not modified this session — just confirmed present/absent).
- Fix delivered via merge: `main` merged into the morph branch, landing on a new branch `gale` at commit `7a3f2c4`, bringing in `aa9e932` and its ancestry. User confirmed fixed on-device: "symmetric works, a bit better."

## When

**Future:**
- (none — target resolved)

**Past:**
- Flat/no-spatial-perception HMD regression root-caused to `symmmetric-greedy` missing `main`'s stereo matrix fix (`aa9e932`); resolved by merging `main` in, confirmed fixed on `gale` branch.
