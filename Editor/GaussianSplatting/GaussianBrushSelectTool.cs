// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Brush selection mode — driven from GaussianToolContext.OnToolGUI when BrushModeActive is true.
    static class GaussianBrushSelectTool
    {
        const float k_ScrollSensitivity = 4f;
        const float k_PxMin = 5f;
        const float k_PxMax = 500f;
        const float k_WorldMin = 0.01f;
        const float k_WorldMax = 50f;

        public static bool BrushModeActive  { get; set; }
        public static bool WorldSpaceMode   { get; set; }   // false = screen-space, true = world-space

        static float s_RadiusPx    = 80f;
        static float s_RadiusWorld = 2f;

        public static float BrushRadiusPx
        {
            get => s_RadiusPx;
            set => s_RadiusPx = Mathf.Clamp(value, k_PxMin, k_PxMax);
        }

        public static float BrushRadiusWorld
        {
            get => s_RadiusWorld;
            set => s_RadiusWorld = Mathf.Clamp(value, k_WorldMin, k_WorldMax);
        }

        // Called by GaussianToolContext.OnToolGUI when BrushModeActive is true.
        public static void HandleBrushGUI(GaussianSplatRenderer gs, int id, Event evt, EventType evtType)
        {
            if (evt.type == EventType.ScrollWheel && !evt.alt)
            {
                if (WorldSpaceMode)
                    s_RadiusWorld = Mathf.Clamp(s_RadiusWorld * (1f - evt.delta.y * 0.05f), k_WorldMin, k_WorldMax);
                else
                    s_RadiusPx = Mathf.Clamp(s_RadiusPx - evt.delta.y * k_ScrollSensitivity, k_PxMin, k_PxMax);
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
                    DrawBrushGizmo(gs, evt.mousePosition);
                    break;
            }
        }

        static void ApplyBrush(GaussianSplatRenderer gs, Event evt)
        {
            var cam = SceneView.currentDrawingSceneView?.camera;
            if (cam == null) return;
            bool subtract = evt.control;

            if (WorldSpaceMode)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                Vector3 center = GetBrushCenter(gs, ray);
                gs.EditBrushSelectWorld(center, s_RadiusWorld, cam, subtract);
            }
            else
            {
                Vector2 screenPx = HandleUtility.GUIPointToScreenPixelCoordinate(evt.mousePosition);
                gs.EditBrushSelect(screenPx, s_RadiusPx, cam, subtract);
            }
            GaussianSplatRendererEditor.RepaintAll();
        }

        // Sphere center = first splat hit by the mouse ray within a screen-pixel-derived hit radius.
        // Falls back to splat GO center depth if nothing is hit.
        static Vector3 GetWorldCenter(GaussianSplatRenderer gs, Camera cam, Vector2 guiMousePos)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(guiMousePos);
            return GetBrushCenter(gs, ray);
        }

        // Returns the world-space center where the brush sphere should sit.
        // Travels to the AABB far wall, stopping and snapping to the first splat hit.
        static Vector3 GetBrushCenter(GaussianSplatRenderer gs, Ray ray)
        {
            var asset = gs.asset;
            Bounds worldBounds = GaussianSplatRendererEditor.TransformBounds(gs.transform,
                new Bounds((asset.boundsMin + asset.boundsMax) * 0.5f, asset.boundsMax - asset.boundsMin));

            float maxT;
            if (worldBounds.Contains(ray.origin))
                maxT = Mathf.Max(RayAABBFarT(ray, worldBounds), 0.3f);
            else
            {
                float d = Vector3.Dot(worldBounds.center - ray.origin, ray.direction);
                maxT = Mathf.Max(d, 0.5f);
            }

            if (gs.EditFindBrushStopT(ray, s_RadiusWorld, maxT, out _, out Vector3 snapCenter))
                return snapCenter;

            return ray.origin + ray.direction * maxT;
        }

        // Returns the far-side t of a ray-AABB intersection (assumes ray origin is inside the box).
        static float RayAABBFarT(Ray ray, Bounds b)
        {
            float tmax = float.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? ray.origin.x : i == 1 ? ray.origin.y : ray.origin.z;
                float d = i == 0 ? ray.direction.x : i == 1 ? ray.direction.y : ray.direction.z;
                float bmin = i == 0 ? b.min.x : i == 1 ? b.min.y : b.min.z;
                float bmax = i == 0 ? b.max.x : i == 1 ? b.max.y : b.max.z;
                if (Mathf.Abs(d) > 1e-6f)
                {
                    float t1 = (bmin - o) / d;
                    float t2 = (bmax - o) / d;
                    tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));
                }
            }
            return tmax < 0 ? 1f : tmax;
        }

        static void DrawBrushGizmo(GaussianSplatRenderer gs, Vector2 guiMousePos)
        {
            var cam = SceneView.currentDrawingSceneView?.camera;
            if (cam == null) return;

            bool subtract = Event.current.control;
            Handles.color = subtract ? new Color(1f, 0.2f, 0.2f, 0.8f) : new Color(0.2f, 0.8f, 1f, 0.8f);

            if (WorldSpaceMode)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(guiMousePos);
                Vector3 center = GetBrushCenter(gs, ray);
                Handles.color = subtract ? new Color(1f, 0.2f, 0.2f, 0.8f) : new Color(0.2f, 0.8f, 1f, 0.8f);
                Handles.DrawWireDisc(center, Vector3.up,            s_RadiusWorld);
                Handles.DrawWireDisc(center, Vector3.right,         s_RadiusWorld);
                Handles.DrawWireDisc(center, cam.transform.forward, s_RadiusWorld);
            }
            else
            {
                // Screen-space: draw a disc facing the camera at a neutral depth, sized to match pixel radius.
                Ray ray = HandleUtility.GUIPointToWorldRay(guiMousePos);
                float depth = 10f;
                Vector3 center = ray.origin + ray.direction * depth;
                float halfH = depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float worldRadius = halfH * (s_RadiusPx * 2f / cam.pixelHeight);
                Handles.DrawWireDisc(center, cam.transform.forward, worldRadius);
            }
        }
    }
}
