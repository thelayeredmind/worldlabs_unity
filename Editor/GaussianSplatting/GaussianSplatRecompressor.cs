// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Decodes a VeryHigh (Float32) GaussianSplatAsset from disk and re-encodes it
    /// at the requested quality, producing a new sibling asset.
    ///
    /// Used by the renderer inspector and by <see cref="GaussianSplatBuildProcessor"/> at build time.
    /// Only supports single-layer, VeryHigh-quality source assets (no chunk compression).
    /// </summary>
    public static class GaussianSplatRecompressor
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Menu entry — operates on the selected GaussianSplatRenderer
        // ─────────────────────────────────────────────────────────────────────

        [MenuItem("Tools/Gaussian Splats/Recompress Selected Asset for Mobile Preview")]
        static void MenuRecompressSelected()
        {
            var gs = Selection.activeGameObject?.GetComponent<GaussianSplatting.Runtime.GaussianSplatRenderer>();
            var asset = gs?.asset;
            if (asset == null)
            {
                EditorUtility.DisplayDialog("No selection",
                    "Select a GameObject with a GaussianSplatRenderer first.", "OK");
                return;
            }
            if (asset.chunkDataSize > 0)
            {
                EditorUtility.DisplayDialog("Cannot Recompress",
                    "Source asset must be VeryHigh quality (no chunk compression).", "OK");
                return;
            }
            if (asset.mobileQuality == GaussianSplatAsset.MobileQuality.None)
            {
                EditorUtility.DisplayDialog("No quality set",
                    "Set a Mobile Quality on the asset (in the renderer inspector) before recompressing.", "OK");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(asset);
            string dir       = Path.GetDirectoryName(assetPath);
            string baseName  = asset.name + "_" + asset.mobileQuality.ToString().ToLower();

            try
            {
                var result = Recompress(asset, dir, baseName, asset.mobileQuality,
                    (msg, t) => EditorUtility.DisplayProgressBar("Recompressing for Mobile Preview", msg, t));
                EditorUtility.ClearProgressBar();
                if (result != null)
                {
                    EditorGUIUtility.PingObject(result);
                    Debug.Log($"[GaussianSplat] Preview asset created: {AssetDatabase.GetAssetPath(result)}");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Recompress Failed", ex.Message, "OK");
                Debug.LogError($"[GaussianSplat] Recompress failed: {ex}");
            }
        }

        [MenuItem("Tools/Gaussian Splats/Recompress Selected Asset for Mobile Preview", true)]
        static bool MenuRecompressSelectedValidate()
        {
            var gs = Selection.activeGameObject?.GetComponent<GaussianSplatting.Runtime.GaussianSplatRenderer>();
            return gs != null && gs.asset != null;
        }

        [MenuItem("Tools/Gaussian Splats/Recompress All Mobile Assets")]
        static void MenuRecompressAll()
        {
            var settings = LoadBuildSettings();
            string[] guids = AssetDatabase.FindAssets("t:GaussianSplatAsset");
            int done = 0, skipped = 0;

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);

                    if (settings != null && GaussianSplatBuildProcessor.IsExcludedPublic(assetPath, settings.excludePatterns))
                        continue;

                    var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                    if (asset == null) continue;
                    if (asset.chunkDataSize > 0) continue;
                    if (asset.mobileQuality == GaussianSplatAsset.MobileQuality.None) continue;

                    EditorUtility.DisplayProgressBar("Recompressing All Mobile Assets",
                        $"{asset.name} ({i + 1}/{guids.Length})", (float)i / guids.Length);

                    string dir      = Path.GetDirectoryName(assetPath);
                    string baseName = asset.name + "_" + asset.mobileQuality.ToString().ToLower();

                    try
                    {
                        Recompress(asset, dir, baseName, asset.mobileQuality);
                        done++;
                        Debug.Log($"[GaussianSplat] Recompressed: {assetPath}");
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Debug.LogError($"[GaussianSplat] Failed to recompress {assetPath}: {ex.Message}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            EditorUtility.DisplayDialog("Recompress All Done",
                $"Recompressed: {done}\nFailed/skipped: {skipped}", "OK");
        }

        static GaussianSplatBuildSettings LoadBuildSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:GaussianSplatBuildSettings");
            if (guids.Length == 0) return null;
            return AssetDatabase.LoadAssetAtPath<GaussianSplatBuildSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Core API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes <paramref name="srcAsset"/> (must be VeryHigh / Float32, single-layer) and
        /// writes a new <see cref="GaussianSplatAsset"/> at <paramref name="quality"/> into
        /// <paramref name="outputFolder"/>/<paramref name="baseName"/>.
        /// Returns the newly created asset, or null on failure.
        /// </summary>
        public static GaussianSplatAsset Recompress(
            GaussianSplatAsset srcAsset,
            string outputFolder,
            string baseName,
            GaussianSplatAsset.MobileQuality quality,
            Action<string, float> progress = null)
        {
            if (srcAsset == null) throw new ArgumentNullException(nameof(srcAsset));
            if (srcAsset.chunkDataSize > 0)
                throw new InvalidOperationException("Source asset must be VeryHigh (no chunk compression).");
            if (srcAsset.LayerData.Count != 1)
                throw new InvalidOperationException("Recompressor only supports single-layer assets.");

            progress?.Invoke("Decoding VeryHigh buffers", 0.05f);
            NativeArray<InputSplatData> splats = DecodeVeryHighToDisk(srcAsset);
            try
            {
                var (posFormat, scaleFormat, colorFormat, shFormat) =
                    GaussianSplatAsset.GetFormatsForMobileQuality(quality);

                progress?.Invoke("Encoding at target quality", 0.2f);
                RuntimeSplatData runtimeData = RuntimeSplatProcessing.Process(
                    splats, posFormat, scaleFormat, colorFormat, shFormat,
                    (msg, t) => progress?.Invoke(msg, 0.2f + t * 0.6f));

                progress?.Invoke("Writing .bytes files", 0.82f);
                Directory.CreateDirectory(outputFolder);

                string pathChunk = $"{outputFolder}/{baseName}_chk.bytes";
                string pathPos   = $"{outputFolder}/{baseName}_pos.bytes";
                string pathOther = $"{outputFolder}/{baseName}_oth.bytes";
                string pathCol   = $"{outputFolder}/{baseName}_col.bytes";
                string pathSh    = $"{outputFolder}/{baseName}_shs.bytes";

                bool useChunks = runtimeData.chkData != null;
                if (useChunks) File.WriteAllBytes(pathChunk, runtimeData.chkData);
                File.WriteAllBytes(pathPos,   runtimeData.posData);
                File.WriteAllBytes(pathOther, runtimeData.othData);

                byte[] colBytes = runtimeData.colData;
                if (colorFormat == GaussianSplatAsset.ColorFormat.BC7)
                {
                    using var native = new NativeArray<byte>(runtimeData.colData, Allocator.TempJob);
                    var compressed = GaussianImageCreator.CreateColorData(native.Reinterpret<float4>(1), colorFormat);
                    colBytes = compressed.ToArray();
                    compressed.Dispose();
                }
                File.WriteAllBytes(pathCol, colBytes);
                File.WriteAllBytes(pathSh,  runtimeData.shData);

                progress?.Invoke("Importing assets", 0.88f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

                progress?.Invoke("Building asset", 0.94f);
                int2[] layerInfo = new int2[] { new int2(0, splats.Length) };
                var newAsset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                newAsset.Initialize(splats.Length, posFormat, scaleFormat, colorFormat, shFormat,
                    srcAsset.boundsMin, srcAsset.boundsMax, srcAsset.cameras, layerInfo);
                newAsset.name = baseName;
                newAsset.SetDataHash(srcAsset.dataHash);

                newAsset.SetAssetFiles(
                    0,
                    useChunks ? AssetDatabase.LoadAssetAtPath<TextAsset>(pathChunk) : null,
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathPos),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathOther),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathCol),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathSh));

                string assetPath = $"{outputFolder}/{baseName}.asset";
                GaussianSplatAsset saved;
                var existing = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                if (existing == null)
                {
                    AssetDatabase.CreateAsset(newAsset, assetPath);
                    saved = newAsset;
                }
                else
                {
                    EditorUtility.CopySerialized(newAsset, existing);
                    saved = existing;
                }

                EditorUtility.SetDirty(saved);
                AssetDatabase.SaveAssets();
                progress?.Invoke("Done", 1.0f);
                return saved;
            }
            finally
            {
                if (splats.IsCreated) splats.Dispose();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  VeryHigh .bytes → InputSplatData decoder
        // ─────────────────────────────────────────────────────────────────────

        // Called by GaussianSplatBuildProcessor.
        public static Unity.Collections.NativeArray<InputSplatData> DecodeVeryHigh(GaussianSplatAsset asset) =>
            DecodeVeryHighToDisk(asset);

        // Decodes a VeryHigh (Float32, no-chunk) asset from disk .bytes files back to InputSplatData[].
        // Buffer layouts for VeryHigh:
        //   pos:   float3[splatCount]                       — raw world positions
        //   other: [uint rot_norm10 | float3 scale][count]  — rot packed as EncodeQuatToNorm10, scale raw float3
        //   col:   float4[splatCount]                       — flat (dc0.rgb, opacity), NOT Morton-tiled
        //   shs:   SHTableItemFloat32[splatCount]           — 15×float3 + float3 pad = 192 bytes each
        static unsafe NativeArray<InputSplatData> DecodeVeryHighToDisk(GaussianSplatAsset asset)
        {
            var layer = asset.LayerData[0];

            byte[] posBytes   = layer.m_PosData.bytes;
            byte[] otherBytes = layer.m_OtherData.bytes;
            byte[] colBytes   = layer.m_ColorData.bytes;
            byte[] shBytes    = layer.m_SHData.bytes;

            int count = asset.splatCount;
            var splats = new NativeArray<InputSplatData>(count, Allocator.TempJob);

            const int otherStride = 4 + 12; // rot uint + float3 scale
            const int shStride    = 192;    // SHTableItemFloat32

            fixed (byte* posPtr   = posBytes)
            fixed (byte* othPtr   = otherBytes)
            fixed (byte* colPtr   = colBytes)
            fixed (byte* shPtr    = shBytes)
            {
                for (int i = 0; i < count; i++)
                {
                    InputSplatData s = default;

                    // Position — raw float3
                    float* p = (float*)(posPtr + i * 12);
                    s.pos = new Vector3(p[0], p[1], p[2]);

                    // Other — uint rot | float3 scale
                    byte* o = othPtr + i * otherStride;
                    uint rotEnc = *(uint*)o;
                    s.rot = DecodeNorm10Quat(rotEnc);
                    float* scl = (float*)(o + 4);
                    s.scale = new Vector3(scl[0], scl[1], scl[2]);

                    // Color — flat float4 (dc0.rgb, opacity)
                    float* c = (float*)(colPtr + i * 16);
                    s.dc0    = new Vector3(c[0], c[1], c[2]);
                    s.opacity = c[3];

                    // SH — SHTableItemFloat32 (15 float3s)
                    float* sh = (float*)(shPtr + i * shStride);
                    s.sh1 = ReadFloat3(sh,  0); s.sh2 = ReadFloat3(sh,  1); s.sh3 = ReadFloat3(sh,  2);
                    s.sh4 = ReadFloat3(sh,  3); s.sh5 = ReadFloat3(sh,  4); s.sh6 = ReadFloat3(sh,  5);
                    s.sh7 = ReadFloat3(sh,  6); s.sh8 = ReadFloat3(sh,  7); s.sh9 = ReadFloat3(sh,  8);
                    s.shA = ReadFloat3(sh,  9); s.shB = ReadFloat3(sh, 10); s.shC = ReadFloat3(sh, 11);
                    s.shD = ReadFloat3(sh, 12); s.shE = ReadFloat3(sh, 13); s.shF = ReadFloat3(sh, 14);

                    splats[i] = s;
                }
            }
            return splats;
        }

        static unsafe Vector3 ReadFloat3(float* base_, int idx3)
        {
            float* p = base_ + idx3 * 3;
            return new Vector3(p[0], p[1], p[2]);
        }

        // Inverse of EncodeQuatToNorm10(PackSmallest3Rotation(q)).
        // Stored format: xyz = smallest-3 components in [0,1] → packed to 10 bits each.
        //                w   = dropped-component index (0–3) stored as 2 bits.
        // Returns a Quaternion with the packed-smallest-3 float4 stuffed in xyzw so that
        // CreateOtherDataJob can call EncodeQuatToNorm10 on it and reproduce the original bits.
        static Quaternion DecodeNorm10Quat(uint enc)
        {
            float x = (enc & 0x3FFu) / 1023.5f;
            float y = ((enc >> 10) & 0x3FFu) / 1023.5f;
            float z = ((enc >> 20) & 0x3FFu) / 1023.5f;
            float w = ((enc >> 30) & 0x3u) / 3.0f; // index/3, matches PackSmallest3Rotation output
            return new Quaternion(x, y, z, w);
        }
    }
}
