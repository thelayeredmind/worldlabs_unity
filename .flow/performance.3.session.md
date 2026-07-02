# performance — Session 3

- session opened

- user: built 9 different builds, each maximizing/removing/adding a different pipeline feature. Plan: run MQDH perfetto capture per app, record average values into an Excel comparison sheet. First need: which Perfetto values give full insight into pipeline resources/bottlenecks, so every build's row uses the same metric set.

[^] Continue performance. Last: session opened, user wants a defined Perfetto metric set (CPU/GPU/bandwidth/pipeline-stage counters) to use consistently across 9 comparison builds before capturing traces via MQDH. Next: identify the relevant Perfetto/GPU counters — likely candidates from this target's own history (docs/GaussianSplat_Analysis.md's GPU counter reference, ALU utilization, CPU utilization, frame time, bandwidth) plus whatever MQDH/hzos-dev-mcp exposes — and settle on the fixed metric list before any build gets run. Confirm: none.

- user: skip docs, clean sheet. CPU/GPU levels locked to 4 in MQDH already, don't second-guess that.

[Activity] Establish a fixed Perfetto metric set covering CPU/GPU pipeline stages (frame time, bandwidth, vertex/fragment/compute stage split, memory) sufficient for full pipeline insight, then run all 9 pre-built comparison test builds (found at builds\KOM_Playground_Quest, all modified after 11:00 today) through MQDH/hzos-dev-mcp perfetto capture, recording each build's values into a CSV for the user's Excel comparison.

[F] Test capture against app kom.TechTests before it was launched came back attached to com.oculus.ovrmonitormetricsservice with frame_timing null and only 310 slices/7.8s — confirmed via AskUserQuestion this was because the app wasn't running yet, not a capture bug.

[F] Second test capture (app running) returned target_app: com.oculus.vrruntimeservice (not kom.TechTests) but with rich live GPU counters (98.5% GPU util, real ALU/bandwidth numbers) — verified via direct SQL query that com.kom.TechTests (pid 10970) was present with 34283 slices; target_app label is just this hzos-dev-mcp tool's naming quirk (picks a different process), not a real attachment failure.

[D] Locked metric set (19 columns): app_gpu_ms (frame time), stale_frames_per_second, application_prediction_microseconds (XR latency) — frame/output tier. % Time Compute / % Time Shading Vertices / % Time Shading Fragments / % Shaders Busy — pipeline stage split, added after user asked specifically about vertex+fragment+compute shader visibility (original proposal only had one fragment metric, no vertex or compute-specific counter). Vertex Instructions/Sec, Vertex Memory Read BW — vertex detail. Fragments Shaded/Sec, Fragment ALU Instructions/Sec (Full) — fragment detail. GPU % Bus Busy, Texture Memory Read BW, % Stalled on System Memory, % Texture L2 Miss — bandwidth (primary bottleneck per project docs). app_rss_mb, app_uss_mb — memory footprint. gpu_level, gpu_frequency_mhz — sanity check MQDH's level-4 lock held throughout each capture.

