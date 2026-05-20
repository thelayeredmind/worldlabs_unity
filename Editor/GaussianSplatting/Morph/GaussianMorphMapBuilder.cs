// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
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
            /// Upload positions and colors for both sides, dispatch the search, return best-match indices per L splat.
            /// Must be called on the main thread.
            /// </summary>
            int[] FindBestMatches(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                float posWeight, float colWeight);
        }

        // ── Public entry point ────────────────────────────────────────────────

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
            progress?.Report(0.1f);

            float posWeight = 1f - colorWeight;
            int[] bestMatch = dispatcher.FindBestMatches(posL, colL, posR, colR, posWeight, colorWeight);
            ct.ThrowIfCancellationRequested();
            progress?.Report(0.85f);

            ResolveOneToOne(bestMatch, posL.Length, posR.Length,
                out var pairs, out var uL, out var uR);
            progress?.Report(1f);

            return new Result
            {
                matchedPairs   = pairs,
                unmatchedLeft  = uL,
                unmatchedRight = uR,
            };
        }

        // ── Duplicate resolution ──────────────────────────────────────────────

        /// <summary>
        /// GPU allows multiple L splats to claim the same R splat. Resolve greedily:
        /// keep the L splat with the smallest index (stable), mark the rest unmatched.
        /// O(N) — trivially fast after GPU readback.
        /// </summary>
        static void ResolveOneToOne(int[] bestMatch, int nL, int nR,
            out int2[] matchedPairs,
            out int[]  unmatchedLeft, out int[] unmatchedRight)
        {
            // claimedBy[j] = first L that claimed R splat j (-1 = unclaimed)
            int[] claimedBy = new int[nR];
            for (int j = 0; j < nR; j++) claimedBy[j] = -1;

            var pairs = new List<int2>(nL);
            var uL    = new List<int>();

            for (int i = 0; i < nL; i++)
            {
                int j = bestMatch[i];
                if (claimedBy[j] == -1)
                {
                    claimedBy[j] = i;
                    pairs.Add(new int2(i, j));
                }
                else
                {
                    uL.Add(i);
                }
            }

            var claimedR = new HashSet<int>();
            foreach (var p in pairs) claimedR.Add(p.y);

            var uR = new List<int>();
            for (int j = 0; j < nR; j++)
                if (!claimedR.Contains(j)) uR.Add(j);

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
