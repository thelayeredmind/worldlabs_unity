// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatEffectLayer))]
    class GaussianSplatEffectLayerEditor : UnityEditor.Editor
    {
        SerializedProperty m_EffectType;
        SerializedProperty m_Intensity;
        SerializedProperty m_Loop;
        SerializedProperty m_Duration;
        SerializedProperty m_WindDir;
        SerializedProperty m_WaveAmplitude;
        SerializedProperty m_WaveFrequency;
        SerializedProperty m_BlendScale;
        SerializedProperty m_LightWaveAmplitude;
        SerializedProperty m_LightWaveFrequency;
        SerializedProperty m_LightWaveSpeed;
        SerializedProperty m_GlitterDensity;
        SerializedProperty m_BurnDuration;
        SerializedProperty m_GlowColor;

        void OnEnable()
        {
            // Notify the renderer that the layer list may have changed
            var layer = (GaussianSplatEffectLayer)target;
            layer.GetComponent<GaussianSplatting.Runtime.GaussianSplatRenderer>()?.RefreshEffectLayers();
            m_EffectType         = serializedObject.FindProperty("effectType");
            m_Intensity          = serializedObject.FindProperty("intensity");
            m_Loop               = serializedObject.FindProperty("loop");
            m_Duration           = serializedObject.FindProperty("duration");
            m_WindDir            = serializedObject.FindProperty("windDir");
            m_WaveAmplitude      = serializedObject.FindProperty("waveAmplitude");
            m_WaveFrequency      = serializedObject.FindProperty("waveFrequency");
            m_BlendScale         = serializedObject.FindProperty("blendScale");
            m_LightWaveAmplitude = serializedObject.FindProperty("lightWaveAmplitude");
            m_LightWaveFrequency = serializedObject.FindProperty("lightWaveFrequency");
            m_LightWaveSpeed     = serializedObject.FindProperty("lightWaveSpeed");
            m_GlitterDensity     = serializedObject.FindProperty("glitterDensity");
            m_BurnDuration       = serializedObject.FindProperty("burnDuration");
            m_GlowColor          = serializedObject.FindProperty("glowColor");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(m_EffectType);

            var layer = (GaussianSplatEffectLayer)target;
            var effect = (GaussianSplatEffectLayer.EffectType)m_EffectType.enumValueIndex;

            if (effect == GaussianSplatEffectLayer.EffectType.None)
            {
                EditorGUILayout.HelpBox("No effect active. GPU cost: zero.", MessageType.None);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(4);

            // --- Playback controls ---
            DrawPlaybackControls(layer, effect);

            EditorGUILayout.Space(4);

            // --- Effect parameters ---
            if (NeedsIntensity(effect))
                EditorGUILayout.PropertyField(m_Intensity);

            if (effect == GaussianSplatEffectLayer.EffectType.Wind)
            {
                EditorGUILayout.LabelField("Wind", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_WindDir, new GUIContent("Direction"));
            }

            if (effect == GaussianSplatEffectLayer.EffectType.PerlinWave)
            {
                EditorGUILayout.LabelField("Wave", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_WaveAmplitude, new GUIContent("Amplitude"));
                EditorGUILayout.PropertyField(m_WaveFrequency, new GUIContent("Frequency"));
                EditorGUILayout.PropertyField(m_BlendScale,    new GUIContent("Blend Scale"));
            }

            if (effect == GaussianSplatEffectLayer.EffectType.LightWave3D)
            {
                EditorGUILayout.LabelField("Light Wave", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_LightWaveAmplitude, new GUIContent("Amplitude"));
                EditorGUILayout.PropertyField(m_LightWaveFrequency, new GUIContent("Frequency"));
                EditorGUILayout.PropertyField(m_LightWaveSpeed,     new GUIContent("Speed"));
            }

            if (NeedsGlitter(effect))
            {
                EditorGUILayout.LabelField("Glitter", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_GlitterDensity, new GUIContent("Density"));
            }

            if (effect == GaussianSplatEffectLayer.EffectType.GlowDissolve)
            {
                EditorGUILayout.LabelField("Burn", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_BurnDuration, new GUIContent("Burn fraction of cycle"));
                EditorGUILayout.PropertyField(m_GlowColor,    new GUIContent("Color"));
            }

            serializedObject.ApplyModifiedProperties();
        }

        void DrawPlaybackControls(GaussianSplatEffectLayer layer, GaussianSplatEffectLayer.EffectType effect)
        {
            bool isAnimated = IsAnimated(effect);

            EditorGUILayout.LabelField("Playback", EditorStyles.boldLabel);

            if (isAnimated)
            {
                EditorGUILayout.PropertyField(m_Loop,     new GUIContent("Loop"));
                EditorGUILayout.PropertyField(m_Duration, new GUIContent("Duration (s)"));
            }

            // Show both real elapsed time and the scaled shader time so it's easy to
            // correlate what the shader receives with what you see on screen.
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField(new GUIContent("Time (s)", "Real elapsed seconds"), layer.effectTime);
                EditorGUILayout.FloatField(new GUIContent("Shader t", "Value received by the effect shader"), layer.shaderTime);
            }

            EditorGUILayout.BeginHorizontal();

            if (isAnimated)
            {
                bool playing = layer.isPlaying;
                string playPauseLabel = playing ? "❙❙ Pause" : "▶ Play";
                if (GUILayout.Button(playPauseLabel))
                {
                    if (playing) layer.Pause();
                    else         layer.Play();
                }
            }

            if (GUILayout.Button("↺ Restart"))
                layer.Restart();

            EditorGUILayout.EndHorizontal();

            // Continuously repaint while playing so the time field updates
            if (layer.isPlaying)
                Repaint();
        }

        // Effects whose time input meaningfully drives an animation arc
        // (as opposed to continuous oscillations that look the same at any time offset)
        static bool IsAnimated(GaussianSplatEffectLayer.EffectType e)
        {
            switch (e)
            {
                case GaussianSplatEffectLayer.EffectType.Flare:
                case GaussianSplatEffectLayer.EffectType.Disintegrate:
                case GaussianSplatEffectLayer.EffectType.MagicReveal:
                case GaussianSplatEffectLayer.EffectType.Spread:
                case GaussianSplatEffectLayer.EffectType.Unroll:
                case GaussianSplatEffectLayer.EffectType.Twister:
                case GaussianSplatEffectLayer.EffectType.Rain:
                case GaussianSplatEffectLayer.EffectType.FlyingDissolve:
                case GaussianSplatEffectLayer.EffectType.GlowDissolve:
                    return true;
                default:
                    return false;
            }
        }

        static bool NeedsIntensity(GaussianSplatEffectLayer.EffectType e)
        {
            switch (e)
            {
                case GaussianSplatEffectLayer.EffectType.DeepMeditation:
                case GaussianSplatEffectLayer.EffectType.LightWave3D:
                case GaussianSplatEffectLayer.EffectType.Disintegrate:
                case GaussianSplatEffectLayer.EffectType.Wind:
                case GaussianSplatEffectLayer.EffectType.PerlinWave:
                case GaussianSplatEffectLayer.EffectType.Twister:
                case GaussianSplatEffectLayer.EffectType.Rain:
                case GaussianSplatEffectLayer.EffectType.FlyingDissolve:
                case GaussianSplatEffectLayer.EffectType.GlowDissolve:
                case GaussianSplatEffectLayer.EffectType.RadialExpansion:
                    return true;
                default:
                    return false;
            }
        }

        // Effects that have an optional light wave accent (separate from LightWave3D which is the primary)
        static bool NeedsLightWave(GaussianSplatEffectLayer.EffectType e)
        {
            switch (e)
            {
                case GaussianSplatEffectLayer.EffectType.PerlinWave:
                case GaussianSplatEffectLayer.EffectType.Twister:
                case GaussianSplatEffectLayer.EffectType.Rain:
                    return true;
                default:
                    return false;
            }
        }

        static bool NeedsGlitter(GaussianSplatEffectLayer.EffectType e)
        {
            switch (e)
            {
                case GaussianSplatEffectLayer.EffectType.Glitter:
                case GaussianSplatEffectLayer.EffectType.GlitterGalaxy:
                case GaussianSplatEffectLayer.EffectType.FlyingDissolve:
                    return true;
                default:
                    return false;
            }
        }
    }
}
