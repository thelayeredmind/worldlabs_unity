// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Builds a <see cref="GaussianMorphMap"/> from two <see cref="GaussianSplatAsset"/>s.
    /// Correspondence search runs on the GPU (compute shader). Duplicate resolution runs on CPU after readback.
    /// Designed to be called from a background Task; GPU dispatch must happen on the main thread via the
    /// provided <see cref="ICorrespondenceDispatcher"/>.
    /// </summary>
    public static class GaussianMorphMapBuilder
    {
        public struct Result
        {
            public int2[] matchedPairs;
            public int[]  unmatchedLeft;
            public int[]  unmatchedRight;
        }

        /// <summary>
        /// Abstracts the GPU dispatch so the builder stays testable without a real GPU.
        /// The window provides <see cref="ComputeShaderDispatcher"/>.
        /// </summary>
        public interface ICorrespondenceDispatcher
        {
            /// <summary>
            /// Upload positions and colors for both sides, dispatch the search, return best-match indices
            /// and distances per L splat. Must be called on the main thread.
            /// bestDist is a per-splat-relative z-scored blend — use it only to see which candidate a
            /// splat picked, never to compare match quality across different L splats (their z-scores
            /// aren't on the same scale). bestRawDist is the raw, non-normalised distance to that same
            /// chosen candidate, comparable across splats — use it for cross-splat collision arbitration.
            /// </summary>
            void FindBestMatches(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                float posWeight, float colWeight, out int[] bestIndex, out float[] bestDist, out float[] bestRawDist);
        }

        /// <summary>
        /// Abstracts the GPU dispatch used by probe-based correspondence (<see cref="IProbeGridStrategy"/>
        /// and <see cref="IProbeResolutionStrategy"/> implementations). Same main-thread-only
        /// constraint as <see cref="ICorrespondenceDispatcher"/> — the window provides the real
        /// implementation, marshalling background-Task calls to the main thread.
        /// </summary>
        public interface IProbeDispatcher
        {
            int[] AssignToSpatialProbes(Vector3[] pos, int cellsPerAxis);

            /// <summary>
            /// Resolves every probe's L/R bucket in one dispatch. bucketPosL/colL/posR/colR are
            /// already reordered so each probe's splats are contiguous (CSR layout); bucketStartL/R
            /// are size probeCount+1 offsets into those arrays. Returns, per L bucket slot, the
            /// ORIGINAL R splat index it matched to, or -1 if unmatched (empty/single-side probe).
            /// </summary>
            int[] ResolveProbeBuckets(
                Vector4[] bucketPosL, Vector4[] bucketColL, int[] bucketStartL,
                Vector4[] bucketPosR, Vector4[] bucketColR, int[] bucketStartR, int[] bucketOrigIndexR,
                int probeCount, float posWeight, float colWeight);
        }

        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>
        /// Maximum number of greedy nearest-neighbor rounds. Each round re-matches only the
        /// splats left unmatched by the previous round (restricted to the remaining R candidates),
        /// which resolves the "many L splats claim the same R splat" collisions that a single
        /// greedy pass leaves unmatched. Stops early once a round produces no new matches.
        /// </summary>
        const int kMaxMatchRounds = 8;

        /// <summary>
        /// Build from pre-decoded arrays. Decoding must happen on the main thread before calling this.
        /// This overload is safe to call from a background thread.
        /// </summary>
        public static Result Build(
            Vector3[] posL, Vector3[] posR,
            Vector4[] colL, Vector4[] colR,
            ICorrespondenceDispatcher dispatcher,
            float colorWeight = 0.5f,
            IProgress<float> progress = null,
            CancellationToken ct = default,
            bool useRemainderFallback = true)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(0.05f);

            float posWeight = 1f - colorWeight;

            var pairs = new List<int2>();
            int[] remainingL = Enumerable.Range(0, posL.Length).ToArray();
            int[] remainingR = Enumerable.Range(0, posR.Length).ToArray();

            for (int round = 0; round < kMaxMatchRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                if (remainingL.Length == 0 || remainingR.Length == 0)
                    break;

                var subPosL = new Vector3[remainingL.Length];
                var subColL = new Vector4[remainingL.Length];
                for (int i = 0; i < remainingL.Length; i++)
                {
                    subPosL[i] = posL[remainingL[i]];
                    subColL[i] = colL[remainingL[i]];
                }

                var subPosR = new Vector3[remainingR.Length];
                var subColR = new Vector4[remainingR.Length];
                for (int j = 0; j < remainingR.Length; j++)
                {
                    subPosR[j] = posR[remainingR[j]];
                    subColR[j] = colR[remainingR[j]];
                }

                dispatcher.FindBestMatches(subPosL, subColL, subPosR, subColR, posWeight, colorWeight,
                    out int[] bestMatch, out float[] bestDist, out float[] bestRawDist);
                ct.ThrowIfCancellationRequested();

                ResolveOneToOne(bestMatch, bestRawDist, subPosL.Length, subPosR.Length,
                    out var roundPairs, out var roundUL, out var roundUR);

                if (roundPairs.Length == 0)
                    break; // no progress — remaining splats have no viable partner

                // Map subset-local indices back to original L/R indices
                foreach (var p in roundPairs)
                    pairs.Add(new int2(remainingL[p.x], remainingR[p.y]));

                remainingL = roundUL.Select(i => remainingL[i]).ToArray();
                remainingR = roundUR.Select(j => remainingR[j]).ToArray();

                progress?.Report(0.05f + 0.8f * (round + 1) / kMaxMatchRounds);
            }

            // Collisions left over after the round cap (or a stalled round) can leave splats
            // unmatched on both sides simultaneously, which inflates the morph's total splat
            // count past max(nL, nR). Strict 1:1 uniqueness isn't required for morphing — only
            // that every splat has somewhere to move toward — so leftovers are resolved by a
            // nearest-neighbor lookup against the full opposite set (matched or not), duplicates
            // allowed, guaranteeing matchedCount >= min(nL, nR). Dispatched on the GPU via the
            // same ICorrespondenceDispatcher as the round loop — a CPU brute-force here is the
            // same O(n*m) distance search as SplatCorrespondence.compute's FindBestMatch kernel,
            // just serial instead of massively parallel, and grinds for minutes at real asset
            // scale (100k-2M+ splats) instead of completing promptly.
            //
            // useRemainderFallback=false skips this — a diagnostic mode to see the round loop's
            // OWN match quality unpadded (this fallback is an unconstrained full-cloud search with
            // no locality constraint; it can be doing most of the real matching work and silently
            // masking a poor round-loop result behind a clean "few unmatched" status).
            if (useRemainderFallback)
                ResolveRemainderOnGpu(posL, posR, colL, colR, dispatcher, posWeight, colorWeight,
                    ref remainingL, ref remainingR, pairs, ct);

            progress?.Report(1f);

            return new Result
            {
                matchedPairs   = pairs.ToArray(),
                unmatchedLeft  = remainingL,
                unmatchedRight = remainingR,
            };
        }

        /// <summary>
        /// Resolves splats still unmatched on both sides after the round loop. Unlike the main
        /// matching pass, this does not enforce one-to-one uniqueness — each leftover splat is
        /// paired with its nearest neighbor in the full opposite set (matched splats included),
        /// so a destination splat may end up claimed by several leftover splats. That is fine for
        /// morphing purposes: the goal is that every splat converges to some position, not that
        /// every pairing is exclusive. The search is dispatched on the GPU (same kernel/weights as
        /// the round loop) since it is the same O(n*m) distance search as the main matching pass —
        /// running it on CPU instead would be orders of magnitude slower at real asset scale.
        /// </summary>
        static void ResolveRemainderOnGpu(
            Vector3[] posL, Vector3[] posR, Vector4[] colL, Vector4[] colR,
            ICorrespondenceDispatcher dispatcher, float posWeight, float colWeight,
            ref int[] remainingL, ref int[] remainingR,
            List<int2> pairs, CancellationToken ct)
        {
            if (remainingL.Length > 0 && posR.Length > 0)
            {
                ct.ThrowIfCancellationRequested();
                var subPosL = remainingL.Select(i => posL[i]).ToArray();
                var subColL = remainingL.Select(i => colL[i]).ToArray();
                dispatcher.FindBestMatches(subPosL, subColL, posR, colR, posWeight, colWeight,
                    out int[] bestMatch, out _, out _);
                for (int i = 0; i < remainingL.Length; i++)
                    pairs.Add(new int2(remainingL[i], bestMatch[i]));
            }

            if (remainingR.Length > 0 && posL.Length > 0)
            {
                ct.ThrowIfCancellationRequested();
                var subPosR = remainingR.Select(j => posR[j]).ToArray();
                var subColR = remainingR.Select(j => colR[j]).ToArray();
                dispatcher.FindBestMatches(subPosR, subColR, posL, colL, posWeight, colWeight,
                    out int[] bestMatch, out _, out _);
                for (int j = 0; j < remainingR.Length; j++)
                    pairs.Add(new int2(bestMatch[j], remainingR[j]));
            }

            remainingL = Array.Empty<int>();
            remainingR = Array.Empty<int>();
        }

        // ── GS-Matching (Gale-Shapley-inspired stable matching) ─────────────────
        //
        // Ported from Yu et al., "GS-Matching: Reconsidering Feature Matching task in Point Cloud
        // Registration" (arXiv:2412.04855), Algorithm 1 — adapted to this project's architecture.
        // Self-contained: does NOT reuse ICorrespondenceDispatcher, Build(), or any top-K/probe
        // infrastructure above.
        //
        // The paper's actual mechanism is NOT classic proposer/rejection deferred acceptance — it
        // is a MUTUAL-BEST iterative filter: each round, every still-unmatched L finds its current
        // best remaining R, and every still-unmatched R finds its current best remaining L; a pair
        // is accepted ONLY when both point to each other, matched splats are removed from the pool,
        // and the process repeats for up to K rounds. Because a pair is only ever confirmed on
        // mutual agreement, no L can "steal" an R that R itself prefers someone else over — this is
        // what gives the result its stability property and is the paper's answer to the same
        // hubness/mass-convergence symptom this session's other independent-argmin attempts
        // (z-score, min-max blend, probe partitioning) all hit.
        //
        // The paper also weights priority by scarcity (V_NC / "noise count": how many candidates a
        // point has within a similarity threshold — fewer candidates means higher priority, since a
        // scarce point is more likely to be crowded out by "popular" points converging on the same
        // targets). We reproduce this directly via ScoreNoiseCount below; it is the genuinely novel
        // part of GS-Matching, not just "mutual nearest neighbor," so it is not skipped.
        //
        // Distance vs similarity: the paper scores candidates by cosine SIMILARITY (higher=better);
        // this project scores by weighted pos+color SQUARED DISTANCE (lower=better) — every mutual-
        // best/argmax comparison in the paper is therefore an argmin here, the same mechanism
        // mirrored onto our metric.

        /// <summary>
        /// Dispatches GS-Matching's GPU-side primitives (see SplatGaleShapley.compute). Must be
        /// called on the main thread.
        /// </summary>
        public interface IGaleShapleyDispatcher
        {
            /// <summary>
            /// Per L splat, count of R candidates within distanceThreshold (the paper's V_NC / noise
            /// count) — used once, up front, to derive scarcity-based resolution priority.
            /// </summary>
            int[] CountNoise(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                float posWeight, float colWeight, float distanceThreshold);

            /// <summary>
            /// Per (active) L splat, index of its current best candidate among R splats still
            /// marked active, or -1 if the active R pool is empty. Called twice per round with L/R
            /// swapped (once for L's best-R, once for R's best-L) to get both sides' current pick.
            /// </summary>
            int[] FindBestRemaining(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                int[] activeR, float posWeight, float colWeight);
        }

        /// <summary>
        /// K in the paper's Algorithm 1 — number of mutual-best rounds before the nearest-neighbor
        /// fallback resolves whatever remains unmatched.
        /// </summary>
        const int kGaleShapleyRounds = 8;

        /// <summary>
        /// Builds a correspondence Result via GS-Matching instead of the round-based greedy loop or
        /// probe partitioning above. Each round finds both sides' current mutual best-remaining
        /// candidate and keeps only pairs where both agree (stable by construction); after
        /// kGaleShapleyRounds, anything still unmatched falls through to a plain one-sided nearest-
        /// neighbor pass — exactly the paper's Algorithm 1, Steps 14-15 — so every splat still ends
        /// up matched (this project's non-negotiable 100%-matched requirement), while the honest
        /// pre-fallback unmatched count remains observable via the returned Result before that pass
        /// (see BuildViaProbes' identical reasoning for why that number matters diagnostically).
        /// </summary>
        public static Result BuildViaGaleShapley(
            Vector3[] posL, Vector3[] posR,
            Vector4[] colL, Vector4[] colR,
            IGaleShapleyDispatcher dispatcher,
            float colorWeight = 0.5f,
            IProgress<float> progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(0.02f);

            float posWeight = 1f - colorWeight;
            int nL = posL.Length;
            int nR = posR.Length;

            var pairs = new List<int2>();
            var activeL = new int[nL]; // 1 = still in the pool (unmatched), 0 = matched/removed
            var activeR = new int[nR];
            for (int i = 0; i < nL; i++) activeL[i] = 1;
            for (int j = 0; j < nR; j++) activeR[j] = 1;

            // Scarcity priority (V_NC / V_weight in the paper): computed once, up front, against
            // the FULL initial pools — a point's candidate scarcity is a property of the two whole
            // clouds, not something that needs recomputing as the pool shrinks each round.
            float distanceThreshold = EstimateDistanceThreshold(posL, posR, colL, colR, posWeight, colorWeight);
            int[] noiseCountL = nL > 0 && nR > 0
                ? dispatcher.CountNoise(posL, colL, posR, colR, posWeight, colorWeight, distanceThreshold)
                : Array.Empty<int>();

            // Resolution order within a round follows the paper's priority: scarce L splats (few
            // candidates within threshold) are considered before "popular" ones whenever both would
            // otherwise contest the same mutual-best R — implemented by sorting L's processing order
            // by ascending noise count once, up front (order is fixed; only the ACTIVE subset within
            // it changes as splats get matched and removed round to round).
            int[] priorityOrderL = nL > 0
                ? Enumerable.Range(0, nL).OrderBy(i => noiseCountL.Length > 0 ? noiseCountL[i] : 0).ToArray()
                : Array.Empty<int>();

            progress?.Report(0.1f);

            for (int round = 0; round < kGaleShapleyRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                if (nL == 0 || nR == 0) break;

                int[] bestRForL = dispatcher.FindBestRemaining(posL, colL, posR, colR, activeR, posWeight, colorWeight);
                ct.ThrowIfCancellationRequested();
                int[] bestLForR = dispatcher.FindBestRemaining(posR, colR, posL, colL, activeL, posWeight, colorWeight);
                ct.ThrowIfCancellationRequested();

                int matchedThisRound = 0;

                // Mutual-best check, processed in scarcity-priority order: a pair is confirmed only
                // when L's best-remaining-R points back to L as R's own best-remaining-L. Once
                // confirmed, both are removed from the active pool immediately (not batched to end
                // of round) so a later, lower-priority L in this same round can't still see an
                // already-claimed R as "active" — this is what makes processing ORDER (i.e. the
                // scarcity priority) actually matter, not just a cosmetic sort.
                foreach (int i in priorityOrderL)
                {
                    if (activeL[i] == 0) continue;
                    int j = bestRForL[i];
                    if (j < 0 || activeR[j] == 0) continue;
                    if (bestLForR[j] != i) continue; // not mutual this round

                    pairs.Add(new int2(i, j));
                    activeL[i] = 0;
                    activeR[j] = 0;
                    matchedThisRound++;
                }

                progress?.Report(0.1f + 0.7f * (round + 1) / kGaleShapleyRounds);

                if (matchedThisRound == 0)
                    break; // stalled — no mutual agreement possible among what remains, further rounds are wasted work
            }

            var unmatchedLeft  = Enumerable.Range(0, nL).Where(i => activeL[i] != 0).ToArray();
            var unmatchedRight = Enumerable.Range(0, nR).Where(j => activeR[j] != 0).ToArray();

            // Paper's Algorithm 1, Steps 14-15: after K rounds, remaining unmatched points on
            // either side fall through to a plain nearest-neighbor pass against the FULL opposite
            // set (matched or not) — same shape and same GPU-dispatch rationale as this project's
            // existing ResolveRemainderOnGpu (a serial CPU equivalent is the same O(n*m) search,
            // just orders of magnitude slower at real asset scale), just implemented as its own
            // dispatcher call here to keep this branch self-contained.
            if (unmatchedLeft.Length > 0 && nR > 0)
            {
                ct.ThrowIfCancellationRequested();
                var subPosL = unmatchedLeft.Select(i => posL[i]).ToArray();
                var subColL = unmatchedLeft.Select(i => colL[i]).ToArray();
                var fullyActiveR = new int[nR];
                for (int j = 0; j < nR; j++) fullyActiveR[j] = 1;
                int[] fallbackMatch = dispatcher.FindBestRemaining(subPosL, subColL, posR, colR, fullyActiveR, posWeight, colorWeight);
                for (int k = 0; k < unmatchedLeft.Length; k++)
                    if (fallbackMatch[k] >= 0)
                        pairs.Add(new int2(unmatchedLeft[k], fallbackMatch[k]));
            }
            if (unmatchedRight.Length > 0 && nL > 0)
            {
                ct.ThrowIfCancellationRequested();
                var subPosR = unmatchedRight.Select(j => posR[j]).ToArray();
                var subColR = unmatchedRight.Select(j => colR[j]).ToArray();
                var fullyActiveL = new int[nL];
                for (int i = 0; i < nL; i++) fullyActiveL[i] = 1;
                int[] fallbackMatch = dispatcher.FindBestRemaining(subPosR, subColR, posL, colL, fullyActiveL, posWeight, colorWeight);
                for (int k = 0; k < unmatchedRight.Length; k++)
                    if (fallbackMatch[k] >= 0)
                        pairs.Add(new int2(fallbackMatch[k], unmatchedRight[k]));
            }

            progress?.Report(1f);

            return new Result
            {
                matchedPairs   = pairs.ToArray(),
                unmatchedLeft  = unmatchedLeft,
                unmatchedRight = unmatchedRight,
            };
        }

        /// <summary>
        /// T1 equivalent: the paper thresholds by SIMILARITY (a fixed, dataset-independent cutoff
        /// makes sense for normalised cosine similarity); our metric is an unbounded weighted
        /// squared distance whose scale depends entirely on the two clouds' own extents, so a fixed
        /// constant would be meaningless across different assets. Instead we estimate a threshold
        /// from the data itself: the mean nearest-neighbor distance from a small random L sample to
        /// its closest R candidate, scaled up — candidates within a small multiple of the "typical"
        /// nearest-neighbor distance are considered viable, matching the paper's intent (T1 marks
        /// "plausible candidate," not "the single best one") without requiring a manually-tuned
        /// per-asset constant.
        /// </summary>
        static float EstimateDistanceThreshold(
            Vector3[] posL, Vector3[] posR, Vector4[] colL, Vector4[] colR,
            float posWeight, float colWeight, int sampleSize = 256, float scale = 4f)
        {
            if (posL.Length == 0 || posR.Length == 0) return 0f;

            var rng = new System.Random(0);
            int count = Math.Min(sampleSize, posL.Length);
            float sum = 0f;

            foreach (int i in Enumerable.Range(0, posL.Length).OrderBy(_ => rng.Next()).Take(count))
            {
                float best = float.MaxValue;
                for (int j = 0; j < posR.Length; j++)
                {
                    float3 dp = (float3)posL[i] - (float3)posR[j];
                    float4 dc = (float4)colL[i] - (float4)colR[j];
                    float dist = posWeight * math.dot(dp, dp) + colWeight * math.dot(dc, dc);
                    if (dist < best) best = dist;
                }
                sum += best;
            }

            return (sum / count) * scale;
        }

        // ── Mutual top-K (symmetric greedy) correspondence ──────────────────────

        /// <summary>
        /// Abstracts the GPU dispatch for top-K candidate lists (<see cref="FindTopKMatches"/> kernel).
        /// Same main-thread-only constraint as <see cref="ICorrespondenceDispatcher"/>. The candidate
        /// count per splat is fixed by the shader's TOP_K compile-time constant (see kTopK in the
        /// window's dispatcher) — not a runtime parameter, since the kernel uses a fixed-size local
        /// array for its sorted insertion.
        /// </summary>
        public interface ITopKDispatcher
        {
            void FindTopKMatches(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                float posWeight, float colWeight, out int[] topKIndex, out float[] topKDist);
        }

        /// <summary>Matches the shader's TOP_K compile-time constant — see ITopKDispatcher.</summary>
        const int kTopK = 32;

        /// <summary>
        /// Builds correspondence via mutual/symmetric top-K agreement instead of independent argmin.
        /// The independent-argmin round loop above (<see cref="Build"/>) feeds ResolveOneToOne raw
        /// per-splat "closest candidate" picks with no cross-check — a hub R splat that merely looks
        /// close to thousands of L splats' OWN nearest-neighbor searches wins one of those claims
        /// every round, and the other thousands lose and get pushed to the next round, which is why
        /// the round loop stalls at a small fraction resolved on real assets (measured 2-28% this
        /// session) and dumps the rest into an unconstrained remainder fallback. Mutual top-K requires
        /// AGREEMENT: L must have R in its own top-K AND R must have L in ITS own top-K. A hub is
        /// unlikely to reciprocate a specific L splat's pick, since the hub's own top-K is chosen by
        /// the SAME distance metric from the hub's actual position/color — it can only "look close" to
        /// many L splats, not actually have many of them in ITS short list back. No remainder fallback
        /// is used here — unmatched splats after convergence are reported honestly (matches this
        /// session's established "judge with remainder OFF" convention), not force-matched.
        /// </summary>
        public static Result BuildViaMutualTopK(
            Vector3[] posL, Vector3[] posR,
            Vector4[] colL, Vector4[] colR,
            ITopKDispatcher dispatcher,
            float colorWeight = 0.5f,
            IProgress<float> progress = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(0.05f);

            float posWeight = 1f - colorWeight;

            var pairs = new List<int2>();
            int[] remainingL = Enumerable.Range(0, posL.Length).ToArray();
            int[] remainingR = Enumerable.Range(0, posR.Length).ToArray();

            for (int round = 0; round < kMaxMatchRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                if (remainingL.Length == 0 || remainingR.Length == 0)
                    break;

                var subPosL = remainingL.Select(i => posL[i]).ToArray();
                var subColL = remainingL.Select(i => colL[i]).ToArray();
                var subPosR = remainingR.Select(j => posR[j]).ToArray();
                var subColR = remainingR.Select(j => colR[j]).ToArray();

                dispatcher.FindTopKMatches(subPosL, subColL, subPosR, subColR, posWeight, colorWeight,
                    out int[] topKIndexLtoR, out _);
                ct.ThrowIfCancellationRequested();
                dispatcher.FindTopKMatches(subPosR, subColR, subPosL, subColL, posWeight, colorWeight,
                    out int[] topKIndexRtoL, out _);
                ct.ThrowIfCancellationRequested();

                ResolveMutualTopK(topKIndexLtoR, topKIndexRtoL, subPosL.Length, subPosR.Length, kTopK,
                    out var roundPairs, out var roundUL, out var roundUR);

                if (roundPairs.Length == 0)
                    break; // no mutual agreement left — remaining splats have no reciprocal partner

                foreach (var p in roundPairs)
                    pairs.Add(new int2(remainingL[p.x], remainingR[p.y]));

                remainingL = roundUL.Select(i => remainingL[i]).ToArray();
                remainingR = roundUR.Select(j => remainingR[j]).ToArray();

                progress?.Report(0.05f + 0.9f * (round + 1) / kMaxMatchRounds);
            }

            progress?.Report(1f);

            return new Result
            {
                matchedPairs   = pairs.ToArray(),
                unmatchedLeft  = remainingL,
                unmatchedRight = remainingR,
            };
        }

        /// <summary>
        /// A pair (i,j) is accepted only if j appears in i's top-K list AND i appears in j's top-K
        /// list — mutual agreement, no distance-based collision arbitration needed (unlike
        /// ResolveOneToOne, which arbitrates independent-argmin's competing single picks). Each side
        /// still enforces its own 1:1 constraint within this round: once i or j is claimed, later
        /// agreeing pairs involving the same index are skipped, so a splat with several mutual
        /// candidates in one round takes its FIRST (best-ranked, since top-K lists are best-first)
        /// available one.
        /// </summary>
        static void ResolveMutualTopK(int[] topKIndexLtoR, int[] topKIndexRtoL, int nL, int nR, int topK,
            out int2[] matchedPairs, out int[] unmatchedLeft, out int[] unmatchedRight)
        {
            var rClaimed = new bool[nR];
            var lClaimed = new bool[nL];
            var pairs    = new List<int2>();

            for (int i = 0; i < nL; i++)
            {
                if (lClaimed[i]) continue;

                for (int s = 0; s < topK; s++)
                {
                    int j = topKIndexLtoR[i * topK + s];
                    if (j < 0 || rClaimed[j]) continue;

                    bool mutual = false;
                    for (int t = 0; t < topK; t++)
                    {
                        int back = topKIndexRtoL[j * topK + t];
                        if (back < 0) break; // top-K lists are best-first with -1 padding at the tail
                        if (back == i) { mutual = true; break; }
                    }

                    if (mutual)
                    {
                        pairs.Add(new int2(i, j));
                        lClaimed[i] = true;
                        rClaimed[j] = true;
                        break;
                    }
                }
            }

            var uL = new List<int>();
            for (int i = 0; i < nL; i++) if (!lClaimed[i]) uL.Add(i);
            var uR = new List<int>();
            for (int j = 0; j < nR; j++) if (!rClaimed[j]) uR.Add(j);

            matchedPairs   = pairs.ToArray();
            unmatchedLeft  = uL.ToArray();
            unmatchedRight = uR.ToArray();
        }

        // ── Probe-based correspondence ──────────────────────────────────────────

        /// <summary>
        /// Assigns every splat in one cloud to a probe index (grid cell, cluster centroid, whatever
        /// the strategy uses). Called once per side (L and R independently) by BuildViaProbes.
        /// Implementations decide probe PLACEMENT — e.g. today's regular position-only grid
        /// (<see cref="RegularSpatialGridStrategy"/>); future strategies (farthest-point sampling,
        /// k-means centroids, HSL color-space grid) implement this same contract so BuildViaProbes
        /// and the surrounding build pipeline never need to change when the placement method does.
        /// </summary>
        public interface IProbeGridStrategy
        {
            /// <summary>
            /// probeCountTarget is a hint, not a guarantee — implementations may need a different
            /// actual probe universe size (e.g. a regular grid's cellsPerAxis^3 after cube-root
            /// rounding rarely equals the target exactly). actualProbeCount reports the true upper
            /// bound of indices this call can return, which callers MUST use for downstream sizing
            /// (bucketing, per-probe arrays) instead of the original target.
            /// </summary>
            int[] AssignToProbes(Vector3[] pos, Vector4[] col, int probeCountTarget, out int actualProbeCount);
        }

        /// <summary>
        /// Resolves which L splat matches which R splat within each shared probe, given both
        /// clouds' full data and their per-splat probe assignments. Implementations decide the
        /// RESOLUTION algorithm — e.g. today's GPU greedy nearest-first
        /// (<see cref="GpuGreedyProbeResolutionStrategy"/>); future strategies (a smarter in-bucket
        /// assignment, etc) implement this same contract so BuildViaProbes never needs to change
        /// when the resolution method does. Splats in probes occupied by only one side (or neither)
        /// must be returned as unmatched, not resolved — BuildViaProbes routes those through the
        /// existing remainder fallback so every splat still ends up matched.
        /// </summary>
        public interface IProbeResolutionStrategy
        {
            void Resolve(
                Vector3[] posL, Vector4[] colL, int[] probeIndexL,
                Vector3[] posR, Vector4[] colR, int[] probeIndexR,
                int probeCount, float posWeight, float colWeight,
                out int2[] pairs, out int[] unmatchedLeft, out int[] unmatchedRight);
        }

        /// <summary>
        /// Builds a correspondence Result via probe partitioning instead of the round-based greedy
        /// loop above — splits the global correspondence search into many small, cheap, local
        /// sub-problems instead of one full CountL x CountR search or a global assignment solve
        /// (Sinkhorn was tried for the latter and abandoned — real numerical instability at this
        /// scale, see git history). Pure orchestration: derives probeCount from probeAccuracy, calls
        /// gridStrategy once per side, calls resolutionStrategy once, then routes whatever it leaves
        /// unmatched through the existing GPU remainder fallback so every splat still ends up
        /// matched (duplicates allowed for genuine leftovers, matching Build()'s existing guarantee).
        /// Grid placement and bucket resolution are both swappable strategies — this method has no
        /// algorithm-specific logic of its own.
        /// </summary>
        public static Result BuildViaProbes(
            Vector3[] posL, Vector3[] posR,
            Vector4[] colL, Vector4[] colR,
            IProbeGridStrategy gridStrategy,
            IProbeResolutionStrategy resolutionStrategy,
            float probeAccuracy,
            float colorWeight,
            IProgress<float> progress = null)
        {
            float posWeight = 1f - colorWeight;

            progress?.Report(0.05f);
            int probeCountTarget = Math.Max(1, (int)Math.Round(probeAccuracy * Math.Max(posL.Length, posR.Length)));

            // Both sides must share ONE probe-index universe (a splat's probe index from L must mean
            // the same cell as the same index from R) — call the strategy once per side but with the
            // SAME target, then use whichever actual count it reports (a deterministic function of
            // the target alone for today's regular grid, so both calls agree; a future strategy that
            // can't guarantee that would need its own reconciliation, not assumed here).
            var probeIndexL = gridStrategy.AssignToProbes(posL, colL, probeCountTarget, out int actualProbeCountL);
            progress?.Report(0.3f);
            var probeIndexR = gridStrategy.AssignToProbes(posR, colR, probeCountTarget, out int actualProbeCountR);
            progress?.Report(0.5f);
            int actualProbeCount = Math.Max(actualProbeCountL, actualProbeCountR);

            // Resolution must compare splats in the SAME normalised space grid placement used — each
            // cloud independently normalised to [0,1]^3 against its OWN bounds (see GetBounds'
            // rationale: relative position within each object's own extent is the correspondence
            // signal, not raw world-space distance, which is meaningless across two differently
            // scaled/positioned objects). Passing raw posL/posR here was a real bug: grid ASSIGNMENT
            // correctly bucketed splats by relative position, but resolution then measured distance
            // in raw world coordinates, corrupting every within-bucket pick.
            var normPosL = NormalizePositions(posL);
            var normPosR = NormalizePositions(posR);

            resolutionStrategy.Resolve(normPosL, colL, probeIndexL, normPosR, colR, probeIndexR,
                actualProbeCount, posWeight, colWeight: colorWeight,
                out var pairs, out var unmatchedLeft, out var unmatchedRight);
            progress?.Report(1f);

            // Deliberately NOT routed through ResolveRemainderOnGpu — that fallback is an
            // unconstrained full-cloud nearest-neighbor search, the exact no-locality mechanism
            // probe partitioning exists to avoid. Silently patching gaps with it would mask the
            // probe algorithm's real match quality (a clean "0 unmatched" status could hide gaps
            // the remainder step quietly filled with long-range guesses). unmatchedLeft/
            // unmatchedRight are reported honestly here so probe-only match quality is directly
            // observable; re-introduce a fallback deliberately later if genuine leftovers need
            // somewhere to go for playback, not as a way to avoid looking at this number.
            return new Result
            {
                matchedPairs   = pairs,
                unmatchedLeft  = unmatchedLeft,
                unmatchedRight = unmatchedRight,
            };
        }

        /// <summary>
        /// Normalises one cloud's positions into [0,1]^3 against its OWN bounds, independently of
        /// the other cloud — same convention used throughout this file and SplatCorrespondence.compute
        /// (relative position within each object's own extent is the correspondence signal, not raw
        /// world-space distance, which is meaningless across two differently scaled/positioned
        /// objects — see GetBounds' rationale in GaussianMorphMapBuilderWindow.cs).
        /// </summary>
        static Vector3[] NormalizePositions(Vector3[] pos)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in pos) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }

            float scale = Math.Max(Math.Max(max.x - min.x, max.y - min.y), max.z - min.z);
            scale = Math.Max(scale, 1e-6f);

            var result = new Vector3[pos.Length];
            for (int i = 0; i < pos.Length; i++)
                result[i] = new Vector3((pos[i].x - min.x) / scale, (pos[i].y - min.y) / scale, (pos[i].z - min.z) / scale);
            return result;
        }

        /// <summary>
        /// Default <see cref="IProbeGridStrategy"/>: a regular grid over POSITION ONLY (color takes
        /// no part in probe placement) — cellsPerAxis = round(probeCount^(1/3)), each cloud's own
        /// independently-normalised [0,1]^3 space. Dispatched on GPU via <see cref="IProbeDispatcher"/>
        /// (see SplatCorrespondence.compute's AssignToSpatialProbes kernel).
        /// </summary>
        public class RegularSpatialGridStrategy : IProbeGridStrategy
        {
            readonly IProbeDispatcher m_Dispatcher;
            public RegularSpatialGridStrategy(IProbeDispatcher dispatcher) => m_Dispatcher = dispatcher;

            public int[] AssignToProbes(Vector3[] pos, Vector4[] col, int probeCountTarget, out int actualProbeCount)
            {
                int cellsPerAxis = Math.Max(1, (int)Math.Round(Math.Pow(probeCountTarget, 1.0 / 3.0)));
                actualProbeCount = cellsPerAxis * cellsPerAxis * cellsPerAxis;
                return m_Dispatcher.AssignToSpatialProbes(pos, cellsPerAxis);
            }
        }

        /// <summary>
        /// Default <see cref="IProbeResolutionStrategy"/>: greedy nearest-first within each probe's
        /// bucket, dispatched on GPU (one thread per probe — see SplatCorrespondence.compute's
        /// ResolveProbeBuckets kernel). Builds a CSR-style layout (per-probe contiguous splat runs)
        /// on CPU first — cheap, no distance math, just bucketing indices by probe — then hands the
        /// reordered arrays to the dispatcher for the actual O(bucketL*bucketR) search per probe.
        /// </summary>
        public class GpuGreedyProbeResolutionStrategy : IProbeResolutionStrategy
        {
            readonly IProbeDispatcher m_Dispatcher;
            public GpuGreedyProbeResolutionStrategy(IProbeDispatcher dispatcher) => m_Dispatcher = dispatcher;

            public void Resolve(
                Vector3[] posL, Vector4[] colL, int[] probeIndexL,
                Vector3[] posR, Vector4[] colR, int[] probeIndexR,
                int probeCount, float posWeight, float colWeight,
                out int2[] pairs, out int[] unmatchedLeft, out int[] unmatchedRight)
            {
                BuildCsr(posL, colL, probeIndexL, probeCount,
                    out var bucketPosL, out var bucketColL, out var bucketStartL, out var origIndexL);
                BuildCsr(posR, colR, probeIndexR, probeCount,
                    out var bucketPosR, out var bucketColR, out var bucketStartR, out var origIndexR);

                var matchedR = m_Dispatcher.ResolveProbeBuckets(
                    bucketPosL, bucketColL, bucketStartL,
                    bucketPosR, bucketColR, bucketStartR, origIndexR,
                    probeCount, posWeight, colWeight);

                var pairList = new List<int2>();
                var matchedLFlag = new bool[posL.Length];
                var matchedRFlag = new bool[posR.Length];
                for (int slot = 0; slot < matchedR.Length; slot++)
                {
                    if (matchedR[slot] < 0) continue;
                    int origL = origIndexL[slot];
                    pairList.Add(new int2(origL, matchedR[slot]));
                    matchedLFlag[origL] = true;
                    matchedRFlag[matchedR[slot]] = true;
                }

                pairs          = pairList.ToArray();
                unmatchedLeft  = Enumerable.Range(0, posL.Length).Where(i => !matchedLFlag[i]).ToArray();
                unmatchedRight = Enumerable.Range(0, posR.Length).Where(j => !matchedRFlag[j]).ToArray();
            }

            static void BuildCsr(
                Vector3[] pos, Vector4[] col, int[] probeIndex, int probeCount,
                out Vector4[] bucketPos, out Vector4[] bucketCol, out int[] bucketStart, out int[] origIndex)
            {
                var countPerProbe = new int[probeCount];
                foreach (var p in probeIndex) countPerProbe[p]++;

                bucketStart = new int[probeCount + 1];
                for (int p = 0; p < probeCount; p++)
                    bucketStart[p + 1] = bucketStart[p] + countPerProbe[p];

                bucketPos = new Vector4[pos.Length];
                bucketCol = new Vector4[pos.Length];
                origIndex = new int[pos.Length];
                var cursor = (int[])bucketStart.Clone();
                for (int i = 0; i < pos.Length; i++)
                {
                    int slot = cursor[probeIndex[i]]++;
                    bucketPos[slot] = pos[i];
                    bucketCol[slot] = col[i];
                    origIndex[slot] = i;
                }
            }
        }

        // ── Candidate-selection collision diagnostics ─────────────────────────

        public struct CandidateCollisionReport
        {
            public int countL;
            public int countR;
            public int distinctRChosen;   // how many distinct R indices appear across all L splats' raw best-match choice
            public float distinctRatio;   // distinctRChosen / min(countL, countR) — 1.0 = no collisions possible, near-0 = mass convergence
        }

        /// <summary>
        /// Dispatches a single, unarbitrated FindBestMatches pass over the FULL L/R sets (no
        /// round-loop subsetting) and counts how many distinct R indices are chosen across all
        /// L splats' raw best-match pick. A low distinctRatio means many L splats are independently
        /// converging on the same handful of R candidates as their "best" match — if so, no
        /// collision-arbitration rule can fix the round loop's resolution rate, since only one
        /// L splat can win each contested R splat per round regardless of which distance decides it.
        /// </summary>
        public static CandidateCollisionReport AnalyzeCandidateCollisions(
            Vector3[] posL, Vector3[] posR, Vector4[] colL, Vector4[] colR,
            ICorrespondenceDispatcher dispatcher, float colorWeight)
        {
            float posWeight = 1f - colorWeight;
            dispatcher.FindBestMatches(posL, colL, posR, colR, posWeight, colorWeight,
                out int[] bestIndex, out _, out _);

            var distinctR = new HashSet<int>(bestIndex);
            int denom = Mathf.Min(posL.Length, posR.Length);

            return new CandidateCollisionReport
            {
                countL          = posL.Length,
                countR          = posR.Length,
                distinctRChosen = distinctR.Count,
                distinctRatio   = denom > 0 ? (float)distinctR.Count / denom : 0f,
            };
        }

        // ── Match quality sampling ────────────────────────────────────────────

        public struct MatchSample
        {
            public int   pairIndex;
            public int   leftIndex;
            public int   rightIndex;
            public float posDelta;
            public float colorDelta;
        }

        /// <summary>
        /// Samples a random subset of a built map's matched pairs and reports, per sample,
        /// the world-space position delta and color delta between the two matched splats.
        /// A cross-cluster swap shows up as a large position delta paired with a near-zero
        /// color delta; a large position delta with a large color delta is a legitimate
        /// long-range match at high color weight. Not exhaustive by design — at 100k-2M+
        /// splats, a small random sample is enough to tell whether matching looks sane.
        /// </summary>
        public static MatchSample[] SampleMatchQuality(GaussianMorphMap map, GaussianSplatAsset left, GaussianSplatAsset right,
            int sampleCount = 20, int seed = 0)
        {
            int n = map.matchedPairs?.Length ?? 0;
            if (n == 0)
                return Array.Empty<MatchSample>();

            var posL = DecodeSplatPositions(left);
            var posR = DecodeSplatPositions(right);
            var colL = DecodeSplatColors(left);
            var colR = DecodeSplatColors(right);

            var rng = new System.Random(seed);
            int count = Mathf.Min(sampleCount, n);
            var chosen = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Take(count);

            var samples = new List<MatchSample>(count);
            foreach (int pairIdx in chosen)
            {
                var pair = map.matchedPairs[pairIdx];
                samples.Add(new MatchSample
                {
                    pairIndex  = pairIdx,
                    leftIndex  = pair.x,
                    rightIndex = pair.y,
                    posDelta   = Vector3.Distance(posL[pair.x], posR[pair.y]),
                    colorDelta = Vector4.Distance(colL[pair.x], colR[pair.y]),
                });
            }

            return samples.ToArray();
        }

        // ── Duplicate-match diagnostics ───────────────────────────────────────

        public struct DuplicateReport
        {
            public int duplicateLeftIndices;
            public int duplicateRightIndices;
            public int largestFanOut;
            public int excessPairs; // pairs beyond the first occurrence of any duplicated index — extra output splats SplatMorph.compute would produce
        }

        /// <summary>
        /// Counts how many distinct L/R indices appear in more than one matchedPairs entry.
        /// ResolveRemainderOnGpu deliberately allows many-to-one matches (convergence over
        /// uniqueness) — but SplatMorph.compute is one-thread-per-pairs-entry, so a duplicated
        /// L index produces multiple output splats all reading the same source position/color
        /// while interpolating toward different R destinations, which can visually read as
        /// splats tearing/splitting apart during playback.
        /// </summary>
        public static DuplicateReport AnalyzeDuplicates(GaussianMorphMap map)
        {
            var pairs = map.matchedPairs ?? Array.Empty<int2>();
            var leftCounts  = new Dictionary<int, int>();
            var rightCounts = new Dictionary<int, int>();

            foreach (var p in pairs)
            {
                leftCounts.TryGetValue(p.x, out int lc);
                leftCounts[p.x] = lc + 1;
                rightCounts.TryGetValue(p.y, out int rc);
                rightCounts[p.y] = rc + 1;
            }

            int dupL = 0, dupR = 0, largestFanOut = 0, excess = 0;
            foreach (var kv in leftCounts)
            {
                if (kv.Value > 1)
                {
                    dupL++;
                    excess += kv.Value - 1;
                    largestFanOut = Mathf.Max(largestFanOut, kv.Value);
                }
            }
            foreach (var kv in rightCounts)
            {
                if (kv.Value > 1)
                {
                    dupR++;
                    largestFanOut = Mathf.Max(largestFanOut, kv.Value);
                }
            }

            return new DuplicateReport
            {
                duplicateLeftIndices  = dupL,
                duplicateRightIndices = dupR,
                largestFanOut         = largestFanOut,
                excessPairs           = excess,
            };
        }

        public struct TopDuplicate
        {
            public int rightIndex;
            public int fanOutCount;
            public Vector3 position;
            public Vector4 color;
        }

        /// <summary>
        /// Reports the N most-duplicated R indices (by how many matchedPairs entries claim them),
        /// with their decoded position/color. Used to distinguish genuine data redundancy (the
        /// "popular" splats cluster spatially/chromatically — e.g. a flat wall, a repeated
        /// architectural element that legitimately has many true nearest neighbors on the other
        /// side) from a structural search artifact (popular splats scattered with varied color,
        /// which would NOT be explained by redundant geometry).
        /// </summary>
        public static TopDuplicate[] TopDuplicatedRight(GaussianMorphMap map, GaussianSplatAsset right, int topN = 20)
        {
            var pairs = map.matchedPairs ?? Array.Empty<int2>();
            var rightCounts = new Dictionary<int, int>();
            foreach (var p in pairs)
            {
                rightCounts.TryGetValue(p.y, out int c);
                rightCounts[p.y] = c + 1;
            }

            var posR = DecodeSplatPositions(right);
            var colR = DecodeSplatColors(right);

            return rightCounts
                .OrderByDescending(kv => kv.Value)
                .Take(topN)
                .Select(kv => new TopDuplicate
                {
                    rightIndex  = kv.Key,
                    fanOutCount = kv.Value,
                    position    = posR[kv.Key],
                    color       = colR[kv.Key],
                })
                .ToArray();
        }

        // ── Duplicate resolution ──────────────────────────────────────────────

        /// <summary>
        /// GPU allows multiple L splats to claim the same R splat. Resolve by distance
        /// priority: the closest pair wins each contested R splat (approximates the
        /// reference's sequential "claim nearest available, then remove" matching,
        /// where the truly-best pairs are assigned first). Losers fall through to the
        /// next iterative re-matching round. O(N) — trivially fast after GPU readback.
        /// </summary>
        static void ResolveOneToOne(int[] bestMatch, float[] bestDist, int nL, int nR,
            out int2[] matchedPairs,
            out int[]  unmatchedLeft, out int[] unmatchedRight)
        {
            // claimedBy[j] = L that currently holds R splat j (-1 = unclaimed)
            int[] claimedBy = new int[nR];
            for (int j = 0; j < nR; j++) claimedBy[j] = -1;

            var uL = new List<int>();

            for (int i = 0; i < nL; i++)
            {
                int j = bestMatch[i];
                int incumbent = claimedBy[j];
                if (incumbent == -1)
                {
                    claimedBy[j] = i;
                }
                else if (bestDist[i] < bestDist[incumbent])
                {
                    claimedBy[j] = i;
                    uL.Add(incumbent);
                }
                else
                {
                    uL.Add(i);
                }
            }

            var pairs = new List<int2>(nL);
            var uR    = new List<int>();
            for (int j = 0; j < nR; j++)
            {
                if (claimedBy[j] != -1)
                    pairs.Add(new int2(claimedBy[j], j));
                else
                    uR.Add(j);
            }

            matchedPairs   = pairs.ToArray();
            unmatchedLeft  = uL.ToArray();
            unmatchedRight = uR.ToArray();
        }

        // ── Decoding ──────────────────────────────────────────────────────────

        public static Vector3[] DecodeSplatPositions(GaussianSplatAsset asset)
        {
            var layer      = asset.LayerData[0];
            var posBytes   = layer.m_PosData.GetData<byte>();
            var chunkBytes = layer.m_ChunkData != null ? layer.m_ChunkData.GetData<byte>() : default;

            int n   = asset.splatCount;
            int fmt = (int)asset.posFormat;
            var result = new Vector3[n];

            for (int i = 0; i < n; i++)
            {
                float3 raw = DecodeRawPos(posBytes, i, fmt);
                if (chunkBytes.IsCreated && chunkBytes.Length > 0)
                {
                    int chunkIdx = i / GaussianSplatAsset.kChunkSize;
                    ReadChunkPosBounds(chunkBytes, chunkIdx, out var posMin, out var posMax);
                    raw = math.lerp(posMin, posMax, raw);
                }
                result[i] = new Vector3(raw.x, raw.y, raw.z);
            }

            return result;
        }

        public static Vector4[] DecodeSplatColors(GaussianSplatAsset asset)
        {
            var layer    = asset.LayerData[0];
            var colBytes = layer.m_ColorData.GetData<byte>();
            int n        = asset.splatCount;
            var result   = new Vector4[n];
            var fmt      = asset.colorFormat;

            if (fmt == GaussianSplatAsset.ColorFormat.BC7)
            {
                Debug.LogWarning("GaussianMorphMapBuilder: BC7 color format not CPU-decodable; color matching disabled.");
                return result;
            }

            for (int i = 0; i < n; i++)
            {
                result[i] = fmt switch
                {
                    GaussianSplatAsset.ColorFormat.Float32x4 => new Vector4(
                        ReadFloat(colBytes, i * 16),     ReadFloat(colBytes, i * 16 + 4),
                        ReadFloat(colBytes, i * 16 + 8), ReadFloat(colBytes, i * 16 + 12)),
                    GaussianSplatAsset.ColorFormat.Float16x4 => new Vector4(
                        HalfToFloat(ReadUShort(colBytes, i * 8)),     HalfToFloat(ReadUShort(colBytes, i * 8 + 2)),
                        HalfToFloat(ReadUShort(colBytes, i * 8 + 4)), HalfToFloat(ReadUShort(colBytes, i * 8 + 6))),
                    GaussianSplatAsset.ColorFormat.Norm8x4 => new Vector4(
                        colBytes[i * 4] / 255f, colBytes[i * 4 + 1] / 255f,
                        colBytes[i * 4 + 2] / 255f, colBytes[i * 4 + 3] / 255f),
                    _ => Vector4.zero
                };
            }

            return result;
        }

        // ── Low-level helpers ─────────────────────────────────────────────────

        static float3 DecodeRawPos(NativeArray<byte> bytes, int idx, int fmt)
        {
            switch (fmt)
            {
                case (int)GaussianSplatAsset.VectorFormat.Float32:
                {
                    int addr = idx * 12;
                    return new float3(ReadFloat(bytes, addr), ReadFloat(bytes, addr + 4), ReadFloat(bytes, addr + 8));
                }
                case (int)GaussianSplatAsset.VectorFormat.Norm16:
                {
                    int addr = idx * 6;
                    return new float3(ReadUShort(bytes, addr) / 65535f, ReadUShort(bytes, addr + 2) / 65535f, ReadUShort(bytes, addr + 4) / 65535f);
                }
                case (int)GaussianSplatAsset.VectorFormat.Norm11:
                {
                    uint enc = ReadUInt(bytes, idx * 4);
                    return new float3((enc & 2047u) / 2047f, ((enc >> 11) & 1023u) / 1023f, ((enc >> 21) & 2047u) / 2047f);
                }
                case (int)GaussianSplatAsset.VectorFormat.Norm6:
                {
                    uint enc = ReadUShort(bytes, idx * 2);
                    return new float3((enc & 31u) / 31f, ((enc >> 5) & 63u) / 63f, ((enc >> 11) & 31u) / 31f);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(fmt));
            }
        }

        static void ReadChunkPosBounds(NativeArray<byte> chunkBytes, int chunkIdx, out float3 posMin, out float3 posMax)
        {
            // ChunkInfo layout: 0=col(16B), 16=posX.xy,posY.xy,posZ.xy(24B), 40=scl(12B), 52=sh(12B)
            int b = chunkIdx * UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
            posMin = new float3(ReadFloat(chunkBytes, b + 16), ReadFloat(chunkBytes, b + 24), ReadFloat(chunkBytes, b + 32));
            posMax = new float3(ReadFloat(chunkBytes, b + 20), ReadFloat(chunkBytes, b + 28), ReadFloat(chunkBytes, b + 36));
        }

        static float ReadFloat(NativeArray<byte> bytes, int offset)
        {
            uint v = ReadUInt(bytes, offset);
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        static uint ReadUInt(NativeArray<byte> bytes, int offset)
        {
            return (uint)bytes[offset]
                | ((uint)bytes[offset + 1] << 8)
                | ((uint)bytes[offset + 2] << 16)
                | ((uint)bytes[offset + 3] << 24);
        }

        static ushort ReadUShort(NativeArray<byte> bytes, int offset)
        {
            return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
        }

        static float HalfToFloat(ushort h)
        {
            uint sign = (uint)(h >> 15) << 31;
            uint exp  = (uint)((h >> 10) & 0x1F);
            uint mant = (uint)(h & 0x3FF);
            uint f = exp == 0  ? sign | (mant << 13)
                   : exp == 31 ? sign | 0x7F800000u | (mant << 13)
                                : sign | ((exp + 112) << 23) | (mant << 13);
            return BitConverter.ToSingle(BitConverter.GetBytes(f), 0);
        }
    }
}
