// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Separates the current selection of a VeryHigh GaussianSplatRenderer into a new sibling asset.
    // Selected splats are removed from the original and placed in a new GaussianSplatAsset + GameObject.
    static class GaussianSplatSeparator
    {
        // Must match GaussianSplatting.hlsl: kTexWidth = 2048
        const int kTexWidth = 2048;

        // Matches DecodeMorton2D_16x16 in GaussianSplatting.hlsl
        static (int x, int y) DecodeMorton2D_16x16(uint t)
        {
            t = (t & 0xFF) | ((t & 0xFE) << 7);
            t &= 0x5555;
            t = (t ^ (t >> 1)) & 0x3333;
            t = (t ^ (t >> 2)) & 0x0f0f;
            return ((int)(t & 0xF), (int)(t >> 8));
        }

        // Matches SplatIndexToPixelIndex in GaussianSplatting.hlsl
        static (int px, int py) SplatIndexToPixel(int idx)
        {
            var (lx, ly) = DecodeMorton2D_16x16((uint)idx);
            int tilesPerRow = kTexWidth / 16;
            int tileIdx = idx >> 8;
            int px = (tileIdx % tilesPerRow) * 16 + lx;
            int py = (tileIdx / tilesPerRow) * 16 + ly;
            return (px, py);
        }

        public static void Separate(GaussianSplatRenderer gs)
        {
            if (gs.asset.chunkDataSize > 0)
            {
                EditorUtility.DisplayDialog("Cannot Separate",
                    "Separate only works on VeryHigh quality assets (no chunk compression).", "OK");
                return;
            }

            int selectedCount = (int)gs.editSelectedSplats;
            if (selectedCount == 0)
            {
                EditorUtility.DisplayDialog("No Selection", "Select some splats first.", "OK");
                return;
            }

            string srcAssetPath = AssetDatabase.GetAssetPath(gs.asset);
            string srcDir = Path.GetDirectoryName(srcAssetPath);
            string defaultName = gs.asset.name + "_separated";
            string savePath = EditorUtility.SaveFilePanelInProject(
                "Save Separated Asset", defaultName, "asset",
                "Choose location for the new GaussianSplatAsset", srcDir);
            if (string.IsNullOrEmpty(savePath))
                return;

            string baseName = Path.GetFileNameWithoutExtension(savePath);
            string saveDir  = Path.GetDirectoryName(savePath);

            EditorUtility.DisplayProgressBar("Separating Splats", "Reading GPU buffers...", 0.1f);
            try
            {
                if (!gs.EditReadbackBuffers(out byte[] posData, out byte[] otherData, out byte[] shData, out byte[] colorData))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", "Failed to read GPU buffers.", "OK");
                    return;
                }

                var srcAsset = gs.asset;
                int splatCount = gs.splatCount;
                uint[] deletedBits  = gs.SnapshotDeletedBits();
                uint[] selectedBits = gs.SnapshotSelectedBits();

                EditorUtility.DisplayProgressBar("Separating Splats", "Compacting selected splats...", 0.3f);

                // VeryHigh (Float32) strides — these are exact for no-chunk assets
                const int posStride   = 12; // float3
                const int otherStride = 16; // rot 10.10.10.2 (4B) + scale float3 (12B)
                const int shStride    = 192; // 15 × float3 + padding (16 × 12B)
                const int colorBytesPerPixel = 16; // Float32x4

                // Allocate destination texture (new splat count, padded to CalcTextureSize)
                // We don't know newSplatCount yet, so pre-pass to count.
                int newSplatCount = 0;
                for (int i = 0; i < splatCount; i++)
                {
                    int wIdx = i >> 5; int bIdx = i & 31;
                    bool isDeleted  = deletedBits  != null && (deletedBits[wIdx]  & (1u << bIdx)) != 0;
                    bool isSelected = selectedBits != null && (selectedBits[wIdx] & (1u << bIdx)) != 0;
                    if (isSelected && !isDeleted) newSplatCount++;
                }

                if (newSplatCount == 0)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", "No splats to separate.", "OK");
                    return;
                }

                var (tw, th) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
                int srcTexWidth = kTexWidth;

                byte[] newPos   = new byte[newSplatCount * posStride];
                byte[] newOther = new byte[newSplatCount * otherStride];
                byte[] newSH    = new byte[newSplatCount * shStride];
                byte[] newColor = new byte[tw * th * colorBytesPerPixel];

                int dstIdx = 0;
                for (int i = 0; i < splatCount; i++)
                {
                    int wIdx = i >> 5; int bIdx = i & 31;
                    bool isDeleted  = deletedBits  != null && (deletedBits[wIdx]  & (1u << bIdx)) != 0;
                    bool isSelected = selectedBits != null && (selectedBits[wIdx] & (1u << bIdx)) != 0;
                    if (!isSelected || isDeleted) continue;

                    // pos
                    Array.Copy(posData,   i * posStride,   newPos,   dstIdx * posStride,   posStride);
                    // other
                    Array.Copy(otherData, i * otherStride, newOther, dstIdx * otherStride, otherStride);
                    // SH
                    Array.Copy(shData,    i * shStride,    newSH,    dstIdx * shStride,    shStride);

                    // color — both source and dest use the same Morton-tiled layout
                    var (srcPx, srcPy) = SplatIndexToPixel(i);
                    int srcByteOffset  = (srcPy * srcTexWidth + srcPx) * colorBytesPerPixel;

                    var (dstPx, dstPy) = SplatIndexToPixel(dstIdx);
                    int dstByteOffset  = (dstPy * tw + dstPx) * colorBytesPerPixel;

                    Array.Copy(colorData, srcByteOffset, newColor, dstByteOffset, colorBytesPerPixel);

                    dstIdx++;
                }

                EditorUtility.DisplayProgressBar("Separating Splats", "Writing asset files...", 0.6f);

                Directory.CreateDirectory(saveDir);
                string pathPos   = $"{saveDir}/{baseName}_pos.bytes";
                string pathOther = $"{saveDir}/{baseName}_oth.bytes";
                string pathSH    = $"{saveDir}/{baseName}_shs.bytes";
                string pathColor = $"{saveDir}/{baseName}_col.bytes";

                File.WriteAllBytes(pathPos,   newPos);
                File.WriteAllBytes(pathOther, newOther);
                File.WriteAllBytes(pathSH,    newSH);
                File.WriteAllBytes(pathColor, newColor);

                EditorUtility.DisplayProgressBar("Separating Splats", "Importing files...", 0.75f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

                var newAsset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                newAsset.Initialize(
                    newSplatCount,
                    GaussianSplatAsset.VectorFormat.Float32,
                    GaussianSplatAsset.VectorFormat.Float32,
                    GaussianSplatAsset.ColorFormat.Float32x4,
                    GaussianSplatAsset.SHFormat.Float32,
                    srcAsset.boundsMin, srcAsset.boundsMax,
                    null,
                    new[] { new Unity.Mathematics.int2(0, newSplatCount) }
                );

                newAsset.SetAssetFiles(
                    0,
                    null, // no chunks for VeryHigh
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathPos),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathOther),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathColor),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathSH)
                );

                EditorUtility.DisplayProgressBar("Separating Splats", "Saving asset...", 0.9f);
                AssetDatabase.CreateAsset(newAsset, savePath);
                AssetDatabase.SaveAssets();

                Undo.RecordObject(gs, "Separate Splats");
                gs.EditDeleteSelection();

                var newGo = new GameObject(baseName);
                Undo.RegisterCreatedObjectUndo(newGo, "Separate Splats");
                newGo.transform.SetParent(gs.transform.parent, false);
                newGo.transform.localPosition = gs.transform.localPosition;
                newGo.transform.localRotation = gs.transform.localRotation;
                newGo.transform.localScale    = gs.transform.localScale;

                var newRenderer = Undo.AddComponent<GaussianSplatRenderer>(newGo);
                newRenderer.m_Asset = newAsset;

                EditorUtility.ClearProgressBar();
                Selection.activeGameObject = newGo;
                Debug.Log($"[GaussianSplat] Separated {newSplatCount:N0} splats into {savePath}");
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Error", $"Separation failed:\n{e.Message}", "OK");
            }
        }
    }
}
