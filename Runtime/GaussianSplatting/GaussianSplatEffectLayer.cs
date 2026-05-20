// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Add this component alongside GaussianSplatRenderer to drive one of the 16 vertex-stage
    /// splat effects ported from gsplat-unity-vfx (CocoLinux0101 / sparkjsdev).
    ///
    /// Effect parameters are injected into the CSCalcViewData compute dispatch each frame.
    /// Setting EffectType to None is zero-cost on the GPU (the shader early-exits).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [AddComponentMenu("Gaussian Splatting/Gaussian Splat Effect Layer")]
    public class GaussianSplatEffectLayer : MonoBehaviour
    {
        public enum EffectType
        {
            None              = 0,
            DeepMeditation    = 1,
            LightWave3D       = 2,
            Flare             = 3,
            Disintegrate      = 4,
            Wind              = 5,
            PerlinWave        = 6,
            MagicReveal       = 7,
            Spread            = 8,
            Unroll            = 9,
            Twister           = 10,
            Rain              = 11,
            Glitter           = 12,
            GlitterGalaxy     = 13,
            FlyingDissolve    = 14,
            GlowDissolve      = 15,
            RadialExpansion   = 16,
        }

        [Header("Effect")]
        [Tooltip("Which effect to apply. None = zero GPU cost.")]
        public EffectType effectType = EffectType.None;

        [Range(0f, 2f)]
        public float intensity = 1f;

        [Tooltip("Override effect time. When negative, uses Time.time automatically.")]
        public float timeOverride = -1f;

        [Header("Wind / Wave")]
        public Vector3 windDir = Vector3.right;

        [Range(0f, 2f)]
        public float waveAmplitude = 0.3f;

        [Range(0f, 10f)]
        public float waveFrequency = 1f;

        [Range(0f, 5f)]
        public float waveSpeed = 1f;

        [Range(0f, 2f)]
        public float blendScale = 1f;

        [Header("Light Wave")]
        [Range(0f, 2f)]
        public float lightWaveAmplitude = 0.3f;

        [Range(0f, 10f)]
        public float lightWaveFrequency = 1f;

        [Range(0f, 5f)]
        public float lightWaveSpeed = 1f;

        [Header("Glitter / Dissolve")]
        [Range(0f, 1f)]
        public float glitterDensity = 0.3f;

        [Range(0f, 2f)]
        public float dissolveDriftSpeed = 0.3f;

        [Range(0.1f, 5f)]
        public float burnDuration = 2f;

        // -----------------------------------------------------------------
        // Internal

        internal static readonly int EffectTypeShaderProp = Shader.PropertyToID("_EffectType");
        static readonly int s_EffectType          = EffectTypeShaderProp;
        static readonly int s_EffectTime          = Shader.PropertyToID("_EffectTime");
        static readonly int s_EffectIntensity     = Shader.PropertyToID("_EffectIntensity");
        static readonly int s_EffectWindDir       = Shader.PropertyToID("_EffectWindDir");
        static readonly int s_WaveAmplitude       = Shader.PropertyToID("_WaveAmplitude");
        static readonly int s_WaveFrequency       = Shader.PropertyToID("_WaveFrequency");
        static readonly int s_WaveSpeed           = Shader.PropertyToID("_WaveSpeed");
        static readonly int s_BlendScale          = Shader.PropertyToID("_BlendScale");
        static readonly int s_LightWaveAmplitude  = Shader.PropertyToID("_LightWaveAmplitude");
        static readonly int s_LightWaveFrequency  = Shader.PropertyToID("_LightWaveFrequency");
        static readonly int s_LightWaveSpeed      = Shader.PropertyToID("_LightWaveSpeed");
        static readonly int s_GlitterDensity      = Shader.PropertyToID("_GlitterDensity");
        static readonly int s_DissolveDriftSpeed  = Shader.PropertyToID("_DissolveDriftSpeed");
        static readonly int s_BurnDuration        = Shader.PropertyToID("_BurnDuration");

        float m_EffectTime;

        void OnEnable()
        {
            m_EffectTime = 0f;
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
        }

#if UNITY_EDITOR
        // Drives continuous editor repaint so time-based effects animate in edit mode.
        // Update() is called by the queued player loop, which advances m_EffectTime.
        void EditorTick() => EditorApplication.QueuePlayerLoopUpdate();
#endif

        void Update()
        {
            if (timeOverride < 0f)
                m_EffectTime += Time.deltaTime;
        }

        /// <summary>
        /// Called by GaussianSplatRenderer.CalcViewData to inject effect uniforms into the
        /// compute command buffer before the CSCalcViewData dispatch.
        /// </summary>
        internal void SetEffectParams(CommandBuffer cmb, ComputeShader cs)
        {
            float t = timeOverride >= 0f ? timeOverride : m_EffectTime;

            cmb.SetComputeIntParam   (cs, s_EffectType,         (int)effectType);
            cmb.SetComputeFloatParam (cs, s_EffectTime,         t);
            cmb.SetComputeFloatParam (cs, s_EffectIntensity,    intensity);
            cmb.SetComputeVectorParam(cs, s_EffectWindDir,      windDir);
            cmb.SetComputeFloatParam (cs, s_WaveAmplitude,      waveAmplitude);
            cmb.SetComputeFloatParam (cs, s_WaveFrequency,      waveFrequency);
            cmb.SetComputeFloatParam (cs, s_WaveSpeed,          waveSpeed);
            cmb.SetComputeFloatParam (cs, s_BlendScale,         blendScale);
            cmb.SetComputeFloatParam (cs, s_LightWaveAmplitude, lightWaveAmplitude);
            cmb.SetComputeFloatParam (cs, s_LightWaveFrequency, lightWaveFrequency);
            cmb.SetComputeFloatParam (cs, s_LightWaveSpeed,     lightWaveSpeed);
            cmb.SetComputeFloatParam (cs, s_GlitterDensity,     glitterDensity);
            cmb.SetComputeFloatParam (cs, s_DissolveDriftSpeed, dissolveDriftSpeed);
            cmb.SetComputeFloatParam (cs, s_BurnDuration,       burnDuration);
        }

        /// <summary>
        /// Clears effect type to 0 on the compute shader so a disabled layer has no GPU cost.
        /// </summary>
        internal void ClearEffectParams(CommandBuffer cmb, ComputeShader cs)
        {
            cmb.SetComputeIntParam(cs, s_EffectType, 0);
        }
    }
}
