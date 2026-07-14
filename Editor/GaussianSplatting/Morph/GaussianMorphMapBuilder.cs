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
            /// </summary>
            void FindBestMatches(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                float posWeight, float colWeight, out int[] bestIndex, out float[] bestDist);
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
                    out int[] bestMatch, out float[] bestDist);
                ct.ThrowIfCancellationRequested();

                ResolveOneToOne(bestMatch, bestDist, subPosL.Length, subPosR.Length,
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
                    out int[] bestMatch, out _);
                for (int i = 0; i < remainingL.Length; i++)
                    pairs.Add(new int2(remainingL[i], bestMatch[i]));
            }

            if (remainingR.Length > 0 && posL.Length > 0)
            {
                ct.ThrowIfCancellationRequested();
                var subPosR = remainingR.Select(j => posR[j]).ToArray();
                var subColR = remainingR.Select(j => colR[j]).ToArray();
                dispatcher.FindBestMatches(subPosR, subColR, posL, colL, posWeight, colWeight,
                    out int[] bestMatch, out _);
                for (int j = 0; j < remainingR.Length; j++)
                    pairs.Add(new int2(bestMatch[j], remainingR[j]));
            }

            remainingL = Array.Empty<int>();
            remainingR = Array.Empty<int>();
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
