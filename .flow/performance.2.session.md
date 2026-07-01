# performance — Session 2

- session opened

- user corrected the orientation summary's "Pass 1 ready for promotion" framing: Pass 1 is visually non-viable as-is — renders fully colored quads, not gaussian circles.

[F] Confirmed via direct read of RenderGaussianSplats.shader (lines 284-289): Pass 1's frag shader computes `alpha = saturate(exp(power) * max(i.col.a, 0))` (line 287) but never uses it — line 288 returns `half4(i.col.rgb, 1)` unconditionally, hardcoding full opacity. Separate from the pass's intentional discard-removal (justified by tight vertex-side quad sizing) — the gaussian falloff multiply was dropped too, which the "no discard needed" comment does not justify. Root cause of the "fully colored quads instead of circles" regression the user observed.

[D] Downgrades open thread "promote Pass 1 to default" from ready-to-ship to blocked-on-fix: needs its frag shader fixed (apply computed alpha to color/output) and revisual-verified before promotion can even be considered, on top of the never-done on-device perf confirmation of its claimed ~19ms saving.

[^] Continue performance. Last: corrected Pass 1's status — its frag shader silently drops the computed gaussian alpha (hardcodes opacity=1), producing flat colored quads instead of circles; root-caused via direct shader read. Not fixed. Next: drift — decide whether to open an Activity to fix Pass 1's alpha application, or work a different open thread (frustum-cull compaction, LOD, contribution-cull reordering, on-device compression test, cold-start readback artifact). Confirm: none.

- user: work thread 2 (frustum-cull compaction) then thread 3 (LOD), in that order — matches the sequential plan already logged (LOD depends on compaction's dispatch-count-elastic pipeline).

[Activity] Frustum-cull compaction: move the existing per-splat plane-vs-sphere cull (currently inside CSCalcViewData, runs after CSSetIndices/sort) earlier in dispatch order so it produces a compacted index buffer of surviving splats before the sort runs, and shrink CSCalcDistances/sort/CSCalcViewData's actual dispatch size to the compacted count — not just skip work inside a still-full-count dispatch.

- user asked whether relocating the cull kernel alone would be enough to shrink dispatch counts.

[D] No — Dispatch()/DispatchCompute() calls take a thread-group count fixed at call time in C#; a kernel cannot shrink its own already-launched dispatch or a subsequent one just by writing a smaller survivor count mid-run. Two genuinely separate problems: (1) which splats survive (the existing cull, relocatable), (2) how many threads the next kernel launches (needs either GPU-side indirect dispatch args or a CPU-visible count).

- user asked whether changing dispatch thread-count after running the cull kernel is expensive.

[F] Read GpuSorting.cs in full (both DeviceRadixSort and FidelityFxSort). Neither backend uses DispatchIndirect anywhere — every cmd.DispatchCompute call in both classes takes a thread-group count computed arithmetically on the CPU from args.count (a plain uint set once in Initialize), never from a GPU buffer. Confirms indirect dispatch would require converting two separate third-party-derived sort implementations, not a small/local change.

[D] Two real options for reaching this Activity's destination: (a) convert both sort backends to DispatchIndirect with GPU args buffers — correct architecturally (same-frame, no stall) but a nontrivial change across two sort implementations; (b) CPU AsyncGPUReadback of the survivor count, re-dispatch next frame with a smaller args.count — no sort-backend changes needed, but introduces a one-frame lag between cull and sort/view-data sizing (acceptable for temporally-coherent camera motion, same tradeoff surfaced earlier this session before the sort code was actually read). Not yet chosen — user paused before deciding to explain indirect dispatch mechanics/tradeoffs (answered inline, no log-worthy finding — general GPU concept explanation, not project-specific).

- user: put this on hold — the culling work introduced a standalone-only regression, sort appears flipped with a per-side offset. Different destination and different nature of doing (debugging a live symptom vs. designing dispatch mechanics) — pivoting, not continuing.

[^] Suspended — frustum-cull compaction (destination: move cull earlier, produce compacted index buffer, shrink CSCalcDistances/sort/CSCalcViewData dispatch to compacted count; blocked on choosing CPU-readback-lag vs. indirect-dispatch-conversion route, not yet decided) — opening sub-activity: root-cause and fix a standalone-only regression introduced by this session's (session 1's) culling work — not a sort-order issue, described as the whole world visually warping/pulling inward toward the camera like a black hole. A near-identical symptom occurred earlier in the project's history; root cause that time was clip-space/matrix math. Reproduces standalone/Quest build only, not in editor Game view.
[/Activity]

