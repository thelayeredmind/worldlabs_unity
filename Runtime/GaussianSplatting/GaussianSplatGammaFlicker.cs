// SPDX-License-Identifier: MIT

using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Add this component alongside GaussianSplatRenderer to drive its gamma value with an
    /// irregular, organic flicker. Only active while the renderer's gamma correction
    /// (m_SplatLinearToGamma) is enabled — otherwise the base gamma value is left untouched.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [AddComponentMenu("Gaussian Splatting/Gaussian Splat Gamma Flicker")]
    public class GaussianSplatGammaFlicker : MonoBehaviour
    {
        [Tooltip("Seeds the noise sample so different components/instances don't flicker in lockstep.")]
        public int seed = 0;

        [Tooltip("How fast the flicker oscillates. Higher = faster.")]
        [Min(0f)]
        public float frequency = 1f;

        [Tooltip("How far the flicker pushes gamma away from its base value, in either direction.")]
        [Min(0f)]
        public float amplitude = 0.2f;

        GaussianSplatRenderer m_Renderer;
        float m_BaseGamma;
        float m_SeedOffset;

        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
            m_BaseGamma = m_Renderer.m_SplatGammaValue;
            m_SeedOffset = seed * 1000.7f;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorApplication.update += EditorTick;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                EditorApplication.update -= EditorTick;
#endif
            if (m_Renderer != null)
                m_Renderer.m_SplatGammaValue = m_BaseGamma;
        }

#if UNITY_EDITOR
        void EditorTick() => EditorApplication.QueuePlayerLoopUpdate();
#endif

        void Update()
        {
            if (m_Renderer == null || !m_Renderer.m_SplatLinearToGamma)
                return;

            float noise = Mathf.PerlinNoise(m_SeedOffset + Time.time * frequency, 0f);
            m_Renderer.m_SplatGammaValue = m_BaseGamma + (noise - 0.5f) * 2f * amplitude;
        }
    }
}
