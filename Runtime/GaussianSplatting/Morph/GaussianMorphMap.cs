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
