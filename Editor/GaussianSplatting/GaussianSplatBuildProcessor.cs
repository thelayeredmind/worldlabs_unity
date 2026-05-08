// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Before a build: for every <see cref="GaussianSplatAsset"/> with <c>mobileQuality != None</c>,
    /// finds the pre-baked sibling asset (e.g. MyAsset_low.asset) and redirects the source asset's
    /// TextAsset references to the sibling's .bytes files.  No recompression happens here.
    ///
    /// After the build: restores all assets to their original references.
    ///
    /// Pre-bake mobile assets via: Tools > Gaussian Splats > Recompress Selected Asset for Mobile Preview
    /// </summary>
    class GaussianSplatBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        static readonly List<AssetSnapshot> s_Snapshots = new();

        // ─────────────────────────────────────────────────────────────────────
        //  Build callbacks
        // ─────────────────────────────────────────────────────────────────────

        public void OnPreprocessBuild(BuildReport report)
        {
            s_Snapshots.Clear();
            SwapAllMobileAssets();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            RestoreAll();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Core logic
        // ─────────────────────────────────────────────────────────────────────

        static void SwapAllMobileAssets()
        {
            var settings = LoadSettings();
            string[] guids = AssetDatabase.FindAssets("t:GaussianSplatAsset");

            foreach (var guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);

                if (settings != null && IsExcluded(assetPath, settings.excludePatterns))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                if (asset == null) continue;
                if (asset.chunkDataSize > 0) continue; // already compressed — not a VeryHigh editing asset
                if (asset.mobileQuality == GaussianSplatAsset.MobileQuality.None)
                {
                    Debug.LogWarning($"[GaussianSplatBuildProcessor] VeryHigh asset '{asset.name}' has no Mobile Quality set — it will be included UNCOMPRESSED in the build.\n{assetPath}");
                    continue;
                }

                string siblingPath = GetSiblingAssetPath(assetPath, asset.mobileQuality);
                var sibling = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(siblingPath);
                if (sibling == null)
                {
                    Debug.LogWarning($"[GaussianSplatBuildProcessor] No pre-baked mobile asset found for {assetPath}\n" +
                                     $"Expected: {siblingPath}\n" +
                                     $"Run 'Tools > Gaussian Splats > Recompress Selected Asset for Mobile Preview' first.");
                    continue;
                }
                if (sibling.LayerData.Count == 0)
                {
                    Debug.LogWarning($"[GaussianSplatBuildProcessor] Sibling asset {siblingPath} has no layer data — skipping.");
                    continue;
                }

                s_Snapshots.Add(new AssetSnapshot(assetPath, asset));
                SwapToSibling(asset, sibling);
                Debug.Log($"[GaussianSplatBuildProcessor] Swapped {asset.name} → {sibling.name} for build.");
            }
        }

        // Redirects source asset's TextAsset refs and format fields to match the sibling.
        static void SwapToSibling(GaussianSplatAsset source, GaussianSplatAsset sibling)
        {
            var layerInfo = sibling.layerInfo
                .Select(kv => new int2(kv.Key, kv.Value))
                .ToArray();

            source.Initialize(sibling.splatCount, sibling.posFormat, sibling.scaleFormat,
                sibling.colorFormat, sibling.shFormat,
                sibling.boundsMin, sibling.boundsMax, source.cameras, layerInfo);

            source.ClearLayerData();
            foreach (var la in sibling.LayerData)
                source.SetAssetFiles(la.layer, la.m_ChunkData, la.m_PosData, la.m_OtherData, la.m_ColorData, la.m_SHData);

            if (sibling.ClusteredSHData != null)
                source.SetClusteredSHAssetFile(sibling.ClusteredSHData);

            EditorUtility.SetDirty(source);
            AssetDatabase.SaveAssets();
        }

        // Expected sibling path: same folder, name + "_low" / "_medium" / "_high"
        static string GetSiblingAssetPath(string assetPath, GaussianSplatAsset.MobileQuality quality)
        {
            string dir      = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string baseName = Path.GetFileNameWithoutExtension(assetPath);
            return $"{dir}/{baseName}_{quality.ToString().ToLower()}.asset";
        }

        static void RestoreAll()
        {
            foreach (var snap in s_Snapshots)
            {
                try { snap.Restore(); }
                catch (Exception ex)
                {
                    Debug.LogError($"[GaussianSplatBuildProcessor] Restore failed for {snap.AssetPath}: {ex}");
                }
            }
            s_Snapshots.Clear();
            AssetDatabase.SaveAssets();
            Debug.Log("[GaussianSplatBuildProcessor] All assets restored to VeryHigh.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Settings + glob
        // ─────────────────────────────────────────────────────────────────────

        static GaussianSplatBuildSettings LoadSettings()
        {
            string[] guids = AssetDatabase.FindAssets("t:GaussianSplatBuildSettings");
            if (guids.Length == 0) return null;
            if (guids.Length > 1)
                Debug.LogWarning("[GaussianSplatBuildProcessor] Multiple GaussianSplatBuildSettings found — using the first one.");
            return AssetDatabase.LoadAssetAtPath<GaussianSplatBuildSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        internal static bool IsExcludedPublic(string assetPath, List<string> patterns) => IsExcluded(assetPath, patterns);

        static bool IsExcluded(string assetPath, List<string> patterns)
        {
            foreach (var pattern in patterns)
                if (GlobMatch(assetPath, pattern)) return true;
            return false;
        }

        static bool GlobMatch(string path, string pattern)
        {
            path    = path.Replace('\\', '/');
            pattern = pattern.Replace('\\', '/');
            return GlobMatchRecursive(path, 0, pattern, 0);
        }

        static bool GlobMatchRecursive(string path, int pi, string pattern, int gi)
        {
            while (gi < pattern.Length)
            {
                if (pattern[gi] == '*' && gi + 1 < pattern.Length && pattern[gi + 1] == '*')
                {
                    int nextGi = gi + 2;
                    if (nextGi < pattern.Length && pattern[nextGi] == '/') nextGi++;
                    for (int i = pi; i <= path.Length; i++)
                    {
                        if (GlobMatchRecursive(path, i, pattern, nextGi)) return true;
                        if (i == path.Length) break;
                    }
                    return false;
                }
                else if (pattern[gi] == '*')
                {
                    int nextGi = gi + 1;
                    for (int i = pi; i <= path.Length; i++)
                    {
                        if (i < path.Length && path[i] == '/') break;
                        if (GlobMatchRecursive(path, i, pattern, nextGi)) return true;
                    }
                    return false;
                }
                else
                {
                    if (pi >= path.Length) return false;
                    if (char.ToLowerInvariant(path[pi]) != char.ToLowerInvariant(pattern[gi])) return false;
                    pi++; gi++;
                }
            }
            return pi == path.Length;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Snapshot
        // ─────────────────────────────────────────────────────────────────────

        class AssetSnapshot
        {
            public string AssetPath { get; }

            readonly GaussianSplatAsset _asset;
            readonly GaussianSplatAsset.VectorFormat _posFormat;
            readonly GaussianSplatAsset.VectorFormat _scaleFormat;
            readonly GaussianSplatAsset.ColorFormat  _colorFormat;
            readonly GaussianSplatAsset.SHFormat     _shFormat;
            readonly int _splatCount;
            readonly Vector3 _boundsMin, _boundsMax;
            readonly GaussianSplatAsset.CameraInfo[] _cameras;
            readonly List<GaussianSplatAsset.LayerAssets> _layerData;
            readonly TextAsset _clusteredSH;
            readonly List<int2> _layerInfo;

            public AssetSnapshot(string assetPath, GaussianSplatAsset asset)
            {
                AssetPath    = assetPath;
                _asset       = asset;
                _posFormat   = asset.posFormat;
                _scaleFormat = asset.scaleFormat;
                _colorFormat = asset.colorFormat;
                _shFormat    = asset.shFormat;
                _splatCount  = asset.splatCount;
                _boundsMin   = asset.boundsMin;
                _boundsMax   = asset.boundsMax;
                _cameras     = asset.cameras;
                _layerData   = new List<GaussianSplatAsset.LayerAssets>(asset.LayerData);
                _clusteredSH = asset.ClusteredSHData;
                _layerInfo   = asset.layerInfo.Select(kv => new int2(kv.Key, kv.Value)).ToList();
            }

            public void Restore()
            {
                _asset.Initialize(_splatCount, _posFormat, _scaleFormat, _colorFormat, _shFormat,
                    _boundsMin, _boundsMax, _cameras, _layerInfo);
                _asset.ClearLayerData();
                foreach (var la in _layerData)
                    _asset.SetAssetFiles(la.layer, la.m_ChunkData, la.m_PosData, la.m_OtherData, la.m_ColorData, la.m_SHData);
                if (_clusteredSH != null)
                    _asset.SetClusteredSHAssetFile(_clusteredSH);
                EditorUtility.SetDirty(_asset);
            }
        }
    }
}
