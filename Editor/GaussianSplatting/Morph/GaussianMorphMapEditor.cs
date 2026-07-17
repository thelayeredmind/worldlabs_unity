// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianMorphMap))]
    public class GaussianMorphMapEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var map = (GaussianMorphMap)target;

            EditorGUILayout.LabelField("Splat Counts", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Left / Right", $"{map.splatCountLeft} / {map.splatCountRight}");

            int total = Mathf.Max(map.MatchedCount + (map.unmatchedLeft?.Length ?? 0) + (map.unmatchedRight?.Length ?? 0), 1);
            EditorGUILayout.LabelField("Matched / Unmatched",
                $"{map.MatchedCount} / {(map.unmatchedLeft?.Length ?? 0) + (map.unmatchedRight?.Length ?? 0)} ({map.MatchedCount * 100f / total:F1}% matched)");

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Build Settings", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.EnumPopup("Algorithm", map.builtWithAlgorithm);
                EditorGUILayout.Toggle("Force Match Pass", map.builtWithForceMatchPass);
                EditorGUILayout.Slider("Color Weight", map.builtWithColorWeight, 0f, 1f);
                if (map.builtWithAlgorithm == CorrespondenceAlgorithm.SpatialProbes)
                    EditorGUILayout.Slider("Probe Accuracy", map.builtWithProbeAccuracy, 0.01f, 1f);
            }
        }
    }
}
