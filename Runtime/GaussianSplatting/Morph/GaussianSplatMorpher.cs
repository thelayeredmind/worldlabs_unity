// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Drives a morphing blend between two <see cref="GaussianSplatAsset"/>s using a precomputed
    /// <see cref="GaussianMorphMap"/>. Attach alongside a <see cref="GaussianSplatRenderer"/>.
    ///
    /// The MorphMap is located automatically from the project when both assets are assigned.
    /// Exposes <see cref="t"/> (0 = AssetLeft, 1 = AssetRight) for Timeline animation or scripting.
    /// Optional auto-play linearly drives t from 0 to 1 over <see cref="m_Duration"/> seconds.
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [AddComponentMenu("Gaussian Splatting/Gaussian Splat Morpher")]
    public class GaussianSplatMorpher : MonoBehaviour
    {
        [SerializeField] GaussianSplatAsset m_AssetLeft;
        [SerializeField] GaussianSplatAsset m_AssetRight;

        [Tooltip("Precomputed correspondence. Located automatically when both assets are assigned; assign manually to override.")]
        [SerializeField] GaussianMorphMap m_MorphMap;

        [SerializeField, Range(0f, 1f)]
        float m_T;

        [Header("Auto-play")]
        [Tooltip("Automatically animate t from 0 to 1 at runtime.")]
        [SerializeField] bool m_AutoPlay;

        [Tooltip("Seconds to travel from t=0 to t=1.")]
        [SerializeField, Min(0.01f)] float m_Duration = 2f;

        [Tooltip("Loop the animation when it reaches the end.")]
        [SerializeField] bool m_Loop = true;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Blend value. 0 = AssetLeft, 1 = AssetRight. Animatable via Timeline.</summary>
        public float t
        {
            get => m_T;
            set => m_T = Mathf.Clamp01(value);
        }

        public GaussianSplatAsset assetLeft  => m_AssetLeft;
        public GaussianSplatAsset assetRight => m_AssetRight;
        public GaussianMorphMap   morphMap   => m_MorphMap;

        /// <summary>Swap Left and Right assets (and MorphMap direction) without rebuilding correspondence.</summary>
        public void SwapAssets()
        {
            (m_AssetLeft, m_AssetRight) = (m_AssetRight, m_AssetLeft);
            m_T = 1f - m_T;
        }

        // ── Unity messages ────────────────────────────────────────────────────

        void Update()
        {
            if (!m_AutoPlay) return;

            m_T += Time.deltaTime / m_Duration;
            if (m_T >= 1f)
            {
                m_T = m_Loop ? 0f : 1f;
                if (!m_Loop) m_AutoPlay = false;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Searches the AssetDatabase for a MorphMap whose name matches the canonical
        /// "{left}_{right}_MorphMap" pattern produced by <see cref="GaussianMorphMapBuilderWindow"/>.
        /// Returns null if not found.
        /// </summary>
        public static GaussianMorphMap FindMorphMap(GaussianSplatAsset left, GaussianSplatAsset right)
        {
            if (left == null || right == null) return null;

            string expected = $"{left.name}_{right.name}_MorphMap";
            string[] guids  = UnityEditor.AssetDatabase.FindAssets($"t:GaussianMorphMap {expected}");
            if (guids.Length == 0) return null;

            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GaussianMorphMap>(path);
        }
#endif
    }
}