[S] Build CSV comparison sheet at Packages/worldlabs_gaussian/.flow/performance_build_comparison.csv — builds as rows (user's explicit layout choice), one column per locked metric, seeded with the 9 real build names found on disk (test_perf_fullOpaque, test_perf_fullOpacityCulloff, test_perf_fullAlphaCulloff, test_perf_RT0.5, test_perf_Stencil1, test_perf_ProximityDepthRange, test_perf_NoSort, test_perf_Full, test_perf_LODCull — a 10th folder found alongside them, TechTests_BurstDebugInformation_DoNotShip, is debug symbols not a build, excluded).

[C] performance_build_comparison.csv created — header row + 9 empty build rows, ready to fill per-capture.

[F] Recurring capture-quality problem across almost every build this session: traces frequently show real values for the first several seconds then either (a) flatline to exactly/near 0.0 for the rest of the capture, or (b) the app visibly drops to passthrough/backgrounds. Confirmed via direct ts-ordered SQL query of app_gpu_ms each time before trusting a trace — this became the standing verification step before writing any row. Retracing after the freeze consistently produced clean data in every case prior to LODCull.

[F] test_perf_fullOpaque, test_perf_fullOpacityCulloff, test_perf_fullAlphaCulloff, test_perf_Stencil1, test_perf_ProximityDepthRange, test_perf_NoSort each required 2-3 capture attempts before a clean trace was obtained (bad attempts: frozen/flatlined tail, one passthrough drop, one all-zero stage-split glitch, one corrupted single-sample counter value at 1e23 scale — excluded from that build's averages only). All bad attempts discarded and noted in the CSV's Notes column with the reason, not silently dropped.

[D] User caught that averaging a bimodal trace (test_perf_fullAlphaCulloff oscillating almost every sample between ~10-14ms and ~38-68ms) produces a misleading single number — the 24.53ms average buried the ~64ms peaks the user actually observed on-device. Same bimodal pattern independently confirmed real (reproduced twice, once in-app-confirmed) for test_perf_ProximityDepthRange rather than discarded as a bad capture.

[D] Following the bimodal-averaging miss, user redirected the whole CSV's primary lens from average to max values. Rebuilt performance_build_comparison.csv with every metric's max column immediately left of its average column (not replacing average — max leads, avg retained for steady-state context), re-derived for all already-completed rows.

[R] test_perf_LODCull now hit the freeze pattern on its very first capture attempt (2324.7ms single-sample spike, likely a loading/shader-compile stall, plus the same flatline-to-near-zero tail after ts~377M) and then failed to even start on 3 consecutive retries afterward — user reports the perfetto capture call itself appears to freeze the app on this build, not merely correlate with app/scene state as assumed for every earlier freeze this session. Not yet root-caused: could be genuinely capture-triggered (e.g. capture overhead pushes this specific build past some threshold, a MQDH/perfetto-vs-app interaction specific to LODCull's code path) or could still be coincidental app/build instability that happens to align with capture timing. User explicitly deferred investigation — logged as an open thread, continuing to attempt LODCull capture for now rather than pausing the comparison to root-cause it.

[>] Open thread, not yet investigated: perfetto capture calls apparently freezing kom.TechTests specifically on the LODCull build (3 consecutive capture-start failures/rejections after the pattern was first noticed) — worth its own investigation activity later, distinct from and more specific than the general "traces need 2-3 attempts" pattern seen across all other builds this session (which always self-resolved on retry).

[^] Continue performance. Last: 6 of 9 builds' CSV rows filled (Full, NoSort, ProximityDepthRange, Stencil1, RT0.5, fullAlphaCulloff, fullOpacityCulloff, fullOpaque — 8 actually done; LODCull is the only one remaining and blocked). CSV restructured to max-first/avg-second column order per user's steer after the bimodal-averaging miss on fullAlphaCulloff. Next: retry LODCull capture (user chose to keep trying rather than pause for root-cause); if it keeps failing to start, consider whether this needs its own investigation activity per the open thread above. Confirm: none.

- user: device restarted after repeated LODCull capture failures/freezes.

[F] LODCull capture attempt #2 (v2) froze with the same signature as every other build's bad attempts: real values (11-22ms) then near-zero flatline tail after ts~628M — confirms this build is not uniquely broken, it's hitting the same general pattern, just needed more retries than most.

[F] LODCull capture attempt #3 (v3, first after device restart) succeeded structurally (no freeze, continuous data) but gpu_frequency_mhz averaged only 254MHz vs. every other completed build's steady 518-545MHz — flagged as likely still-settling clock state post-restart, not necessarily comparable to the rest of the CSV. Row wa written with this caveat noted rather than treated as clean.

[D] User asked to repeat specifically with GPU level reconfirmed at 4 post-restart before trusting a row. Capture attempt #4 (v4) came back with gpu_level flat at 4 (11/11 samples) and gpu_frequency_mhz flat at 545 (11/11 samples) — clean, comparable clock state. This v4 trace superseded v3 in the CSV; v3's row was overwritten, not kept alongside.

[C] performance_build_comparison.csv — LODCull row filled from v4: app_gpu_ms max 1562.79ms (one isolated warmup-like spike, same pattern seen on other builds' first real samples), avg 54.43ms, steady sustained range ~10-30ms otherwise. All 9 builds' rows are now complete.

[F] Milestone: all 9 rows in performance_build_comparison.csv are filled with clock-comparable (gpu_level=4, gpu_frequency_mhz=545 confirmed per-row where checked) data. Best result: test_perf_fullOpaque (20.3ms max app_gpu_ms, lowest of all builds, lowest max stale_frames too). Worst: test_perf_ProximityDepthRange (124.6ms max) and test_perf_LODCull (1562.79ms max, though that's a single spike not sustained). Highest compute-stage load: fullOpaque (58.3%) and fullOpacityCulloff (54.6%) — both opaque-family builds. Highest bandwidth pressure: Stencil1 (71.5% GPU Bus Busy, 51.7GB/s max Texture BW).

[>] Open thread carried over, still not investigated: perfetto capture calls appear to freeze/flatline kom.TechTests intermittently across almost every build this session (self-resolves on retry in most cases, took 4 attempts + a device restart specifically for LODCull). Root cause unknown — could be genuine capture-triggered overhead, could be coincidental app/build instability. Worth its own investigation activity if it recurs in future capture sessions.

[^] Continue performance. Last: all 9 builds' CSV rows complete in performance_build_comparison.csv (Packages/worldlabs_gaussian/.flow/), max-first/avg-second column layout, every row's bad-attempt history and anomalies logged in the Notes column. LODCull required 4 capture attempts + a device restart to get a clock-comparable clean trace (v4, gpu_level=4 and gpu_frequency_mhz=545 both confirmed flat). This log entry is a mid-session checkpoint, not a wrap-up — user is starting a new chat and will resume from here; context.md has NOT been updated yet (that only happens at wrap-up). Next: on resume, either wrap up properly (route this session's [F]/[D]/[>] entries into performance.context.md, confirm the open capture-freeze thread) or continue directly into analysis/next steps on the completed CSV (e.g. the user's Excel workflow, or investigating the flagged anomalies like ProximityDepthRange's bimodal stutter or the capture-freeze pattern). Confirm: none.

- user: rerun the whole stack (all 9 builds), this time each trace only 1 second (a snapshot), verify consistent Hz, focus columns on avg/lowest/highest instead of max/avg.

[D] New rerun writes to a separate CSV, not overwriting performance_build_comparison.csv — preserves the existing max/avg dataset for comparison against the new 1s-snapshot avg/lowest/highest dataset.

[D] "Consistent Hz" bar: gpu_level and gpu_frequency_mhz must be flat across every sample in the 1s capture window (same bar LODCull v4 needed last session) — any variance means retry the capture, don't accept the row.

[D] New CSV reuses the same locked 19-metric set from session 3 verbatim, just recomputing avg/lowest/highest per metric instead of max/avg — no metric trimming despite the shorter 1s window.

- user: will also record render docs side by side (manual, outside the CSV/capture workflow — noted, not gap-closed as part of this activity).

[D] New CSV location: Packages/worldlabs_gaussian/.flow/performance_build_comparison_1s.csv, same 9-build-row layout as the original.

[Activity] Rerun MQDH/hzos-dev-mcp Perfetto capture across all 9 comparison builds (test_perf_fullOpaque, fullOpacityCulloff, fullAlphaCulloff, RT0.5, Stencil1, ProximityDepthRange, NoSort, Full, LODCull), this time capturing a single 1-second snapshot trace per build instead of the longer captures used previously. For each build, verify gpu_level and gpu_frequency_mhz are flat/consistent across every sample in that 1s window before accepting the trace — retry the capture if Hz varies. Record results into a new CSV, Packages/worldlabs_gaussian/.flow/performance_build_comparison_1s.csv, using the same locked 19-metric set as before but with avg/lowest/highest columns per metric (replacing the previous max/avg layout). Complete when all 9 builds have a Hz-verified row in the new CSV.

[F] Recon: only com.kom.TechTests is installed on the Quest (device 2G0YC5ZH3H0090), installed 2026-07-02 14:16 — confirms session 3's pattern of one build installed at a time, not 9 parallel packages. User will install/launch each of the 9 builds manually; capture happens once they confirm a build is active.

[S] test_perf_fullOpaque — 1s snapshot capture, Hz-verify, write row to performance_build_comparison_1s.csv.

[F] gpu_frequency_mhz (the counter used for Hz-verification last session) only yields 1 sample per 1s capture — too sparse to "verify flat across all samples" as literally specified. Switched to the richer "GPU Frequency" counter (251 samples this capture) for Hz consistency checks going forward; confirmed flat at 545MHz for fullOpaque.

[C] test_perf_fullOpaque captured (perf1s_fullOpaque, 1000ms standard mode). GPU Frequency flat 545MHz/251 samples, gpu_level flat 4 — accepted. app_gpu_ms only has 4 samples in the 1s window (avg 11.09, lo 1.45, hi 18.12) — user accepted single-capture per activity scope rather than requiring a denser resample.

[C] performance_build_comparison_1s.csv created — header (avg/lowest/highest per metric, same 19-metric set) + fullOpaque row filled.

[/S]

- user: needs to redo builds for RenderDoc capture (the "record render docs side by side" from earlier) — will do that later, separately. Also flagged: fullOpaque doesn't actually render on Vulkan anyway.

[F] fullOpaque doesn't render on Vulkan — user-stated fact, not Claude-derived, but load-bearing: the fullOpaque row just captured (perf1s_fullOpaque) may not reflect real rendered output on-device if the Quest build is running Vulkan. Not yet resolved whether this invalidates the row or is a known/expected non-issue for this specific build's perf profile.

[>] Open thread: fullOpaque + Vulkan non-render issue — needs clarification before trusting/reusing that row. Not investigated this turn, user moved on.

[D] fullOpaque's Vulkan non-render issue does not trigger a re-capture — the perf1s_fullOpaque row stays in performance_build_comparison_1s.csv as-is.

[!] User: "One of your job is being my notebook that tracks my thoughts, so it's not just a heads up. Do your job." — corrective on log discipline: user-stated facts that carry implications for how existing data should be read/trusted need their actual downstream consequence spelled out, not summarized down to "keep the row, noted."

[>] Parked: fullOpaque + Vulkan non-render — the captured GPU counters (58% compute, 545MHz, etc.) reflect whatever Vulkan actually executes for this build's shader/pass config while failing to produce correct visual output, not a validated measurement of the intended opaque/early-Z path. Relevant if/when the Pass-1 promotion thread (performance.context.md, blocked since session 2 on the falloff-alpha bug) is revisited — this row should not be read as that path's real perf number without re-checking render correctness first. Not investigated further, not acted on now.

[>] Parked: RenderDoc rebuild + capture — user will redo builds for side-by-side RenderDoc capture later, separate from this 1s-snapshot Perfetto rerun. No action this session.

- user: cancel the 1s-snapshot rerun, park it — pivoting to a new feature.

[>] Parked: 1s-snapshot rerun Activity (started this turn-block, only 1 of 9 builds done — fullOpaque). performance_build_comparison_1s.csv exists with just that one row. Resume point: install/launch test_perf_fullOpacityCulloff next, repeat through fullAlphaCulloff, RT0.5, Stencil1, ProximityDepthRange, NoSort, Full, LODCull. Hz-verification uses the dense "GPU Frequency" counter (251 samples/capture), not the sparse gpu_frequency_mhz (1 sample/capture) — carry this forward on resume. Two sub-caveats travel with it: fullOpaque/Vulkan render-correctness (row kept, but not a validated opaque/early-Z measurement) and the deferred RenderDoc rebuild (unrelated, user's own separate task).

[/Activity]

[^] Suspended — performance (1s-snapshot rerun activity) — user pivoting to a new feature/target. Next: ask what the new feature/target is; new session or resuming existing.
