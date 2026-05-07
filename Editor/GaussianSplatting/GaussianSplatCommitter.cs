// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Commits deleted-splat edits back to disk, permanently compacting the asset.
    // Only works on VeryHigh (Float32, no chunk compression) assets.
    static class GaussianSplatCommitter
    {
        const int kTexWidth = 2048;

        static (int x, int y) DecodeMorton2D_16x16(uint t)
        {
            t = (t & 0xFF) | ((t & 0xFE) << 7);
            t &= 0x5555;
            t = (t ^ (t >> 1)) & 0x3333;
            t = (t ^ (t >> 2)) & 0x0f0f;
            return ((int)(t & 0xF), (int)(t >> 8));
        }

        static (int px, int py) SplatIndexToPixel(int idx)
        {
            var (lx, ly) = DecodeMorton2D_16x16((uint)idx);
            int tilesPerRow = kTexWidth / 16;
            int tileIdx = idx >> 8;
            int px = (tileIdx % tilesPerRow) * 16 + lx;
            int py = (tileIdx / tilesPerRow) * 16 + ly;
            return (px, py);
        }

        public static void Commit(GaussianSplatRenderer gs)
        {
            if (gs.asset.chunkDataSize > 0)
            {
                EditorUtility.DisplayDialog("Cannot Commit",
                    "Commit only works on VeryHigh quality assets (no chunk compression).", "OK");
                return;
            }

            if (!gs.editModified)
            {
                EditorUtility.DisplayDialog("Nothing to Commit", "No edits have been made.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Commit Edits",
                    $"This will permanently remove {gs.editDeletedSplats:N0} deleted splats from the asset on disk.\n\nThis cannot be undone. Continue?",
                    "Commit", "Cancel"))
                return;

            EditorUtility.DisplayProgressBar("Committing Splats", "Reading GPU buffers...", 0.1f);
            try
            {
                if (!gs.EditReadbackBuffers(out byte[] posData, out byte[] otherData, out byte[] shData, out byte[] colorData))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", "Failed to read GPU buffers.", "OK");
                    return;
                }

                int splatCount = gs.splatCount;
                uint[] deletedBits = gs.SnapshotDeletedBits();

                EditorUtility.DisplayProgressBar("Committing Splats", "Compacting...", 0.3f);

                const int posStride          = 12;
                const int otherStride        = 16;
                const int shStride           = 192;
                const int colorBytesPerPixel = 16;

                int newSplatCount = 0;
                for (int i = 0; i < splatCount; i++)
                {
                    int wIdx = i >> 5; int bIdx = i & 31;
                    bool isDeleted = deletedBits != null && (deletedBits[wIdx] & (1u << bIdx)) != 0;
                    if (!isDeleted) newSplatCount++;
                }

                if (newSplatCount == 0)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", "All splats are deleted — nothing to write.", "OK");
                    return;
                }

                var (tw, th) = GaussianSplatAsset.CalcTextureSize(newSplatCount);

                byte[] newPos   = new byte[newSplatCount * posStride];
                byte[] newOther = new byte[newSplatCount * otherStride];
                byte[] newSH    = new byte[newSplatCount * shStride];
                byte[] newColor = new byte[tw * th * colorBytesPerPixel];

                int dstIdx = 0;
                for (int i = 0; i < splatCount; i++)
                {
                    int wIdx = i >> 5; int bIdx = i & 31;
                    bool isDeleted = deletedBits != null && (deletedBits[wIdx] & (1u << bIdx)) != 0;
                    if (isDeleted) continue;

                    Array.Copy(posData,   i * posStride,   newPos,   dstIdx * posStride,   posStride);
                    Array.Copy(otherData, i * otherStride, newOther, dstIdx * otherStride, otherStride);
                    Array.Copy(shData,    i * shStride,    newSH,    dstIdx * shStride,    shStride);

                    var (srcPx, srcPy) = SplatIndexToPixel(i);
                    int srcOff = (srcPy * kTexWidth + srcPx) * colorBytesPerPixel;
                    var (dstPx, dstPy) = SplatIndexToPixel(dstIdx);
                    int dstOff = (dstPy * tw + dstPx) * colorBytesPerPixel;
                    Array.Copy(colorData, srcOff, newColor, dstOff, colorBytesPerPixel);

                    dstIdx++;
                }

                EditorUtility.DisplayProgressBar("Committing Splats", "Writing asset files...", 0.6f);

                // Overwrite the existing .bytes files in-place
                var layerData = gs.asset.LayerData;
                if (layerData == null || layerData.Count == 0)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", "Asset has no layer data.", "OK");
                    return;
                }

                var layer = layerData[0];
                string pathPos   = AssetDatabase.GetAssetPath(layer.m_PosData);
                string pathOther = AssetDatabase.GetAssetPath(layer.m_OtherData);
                string pathSH    = AssetDatabase.GetAssetPath(layer.m_SHData);
                string pathColor = AssetDatabase.GetAssetPath(layer.m_ColorData);

                File.WriteAllBytes(pathPos,   newPos);
                File.WriteAllBytes(pathOther, newOther);
                File.WriteAllBytes(pathSH,    newSH);
                File.WriteAllBytes(pathColor, newColor);

                EditorUtility.DisplayProgressBar("Committing Splats", "Reimporting...", 0.75f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

                // Update splat count and bounds on the asset
                EditorUtility.DisplayProgressBar("Committing Splats", "Updating asset...", 0.9f);
                var srcAsset = gs.asset;
                Undo.RecordObject(srcAsset, "Commit Splat Edits");
                srcAsset.Initialize(
                    newSplatCount,
                    GaussianSplatAsset.VectorFormat.Float32,
                    GaussianSplatAsset.VectorFormat.Float32,
                    GaussianSplatAsset.ColorFormat.Float32x4,
                    GaussianSplatAsset.SHFormat.Float32,
                    srcAsset.boundsMin, srcAsset.boundsMax,
                    srcAsset.cameras,
                    new[] { new Unity.Mathematics.int2(0, newSplatCount) }
                );
                srcAsset.SetAssetFiles(
                    0,
                    null,
                    layer.m_PosData,
                    layer.m_OtherData,
                    layer.m_ColorData,
                    layer.m_SHData
                );
                EditorUtility.SetDirty(srcAsset);
                AssetDatabase.SaveAssets();

                // Reload the renderer so it picks up the new splat count
                gs.enabled = false;
                gs.enabled = true;

                EditorUtility.ClearProgressBar();
                Debug.Log($"[GaussianSplat] Committed: {splatCount - newSplatCount:N0} splats removed, {newSplatCount:N0} remaining in {AssetDatabase.GetAssetPath(srcAsset)}");
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Error", $"Commit failed:\n{e.Message}", "OK");
            }
        }
    }
}
