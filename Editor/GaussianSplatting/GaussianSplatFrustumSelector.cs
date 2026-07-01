using UnityEditor;
using UnityEngine;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Editor
{
    // Selects all GaussianSplatRenderer objects whose world-space bounds are inside the current Scene view frustum.
    static class GaussianSplatFrustumSelector
    {
        [MenuItem("Tools/Gaussian Splats/Select Renderers In Frustum")]
        static void SelectRenderersInFrustum()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
            {
                Debug.LogWarning("No active Scene view camera.");
                return;
            }

            var cam = sceneView.camera;
            var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);
            var renderers = Object.FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None);

            var selected = new System.Collections.Generic.List<GameObject>();
            foreach (var gs in renderers)
            {
                if (!gs.isActiveAndEnabled)
                    continue;
                if (!gs.TryGetLocalBounds(out var localBounds))
                    continue;
                var worldBounds = TransformBounds(gs.transform, localBounds);
                if (GeometryUtility.TestPlanesAABB(frustumPlanes, worldBounds))
                    selected.Add(gs.gameObject);
            }

            Selection.objects = selected.ToArray();
            Debug.Log($"Selected {selected.Count} of {renderers.Length} GaussianSplatRenderer(s) inside the Scene view frustum.");
        }

        static Bounds TransformBounds(Transform t, Bounds localBounds)
        {
            var center = localBounds.center;
            var extents = localBounds.extents;
            Bounds worldBounds = new Bounds(t.TransformPoint(center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = center + new Vector3(
                    (i & 1) == 0 ? -extents.x : extents.x,
                    (i & 2) == 0 ? -extents.y : extents.y,
                    (i & 4) == 0 ? -extents.z : extents.z);
                worldBounds.Encapsulate(t.TransformPoint(corner));
            }
            return worldBounds;
        }
    }
}
