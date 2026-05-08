// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Brush selection mode — called from GaussianToolContext.OnToolGUI when BrushModeActive is true.
    // Not an EditorTool; mode is toggled via the inspector Box/Brush buttons.
    static class GaussianBrushSelectTool
    {
        const float k_RadiusScrollSensitivity = 4f;
        const float k_RadiusMin = 5f;
        const float k_RadiusMax = 500f;

        public static bool BrushModeActive { get; set; }

        static float s_BrushRadiusPx = 80f;
        public static float BrushRadiusPx
        {
            get => s_BrushRadiusPx;
            set => s_BrushRadiusPx = Mathf.Clamp(value, k_RadiusMin, k_RadiusMax);
        }

        // Called by GaussianToolContext.OnToolGUI when BrushModeActive is true.
        public static void HandleBrushGUI(GaussianSplatRenderer gs, int id, Event evt, EventType evtType)
        {
            // Scroll wheel resizes brush.
            if (evt.type == EventType.ScrollWheel && !evt.alt)
            {
                s_BrushRadiusPx = Mathf.Clamp(s_BrushRadiusPx - evt.delta.y * k_RadiusScrollSensitivity, k_RadiusMin, k_RadiusMax);
                evt.Use();
                SceneView.RepaintAll();
                GaussianSplatRendererEditor.RepaintAll();
                return;
            }

            switch (evtType)
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(id);
                    break;

                case EventType.MouseDown:
                    if (evt.button == 0 && !evt.alt && HandleUtility.nearestControl == id)
                    {
                        GUIUtility.hotControl = id;
                        ApplyBrush(gs, evt);
                        evt.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id && evt.button == 0)
                    {
                        ApplyBrush(gs, evt);
                        evt.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && evt.button == 0)
                    {
                        GUIUtility.hotControl = 0;
                        evt.Use();
                    }
                    break;

                case EventType.Repaint:
                    DrawBrushGizmo(evt.mousePosition);
                    break;
            }
        }

        static void ApplyBrush(GaussianSplatRenderer gs, Event evt)
        {
            var cam = SceneView.currentDrawingSceneView?.camera;
            if (cam == null) return;

            // Convert GUI mouse position to screen pixel coordinates.
            // GUI space: origin top-left. Screen pixel space: origin bottom-left.
            Vector2 screenPx = HandleUtility.GUIPointToScreenPixelCoordinate(evt.mousePosition);

            gs.EditBrushSelect(screenPx, s_BrushRadiusPx, cam, subtract: evt.control);
            GaussianSplatRendererEditor.RepaintAll();
        }

        static void DrawBrushGizmo(Vector2 guiMousePos)
        {
            // Draw a screen-space circle at the cursor using Handles.BeginGUI overlay.
            bool subtract = Event.current.control;
            Color c = subtract ? new Color(1f, 0.2f, 0.2f, 0.8f) : new Color(0.2f, 0.8f, 1f, 0.8f);

            Handles.BeginGUI();
            var savedColor = GUI.color;
            GUI.color = c;

            // Draw circle via GL or a simple disc outline using EditorGUI trick.
            // Use Handles.DrawWireArc in GUI space by temporarily setting Handles.matrix to identity.
            Handles.EndGUI();

            // Draw in scene space using a fixed-pixel-size handle.
            // We place a world-space disc at the ray's intersection with a plane perpendicular to camera.
            var cam = SceneView.currentDrawingSceneView?.camera;
            if (cam == null) return;

            Ray ray = HandleUtility.GUIPointToWorldRay(guiMousePos);
            // Distance doesn't matter for visual — just pick a neutral depth.
            float depth = 10f;
            Vector3 center = ray.origin + ray.direction * depth;

            // Compute world radius that corresponds to s_BrushRadiusPx pixels at this depth.
            float halfH = depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float worldRadius = halfH * (s_BrushRadiusPx * 2f / cam.pixelHeight);

            Handles.color = c;
            Handles.DrawWireDisc(center, cam.transform.forward, worldRadius);
        }
    }
}
