// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Pure correspondence algorithm. No editor dependencies — callable from a window, build processor, or test.
    /// </summary>
    public static class GaussianMorphMapBuilder
    {
        public struct Result
        {
            public int[] indicesLeft;
            public int[] indicesRight;
            public int[] unmatchedLeft;
            public int[] unmatchedRight;
        }

        /// <summary>
        /// Build correspondence between two assets.
        /// <param name="progress">Optional callback(0..1). Return false to cancel.</param>
        /// </summary>
        public static Result Build(GaussianSplatAsset assetLeft, GaussianSplatAsset assetRight,
            float colorWeight = 0.5f, Func<float, bool> progress = null)
        {
            var posL = DecodeSplatPositions(assetLeft);
            var posR = DecodeSplatPositions(assetRight);
            var colL = DecodeSplatColors(assetLeft);
            var colR = DecodeSplatColors(assetRight);

            CorrespondOneToOne(posL, posR, colL, colR, colorWeight, progress,
                out var mL, out var mR, out var uL, out var uR);

            return new Result
            {
                indicesLeft   = mL,
                indicesRight  = mR,
                unmatchedLeft  = uL,
                unmatchedRight = uR,
            };
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
                        ReadFloat(colBytes, i * 16),
                        ReadFloat(colBytes, i * 16 + 4),
                        ReadFloat(colBytes, i * 16 + 8),
                        ReadFloat(colBytes, i * 16 + 12)),
                    GaussianSplatAsset.ColorFormat.Float16x4 => new Vector4(
                        HalfToFloat(ReadUShort(colBytes, i * 8)),
                        HalfToFloat(ReadUShort(colBytes, i * 8 + 2)),
                        HalfToFloat(ReadUShort(colBytes, i * 8 + 4)),
                        HalfToFloat(ReadUShort(colBytes, i * 8 + 6))),
                    GaussianSplatAsset.ColorFormat.Norm8x4 => new Vector4(
                        colBytes[i * 4]     / 255f,
                        colBytes[i * 4 + 1] / 255f,
                        colBytes[i * 4 + 2] / 255f,
                        colBytes[i * 4 + 3] / 255f),
                    _ => Vector4.zero
                };
            }

            return result;
        }

        // ── Correspondence ────────────────────────────────────────────────────

        static void CorrespondOneToOne(
            Vector3[] posL, Vector3[] posR,
            Vector4[] colL, Vector4[] colR,
            float colorWeight,
            Func<float, bool> progress,
            out int[] matchedL, out int[] matchedR,
            out int[] unmatchedL, out int[] unmatchedR)
        {
            int nL = posL.Length;
            int nR = posR.Length;

            GetBounds(posL, out var minL, out var maxL);
            GetBounds(posR, out var minR, out var maxR);
            float scaleL   = math.cmax(maxL - minL);
            float scaleR   = math.cmax(maxR - minR);
            float posScale = 1f / math.max(math.max(scaleL, scaleR), 1e-6f);
            float pw       = 1f - colorWeight;
            float cw       = colorWeight;

            bool[] usedR = new bool[nR];
            var mL = new List<int>(math.min(nL, nR));
            var mR = new List<int>(math.min(nL, nR));

            for (int i = 0; i < nL; i++)
            {
                if (i % 1000 == 0 && progress != null)
                {
                    if (!progress(i / (float)nL))
                        break;
                }

                float3 pl = new float3(posL[i].x, posL[i].y, posL[i].z) * posScale;
                float4 cl = new float4(colL[i].x, colL[i].y, colL[i].z, colL[i].w);

                float bestDist = float.MaxValue;
                int   bestJ    = -1;

                for (int j = 0; j < nR; j++)
                {
                    if (usedR[j]) continue;
                    float3 pr = new float3(posR[j].x, posR[j].y, posR[j].z) * posScale;
                    float4 cr = new float4(colR[j].x, colR[j].y, colR[j].z, colR[j].w);

                    float dist = pw * math.lengthsq(pl - pr) + cw * math.lengthsq(cl - cr);
                    if (dist < bestDist) { bestDist = dist; bestJ = j; }
                }

                if (bestJ >= 0)
                {
                    usedR[bestJ] = true;
                    mL.Add(i);
                    mR.Add(bestJ);
                }
            }

            matchedL = mL.ToArray();
            matchedR = mR.ToArray();

            var matchedSetL = new HashSet<int>(mL);
            var uL = new List<int>();
            for (int i = 0; i < nL; i++)
                if (!matchedSetL.Contains(i)) uL.Add(i);
            unmatchedL = uL.ToArray();

            var usedSetR = new HashSet<int>(mR);
            var uR = new List<int>();
            for (int j = 0; j < nR; j++)
                if (!usedSetR.Contains(j)) uR.Add(j);
            unmatchedR = uR.ToArray();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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
            int chunkBase = chunkIdx * UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
            posMin = new float3(ReadFloat(chunkBytes, chunkBase + 16), ReadFloat(chunkBytes, chunkBase + 24), ReadFloat(chunkBytes, chunkBase + 32));
            posMax = new float3(ReadFloat(chunkBytes, chunkBase + 20), ReadFloat(chunkBytes, chunkBase + 28), ReadFloat(chunkBytes, chunkBase + 36));
        }

        static void GetBounds(Vector3[] pts, out float3 min, out float3 max)
        {
            min = new float3(float.MaxValue,  float.MaxValue,  float.MaxValue);
            max = new float3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in pts)
            {
                var f = new float3(p.x, p.y, p.z);
                min = math.min(min, f);
                max = math.max(max, f);
            }
        }

        static unsafe float ReadFloat(NativeArray<byte> bytes, int offset)
        {
            uint v = ReadUInt(bytes, offset);
            return *(float*)&v;
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
            uint sign     = (uint)(h >> 15) << 31;
            uint exponent = (uint)((h >> 10) & 0x1F);
            uint mantissa = (uint)(h & 0x3FF);
            uint f = exponent == 0  ? sign | (mantissa << 13)
                   : exponent == 31 ? sign | 0x7F800000u | (mantissa << 13)
                                    : sign | ((exponent + 112) << 23) | (mantissa << 13);
            return *(float*)&f;
        }
    }
}
