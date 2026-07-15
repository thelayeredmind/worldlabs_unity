// SPDX-License-Identifier: MIT
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Scene-view overlay window: input vs post-cull-compute vs post-fragment (actually-shaded) splat
    // counts, summed across all active GaussianSplatRenderer instances in the scene. Standalone window
    // (not a GaussianSplatURPFeature checkbox) so it needs no reference to the URP-gated render feature type.
    class GaussianSplatDebugOverlayWindow : EditorWindow
    {
        const string kMenuPath = "Tools/Gaussian Splats/Debug/Splat Count Overlay";

        [MenuItem(kMenuPath)]
        static void Open() => GetWindow<GaussianSplatDebugOverlayWindow>("Splat Count Overlay");

        bool m_ShowOverlay = true;

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            GaussianSplatRenderer.DebugCountersEnabled = m_ShowOverlay;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            GaussianSplatRenderer.DebugCountersEnabled = false;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            m_ShowOverlay = EditorGUILayout.Toggle("Show Scene View Overlay", m_ShowOverlay);
            if (EditorGUI.EndChangeCheck())
                GaussianSplatRenderer.DebugCountersEnabled = m_ShowOverlay;

            EditorGUILayout.HelpBox(
                "Adds a per-frame GPU readback while enabled — leave off unless actively debugging.",
                MessageType.Info);
        }

        static void OnSceneGUI(SceneView sceneView)
        {
            if (!GaussianSplatRenderer.DebugCountersEnabled)
                return;

            int totalInput = 0;
            uint totalPostCull = 0;
            foreach (var r in Object.FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None))
            {
                var (input, postCull) = r.DebugSplatCounts;
                totalInput += input;
                totalPostCull += postCull;
            }

            float cullPct = totalInput > 0 ? 100f * totalPostCull / totalInput : 0f;

            Handles.BeginGUI();
            var rect = new Rect(10, sceneView.position.height - 110, 260, 54);
            GUI.Box(rect, GUIContent.none);
            var labelRect = new Rect(rect.x + 8, rect.y + 4, rect.width - 16, rect.height - 8);
            GUI.Label(labelRect,
                $"Splat Counts\n" +
                $"Input:     {totalInput}\n" +
                $"Post-cull: {totalPostCull}  ({cullPct:F1}%)");
            Handles.EndGUI();
        }
    }
}
