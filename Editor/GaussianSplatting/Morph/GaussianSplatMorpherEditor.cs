// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatMorpher))]
    public class GaussianSplatMorpherEditor : UnityEditor.Editor
    {
        SerializedProperty m_AssetLeft;
        SerializedProperty m_AssetRight;
        SerializedProperty m_MorphMap;
        SerializedProperty m_T;
        SerializedProperty m_AutoPlay;
        SerializedProperty m_Duration;
        SerializedProperty m_Loop;

        void OnEnable()
        {
            m_AssetLeft  = serializedObject.FindProperty("m_AssetLeft");
            m_AssetRight = serializedObject.FindProperty("m_AssetRight");
            m_MorphMap   = serializedObject.FindProperty("m_MorphMap");
            m_T          = serializedObject.FindProperty("m_T");
            m_AutoPlay   = serializedObject.FindProperty("m_AutoPlay");
            m_Duration   = serializedObject.FindProperty("m_Duration");
            m_Loop       = serializedObject.FindProperty("m_Loop");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var morpher = (GaussianSplatMorpher)target;

            // ── Assets ───────────────────────────────────────────────────────

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(m_AssetLeft,  new GUIContent("Asset Left  (t=0)"));
            EditorGUILayout.PropertyField(m_AssetRight, new GUIContent("Asset Right (t=1)"));
            bool assetsChanged = EditorGUI.EndChangeCheck();

            // Swap button
            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("⇅  Swap Source / Target", GUILayout.Width(180)))
                {
                    Undo.RecordObject(target, "Swap Morph Assets");
                    morpher.SwapAssets();
                    serializedObject.Update();
                    EditorUtility.SetDirty(target);
                }
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(4);

            // ── MorphMap ─────────────────────────────────────────────────────

            EditorGUILayout.PropertyField(m_MorphMap);

            // Auto-locate when assets change or map is missing
            if (assetsChanged || (m_MorphMap.objectReferenceValue == null))
            {
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                var found = GaussianSplatMorpher.FindMorphMap(morpher.assetLeft, morpher.assetRight);
                if (found != null && found != morpher.morphMap)
                {
                    m_MorphMap.objectReferenceValue = found;
                    serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                }
            }

            if (m_MorphMap.objectReferenceValue == null && morpher.assetLeft != null && morpher.assetRight != null)
            {
                EditorGUILayout.HelpBox(
                    "No MorphMap found for these assets. Use Tools → Gaussian Splats → Build Morph Map to create one.",
                    MessageType.Warning);
            }

            // ── Interpolation ────────────────────────────────────────────────

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Interpolation", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_T, new GUIContent("t  (0 = Left, 1 = Right)"));

            // ── Auto-play ────────────────────────────────────────────────────

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Auto-play (optional)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_AutoPlay, new GUIContent("Enable"));
            if (m_AutoPlay.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_Duration, new GUIContent("Duration (s)"));
                EditorGUILayout.PropertyField(m_Loop,     new GUIContent("Loop"));
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
