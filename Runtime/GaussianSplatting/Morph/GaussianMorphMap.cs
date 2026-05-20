// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Precomputed correspondence between two GaussianSplatAssets (Left and Right).
    /// Direction-agnostic: the morpher chooses which side is source and which is target at runtime.
    /// Built once by GaussianMorphMapBuilder.
    ///
    /// indicesLeft[i] ↔ indicesRight[i] — a matched pair.
    /// Unmatched splats fade in/out via opacity depending on morph direction.
    /// </summary>
    public class GaussianMorphMap : ScriptableObject
    {
        [SerializeField] public int splatCountLeft;
        [SerializeField] public int splatCountRight;

        /// <summary>Parallel matched pair arrays. indicesLeft[i] ↔ indicesRight[i].</summary>
        [SerializeField] public int[] indicesLeft;
        [SerializeField] public int[] indicesRight;

        /// <summary>Splats in Left with no partner in Right.</summary>
        [SerializeField] public int[] unmatchedLeft;

        /// <summary>Splats in Right with no partner in Left.</summary>
        [SerializeField] public int[] unmatchedRight;

        public int MatchedCount => indicesLeft?.Length ?? 0;

        /// <summary>
        /// Returns the pair arrays oriented so that <paramref name="fromLeft"/> drives t=0.
        /// When fromLeft=true: src=indicesLeft, dst=indicesRight, srcUnmatched=unmatchedLeft, dstUnmatched=unmatchedRight.
        /// When fromLeft=false: src=indicesRight, dst=indicesLeft, srcUnmatched=unmatchedRight, dstUnmatched=unmatchedLeft.
        /// </summary>
        public void GetDirected(bool fromLeft,
            out int[] srcIndices, out int[] dstIndices,
            out int[] srcUnmatched, out int[] dstUnmatched)
        {
            if (fromLeft)
            {
                srcIndices   = indicesLeft;
                dstIndices   = indicesRight;
                srcUnmatched = unmatchedLeft;
                dstUnmatched = unmatchedRight;
            }
            else
            {
                srcIndices   = indicesRight;
                dstIndices   = indicesLeft;
                srcUnmatched = unmatchedRight;
                dstUnmatched = unmatchedLeft;
            }
        }
    }
}
