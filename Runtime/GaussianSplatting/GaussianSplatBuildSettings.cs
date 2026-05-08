// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Project-wide settings for the Gaussian Splat build processor.
    /// Create via right-click > Create > Gaussian Splat > Build Settings and place in Assets/Settings/.
    /// The build processor finds this asset automatically via AssetDatabase — only one should exist.
    /// </summary>
    [CreateAssetMenu(menuName = "Gaussian Splat/Build Settings", fileName = "GaussianSplatBuildSettings")]
    public class GaussianSplatBuildSettings : ScriptableObject
    {
        [Tooltip("Asset paths matching any of these glob patterns are skipped by the build processor.\n" +
                 "Patterns are matched against the Unity asset path (e.g. Assets/WorldLabsWorlds/Low/**).\n" +
                 "Use ** to match any number of path segments, * to match within a single segment.\n" +
                 "Example: \"**/*_low.asset\" skips all assets whose filename ends with _low.")]
        public List<string> excludePatterns = new()
        {
            "Assets/WorldLabsWorlds/Low/**",
            "**/*_low.asset",
            "**/*_medium.asset",
            "**/*_high.asset",
        };
    }
}
