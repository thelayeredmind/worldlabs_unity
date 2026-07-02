// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Place in a scene to define a world-space focus point for hotspot-driven LOD.
    ///
    /// Splats within <see cref="m_FullDetailRadius"/> receive full LOD (base threshold).
    /// From there to <see cref="m_AttenuationRadius"/> the contribution-cull threshold
    /// ramps up smoothly. Beyond <see cref="m_AttenuationRadius"/> splats are culled entirely.
    ///
    /// When multiple hotspots exist, each splat uses the nearest one.
    /// Parenting this to the camera emulates camera-distance LOD as a special case.
    /// Remove all instances from the scene to fall back to the renderer's flat threshold.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Gaussian Hotspot Volume")]
    public class GaussianHotspotVolume : MonoBehaviour
    {
        /// <summary>Radius within which splats receive full LOD (no threshold tightening).</summary>
        [Min(0f)]
        [Tooltip("Radius within which splats receive full LOD detail.")]
        public float m_FullDetailRadius = 3f;

        /// <summary>
        /// Outer radius of the attenuation band. Splats beyond this distance are culled entirely.
        /// Must be >= m_FullDetailRadius.
        /// </summary>
        [Min(0f)]
        [Tooltip("Beyond this radius splats are culled entirely. Threshold ramps between the two radii.")]
        public float m_AttenuationRadius = 10f;

        static readonly List<GaussianHotspotVolume> s_ActiveHotspots = new();

        /// <summary>All currently active hotspot volumes in the scene.</summary>
        public static IReadOnlyList<GaussianHotspotVolume> ActiveHotspots => s_ActiveHotspots;

        void OnEnable()  => s_ActiveHotspots.Add(this);
        void OnDisable() => s_ActiveHotspots.Remove(this);

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            bool selected = UnityEditor.Selection.Contains(gameObject);

            // Centre marker
            Gizmos.color = new Color(1f, 0.1f, 0.1f, selected ? 1f : 0.6f);
            Gizmos.DrawSphere(transform.position, 0.05f);

            // Full-detail radius — bright red wire sphere
            Gizmos.color = new Color(1f, 0.2f, 0.2f, selected ? 0.9f : 0.35f);
            Gizmos.DrawWireSphere(transform.position, m_FullDetailRadius);

            // Attenuation radius — lighter wire sphere
            Gizmos.color = new Color(1f, 0.6f, 0.4f, selected ? 0.5f : 0.15f);
            Gizmos.DrawWireSphere(transform.position, m_AttenuationRadius);
        }
#endif
    }
}
