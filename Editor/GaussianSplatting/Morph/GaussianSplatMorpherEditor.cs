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
        SerializedProperty m_DebugTintUnmatched;

        void OnEnable()
        {
            m_AssetLeft  = serializedObject.FindProperty("m_AssetLeft");
            m_AssetRight = serializedObject.FindProperty("m_AssetRight");
            m_MorphMap   = serializedObject.FindProperty("m_MorphMap");
            m_T          = serializedObject.FindProperty("m_T");
            m_DebugTintUnmatched = serializedObject.FindProperty("m_DebugTintUnmatched");
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

            EditorGUILayout.Space(2);
            using (new EditorGUI.DisabledScope(morpher.assetLeft == null || morpher.assetRight == null))
            {
                if (GUILayout.Button("Build Morph Map…"))
                    GaussianMorphMapBuilderWindow.Open(morpher.assetLeft, morpher.assetRight);
            }

            EditorGUILayout.Space(4);

            // ── MorphMap ─────────────────────────────────────────────────────

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(m_MorphMap);
            bool morphMapFieldChanged = EditorGUI.EndChangeCheck();

            // Auto-locate only when the assets themselves changed — NOT just because the field is
            // null, since an empty MorphMap is now a valid deliberate choice (all-unmatched/pure-fade
            // baseline). Re-locating on every repaint would silently overwrite that choice.
            if (assetsChanged)
            {
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                var found = GaussianSplatMorpher.FindMorphMap(morpher.assetLeft, morpher.assetRight);
                if (found != null && found != morpher.morphMap)
                {
                    m_MorphMap.objectReferenceValue = found;
                    serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                    morphMapFieldChanged = true;
                }
            }

            if (m_MorphMap.objectReferenceValue == null && morpher.assetLeft != null && morpher.assetRight != null)
            {
                EditorGUILayout.HelpBox(
                    "No MorphMap found for these assets — all splats will be treated as unmatched " +
                    "(pure fade, no lerp). Use Tools → Gaussian Splats → Build Morph Map to create one.",
                    MessageType.Info);
            }

            // Reassigning the MorphMap field only updates the serialized reference — none of the
            // derived GPU state (matched/unmatched counts, index buffers) is rebuilt from it
            // automatically. Without this, the morpher keeps running on the PREVIOUS map's buffers
            // after you pick a different one in the inspector, silently comparing the wrong data.
            if (morphMapFieldChanged)
            {
                serializedObject.ApplyModifiedProperties();
                morpher.SetAssets(morpher.assetLeft, morpher.assetRight, morpher.morphMap);
                serializedObject.Update();
            }

            // ── Interpolation ────────────────────────────────────────────────

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Interpolation", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_T, new GUIContent("t  (0 = Left, 1 = Right)"));
            EditorGUILayout.PropertyField(m_DebugTintUnmatched, new GUIContent("Debug Tint Unmatched", "Tint unmatched splats solid red (fading out) / blue (fading in) to visually isolate the fade path from matched-pair interpolation."));

            serializedObject.ApplyModifiedProperties();
        }
    }
}
