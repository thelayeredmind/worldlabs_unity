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

        [Header("Playback")]
        [Tooltip("Loop the effect. When off, playback stops at the end of duration.")]
        public bool loop = true;

        [Tooltip("How many real seconds one full effect cycle takes. Longer = slower effect.")]
        [Min(0.1f)]
        public float duration = 5f;

        [Header("Wind / Wave")]
        public Vector3 windDir = Vector3.right;

        [Range(0f, 2f)]
        public float waveAmplitude = 0.3f;

        [Range(0f, 10f)]
        public float waveFrequency = 1f;

        [Range(0f, 2f)]
        public float blendScale = 1f;

        [Header("Light Wave (LightWave3D only)")]
        [Range(0f, 0.5f)]
        public float lightWaveAmplitude = 0.3f;

        [Range(0f, 5f)]
        public float lightWaveFrequency = 1f;

        [Range(0f, 2f)]
        public float lightWaveSpeed = 1f;

        [Header("Glitter")]
        [Range(0f, 1f)]
        public float glitterDensity = 0.3f;

        [Header("Glow Dissolve")]
        [Tooltip("Fraction [0–1] of the cycle duration that one burn takes.")]
        [Range(0.05f, 1f)]
        public float burnDuration = 0.3f;

        [Tooltip("Emissive colour of the burn.")]
        public Color glowColor = new Color(2f, 1f, 0.2f);

        // Per-effect shader time at end of one full cycle, tuned to each effect's authored range.
        // Continuous oscillation effects use a smaller window (they repeat naturally).
        // One-shot arc effects use the range where their smoothstep/exp functions complete.
        static readonly float[] k_EffectTimeScale = {
            0f,  // None
            8f,  // DeepMeditation   — fractal oscillation
            8f,  // LightWave3D      — continuous sin
            8f,  // Flare            — sin oscillation
            8f,  // Disintegrate     — sin oscillation
            16f, // Wind             — needs ~2.5 full sin cycles to feel like wind
            8f,  // PerlinWave       — continuous noise travel
            20f, // MagicReveal      — smoothstep arc, needs 0→20
            8f,  // Spread           — t*t*0.4 arc
            6f,  // Unroll           — exp(-t) decay arc
            15f, // Twister          — t*t*0.1 buildup arc
            12f, // Rain             — drop arc, slightly less than Twister
            8f,  // Glitter          — continuous twinkling
            8f,  // GlitterGalaxy    — continuous
            8f,  // FlyingDissolve   — staggered drift
            8f,  // GlowDissolve     — burn fraction of t
            8f,  // RadialExpansion  — intensity-driven
        };

        // -----------------------------------------------------------------
        // Playback state — not serialized, resets on domain reload (intentional)

        [System.NonSerialized] public bool isPlaying = true;

        // Read-only from outside; controlled via Play/Pause/Restart
        public float effectTime => m_EffectTime;
        public float shaderTime
        {
            get
            {
                int idx = (int)effectType;
                float scale = (idx >= 0 && idx < k_EffectTimeScale.Length) ? k_EffectTimeScale[idx] : 8f;
                return (m_EffectTime / duration) * scale;
            }
        }

        // -----------------------------------------------------------------
        // GPU struct — must match SplatEffectParams in GsplatEffects.hlsl (5x float4 = 80 bytes)
        // Layout: [effectType(int), effectTime, intensity, waveAmplitude]
        //         [waveFrequency, blendScale, lightWaveAmplitude, lightWaveFrequency]
        //         [lightWaveSpeed, glitterDensity, burnDuration, _pad0]
        //         [windDir.xyz, _pad1]
        //         [glowColor.xyz, _pad2]
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct ShaderParams
        {
            public int   effectType;
            public float effectTime;
            public float intensity;
            public float waveAmplitude;

            public float waveFrequency;
            public float blendScale;
            public float lightWaveAmplitude;
            public float lightWaveFrequency;

            public float lightWaveSpeed;
            public float glitterDensity;
            public float burnDuration;
            public float _pad0;

            public Vector3 windDir;
            public float   _pad1;

            public Vector3 glowColor;
            public float   _pad2;
        }

        internal static readonly int s_EffectLayers     = Shader.PropertyToID("_EffectLayers");
        internal static readonly int s_EffectLayerCount = Shader.PropertyToID("_EffectLayerCount");

        float m_EffectTime;

        public void Restart()
        {
            m_EffectTime = 0f;
            isPlaying = true;
        }

        public void Play()  => isPlaying = true;
        public void Pause() => isPlaying = false;

        // Drive time externally (Timeline, scripted animation).
        // Disables internal advance for this frame — caller owns the clock.
        public void SetTime(float t) => m_EffectTime = t;

        void OnEnable()
        {
            m_EffectTime = 0f;
            isPlaying = true;
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
            if (!isPlaying) return;

            m_EffectTime += Time.deltaTime;

            if (!loop && m_EffectTime >= duration)
            {
                m_EffectTime = duration;
                isPlaying = false;
            }
            else if (loop && m_EffectTime >= duration)
            {
                m_EffectTime -= duration;
            }
        }

        // Fill a ShaderParams struct for upload into the compute buffer.
        internal ShaderParams FillShaderParams()
        {
            return new ShaderParams
            {
                effectType        = (int)effectType,
                effectTime        = shaderTime,
                intensity         = intensity,
                waveAmplitude     = waveAmplitude,
                waveFrequency     = waveFrequency,
                blendScale        = blendScale,
                lightWaveAmplitude = lightWaveAmplitude,
                lightWaveFrequency = lightWaveFrequency,
                lightWaveSpeed    = lightWaveSpeed,
                glitterDensity    = glitterDensity,
                burnDuration      = burnDuration,
                windDir           = windDir,
                glowColor         = new Vector3(glowColor.r, glowColor.g, glowColor.b),
            };
        }
    }
}
