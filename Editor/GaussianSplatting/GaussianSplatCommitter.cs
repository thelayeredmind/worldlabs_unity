// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Commits deleted-splat edits to disk by overwriting the asset's .bytes files in-place.
    // Originals are backed up to the configured backup folder before any write.
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
            int lastProcessedSplatIdx = -1; // set inside the compaction loop, surfaced by the catch block on failure

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
                    $"This will permanently remove {gs.editDeletedSplats:N0} deleted splats from the asset's data files.\n\nOriginals will be backed up first. Continue?",
                    "Commit", "Cancel"))
                return;

            EditorUtility.DisplayProgressBar("Committing Splats", "Reading GPU buffers...", 0.05f);
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

                const int posStrideExpected   = 12;
                const int otherStrideExpected = 16;
                const int shStrideExpected    = 192;
                const int colorBytesExpected  = 16;
                Debug.Log($"[GaussianSplat] Commit — splatCount={splatCount} " +
                    $"posData={posData?.Length} (expected {splatCount * posStrideExpected}) " +
                    $"otherData={otherData?.Length} (expected {splatCount * otherStrideExpected}) " +
                    $"shData={shData?.Length} (expected {splatCount * shStrideExpected}) " +
                    $"colorData={colorData?.Length} (expected {splatCount * colorBytesExpected}) " +
                    $"posFormat={gs.asset.posFormat} scaleFormat={gs.asset.scaleFormat} " +
                    $"colorFormat={gs.asset.colorFormat} shFormat={gs.asset.shFormat} " +
                    $"chunkDataSize={gs.asset.chunkDataSize}");

                // Resolve source file paths before any writes
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

                // Backup originals — include the .asset file so restore can recover splatCount too
                EditorUtility.DisplayProgressBar("Committing Splats", "Backing up originals...", 0.15f);
                string pathAsset = AssetDatabase.GetAssetPath(gs.asset);
                GaussianSplatBackupManager.BackupFiles(gs.asset.name,
                    pathPos, pathOther, pathSH, pathColor, pathAsset);

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

                // Color on disk is flat float4[splatCount] in splat-index order.
                // colorData (from ReadPixels) is Morton-tiled texture bytes — we reverse the tiling here.
                byte[] newPos   = new byte[newSplatCount * posStride];
                byte[] newOther = new byte[newSplatCount * otherStride];
                byte[] newSH    = new byte[newSplatCount * shStride];
                byte[] newColor = new byte[newSplatCount * colorBytesPerPixel];

                int dstIdx = 0;
                for (int i = 0; i < splatCount; i++)
                {
                    lastProcessedSplatIdx = i;

                    int wIdx = i >> 5; int bIdx = i & 31;
                    bool isDeleted = deletedBits != null && (deletedBits[wIdx] & (1u << bIdx)) != 0;
                    if (isDeleted) continue;

                    Array.Copy(posData,   i * posStride,   newPos,   dstIdx * posStride,   posStride);
                    Array.Copy(otherData, i * otherStride, newOther, dstIdx * otherStride, otherStride);
                    Array.Copy(shData,    i * shStride,    newSH,    dstIdx * shStride,    shStride);

                    // Read from Morton-tiled texture position for src splat i, write flat to dst splat dstIdx
                    var (srcPx, srcPy) = SplatIndexToPixel(i);
                    int srcOff = (srcPy * kTexWidth + srcPx) * colorBytesPerPixel;
                    Array.Copy(colorData, srcOff, newColor, dstIdx * colorBytesPerPixel, colorBytesPerPixel);

                    dstIdx++;
                }

                EditorUtility.DisplayProgressBar("Committing Splats", "Writing asset files...", 0.6f);

                AssetDatabase.ReleaseCachedFileHandles();
                File.WriteAllBytes(pathPos,   newPos);
                File.WriteAllBytes(pathOther, newOther);
                File.WriteAllBytes(pathSH,    newSH);
                File.WriteAllBytes(pathColor, newColor);

                EditorUtility.DisplayProgressBar("Committing Splats", "Reimporting...", 0.75f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

                // Update splat count on the existing asset (same TextAsset references, just new count)
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
                srcAsset.ClearLayerData();
                srcAsset.SetAssetFiles(0, null,
                    layer.m_PosData,
                    layer.m_OtherData,
                    layer.m_ColorData,
                    layer.m_SHData);
                EditorUtility.SetDirty(srcAsset);
                AssetDatabase.SaveAssets();

                // Reload renderer — this naturally clears editModified / editDeletedSplats
                gs.enabled = false;
                gs.enabled = true;

                EditorUtility.ClearProgressBar();
                Debug.Log($"[GaussianSplat] Committed: {splatCount - newSplatCount:N0} splats removed, {newSplatCount:N0} remaining in {AssetDatabase.GetAssetPath(srcAsset)}");
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[GaussianSplat] Commit failed at splat index {lastProcessedSplatIdx} " +
                    $"(splatCount={gs.splatCount}, asset={AssetDatabase.GetAssetPath(gs.asset)})");
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Commit Unsuccessful",
                    $"Your splat has not been overwritten.\n\n{e.Message}\n\nSee the Console for full diagnostic details.",
                    "OK");
            }
        }
    }
}