- user: does NOT happen in editor because editor Game view runs D3D (not the Vulkan backend Quest actually uses) — _OptimizeForQuest is a dead end/placebo (visible directly in code, not worth investigating further). Asked whether the editor can be switched to run Vulkan to get a fast local repro instead of round-tripping through Quest builds.

[F] Grepped _OptimizeForQuest across the whole package: it recomputes centerClipPos in the vertex shader via UNITY_MATRIX_VP (all 4 passes in RenderGaussianSplats.shader), fully separate from the compute-side _MatrixVP/_FrustumPlanes this session's cull work touches — confirms user's dead-end call, this path is unrelated to the regression.

[D] Correct next repro strategy: switch Unity Editor's active graphics API to Vulkan (Player Settings, or -force-vulkan launch arg) to reproduce standalone-only, Quest-relevant (Vulkan-specific) bugs locally without a build/deploy round-trip — matches the "clip-space/matrix math" prior root cause, since D3D vs Vulkan differ in exactly matrix/clip-space conventions (reversed-Z, NDC Y-flip, projection matrix layout) that this codebase already special-cases in several places (UNITY_REVERSED_Z branches, GL.GetGPUProjectionMatrix).

- user switched the editor to Vulkan manually, asked Claude to verify, reported still unable to reproduce.

[F] Verified via unity_execute_code (TechTests instance, port 7890): SystemInfo.graphicsDeviceType == Vulkan, PlayerSettings.GetUseDefaultGraphicsAPIs(StandaloneWindows64) == false. Editor genuinely running Vulkan now, confirmed at the API level, not just Player Settings config.

[D] Vulkan-in-editor does NOT reproduce the black-hole effect even though it's confirmed active. Rules out "any Vulkan backend" as sufficient to trigger it — something else specific to the actual standalone/Quest build (build-time shader variant stripping, a different code path only active outside the editor, mobile/Android-specific shader compilation, or something in the build's actual runtime environment) must be the real differentiator, not the graphics API alone.

- user: reproduces on Quest/Android build only, not a Windows Vulkan standalone. Confirmed real XR/OpenXR stereo rendering is active (not flat/mono deployed to Android) — this is the piece that reframes the whole investigation from "graphics API" to "stereo eye matrices."

[F] Read GaussianSplatRenderer.cs CalcViewData (line 1168 on) in full: `matView = cam.worldToCameraMatrix` and `matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)` (lines 1181-1182) are both mono/single-eye Camera properties — Unity's stereo API instead exposes per-eye matrices via `cam.GetStereoViewMatrix(eye)`/`cam.GetStereoProjectionMatrix(eye)`, never called anywhere in this method. `_MatrixVP` (= matProj*matView, uploaded once per CalcViewData call regardless of which eye XR is currently rendering) feeds both the existing per-splat view-space transform AND this session's new frustum-plane cull (`GeometryUtility.CalculateFrustumPlanes(cam)` at line 1205, same mono `cam` reference). Confirmed via grep: no GetStereoViewMatrix/GetStereoProjectionMatrix call exists anywhere in GaussianSplatRenderer.cs.

[D] Initial theory (WRONG on attribution): assumed the mono matView/matProj in CalcViewData was pre-existing and the new frustum cull just newly exposed the error. Corrected after user asked "how did our last commit cause this" and Claude actually read the commit diff instead of assuming.

