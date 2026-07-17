// SPDX-License-Identifier: MIT

using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Precomputed correspondence between two GaussianSplatAssets (Left and Right).
    /// Direction-agnostic: each matched pair stores both indices as int2 (x=left, y=right).
    /// The morpher chooses which component is src/dst at activation time.
    /// Built once by GaussianMorphMapBuilder.
    /// </summary>
    /// <summary>
    /// The correspondence search strategy a GaussianMorphMap was built with. Shared between the
    /// editor-only builder window (which drives the build) and this Runtime asset (which records
    /// what was used), so it lives here rather than as a private nested type in the window.
    /// </summary>
    public enum CorrespondenceAlgorithm
    {
        RoundBased,
        SpatialProbes,
        MutualTopK,
        GaleShapley,
        Simple,
    }

    public class GaussianMorphMap : ScriptableObject
    {
        [SerializeField] public int splatCountLeft;
        [SerializeField] public int splatCountRight;

        /// <summary>
        /// AssetDatabase GUIDs of the GaussianSplatAssets this map was built from. Empty on maps
        /// built before this field existed — GaussianSplatMorpher.MapIsSwapped() falls back to
        /// the splat-count heuristic in that case.
        /// </summary>
        [SerializeField] public string leftAssetGuid;
        [SerializeField] public string rightAssetGuid;

        /// <summary>
        /// Build-time settings this map was produced with — provenance only, never read by the
        /// morph runtime path. Default on maps built before these fields existed. Read-only outside
        /// this class; only <see cref="StampBuildSettings"/> (called by the builder window at save
        /// time) may set them.
        /// </summary>
        [SerializeField] CorrespondenceAlgorithm m_BuiltWithAlgorithm;
        [SerializeField] bool  m_BuiltWithForceMatchPass;
        [SerializeField] float m_BuiltWithColorWeight;
        [SerializeField] float m_BuiltWithProbeAccuracy;

        public CorrespondenceAlgorithm builtWithAlgorithm      => m_BuiltWithAlgorithm;
        public bool                    builtWithForceMatchPass => m_BuiltWithForceMatchPass;
        public float                   builtWithColorWeight    => m_BuiltWithColorWeight;
        public float                   builtWithProbeAccuracy  => m_BuiltWithProbeAccuracy;

        /// <summary>Called once, by the builder window, right after this asset is created.</summary>
        public void StampBuildSettings(CorrespondenceAlgorithm algorithm, bool forceMatchPass, float colorWeight, float probeAccuracy)
        {
            m_BuiltWithAlgorithm      = algorithm;
            m_BuiltWithForceMatchPass = forceMatchPass;
            m_BuiltWithColorWeight    = colorWeight;
            m_BuiltWithProbeAccuracy  = probeAccuracy;
        }

        /// <summary>
        /// Matched pairs. x = index into Left asset, y = index into Right asset.
        /// No ordering guarantee — the morpher sorts by dst component at activation.
        /// </summary>
        [SerializeField] public int2[] matchedPairs;

        /// <summary>Splats in Left with no partner in Right.</summary>
        [SerializeField] public int[] unmatchedLeft;

        /// <summary>Splats in Right with no partner in Left.</summary>
        [SerializeField] public int[] unmatchedRight;

        public int MatchedCount => matchedPairs?.Length ?? 0;
    }
}
