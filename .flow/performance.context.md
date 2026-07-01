# performance — Context

## What
Understanding the bottleneck(s) limiting Quest 3 Gaussian splat frame time, and the levers (render/format/algorithmic) that circumvent them. Distinct from the `quality` target: quality/compression is one lever on performance, not the whole picture — this target also covers render-scale, culling, sort cadence, overdraw-reduction experiments, and (new this session) splat-count reduction via LOD.

Not concerned with the WorldLabs API client or asset-import UX — purely rendering-path performance.

## Why
25fps measured vs. 70fps target on Quest 3. Root cause per `docs/GaussianSplat_Analysis.md`: the renderer was designed for desktop (RTX 3080 Ti class) and is bandwidth/overdraw-bound on Adreno 740's TBDR architecture, not compute-bound (ALU utilization never exceeds 28% even in the most saturated traced config; CPU utilization sits at 13% during the GPU-bound baseline — CPU has substantial idle headroom the GPU does not).

## Where
**Renderer/pipeline (existing bottleneck levers):**
- `Runtime/GaussianSplatting/GaussianSplatRenderer.cs` — per-asset params: `m_ContributionCullThreshold`, `m_AlphaDiscardThreshold`, `m_OpaqueExperiment`, `m_DepthProximityExperiment`, `m_SortNthFrame`, `m_OptimizeForQuest`.
- `Runtime/GaussianSplatting/GaussianSplatURPFeature.cs` — `resolutionScale` (dominant lever, RS² pixel scaling), `stencilOverdrawCap` (eliminated, no gain), `depthProximityTransparency`, `proximityDepthRange`.
- `Shaders/SplatUtilities_DeviceRadixSort.compute` — `CSCalcViewData`, sort kernels.
- `docs/GaussianSplat_Analysis.md` — full bottleneck hierarchy, GPU counter reference, hardware architecture background.
- `docs/GaussianSplat_Progress.md` — open threads: `[EZ-01]` (true early-Z, blocked on vertex-side NaN cull + tight quad sizing), `[DP-02]` (compute-side pre-binning depth-proximity cull — closest existing idea to LOD), `[SC-01]` (stream compaction ordering artifact), `[BC7-01]` (Low/VeryLow quality broken on Quest Vulkan), `[CAM-01]`, `[CUT-01]`, `[FX-01]`, `[KOM-01]` (target 200-250k splats for KOM).

**LOD reference (external, read-only, not part of this repo):**
- `D:\Dev\playground\spark\src\SparkRenderer.ts` — CPU-side (Web Worker) LOD tree traversal, screen-space-error + splat-budget driven index selection. `defaultSplatTarget()` (line ~1122) hard-codes 500,000 splats for Oculus/Quest specifically.
- `D:\Dev\playground\spark\src\SplatPager.ts`, `worker.ts` — chunk streaming and (unread) tree-traversal internals.

**Test scene:**
- Unity scene `GaussianPerformanceTests` (project `TechTests`, Unity MCP port 7890) — `Splat/BaseLine` (`burning house daylight.asset`, 262,144 splats, Medium quality, small interior bounds) and `Splat/BadPerformance` (`01-02_LoRes - Red trees on rolling hills-2.asset`, 272,669 splats, VeryHigh/Float32 quality, large landscape bounds ~157×59×171 world units — ~25x BaseLine's linear extent). A `_low.asset` sibling variant (same splat count/bounds, Norm11/Norm6/Norm8x4/Cluster16k format) exists on disk but is not yet assigned in-scene.

## When
- Session 1 (2026-07-01): Oriented on the bottleneck model (overdraw/bandwidth primary, sort/CalcView floor secondary). Investigated a compression-tier anomaly (BadPerformance's "Low made no difference" observation) — resolved as an untested claim: the Low asset was never actually reassigned in-scene; a proper VeryHigh-vs-Low pair now exists on disk for a future on-device test. Theoretically reasoned (no Quest access this session) that Low should meaningfully outperform VeryHigh given the documented bandwidth-bound model — the "decompression overhead makes Medium a sweet spot" theory is not well supported by current data. Investigated spark.js's LOD system as a candidate new lever: CPU-worker tree traversal, screen-space-error + hard splat budget (500k on Quest/Oculus specifically) produces a per-frame index subset that the GPU renders — orthogonal to compression, closest existing idea is `[DP-02]`. Resolved the key feasibility question: spark's worker runs on CPU, which our own traces show is idle (13%) while GPU is the bottleneck — Unity's C# Job System / background thread is the direct equivalent, a new but well-motivated pattern for this codebase.

Open threads:
- [>] On-device test pending Quest access: assign `01-02_LoRes...rolling hills-2_low.asset` to a scene GameObject and measure actual fps against the VeryHigh original, to confirm the theoretical bandwidth-model prediction (Low should be faster).
- [>] Sketch a concrete LOD port plan for this renderer: CPU job (tree traversal) → GPU index buffer → consumed by `CSCalcViewData`/`CSCompactVisible`. Not yet started; worker.ts's actual traversal algorithm (tree structure, descent criteria) also not yet read in depth.
- [>] Decompression-overhead-at-Low theory deprioritized (unsupported by the bandwidth-bound model) but not fully ruled out — revisit if on-device Low-vs-Medium-vs-VeryHigh data contradicts the theoretical prediction.