[F] Read `git show a8f2625` (this session's own "add per-splat frustum cull" commit) for SplatUtilities_DeviceRadixSort.compute directly. Found the real cause, a deliberate one-line swap in CSCalcViewData:
```
-float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
+float4 centerClipPos = mul(_MatrixVP, float4(centerWorldPos, 1));
```
Before this commit, CSCalcViewData used `UNITY_MATRIX_VP` — Unity's ambient/global shader matrix, which DOES resolve correctly per-eye during stereo instanced rendering (same SRP mechanism the vertex shader's UNITY_MATRIX_VP already relies on, confirmed correct). This commit replaced it with `_MatrixVP`, an explicit C# constant uploaded once per CalcViewData call from a single mono `cam.worldToCameraMatrix`/`GL.GetGPUProjectionMatrix` (GaussianSplatRenderer.cs lines 1181-1182) — same value for both eyes. The commit's own comment justifies the swap for an unrelated, real problem (UNITY_MATRIX_VP can reflect the wrong camera when SceneView + Game View both dispatch this kernel in the same editor tick) — a legitimate editor-side concern that does not consider the stereo-XR-eye implication.

[D] ROOT CAUSE, corrected: this session's own commit (a8f2625) directly caused the regression. Swapping UNITY_MATRIX_VP → _MatrixVP in CSCalcViewData fixed a real editor multi-camera problem but broke per-eye correctness in stereo XR builds — view.pos (compute-side splat clip position, feeding sort distance ordering AND the vertex shader's centerClipPos before _OptimizeForQuest's already-dead-end recompute) is now the same mono matrix for both eyes, while the vertex shader's own UNITY_MATRIX_VP still correctly resolves per-eye. Splats get positioned/culled once with a matrix matching neither eye, then partially reprojected correctly per-eye — the divergence is the black-hole/pulled-inward effect, worse toward each eye's edge ("offset on each side" = each eye). Not pre-existing — introduced this session, directly attributable to this one line.

- open question, not yet resolved: the SceneView/GameView cross-talk problem the swap was originally solving is real and still needs a fix — the correction can't just revert to UNITY_MATRIX_VP without reintroducing that. Needs either per-eye _MatrixVP upload (query cam.GetStereoViewMatrix/GetStereoProjectionMatrix per eye when in XR, matching what UNITY_MATRIX_VP already does correctly) or another mechanism that is both editor-multi-camera-safe AND stereo-correct.

- user: the SceneView/GameView cross-talk fix was only editor QoL, not load-bearing — try the direct revert to UNITY_MATRIX_VP, user will verify on-device whether it breaks anything (including the cross-talk case).

[C] SplatUtilities_DeviceRadixSort.compute CSCalcViewData: reverted centerClipPos from `mul(_MatrixVP, ...)` back to `mul(UNITY_MATRIX_VP, ...)`, restoring per-eye-correct stereo XR behavior. Comment rewritten to explain the stereo correctness reason (not the original SceneView/GameView reason, which the user has deprioritized). _MatrixVP itself is untouched/still uploaded and still used for the frustum-plane cull and other explicit matrix params elsewhere in the file — only this one clip-position computation changed.

[F] Compile verified clean: unity_get_compilation_errors → 0 errors, isCompiling=false.

[^] Continue performance. Last: reverted CSCalcViewData's centerClipPos to UNITY_MATRIX_VP (from _MatrixVP), undoing the specific line from this session's own commit a8f2625 that broke stereo XR correctness on Quest. Compile clean. Next: user to test on-device (Quest build) — confirm the black-hole/warping effect is gone, AND check whether the original SceneView/GameView cross-talk problem the swap was meant to solve resurfaces (user considers this acceptable risk, editor QoL only, not load-bearing). Confirm: ~ awaiting on-device verification result.
