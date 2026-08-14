// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        // Tick() used to be driven purely from "some registered renderer's own Update()" — this
        // deadlocks the activation-staggering queue below: Unity calls OnEnable() for every
        // GameObject in an Activation Track cascade BEFORE any of that frame's Update() calls run,
        // so if all N renderers self-deactivate via ClaimActivationSlot() in the same cascade,
        // NONE of them are left active to ever call Update()/Tick() again — the queue is stuck
        // forever with nothing to drive it (confirmed live via reflection query, 2026-08-13: 9/9
        // renderers left inactive, IsReady incorrectly true, queue non-empty, Tick() never
        // advancing). Fix: a hidden, DontDestroyOnLoad driver GameObject/MonoBehaviour, created
        // lazily on first use and never itself part of any content Activation Track, whose only
        // job is calling Tick() every Update() — independent of whether any splat renderer is
        // currently active. See performance.session.md 2026-08-13.
        public static int DiagDriverUpdateCount;

        sealed class Driver : MonoBehaviour
        {
            public void Update()
            {
                DiagDriverUpdateCount++;
                instance.Tick(Time.frameCount);
            }
        }

        void EnsureDriver()
        {
            if (m_Driver != null)
                return;
            // HideFlags.HideAndDontSave was tried first but leaves the GameObject outside any
            // valid scene (confirmed live: go.scene.IsValid() == false) — Unity's Update() dispatch
            // is scene-driven, so a HideAndDontSave object's Update() is simply never called,
            // silently. HideInHierarchy alone (no DontSave) keeps it out of the Hierarchy window
            // without breaking scene membership; DontDestroyOnLoad moves it to the persistent
            // scene so it survives scene loads/unloads across the whole additive stack.
            var go = new GameObject("GaussianSplatRenderSystem Driver") { hideFlags = HideFlags.HideInHierarchy };
            if (Application.isPlaying)
                UnityEngine.Object.DontDestroyOnLoad(go);
            m_Driver = go.AddComponent<Driver>();
        }
        Driver m_Driver;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;

        // Cross-renderer budgeted-load scheduler. Replaces each renderer starting its own
        // UpdateRessourcesBudgeted() coroutine independently in the same activation frame
        // (which stacked N renderers' expensive first segments together, see
        // masking.session.md 2026-08-10). Renderers enqueue a request instead of starting
        // their coroutine directly; Tick() (driven once/frame from any registered renderer's
        // own Update(), deduped by frame number) starts at most one newly-queued renderer per
        // frame, and only while fewer than BudgetedLoadConcurrentMax are already loading.
        // Both knobs live in GaussianSplatSchedulerSettings (a Resources-loaded asset,
        // editable via Edit > Project Settings > Gaussian Splat) rather than as fields here,
        // since they need to be tunable from the Editor UI without a code change.
        public float BudgetedLoadFrameTimeMs => GaussianSplatSchedulerSettings.instance.budgetedLoadFrameTimeMs;
        public int BudgetedLoadConcurrentMax => GaussianSplatSchedulerSettings.instance.budgetedLoadConcurrentMax;

        readonly Queue<GaussianSplatRenderer> m_BudgetedLoadQueue = new();
        int m_BudgetedLoadInFlight;
        int m_LastTickFrame = -1;

        // Activation staggering: at most one renderer may claim OnEnable()'s expensive work per
        // frame. Every other renderer whose OnEnable() lands on an already-claimed frame
        // deactivates itself and queues here instead; Tick() re-enables (SetActive(true)) one
        // queued renderer per subsequent frame — mirroring the budgeted-load queue's one-start-
        // per-frame pattern, but applied at GameObject activation itself rather than at the
        // coroutine-start step. See performance.session.md (worldlabs_gaussian) 2026-08-13 [C].
        readonly Queue<GaussianSplatRenderer> m_ActivationQueue = new();
        int m_ActivationClaimedFrame = -1;

        // Set while Tick() is actively reactivating a queued renderer, so that renderer's own
        // OnEnable()/ClaimActivationSlot() call (fired synchronously and inline from
        // SetActive(true) below) is let through unconditionally instead of re-queueing itself —
        // the whole point of dequeuing it was to let it initialize THIS frame.
        bool m_ReactivatingFromQueue;

        // Called from OnEnable() itself (not Update() — the renderer may not reach Update() at
        // all if it ends up queued). Returns true if THIS call is the one allowed to proceed with
        // full initialization this frame; false means the caller must defer (queue + SetActive(false)).
        // Temp diagnostic capture (in-memory, not Debug.Log — the console ring buffer is flooded
        // by an unrelated editor-only log from GaussianSplatRendererEditor's AutoAssignResources,
        // which was masking Debug.Log-based diagnostics here). Read via reflection after a test.
        public static readonly List<string> DiagLog = new();

        public bool ClaimActivationSlot(int frame)
        {
            bool claimed;
            if (m_ReactivatingFromQueue)
                claimed = true;
            else if (frame == m_ActivationClaimedFrame)
                claimed = false;
            else
            {
                m_ActivationClaimedFrame = frame;
                claimed = true;
            }
            DiagLog.Add($"[{Time.realtimeSinceStartup:F3}] ClaimActivationSlot frame={frame} reactivating={m_ReactivatingFromQueue} claimedFrame={m_ActivationClaimedFrame} -> {claimed} queueCount={m_ActivationQueue.Count}");
            return claimed;
        }

        public void QueueForActivation(GaussianSplatRenderer r)
        {
            DiagLog.Add($"[{Time.realtimeSinceStartup:F3}] QueueForActivation {r.gameObject.name} queueCountBefore={m_ActivationQueue.Count}");
            EnsureDriver();
            m_ActivationQueue.Enqueue(r);
        }

        public void RequestBudgetedLoad(GaussianSplatRenderer r)
        {
            m_BudgetedLoadQueue.Enqueue(r);
        }

        public void NotifyBudgetedLoadFinished()
        {
            m_BudgetedLoadInFlight = Mathf.Max(0, m_BudgetedLoadInFlight - 1);
        }

        public void Tick(int frame)
        {
            if (frame == m_LastTickFrame)
                return;
            m_LastTickFrame = frame;

            // Calling BeginBudgetedLoad()/StartCoroutine() directly here IS safe, even though
            // it runs inline within whichever renderer's Update() triggers this Tick():
            // UpdateRessourcesBudgetedInternal()'s own first statement is `yield return null`,
            // which guarantees the coroutine returns immediately on this call and defers all
            // its real work to next frame — so nothing expensive actually runs inline here.
            // (Session-2026-08-12 briefly tried deferring this call itself by one Tick() as a
            // fix, which was redundant/wrong: the coroutine-level yield is what actually
            // matters, and without it deferring the CALL just moves the same inline cost to a
            // different frame rather than removing it. Reverted back to this simpler form.)
            if (m_BudgetedLoadInFlight >= BudgetedLoadConcurrentMax)
                return;

            while (m_BudgetedLoadQueue.Count > 0)
            {
                var r = m_BudgetedLoadQueue.Dequeue();
                if (r == null)
                    continue; // destroyed/unregistered while queued
                m_BudgetedLoadInFlight++;
                r.BeginBudgetedLoad();
                break; // at most one new start per frame
            }

            // Re-enable at most one queued (self-deactivated) renderer per frame. SetActive(true)
            // re-triggers OnEnable() synchronously/inline — m_ReactivatingFromQueue lets that
            // specific call through ClaimActivationSlot() unconditionally instead of re-queueing.
            while (m_ActivationQueue.Count > 0)
            {
                var r = m_ActivationQueue.Dequeue();
                if (r == null)
                    continue; // destroyed while queued
                DiagLog.Add($"[{Time.realtimeSinceStartup:F3}] Tick reactivating {r.gameObject.name} frame={frame} queueCountAfterDequeue={m_ActivationQueue.Count}");
                m_ReactivatingFromQueue = true;
                r.gameObject.SetActive(true);
                m_ReactivatingFromQueue = false;
                break; // at most one reactivation per frame
            }
        }

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            EnsureDriver();

            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        public int CountAllGaussians()
        {
            int count = 0;
            foreach (var splat in m_ActiveSplats)
                count += splat.Item1.splatCount;
            return count;
        }

        // Total splats across every registered renderer, regardless of active/valid/frustum state.
        // Compared against CountAllGaussians() (post-cull) to measure culling effectiveness.
        public int CountTotalGaussiansInScene()
        {
            int count = 0;
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs != null)
                    count += gs.splatCount;
            }
            return count;
        }

        // Transforms a local-space AABB by an arbitrary transform (handles rotation, not just scale/translate).
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

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup || !gs.IsReady)
                    continue;
                // Frustum cull: skip renderers whose world-space bounds don't touch the camera frustum.
                // Renderers with no bounds metadata (external-buffer path) are never culled.
                if (gs.TryGetLocalBounds(out var localBounds))
                {
                    var worldBounds = TransformBounds(gs.transform, localBounds);
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, worldBounds))
                        continue;
                }
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort by render order first, then by depth from camera (higher order = rendered last = on top)
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB) return orderB.CompareTo(orderA);
                var posA = camTr.InverseTransformPoint(a.Item1.transform.position);
                var posB = camTr.InverseTransformPoint(b.Item1.transform.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        // Returns true if any active renderer has m_OpaqueExperiment enabled.
        // Used by the URP feature to decide whether a depth+stencil surface is required.
        public bool AnyOpaqueExperiment()
        {
            foreach (var kvp in m_ActiveSplats)
                if (kvp.Item1.m_OpaqueExperiment) return true;
            return false;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // passOverride: when >= 0, forces this shader pass regardless of per-renderer flags.
        //   0 = transparent (default), 1 = opaque experiment, 2 = depth prepass, 3 = proximity transparent.
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb, int passOverride = -1)
        {
            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                if (Time.frameCount - gs.m_LastSortedFrame >= gs.m_SortNthFrame - 1         // Only sort every nth frame
                    && (!gs.m_CenterEyeOnly || gs.m_LastSortedFrame != Time.frameCount)     // dont sort multiple times a frame 
                    && gs.m_gpuSortType != GpuSorting.SortType.None)    
                {
                    gs.SortPoints(cmb, cam, matrix);
                    gs.m_LastSortedFrame = Time.frameCount;
                }

                gs.EnsureMaterials();

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetFloat(GaussianSplatRenderer.Props.AlphaDiscardThreshold, gs.m_AlphaDiscardThreshold);
                mpb.SetInteger(GaussianSplatRenderer.Props.SplatLinearToGamma, gs.m_SplatLinearToGamma ? 1 : 0);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatGammaValue, gs.m_SplatGammaValue);
                mpb.SetVector(GaussianSplatRenderer.Props.SplatShadowGain, gs.m_SplatShadowGain);
                mpb.SetVector(GaussianSplatRenderer.Props.SplatMidGain, gs.m_SplatMidGain);
                mpb.SetVector(GaussianSplatRenderer.Props.SplatHighlightGain, gs.m_SplatHighlightGain);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatCurvePivot, gs.m_SplatCurvePivot);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam, matrix);
                cmb.EndSample(s_ProfCalcView);

                // draw
                int indexCount = 6;
                int instanceCount = gs.splatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                int shaderPass;
                if (passOverride >= 0)
                    shaderPass = passOverride;
                else if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.Splats && gs.m_OpaqueExperiment)
                    shaderPass = 1;
                else
                    shaderPass = 0;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, shaderPass, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            InitialClearCmdBuffer(cam);

            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }
        public GaussianSplatAsset m_Asset;

        // Runtime-loaded data (no AssetDatabase required, works in builds).
        // Set via LoadFromRuntimeData(); mutually exclusive with m_Asset.
        RuntimeSplatData m_RuntimeData;

        [Range(0.0f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.0f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;

        [Tooltip("Debug: force every splat's world-space scale to a fixed uniform value, bypassing decoded per-splat scale. Used to isolate scale-variation-driven visual artifacts from position/rotation causes.")]
        public bool m_DebugForceUniformScale;
        [Range(0.001f, 2.0f)]
        [Tooltip("Debug: the fixed uniform scale value used when Debug Force Uniform Scale is enabled")]
        public float m_DebugForceSplatSize = 0.05f;
        [Tooltip("Debug: force every splat's alpha to a fixed uniform value, bypassing decoded per-splat opacity. Used to isolate opacity-driven visual artifacts from scale/position/rotation causes.")]
        public bool m_DebugForceUniformAlpha;
        [Range(0.0f, 1.0f)]
        [Tooltip("Debug: the fixed uniform alpha value used when Debug Force Uniform Alpha is enabled")]
        public float m_DebugForceSplatAlpha = 1.0f;

        [Range(0, 1f)] [Tooltip("Splats with peak opacity below this value are culled before rasterization")]
        public float m_ContributionCullThreshold = 0.05f;
        [Range(0, 1f)] [Tooltip("Fragment alpha below this value is discarded. Controls splat edge softness vs. fill rate.")]
        public float m_AlphaDiscardThreshold = 0.05f;
        [Tooltip("Diagnostic: color splats by distance to nearest hotspot (green = full detail, red = attenuation edge) instead of their real color.")]
        public bool m_HotspotDebugVisualize;

        public bool test;

        [Header("Debug Experiments")]
        [Tooltip("GSP-CULL-01: Sort front-to-back and render opaque. Measures overdraw lower bound — output looks wrong by design.")]
        public bool m_OpaqueExperiment;

        [Tooltip("Experiment: apply a linear->gamma curve to splat color at shade time. SH DC coefficients are trained as linear-space values with no gamma correction applied anywhere in this pipeline by default.")]
        public bool m_SplatLinearToGamma;
        [Range(0.1f, 4.0f)] [Tooltip("Gamma exponent for the linear->gamma experiment above. 2.2 is standard display gamma; lower values darken less / raise less contrast, higher values darken more / raise more contrast.")]
        public float m_SplatGammaValue = 2.2f;
        [Tooltip("Per-channel 3-point curve gain (shadows) applied to splat color before the gamma curve above, while the linear->gamma experiment is enabled. 1,1,1 is neutral.")]
        public Vector3 m_SplatShadowGain = Vector3.one;
        [Tooltip("Per-channel 3-point curve gain (midtones).")]
        public Vector3 m_SplatMidGain = Vector3.one;
        [Tooltip("Per-channel 3-point curve gain (highlights).")]
        public Vector3 m_SplatHighlightGain = Vector3.one;
        [Range(0.05f, 0.95f)] [Tooltip("Value (0-1) where the shadow/midtone/highlight curve pivots. 0.5 is a symmetric split.")]
        public float m_SplatCurvePivot = 0.5f;

        [Tooltip("GSP-CULL-03: Depth proximity transparency. Requires depthProximityTransparency enabled in GaussianSplatURPFeature. " +
                 "Enables Pass 2 (Z-prepass) + Pass 3 (transparent + proximity cull) instead of Pass 0.")]
        public bool m_DepthProximityExperiment;

        [Tooltip("Render order relative to other splats. Higher value = rendered last = on top. Splats with equal order are sorted by camera depth.")]
        public int m_RenderOrder;

        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;

        [Tooltip("In VR settings, only sort 1 time for both eyes (middle)")]
        public bool m_CenterEyeOnly = false;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        [Header("Mask")]
        public GaussianSplatMask m_Mask;
        [Range(0f, 1f)] public float m_MaskT = 0f;
        // Per-frame time budget for the deferred/budgeted load used to live here as a
        // per-instance field, forcing hand-tuning on every GameObject (and, left at the
        // shared default across many renderers, was itself part of why N renderers' loads
        // stacked additively — see masking.session.md 2026-08-10). Now owned as a single
        // shared setting: GaussianSplatRenderSystem.instance.BudgetedLoadFrameTimeMs.

        /// <summary>
        /// True once GPU resources are fully created and this renderer is safe to display.
        /// False during a deferred/budgeted load (mask assigned + m_MaskT == 0 at OnEnable) —
        /// external code driving a mask reveal (e.g. a Timeline gate) should wait for this
        /// before starting to animate m_MaskT, to avoid a visual snap once loading catches up.
        /// True immediately for the normal (non-deferred) case.
        /// </summary>
        public bool IsReady { get; private set; } = true;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities_deviceRadixSort;
        public ComputeShader m_CSSplatUtilities_fidelityFX;
        private ComputeShader m_CSSplatUtilities;

        // Effect layer stacking (Option A)
        GaussianSplatEffectLayer[] m_EffectLayers = Array.Empty<GaussianSplatEffectLayer>();
        ComputeBuffer m_EffectLayerBuffer;
        GaussianSplatEffectLayer.ShaderParams[] m_EffectLayerData = Array.Empty<GaussianSplatEffectLayer.ShaderParams>();
        static readonly int s_EffectLayers     = GaussianSplatEffectLayer.s_EffectLayers;
        static readonly int s_EffectLayerCount = GaussianSplatEffectLayer.s_EffectLayerCount;

        // layer stuff
        public List<int2> m_LayerActivationState;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        private GraphicsBuffer m_GpuLayerData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        bool m_GpuChunksExternallyOwned; // when true, do not dispose m_GpuChunks
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;
        internal GraphicsBuffer m_GpuVisibleIndices;
        float m_LastCullCounterLogTime;
#if UNITY_EDITOR
        // Editor-only cull survivor readback for the Scene-view splat count overlay
        // (GaussianSplatDebugOverlayWindow). Only reads back m_GpuVisibleIndices (already allocated
        // unconditionally in CalcViewData) while s_DebugCountersEnabled is on -- no extra buffer needed.
        internal static bool s_DebugCountersEnabled;
        internal uint m_LastPostCullCount;

        /// Editor-only: toggles the per-frame cull-survivor readback used by the Scene view splat count overlay.
        public static bool DebugCountersEnabled
        {
            get => s_DebugCountersEnabled;
            set => s_DebugCountersEnabled = value;
        }
        /// Editor-only: input splat count and post-cull-compute survivor count from the most recent frame
        /// this renderer was drawn, valid only while DebugCountersEnabled is true.
        public (int input, uint postCull) DebugSplatCounts => (splatCount, m_LastPostCullCount);
#endif
        internal GraphicsBuffer m_GpuIndirectArgs;
        internal Camera m_centerEyeCamera;
        internal Matrix4x4 m_centerCamMatrix;

        GraphicsBuffer m_GpuMaskWeights; // per-splat float, evaluated from m_Mask at m_MaskT
        bool m_MaskDirty = true;
        GaussianSplatMask m_MaskPrev;
        float m_MaskTPrev = -1f;
        int m_MaskEntryCountPrev = -1;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditOpacityMult; // per-splat float opacity multiplier, written by Hardness-as-intensity delete blending. 1.0 = untouched.
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        public GpuSorting.SortType m_gpuSortType = GpuSorting.SortType.DeviceRadixSort;
        GpuSorting m_Sorter;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal int m_LastSortedFrame;
        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatLayer = Shader.PropertyToID("_SplatLayer");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int DebugForceSplatSize = Shader.PropertyToID("_DebugForceSplatSize");
            public static readonly int DebugForceSplatAlpha = Shader.PropertyToID("_DebugForceSplatAlpha");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int ContributionCullThreshold = Shader.PropertyToID("_ContributionCullThreshold");
            public static readonly int AlphaDiscardThreshold = Shader.PropertyToID("_AlphaDiscardThreshold");
            public static readonly int SplatLinearToGamma = Shader.PropertyToID("_SplatLinearToGamma");
            public static readonly int SplatGammaValue = Shader.PropertyToID("_SplatGammaValue");
            public static readonly int SplatShadowGain = Shader.PropertyToID("_SplatShadowGain");
            public static readonly int SplatMidGain = Shader.PropertyToID("_SplatMidGain");
            public static readonly int SplatHighlightGain = Shader.PropertyToID("_SplatHighlightGain");
            public static readonly int SplatCurvePivot = Shader.PropertyToID("_SplatCurvePivot");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
            public static readonly int HotspotCount = Shader.PropertyToID("_HotspotCount");
            public static readonly int HotspotPositions = Shader.PropertyToID("_HotspotPositions");
            public static readonly int HotspotFullRadius = Shader.PropertyToID("_HotspotFullRadius");
            public static readonly int HotspotAttenuationRadius = Shader.PropertyToID("_HotspotAttenuationRadius");
            public static readonly int HotspotDebugVisualize = Shader.PropertyToID("_HotspotDebugVisualize");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
            // Repurposed from unused "_VisibleIndices" scaffolding: atomic counter of splats that survive per-splat culling in CalcViewData.
            public static readonly int VisibleIndices = Shader.PropertyToID("_CullSurvivorCounter");
            public static readonly int IndirectArgs = Shader.PropertyToID("_IndirectArgs");
            public static readonly int InvertSort = Shader.PropertyToID("_InvertSort");
            public static readonly int ZTest = Shader.PropertyToID("_ZTest");
            public static readonly int DepthBlendOp = Shader.PropertyToID("_DepthBlendOp");
            public static readonly int DeleteDensity = Shader.PropertyToID("_DeleteDensity");
            public static readonly int DeleteHardness = Shader.PropertyToID("_DeleteHardness");
            public static readonly int DeleteUsePosition = Shader.PropertyToID("_DeleteUsePosition");
            public static readonly int BrushDensity = Shader.PropertyToID("_BrushDensity");
            public static readonly int BrushSeed = Shader.PropertyToID("_BrushSeed");
            public static readonly int BrushColorModeActive = Shader.PropertyToID("_BrushColorModeActive");
            public static readonly int BrushRefColorHsl = Shader.PropertyToID("_BrushRefColorHsl");
            public static readonly int BrushColorTolHsl = Shader.PropertyToID("_BrushColorTolHsl");
            public static readonly int SplatOpacityMult = Shader.PropertyToID("_SplatOpacityMult");
            public static readonly int SplatOpacityMultValid = Shader.PropertyToID("_SplatOpacityMultValid");
            public static readonly int SplatOpacityMultRW = Shader.PropertyToID("_SplatOpacityMultRW");
            public static readonly int PickCenter = Shader.PropertyToID("_PickCenter");
            public static readonly int PickRadius = Shader.PropertyToID("_PickRadius");
            public static readonly int PickResultRW = Shader.PropertyToID("_PickResultRW");
            public static readonly int DeleteSelectionCenter = Shader.PropertyToID("_DeleteSelectionCenter");
            public static readonly int DeleteSelectionExtents = Shader.PropertyToID("_DeleteSelectionExtents");
            public static readonly int SplatDeletedBitsRW = Shader.PropertyToID("_SplatDeletedBitsRW");
            public static readonly int SplatMaskWeights = Shader.PropertyToID("_SplatMaskWeights");
            public static readonly int SplatMaskValid = Shader.PropertyToID("_SplatMaskValid");
            public static readonly int ViewpointPos = Shader.PropertyToID("_ViewpointPos");
            public static readonly int ReprojectAmount = Shader.PropertyToID("_ReprojectAmount");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ReprojectSelection,
            ExportData,
            CopySplats,
            DeleteSelectedWithParams,
            BrushSelect,
            BrushSelectWorld,
            PickSplatColor,
        }

        public bool HasValidRuntimeData => m_RuntimeData != null && m_RuntimeData.splatCount > 0;

        public bool HasValidAsset =>
            HasExternalBuffers ||
            HasValidRuntimeData ||
            (m_Asset != null &&
             m_Asset.splatCount > 0 &&
             m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
             m_Asset.posDataSize > 0 &&
             m_Asset.otherDataSize > 0 &&
             m_Asset.shDataSize > 0 &&
             m_Asset.colorDataSize > 0);
        public bool HasValidRenderSetup => HasExternalBuffers || (m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null);

        // Local-space bounds for culling. External-buffer renderers carry no bounds metadata,
        // so they report false (never culled) rather than guessing.
        public bool TryGetLocalBounds(out Bounds bounds)
        {
            Vector3 min, max;
            if (HasValidRuntimeData)
            {
                min = m_RuntimeData.boundsMin;
                max = m_RuntimeData.boundsMax;
            }
            else if (m_Asset != null)
            {
                min = m_Asset.boundsMin;
                max = m_Asset.boundsMax;
            }
            else
            {
                bounds = default;
                return false;
            }
            bounds = default;
            bounds.SetMinMax(min, max);
            return true;
        }

        const int kGpuViewDataSize = 40;


        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
            {
                DisposeResourcesForAsset();
                return;
            }

            UpdateRessources();
        }

        /// <summary>
        /// Load a world at runtime from pre-processed byte buffers.
        /// Clears any previously assigned <see cref="m_Asset"/>; works in builds without AssetDatabase.
        /// </summary>
        public void LoadFromRuntimeData(RuntimeSplatData data)
        {
            m_RuntimeData = data;
            m_Asset       = null;
            if (m_MatSplats != null)
                UpdateRessources();
        }

        // ── External buffer injection (morpher) ───────────────────────────────

        // When non-null, the morpher owns these buffers; the renderer reads them directly.
        // The renderer never allocates or disposes them.
        GraphicsBuffer m_ExternalPos;
        GraphicsBuffer m_ExternalOther;
        GraphicsBuffer m_ExternalSH;
        Texture        m_ExternalColor;
        int            m_ExternalSplatCount;
        uint           m_ExternalSplatFormat;

        public bool HasExternalBuffers => m_ExternalPos != null;

        /// <summary>
        /// Bind morpher-owned GPU buffers as the active splat data source.
        /// Clears m_Asset and m_RuntimeData — the renderer renders whatever the morpher writes.
        /// Pass all nulls to release; caller must then reassign m_Asset to restore standalone mode.
        /// </summary>
        public void SetExternalBuffers(
            GraphicsBuffer pos, GraphicsBuffer other, GraphicsBuffer sh, Texture color,
            GraphicsBuffer chunks, bool chunksValid,
            int splatCount, uint splatFormat)
        {
            m_ExternalPos         = pos;
            m_ExternalOther       = other;
            m_ExternalSH          = sh;
            m_ExternalColor       = color;
            m_ExternalSplatCount  = splatCount;
            m_ExternalSplatFormat = splatFormat;

            if (pos != null)
            {
                m_Asset       = null;
                m_RuntimeData = null;
                m_SplatCount  = splatCount;

                // Only reallocate view + sort buffers when count changes — caller may call every frame.
                if (m_GpuView == null || m_GpuView.count != m_SplatCount)
                {
                    DisposeBuffer(ref m_GpuView);
                    m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize) { name = "GaussianView" };
                    if (m_CSSplatUtilities != null && m_Sorter != null)
                    {
                        DisposeBuffer(ref m_GpuSortDistances);
                        DisposeBuffer(ref m_GpuSortKeys);
                        InitSortBuffers(m_SplatCount);
                    }
                }

                // Use caller-provided chunk buffer — never dispose it, the caller owns it.
                // Swap when the buffer reference changes, or when m_GpuChunks was never
                // initialized (null == null is otherwise indistinguishable from "no change").
                if (m_GpuChunks != chunks || m_GpuChunks == null)
                {
                    if (!m_GpuChunksExternallyOwned) DisposeBuffer(ref m_GpuChunks);
                    m_GpuChunksExternallyOwned = chunks != null;
                    if (chunks != null)
                    {
                        m_GpuChunks      = chunks;
                        m_GpuChunksValid = chunksValid;
                    }
                    else
                    {
                        m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Raw,
                            UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>() / 4, 4) { name = "GaussianChunkData_Morph" };
                        m_GpuChunksValid = false;
                    }
                }

                // Dummy layer buffer
                if (m_GpuLayerData == null)
                    m_GpuLayerData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GaussianLayerData_Morph" };

                // Index buffer for draw calls — must exist regardless of data source
                if (m_GpuIndexBuffer == null)
                {
                    m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
                    m_GpuIndexBuffer.SetData(new ushort[] { 0,1,2,1,3,2, 4,6,5,5,6,7, 0,2,4,4,2,6, 1,5,3,3,5,7, 0,4,1,1,4,5, 2,3,6,6,3,7 });
                }
            }
        }

        static int SplatIndexToTextureIndex(uint idx)                                                                                                                                                                                  
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            uint width = GaussianSplatAsset.kTextureWidth / 16;
            idx >>= 8;
            uint x = (idx % width) * 16 + xy.x;
            uint y = (idx / width) * 16 + xy.y;
            return (int)(y * GaussianSplatAsset.kTextureWidth + x);
        }
        
        public void UpdateRessources()
        {
            DisposeResourcesForAsset();

            if (!HasValidAsset)
                return;

            if (HasExternalBuffers)
                return;

            if (HasValidRuntimeData)
            {
                LoadFromRuntimeDataInternal(m_RuntimeData);
                return;
            }

            // Initialize m_LayerActivationState if null (default to all layers active)
            if (m_LayerActivationState == null)
            {
                m_LayerActivationState = new List<int2>();
                // If no layer activation state is set, activate all layers by default
                foreach (var layer in asset.layerInfo)
                {
                    m_LayerActivationState.Add(new int2(layer.Key, 1));
                }
            }

            var activeLayers = m_LayerActivationState.Where(kv => kv.y > 0).Select(kv => kv.x).ToHashSet();
            m_SplatCount = asset.layerInfo.Where(kv => activeLayers.Contains(kv.Key)).Sum(kv => kv.Value);

            if (m_SplatCount == 0) 
                return;

            m_centerEyeCamera = ResolveCenterEyeCamera();

            int posSize, posMarker;
            int otherSize, otherMarker;
            int shSize, shMarker;
            int colorSize, colorMarker;
            int chunkSize, chunkMarker;
            posSize = posMarker = otherSize = otherMarker = shSize = shMarker = colorSize = colorMarker = chunkSize = chunkMarker = 0;
            
            foreach (var layerAssets in asset.LayerData.Where(l => activeLayers.Contains(l.layer)))
            {
                posSize += (int) layerAssets.m_PosData.dataSize;
                otherSize += (int) layerAssets.m_OtherData.dataSize;
                shSize += (int) (layerAssets.m_SHData != null ? layerAssets.m_SHData.dataSize : 0);
                colorSize += (int) layerAssets.m_ColorData.dataSize;
                chunkSize += (int) (layerAssets.m_ChunkData != null ? layerAssets.m_ChunkData.dataSize : 0);
            }

            var posDataArr = new NativeArray<byte>(NextMultipleOf(posSize, 4), Allocator.Temp);
            var otherDataArr = new NativeArray<byte>(NextMultipleOf(otherSize, 4), Allocator.Temp);
            var shDataArr = new NativeArray<byte>(NextMultipleOf(shSize, 4), Allocator.Temp);
            var colorDataArr = new NativeArray<byte>(colorSize, Allocator.TempJob);
            var chunkDataArr = new NativeArray<byte>(chunkSize, Allocator.Temp);
            
            foreach (var layerAssets in asset.LayerData.Where(l => activeLayers.Contains(l.layer)))
            {
                var posAssetData = layerAssets.m_PosData.GetData<byte>();
                var posSub = posDataArr.GetSubArray(posMarker, posAssetData.Length);
                posMarker += posAssetData.Length;
                posSub.CopyFrom(posAssetData);
                
                var otherAssetData = layerAssets.m_OtherData.GetData<byte>();
                var otherSub = otherDataArr.GetSubArray(otherMarker, otherAssetData.Length);
                otherMarker += otherAssetData.Length;
                otherSub.CopyFrom(otherAssetData);
                
                var colorAssetData = layerAssets.m_ColorData.GetData<byte>();
                var colorSub = colorDataArr.GetSubArray(colorMarker, colorAssetData.Length);
                colorMarker += colorAssetData.Length;
                colorSub.CopyFrom(colorAssetData);

                if (layerAssets.m_SHData != null)
                {
                    var shAssetData = layerAssets.m_SHData.GetData<byte>();
                    var shSub = shDataArr.GetSubArray(shMarker, shAssetData.Length);
                    shMarker += shAssetData.Length;
                    shSub.CopyFrom(shAssetData);
                }

                if (layerAssets.m_ChunkData != null)
                {
                    var chunkAssetData = layerAssets.m_ChunkData.GetData<byte>();
                    var chunkSub = chunkDataArr.GetSubArray(chunkMarker, chunkAssetData.Length);
                    chunkMarker += chunkAssetData.Length;
                    chunkSub.CopyFrom(chunkAssetData);
                }
            }
            
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, posDataArr.Length / 4, 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(posDataArr);
            
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, otherDataArr.Length / 4, 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(otherDataArr);
            
            if (asset.ClusteredSHData != null) shDataArr = asset.ClusteredSHData.GetData<byte>();
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, shDataArr.Length / 4, 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(shDataArr);

            m_GpuColorData = CreateColorTexture(colorDataArr, asset.colorFormat, m_SplatCount);
            
            if (chunkDataArr.Length > 0)
            {
                // Raw buffer: avoids Vulkan std430 alignment issues with mixed uint/float2 struct layout
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Raw, chunkDataArr.Length / 4, 4) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(chunkDataArr);
                m_GpuChunksValid = true;
            }
            else
            {
                // dummy raw buffer (1 chunk worth of bytes)
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Raw,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>() / 4, 4) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }
            
            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            posDataArr.Dispose();
            otherDataArr.Dispose();
            shDataArr.Dispose();
            colorDataArr.Dispose();
            chunkDataArr.Dispose();

            UpdateSortingType(m_gpuSortType);

            // Only initialize sort buffers if we have valid compute shader and sorter
            if (m_CSSplatUtilities != null && m_Sorter != null)
            {
                InitSortBuffers(splatCount);
            }

            // Mirrors UpdateRessourcesBudgetedInternal()'s own mask build (see that method) —
            // without this, m_GpuMaskWeights stays null after a synchronous load and the mask
            // is never applied until something else (e.g. Update()'s m_MaskT dirty-check)
            // happens to rebuild it.
            UpdateMaskBuffer();
            m_MaskDirty = false;
        }

        // Budgeted variant of UpdateRessources(), used when a mask is assigned and m_MaskT is
        // 0 at OnEnable — the splat isn't supposed to be visible yet, so its GPU init cost can
        // be spread across frames instead of paid synchronously in one. Mirrors the synchronous
        // method's structure exactly, with a Stopwatch-budget yield inserted between each
        // expensive call (same pattern as SceneRenderVisibilityToggle's ApplyBudgeted). Sets
        // IsReady = true only once every resource this method creates is actually ready.
        IEnumerator UpdateRessourcesBudgeted()
        {
            // Runs to completion via UpdateRessourcesBudgetedInternal(); this wrapper only
            // exists to guarantee NotifyBudgetedLoadFinished() fires on every exit path
            // (every yield break below, or normal fall-through) exactly once, so the
            // scheduler's in-flight count in GaussianSplatRenderSystem stays accurate however
            // this coroutine ends.
            var inner = UpdateRessourcesBudgetedInternal();
            try
            {
                while (inner.MoveNext())
                    yield return inner.Current;
            }
            finally
            {
                // DisposeResourcesForAsset() (called at the top of the internal method) disposes
                // m_GpuMaskWeights. Must be rebuilt SYNCHRONOUSLY here, not just marked dirty
                // for Update() to pick up later — confirmed 1-2 frame race, ~95% confidence
                // (masking.session.md 2026-08-12): Unity resumes a `yield return null`
                // coroutine AFTER this frame's Update() calls have already run, but BEFORE
                // this frame's rendering. IsReady is set true earlier in this same coroutine
                // call, making GatherSplatsForCamera include this renderer in THIS SAME
                // frame's render pass — but Update() (the only thing that previously read
                // m_MaskDirty and called UpdateMaskBuffer()) already ran for this frame and
                // won't run again until NEXT frame. Marking m_MaskDirty alone therefore left
                // the renderer submitted-and-rendered for one full frame with a stale/disposed
                // m_GpuMaskWeights before Update() got a chance to rebuild it next frame —
                // exactly the observed "flashes for 1-2 frames every time" symptom. Calling
                // UpdateMaskBuffer() directly closes the gap immediately, in the same
                // coroutine call, before this frame's render pass ever runs.
                UpdateMaskBuffer();
                m_MaskDirty = false;
                GaussianSplatRenderSystem.instance.NotifyBudgetedLoadFinished();
            }
        }

        IEnumerator UpdateRessourcesBudgetedInternal()
        {
            // Force at least one frame boundary before ANY work below runs — including the
            // cheap-looking DisposeResourcesForAsset()/HasValidAsset checks. StartCoroutine()
            // always executes its callee synchronously up to its first yield, on the SAME
            // call that invokes it, no matter WHEN that call happens — the scheduler
            // (GaussianSplatRenderSystem.Tick/m_PendingStart) only controls which frame
            // BeginBudgetedLoad()/StartCoroutine() gets called on, it can't stop that call
            // itself from paying this coroutine's first segment inline. Session-2026-08-12
            // mistakenly removed this yield, assuming the scheduler's per-frame staggering
            // made it redundant — it doesn't: without it, whichever renderer's Update() the
            // scheduler happens to start this frame still eats its own first segment's cost
            // inline in that same Update() call, reproducing the original stacking symptom
            // one layer up. This yield is what actually guarantees deferral; the scheduler's
            // job is only to make sure at most one renderer needs it per frame.
            yield return null;

            // Update()'s own asset-change-detection block (m_PrevAsset/m_PrevHash) normally
            // gets set as a side effect of the synchronous CreateResourcesForAsset() path. This
            // budgeted path bypasses that call entirely, so set them here too — otherwise
            // Update()'s first tick after OnEnable would see m_PrevAsset != m_Asset as still
            // true and re-trigger a second, synchronous load racing this coroutine.
            if (!HasExternalBuffers && !HasValidRuntimeData)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash  = m_Asset ? m_Asset.dataHash : new Hash128();
            }

            DisposeResourcesForAsset();

            if (!HasValidAsset)
            {
                IsReady = true;
                yield break;
            }

            if (HasExternalBuffers)
            {
                IsReady = true;
                yield break;
            }

            if (HasValidRuntimeData)
            {
                LoadFromRuntimeDataInternal(m_RuntimeData);
                IsReady = true;
                yield break;
            }

            if (m_LayerActivationState == null)
            {
                m_LayerActivationState = new List<int2>();
                foreach (var layer in asset.layerInfo)
                {
                    m_LayerActivationState.Add(new int2(layer.Key, 1));
                }
            }

            var activeLayers = m_LayerActivationState.Where(kv => kv.y > 0).Select(kv => kv.x).ToHashSet();
            m_SplatCount = asset.layerInfo.Where(kv => activeLayers.Contains(kv.Key)).Sum(kv => kv.Value);

            if (m_SplatCount == 0)
            {
                IsReady = true;
                yield break;
            }

            // No forced yield needed here anymore: GaussianSplatRenderSystem's scheduler
            // (RequestBudgetedLoad/Tick) already staggers WHEN this coroutine gets started in
            // the first place — at most one new renderer per frame, bounded by
            // BudgetedLoadConcurrentMax in flight — so by the time this method's body runs,
            // it's already on its own scheduled frame rather than stacked with other
            // renderers activated in the same cascade. See masking.session.md 2026-08-10.

            m_centerEyeCamera = ResolveCenterEyeCamera();

            int startFrame = Time.frameCount;
            int yieldCount = 0;
            Debug.Log($"[BudgetedLoad:{name}] started at frame {startFrame}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool CheckBudget()
            {
                if (sw.Elapsed.TotalMilliseconds < GaussianSplatRenderSystem.instance.BudgetedLoadFrameTimeMs) return false;
                sw.Restart();
                yieldCount++;
                Debug.Log($"[BudgetedLoad:{name}] yield #{yieldCount} at frame {Time.frameCount}");
                return true;
            }

            int posSize, posMarker;
            int otherSize, otherMarker;
            int shSize, shMarker;
            int colorSize, colorMarker;
            int chunkSize, chunkMarker;
            posSize = posMarker = otherSize = otherMarker = shSize = shMarker = colorSize = colorMarker = chunkSize = chunkMarker = 0;

            foreach (var layerAssets in asset.LayerData.Where(l => activeLayers.Contains(l.layer)))
            {
                posSize += (int)layerAssets.m_PosData.dataSize;
                otherSize += (int)layerAssets.m_OtherData.dataSize;
                shSize += (int)(layerAssets.m_SHData != null ? layerAssets.m_SHData.dataSize : 0);
                colorSize += (int)layerAssets.m_ColorData.dataSize;
                chunkSize += (int)(layerAssets.m_ChunkData != null ? layerAssets.m_ChunkData.dataSize : 0);
            }

            var posDataArr = new NativeArray<byte>(NextMultipleOf(posSize, 4), Allocator.Persistent);
            var otherDataArr = new NativeArray<byte>(NextMultipleOf(otherSize, 4), Allocator.Persistent);
            var shDataArr = new NativeArray<byte>(NextMultipleOf(shSize, 4), Allocator.Persistent);
            var colorDataArr = new NativeArray<byte>(colorSize, Allocator.Persistent);
            var chunkDataArr = new NativeArray<byte>(chunkSize, Allocator.Persistent);

            foreach (var layerAssets in asset.LayerData.Where(l => activeLayers.Contains(l.layer)))
            {
                var posAssetData = layerAssets.m_PosData.GetData<byte>();
                var posSub = posDataArr.GetSubArray(posMarker, posAssetData.Length);
                posMarker += posAssetData.Length;
                posSub.CopyFrom(posAssetData);

                var otherAssetData = layerAssets.m_OtherData.GetData<byte>();
                var otherSub = otherDataArr.GetSubArray(otherMarker, otherAssetData.Length);
                otherMarker += otherAssetData.Length;
                otherSub.CopyFrom(otherAssetData);

                var colorAssetData = layerAssets.m_ColorData.GetData<byte>();
                var colorSub = colorDataArr.GetSubArray(colorMarker, colorAssetData.Length);
                colorMarker += colorAssetData.Length;
                colorSub.CopyFrom(colorAssetData);

                if (layerAssets.m_SHData != null)
                {
                    var shAssetData = layerAssets.m_SHData.GetData<byte>();
                    var shSub = shDataArr.GetSubArray(shMarker, shAssetData.Length);
                    shMarker += shAssetData.Length;
                    shSub.CopyFrom(shAssetData);
                }

                if (layerAssets.m_ChunkData != null)
                {
                    var chunkAssetData = layerAssets.m_ChunkData.GetData<byte>();
                    var chunkSub = chunkDataArr.GetSubArray(chunkMarker, chunkAssetData.Length);
                    chunkMarker += chunkAssetData.Length;
                    chunkSub.CopyFrom(chunkAssetData);
                }

                if (CheckBudget()) yield return null;
            }

            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, posDataArr.Length / 4, 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(posDataArr);
            if (CheckBudget()) yield return null;

            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, otherDataArr.Length / 4, 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(otherDataArr);
            if (CheckBudget()) yield return null;

            var actualShDataArr = shDataArr;
            if (asset.ClusteredSHData != null) actualShDataArr = asset.ClusteredSHData.GetData<byte>();
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, actualShDataArr.Length / 4, 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(actualShDataArr);
            if (CheckBudget()) yield return null;

            m_GpuColorData = CreateColorTexture(colorDataArr, asset.colorFormat, m_SplatCount);
            if (CheckBudget()) yield return null;

            if (chunkDataArr.Length > 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Raw, chunkDataArr.Length / 4, 4) { name = "GaussianChunkData" };
                m_GpuChunks.SetData(chunkDataArr);
                m_GpuChunksValid = true;
            }
            else
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Raw,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>() / 4, 4) { name = "GaussianChunkData" };
                m_GpuChunksValid = false;
            }
            if (CheckBudget()) yield return null;

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            posDataArr.Dispose();
            otherDataArr.Dispose();
            shDataArr.Dispose();
            colorDataArr.Dispose();
            chunkDataArr.Dispose();

            if (CheckBudget()) yield return null;

            UpdateSortingType(m_gpuSortType);

            if (m_CSSplatUtilities != null && m_Sorter != null)
            {
                InitSortBuffers(m_SplatCount);
            }

            if (CheckBudget()) yield return null;

            // Mask must be fully built and bound BEFORE IsReady flips — not after (a coroutine
            // finally block would run this fully synchronously, un-budgeted, defeating the
            // whole point of this method for large splat counts) and not deferred to Update()'s
            // dirty-flag path (which races IsReady: Unity resumes this coroutine after that
            // frame's Update() already ran but before that frame's render pass, so a
            // dirty-flag-only signal would leave the renderer visible-but-unmasked for one full
            // frame — confirmed bug, masking.session.md 2026-08-12). Building it here, as a
            // budget-respecting step in the SAME gated sequence as the GPU resources above,
            // makes "mask applied" part of the same readiness gate as "GPU resources ready"
            // instead of two independently-timed signals racing each other.
            UpdateMaskBuffer();
            m_MaskDirty = false;

            IsReady = true;
            Debug.Log($"[BudgetedLoad:{name}] IsReady=true at frame {Time.frameCount} ({Time.frameCount - startFrame} frames, {yieldCount} yields)");
        }

        // Returns the packed format integer consumed by shaders and compute shaders.
        uint GetSplatFormat()
        {
            if (HasExternalBuffers)  return m_ExternalSplatFormat;
            if (HasValidRuntimeData) return (uint)m_RuntimeData.posFormat | ((uint)m_RuntimeData.scaleFormat << 8) | ((uint)m_RuntimeData.shFormat << 16);
            return (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
        }

        uint GetSplatPosFormatInt()
        {
            if (HasExternalBuffers)  return m_ExternalSplatFormat & 0xFF;
            if (HasValidRuntimeData) return (uint)m_RuntimeData.posFormat;
            return (uint)m_Asset.posFormat;
        }

        void LoadFromRuntimeDataInternal(RuntimeSplatData data)
        {
            m_SplatCount = data.splatCount;

            // Build NativeArrays from managed byte[] and upload to GPU.
            int posLen   = RuntimeSplatProcessing.NextMultipleOf(data.posData.Length,   4);
            int othLen   = RuntimeSplatProcessing.NextMultipleOf(data.othData.Length,   4);
            int shLen    = RuntimeSplatProcessing.NextMultipleOf(data.shData.Length,    4);
            int colLen   = data.colData.Length;
            int chkLen   = data.chkData?.Length ?? 0;
            int chunkInfoSize = UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();

            var posDataArr   = new NativeArray<byte>(posLen, Allocator.Temp);
            var otherDataArr = new NativeArray<byte>(othLen, Allocator.Temp);
            var shDataArr    = new NativeArray<byte>(shLen,  Allocator.Temp);
            var colorDataArr = new NativeArray<byte>(colLen, Allocator.TempJob);
            var chunkDataArr = new NativeArray<byte>(chkLen, Allocator.Temp);

            NativeArray<byte>.Copy(data.posData,              posDataArr,   data.posData.Length);
            NativeArray<byte>.Copy(data.othData,              otherDataArr, data.othData.Length);
            NativeArray<byte>.Copy(data.shData,               shDataArr,    data.shData.Length);
            NativeArray<byte>.Copy(data.colData,              colorDataArr, data.colData.Length);
            if (data.chkData != null)
                NativeArray<byte>.Copy(data.chkData, chunkDataArr, data.chkData.Length);

            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, posDataArr.Length / 4, 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(posDataArr);

            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, otherDataArr.Length / 4, 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(otherDataArr);

            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, shDataArr.Length / 4, 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(shDataArr);

            m_GpuColorData = CreateColorTexture(colorDataArr, data.colorFormat, m_SplatCount);

            if (chkLen > 0)
            {
                // Raw buffer: avoids Vulkan std430 alignment issues with mixed uint/float2 struct layout
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Raw, chkLen / 4, 4) { name = "GaussianChunkData" };
                m_GpuChunks.SetData(chunkDataArr);
                m_GpuChunksValid = true;
            }
            else
            {
                // dummy raw buffer (1 chunk worth of bytes)
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Raw,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>() / 4, 4) { name = "GaussianChunkData" };
                m_GpuChunksValid = false;
            }

            // No layer buffer needed for runtime single-layer data.
            m_GpuLayerData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GaussianLayerData" };

            m_GpuView        = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });

            posDataArr.Dispose();
            otherDataArr.Dispose();
            shDataArr.Dispose();
            colorDataArr.Dispose();
            chunkDataArr.Dispose();

            UpdateSortingType(m_gpuSortType);
            if (m_CSSplatUtilities != null && m_Sorter != null)
                InitSortBuffers(m_SplatCount);
        }

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            // init keys buffer to splat indices
            if (m_CSSplatUtilities == null)
            {
                Debug.LogError("GaussianSplatRenderer: Compute shader is null. Cannot initialize sort buffers.");
                return;
            }

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.SetIndices, Props.SplatSortKeys, m_GpuSortKeys);
            m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.SetIndices, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.SetIndices, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            if (m_Sorter == null)
            {
                Debug.LogError("GaussianSplatRenderer: GPU Sorter is null. Cannot initialize sort buffers.");
                return;
            }

            m_Sorter.Initialize((uint) count, m_GpuSortDistances, m_GpuSortKeys);
        }
        
        static int NextMultipleOf(int size, int multipleOf)
        {
            return (size + multipleOf - 1) / multipleOf * multipleOf;
        }

        public void OnEnable()
        {
            // Activation staggering: if another renderer already claimed this frame's activation
            // slot (e.g. N renderers enabling together in one Activation Track cascade), this one
            // deactivates itself and queues instead of paying its own OnEnable() cost inline —
            // GaussianSplatRenderSystem re-enables one queued renderer per subsequent frame. This
            // spreads N renderers' OnEnable() cost across N frames instead of stacking it in one.
            // Play-Mode/runtime only — an Editor-authoring toggle should never be delayed by this.
            // See performance.session.md 2026-08-13 [C].
            if (Application.isPlaying && !GaussianSplatRenderSystem.instance.ClaimActivationSlot(Time.frameCount))
            {
                GaussianSplatRenderSystem.instance.QueueForActivation(this);
                gameObject.SetActive(false);
                return;
            }

            RefreshEffectLayers();
            Initialize();
            GaussianQualityHUD.Register(this);
        }

        public void RefreshEffectLayers()
        {
            m_EffectLayers = GetComponents<GaussianSplatEffectLayer>();
        }
        
        // Materials are stateless render templates shared across all renderers using the same
        // shader set — every per-renderer/animated value (scale, opacity, gamma, mask, buffers)
        // is set through a MaterialPropertyBlock at draw time (SetAssetDataOnMaterial), never on
        // the Material object itself, so constructing one shared instance per shader combination
        // instead of one per GameObject is safe. Cache key is the 4 source shaders (identical
        // across every splat renderer in practice). Cuts ~4 Material constructions (each can
        // trigger first-use shader variant compilation) per renderer OnEnable() down to a
        // one-time cost for the whole scene. See performance.session.md 2026-08-13 [C].
        readonly struct MatCacheKey : System.IEquatable<MatCacheKey>
        {
            readonly Shader splats, composite, debugPoints, debugBoxes;
            public MatCacheKey(Shader s, Shader c, Shader dp, Shader db) { splats = s; composite = c; debugPoints = dp; debugBoxes = db; }
            public bool Equals(MatCacheKey o) => splats == o.splats && composite == o.composite && debugPoints == o.debugPoints && debugBoxes == o.debugBoxes;
            public override bool Equals(object o) => o is MatCacheKey k && Equals(k);
            public override int GetHashCode() => System.HashCode.Combine(splats, composite, debugPoints, debugBoxes);
        }

        readonly struct MatCacheEntry
        {
            public readonly Material splats, composite, debugPoints, debugBoxes;
            public MatCacheEntry(Material s, Material c, Material dp, Material db) { splats = s; composite = c; debugPoints = dp; debugBoxes = db; }
        }

        static readonly Dictionary<MatCacheKey, MatCacheEntry> s_MaterialCache = new();

        void AcquireSharedMaterials()
        {
            var key = new MatCacheKey(m_ShaderSplats, m_ShaderComposite, m_ShaderDebugPoints, m_ShaderDebugBoxes);
            if (!s_MaterialCache.TryGetValue(key, out var entry))
            {
                var matSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
                var matComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
                var matDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
                var matDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};

                // Pass 1 ZTest: Vulkan/Quest uses reversed-Z (near=1, far=0) so the correct
                // front-to-back depth test is GEqual, not LEqual as on DirectX/PCVR.
                int zTestValue = SystemInfo.usesReversedZBuffer
                    ? (int)UnityEngine.Rendering.CompareFunction.GreaterEqual
                    : (int)UnityEngine.Rendering.CompareFunction.LessEqual;
                matSplats.SetInt(Props.ZTest, zTestValue);

                // Pass 2 BlendOp for the depth prepass RT:
                // reversed-Z: keep highest depth (nearest) → BlendOp.Max (4)
                // conventional-Z: keep lowest depth (nearest) → BlendOp.Min (3)
                int depthBlendOp = SystemInfo.usesReversedZBuffer ? 4 : 3;
                matSplats.SetInt(Props.DepthBlendOp, depthBlendOp);

                entry = new MatCacheEntry(matSplats, matComposite, matDebugPoints, matDebugBoxes);
                s_MaterialCache[key] = entry;
            }

            m_MatSplats = entry.splats;
            m_MatComposite = entry.composite;
            m_MatDebugPoints = entry.debugPoints;
            m_MatDebugBoxes = entry.debugBoxes;
        }

        public void EnsureMaterials()
        {
            if (m_MatSplats != null) return;
            if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null) return;
            if (!SystemInfo.supportsComputeShaders) return;

            AcquireSharedMaterials();
        }

        private void Initialize()
        {
            UpdateSortingType(m_gpuSortType);

            m_LastSortedFrame = 0;
            if (m_ShaderSplats == null || m_ShaderComposite == null || m_ShaderDebugPoints == null || m_ShaderDebugBoxes == null || m_CSSplatUtilities == null)
                return;
            if (!SystemInfo.supportsComputeShaders)
                return;

            AcquireSharedMaterials();

            GaussianSplatRenderSystem.instance.RegisterSplat(this);

            // Mask assigned + MaskT==0 at OnEnable: not supposed to be visible yet, so the
            // GPU init cost can be spread across frames instead of paid in one. IsReady stays
            // false (external mask-reveal drivers should wait on it) until the coroutine
            // finishes. Every other case keeps today's fully-synchronous behavior, unchanged.
            //
            // Request goes through GaussianSplatRenderSystem's scheduler instead of starting
            // the coroutine directly here — when N renderers activate in the same frame (e.g.
            // one Activation Track cascade), the scheduler starts at most one new load per
            // frame (bounded by BudgetedLoadConcurrentMax concurrently in flight), staggering
            // each renderer's expensive first segment onto its own frame instead of stacking
            // them together. See masking.session.md 2026-08-10.
            if (Application.isPlaying && m_Mask != null && m_MaskT == 0f)
            {
                IsReady = false;
                GaussianSplatRenderSystem.instance.RequestBudgetedLoad(this);
            }
            else
            {
                UpdateRessources();
            }
        }

        // Called by GaussianSplatRenderSystem.Tick() when this renderer's queued budgeted-load
        // request is popped and it's this renderer's turn to actually start loading.
        internal void BeginBudgetedLoad()
        {
            StartCoroutine(UpdateRessourcesBudgeted());
        }

        public void UpdateSortingType(GpuSorting.SortType sortType)
        {
            m_CSSplatUtilities = sortType switch
            {
                GpuSorting.SortType.DeviceRadixSort => m_CSSplatUtilities_deviceRadixSort,
                GpuSorting.SortType.FidelityFX => m_CSSplatUtilities_fidelityFX,
                _ => m_CSSplatUtilities_deviceRadixSort // Fall back to other stuff from there if no sorting specified
            };
            
            if (m_CSSplatUtilities == null)
            {
                // Only log error if this appears to be a configuration problem (at least one shader is assigned)
                // Don't log during initial component setup when all shaders are null
                if (m_CSSplatUtilities_deviceRadixSort != null || m_CSSplatUtilities_fidelityFX != null)
                {
                    Debug.LogError($"GaussianSplatRenderer: Compute shader for sort type {sortType} is null. Please assign the compute shader in the inspector.");
                }
                m_Sorter = null;
                return;
            }
            
            m_Sorter = new GpuSorting(sortType, m_CSSplatUtilities);
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            if (m_CSSplatUtilities == null)
            {
                Debug.LogError("GaussianSplatRenderer: Compute shader is null in SetAssetDataOnCS. Cannot set compute shader parameters.");
                return;
            }
            
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = (int) kernel;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos,   HasExternalBuffers ? m_ExternalPos   : m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatLayer, m_GpuLayerData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, HasExternalBuffers ? m_ExternalOther : m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH,    HasExternalBuffers ? m_ExternalSH    : m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, HasExternalBuffers ? m_ExternalColor : m_GpuColorData);
            var dummyBits = HasExternalBuffers ? m_ExternalPos : m_GpuPosData;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? dummyBits);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits,  m_GpuEditDeleted  ?? dummyBits);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)GetSplatFormat());
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);

            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatMaskWeights, m_GpuMaskWeights ?? (HasExternalBuffers ? m_ExternalPos : m_GpuPosData));
            cmb.SetComputeIntParam(cs, Props.SplatMaskValid, m_GpuMaskWeights != null ? 1 : 0);
        }

        public static Texture2D CreateColorTextureForMorph(Unity.Collections.NativeArray<byte> colorDataArr, GaussianSplatAsset.ColorFormat colorFormat, int splatCount)
            => CreateColorTexture(colorDataArr, colorFormat, splatCount);

        static Texture2D CreateColorTexture(Unity.Collections.NativeArray<byte> colorDataArr, GaussianSplatAsset.ColorFormat colorFormat, int splatCount)
        {
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            bool preCompressed = GraphicsFormatUtility.IsCompressedFormat(texFormat) &&
                                 colorDataArr.Length == (int)GraphicsFormatUtility.ComputeMipmapSize(texWidth, texHeight, texFormat);
            if (preCompressed)
            {
                // BC7 bytes were compressed at import time — upload directly (works on Quest)
                tex.SetPixelData(colorDataArr, 0);
            }
            else
            {
                // Raw float32 from legacy import — convert at runtime (Editor only for compressed formats)
                var convertedColorData = GaussianImageCreator.CreateColorData(colorDataArr.Reinterpret<float4>(1), colorFormat);
                tex.SetPixelData(convertedColorData, 0);
                convertedColorData.Dispose();
            }
            tex.Apply(false, true);
            return tex;
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            var activePos   = HasExternalBuffers ? m_ExternalPos   : m_GpuPosData;
            var activeOther = HasExternalBuffers ? m_ExternalOther : m_GpuOtherData;
            var activeSH    = HasExternalBuffers ? m_ExternalSH    : m_GpuSHData;
            var activeColor = HasExternalBuffers ? m_ExternalColor : m_GpuColorData;

            mat.SetBuffer(Props.SplatPos,   activePos);
            mat.SetBuffer(Props.SplatLayer, m_GpuLayerData);
            mat.SetBuffer(Props.SplatOther, activeOther);
            mat.SetBuffer(Props.SplatSH,    activeSH);
            mat.SetTexture(Props.SplatColor, activeColor);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? activePos);
            mat.SetBuffer(Props.SplatDeletedBits,  m_GpuEditDeleted  ?? activePos);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            mat.SetInteger(Props.SplatFormat,      (int)GetSplatFormat());
            mat.SetInteger(Props.SplatCount,       HasExternalBuffers ? m_ExternalSplatCount : m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount,  m_GpuChunksValid ? m_GpuChunks.count : 0);
            mat.SetBuffer(Props.SplatMaskWeights, m_GpuMaskWeights ?? activePos);
            mat.SetInt(Props.SplatMaskValid, m_GpuMaskWeights != null ? 1 : 0);
            mat.SetBuffer(Props.SplatOpacityMult, m_GpuEditOpacityMult ?? activePos);
            mat.SetInt(Props.SplatOpacityMultValid, m_GpuEditOpacityMult != null ? 1 : 0);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            // All buffers are morpher-owned or already set up by SetExternalBuffers — leave them alone.
            if (HasExternalBuffers) return;

            DestroyImmediate(m_GpuColorData);

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuLayerData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            if (!m_GpuChunksExternallyOwned) DisposeBuffer(ref m_GpuChunks);
            else m_GpuChunks = null;
            m_GpuChunksExternallyOwned = false;

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuVisibleIndices);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditOpacityMult);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);
            DisposeBuffer(ref m_GpuMaskWeights);

            m_Sorter?.DisposeResources();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        public void OnDisable()
        {
            GaussianQualityHUD.Unregister(this);
            m_EffectLayerBuffer?.Release();
            m_EffectLayerBuffer = null;
            DeInitialize();
        }

        private void DeInitialize()
        {
            DisposeResourcesForAsset();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);

            // Materials are shared across renderers via s_MaterialCache (see AcquireSharedMaterials) —
            // do not destroy them here, that would pull them out from under every other renderer still
            // using the same cached entry. Just drop this instance's references; the cache owns lifetime.
            m_MatSplats = null;
            m_MatComposite = null;
            m_MatDebugPoints = null;
            m_MatDebugBoxes = null;
        }

        // Reused across CalcViewData calls to avoid a per-frame allocation for the frustum-plane upload.
        static readonly Vector4[] s_FrustumPlaneVectors = new Vector4[6];

        // Reused scratch arrays for hotspot upload — max 8 hotspots matches MAX_HOTSPOTS in the compute shader.
        const int k_MaxHotspots = 8;
        static readonly Vector4[] s_HotspotPositions    = new Vector4[k_MaxHotspots];
        static readonly float[]   s_HotspotFullRadius   = new float[k_MaxHotspots];
        static readonly float[]   s_HotspotAttenRadius  = new float[k_MaxHotspots];

        internal void CalcViewData(CommandBuffer cmb, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            if (m_CSSplatUtilities == null)
            {
                Debug.LogError("GaussianSplatRenderer: Compute shader is null in CalcViewData. Cannot calculate view data.");
                return;
            }

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            //Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            // calculate view dependent data for each splat
            SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            // World-space frustum planes (same GeometryUtility API already used for the CPU-side per-renderer
            // cull in GatherSplatsForCamera), for the per-splat bounding-sphere-vs-frustum cull in CSCalcViewData.
            var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(cam);
            for (int i = 0; i < 6; i++)
            {
                var p = frustumPlanes[i];
                s_FrustumPlaneVectors[i] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }
            cmb.SetComputeVectorArrayParam(m_CSSplatUtilities, Props.FrustumPlanes, s_FrustumPlaneVectors);

            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.DebugForceSplatSize, m_DebugForceUniformScale ? m_DebugForceSplatSize : 0f);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.DebugForceSplatAlpha, m_DebugForceUniformAlpha ? m_DebugForceSplatAlpha : -1f);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.ContributionCullThreshold, m_ContributionCullThreshold);

            // Hotspot LOD — upload positions and radii from any active GaussianHotspotVolume components.
            var hotspots = GaussianHotspotVolume.ActiveHotspots;
            int hotspotCount = Mathf.Min(hotspots.Count, k_MaxHotspots);
            for (int i = 0; i < hotspotCount; ++i)
            {
                var h = hotspots[i];
                var wp = h.transform.position;
                s_HotspotPositions[i]   = new Vector4(wp.x, wp.y, wp.z, 0f);
                s_HotspotFullRadius[i]  = h.m_FullDetailRadius;
                s_HotspotAttenRadius[i] = h.m_AttenuationRadius;
            }
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.HotspotCount, hotspotCount);
            if (hotspotCount > 0)
            {
                cmb.SetComputeVectorArrayParam(m_CSSplatUtilities, Props.HotspotPositions, s_HotspotPositions);
                cmb.SetComputeFloatParams(m_CSSplatUtilities, Props.HotspotFullRadius, s_HotspotFullRadius);
                cmb.SetComputeFloatParams(m_CSSplatUtilities, Props.HotspotAttenuationRadius, s_HotspotAttenRadius);
            }
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.HotspotDebugVisualize, m_HotspotDebugVisualize ? 1 : 0);

            UpdateEffectLayerBuffer(cmb);

            // Cull survivor counter: 1-uint atomic buffer, cleared each dispatch, incremented in CalcViewData
            // for every splat that survives per-splat culling. Read back on CPU to measure cull effectiveness.
            if (m_GpuVisibleIndices == null || m_GpuVisibleIndices.count != 1)
            {
                m_GpuVisibleIndices?.Release();
                m_GpuVisibleIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint)) { name = "GaussianCullSurvivorCounter" };
            }
            cmb.SetBufferData(m_GpuVisibleIndices, new uint[] { 0 });
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, Props.VisibleIndices, m_GpuVisibleIndices);

            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcViewData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);

#if UNITY_EDITOR
            // Temporary: async-readback the cull survivor counter every 2s while wiring up per-splat GPU culling.
            // Only requested right after a real dispatch, so the value logged is always from this frame, never stale.
            // Editor-only — this diagnostic log is not needed (and has a real perf cost) in device builds.
            if (Time.realtimeSinceStartup - m_LastCullCounterLogTime > 2f)
            {
                m_LastCullCounterLogTime = Time.realtimeSinceStartup;
                string logName = name;
                int logSplatCount = splatCount;
                UnityEngine.Rendering.AsyncGPUReadback.Request(m_GpuVisibleIndices, req =>
                {
                    if (req.hasError) return;
                    uint counter = req.GetData<uint>()[0];
                    float cutPercent = logSplatCount > 0 ? 100f * (1f - (float)counter / logSplatCount) : 0f;
                    Debug.Log($"[CullCounter] {logName}: splatCount={logSplatCount} survivorCounter={counter} cut={cutPercent:F1}%");
                });
            }

            // Editor-only: feed the Scene-view splat count overlay (GaussianSplatDebugOverlayWindow) while active.
            // Reuses the cull survivor counter already dispatched above -- no extra buffer needed.
            if (s_DebugCountersEnabled)
            {
                UnityEngine.Rendering.AsyncGPUReadback.Request(m_GpuVisibleIndices, req =>
                {
                    if (req.hasError) return;
                    m_LastPostCullCount = req.GetData<uint>()[0];
                });
            }
#endif
        }

        void UpdateEffectLayerBuffer(CommandBuffer cmb)
        {
            // Collect active enabled layers
            int count = 0;
            foreach (var l in m_EffectLayers)
                if (l != null && l.enabled && l.effectType != GaussianSplatEffectLayer.EffectType.None)
                    count++;

            // Grow the CPU array if needed (never shrink to avoid GC churn)
            if (m_EffectLayerData.Length < Mathf.Max(count, 1))
                m_EffectLayerData = new GaussianSplatEffectLayer.ShaderParams[Mathf.Max(count, 1)];

            int i = 0;
            foreach (var l in m_EffectLayers)
                if (l != null && l.enabled && l.effectType != GaussianSplatEffectLayer.EffectType.None)
                    m_EffectLayerData[i++] = l.FillShaderParams();

            // Resize GPU buffer only when count changes
            int bufSize = Mathf.Max(count, 1);
            if (m_EffectLayerBuffer == null || m_EffectLayerBuffer.count != bufSize)
            {
                m_EffectLayerBuffer?.Release();
                m_EffectLayerBuffer = new ComputeBuffer(bufSize,
                    System.Runtime.InteropServices.Marshal.SizeOf<GaussianSplatEffectLayer.ShaderParams>());
            }

            m_EffectLayerBuffer.SetData(m_EffectLayerData, 0, 0, bufSize);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcViewData, s_EffectLayers, m_EffectLayerBuffer);
            cmb.SetComputeIntParam   (m_CSSplatUtilities, s_EffectLayerCount, count);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            if (m_CSSplatUtilities == null || m_Sorter == null || m_GpuSortDistances == null || m_GpuSortKeys == null)
                return;

            Matrix4x4 worldToCamMatrix = m_CenterEyeOnly ? m_centerCamMatrix : cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // calculate distance to the camera for each splat
            cmd.BeginSample(s_ProfSort);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatPos, HasExternalBuffers ? m_ExternalPos : m_GpuPosData);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, Props.SplatLayer, m_GpuLayerData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)GetSplatPosFormatInt());
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.InvertSort, m_OpaqueExperiment ? 1 : 0);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.CalcDistances, out uint gsX, out _, out _);
            cmd.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.CalcDistances, (m_GpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);

            // sort the splats
            m_Sorter.Dispatch(cmd);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            // Advances the shared budgeted-load scheduler at most once per frame, regardless
            // of how many registered renderers call Update() — dedup'd internally by frame
            // number (GaussianSplatRenderSystem.Tick). Any registered renderer can drive this;
            // the system itself is a plain class with no Update() of its own.
            GaussianSplatRenderSystem.instance.Tick(Time.frameCount);

            // While a budgeted load is in progress, UpdateRessourcesBudgetedInternal() (the
            // coroutine) owns this renderer's state entirely — the material/asset/sort/mask
            // checks below have nothing to do until it finishes. Without this guard, EVERY
            // renderer paid real per-frame cost here (asset-hash comparison, ResolveCenterEye-
            // Camera()'s Camera.main/SceneView lookup, the mask-dirty comparison) on EVERY
            // frame for the entire loading window, not just once — confirmed via Profiler as
            // direct SELF-time inside Update() itself (not a child call), consistent with N
            // renderers' small-but-nonzero per-frame costs adding up across a multi-second
            // load window. masking.session.md 2026-08-12.
            if (!IsReady)
                return;

            if (m_MatSplats == null || m_MatComposite == null || m_MatDebugPoints == null || m_MatDebugBoxes == null)
            {
                DeInitialize();
                Initialize();
            }

            // Skip asset-change detection when external buffers or runtime data own the splat data.
            if (!HasExternalBuffers && !HasValidRuntimeData)
            {
                var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
                if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
                {
                    m_PrevAsset = m_Asset;
                    m_PrevHash  = curHash;
                    CreateResourcesForAsset();
                }
            }

            // Re-resolve every frame: in edit mode this tracks whichever scene view camera is active,
            // and in play mode picks up Camera.main if it appears after this renderer was initialized.
            m_centerEyeCamera = ResolveCenterEyeCamera();

            if ((m_Sorter == null || m_Sorter.activeType != m_gpuSortType) && splatCount > 0)
            {
                UpdateSortingType(m_gpuSortType);
                // Only initialize sort buffers if we have valid compute shader and sorter
                if (m_CSSplatUtilities != null && m_Sorter != null)
                {
                    InitSortBuffers(splatCount);
                }
            }

            if (m_CenterEyeOnly && m_centerEyeCamera != null)
            {
                m_centerCamMatrix = m_centerEyeCamera.worldToCameraMatrix;
            }

            int entryCount = m_Mask?.entries?.Count ?? 0;
            if (m_Mask != m_MaskPrev || m_MaskT != m_MaskTPrev || entryCount != m_MaskEntryCountPrev)
            {
                m_MaskDirty = true;
                m_MaskPrev = m_Mask;
                m_MaskTPrev = m_MaskT;
                m_MaskEntryCountPrev = entryCount;
            }
            if (m_MaskDirty)
            {
                UpdateMaskBuffer();
                m_MaskDirty = false;
            }
        }

        // Camera.main is a runtime concept and is frequently null in edit mode (no active MainCamera
        // in the scene), which would otherwise leave m_centerEyeCamera permanently null. Fall back to
        // the active scene view camera so sorting and selection behave correctly while editing.
        static Camera ResolveCenterEyeCamera()
        {
            if (Application.isPlaying)
                return Camera.main;
#if UNITY_EDITOR
            var sceneView = UnityEditor.SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.camera != null)
                return sceneView.camera;
#endif
            return Camera.main;
        }

        public static int CompleteGaussianCount()
        {
            return GaussianSplatRenderSystem.instance.CountAllGaussians();
        }


        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        // Sweeps a sphere of radius brushRadius along the ray from minT to maxT.
        // Returns the world position of the first splat whose center is within brushRadius of the ray,
        // snapping the sphere center to that splat position.
        public bool EditFindBrushStopT(Ray worldRay, float brushRadius, float maxT, out float stopT, out Vector3 snapCenter)
        {
            stopT      = maxT;
            snapCenter = worldRay.origin + worldRay.direction * maxT;
            if (m_GpuPosData == null) return false;

            var posBytes = new byte[m_GpuPosData.count * m_GpuPosData.stride];
            m_GpuPosData.GetData(posBytes);

            uint[] delWords = null;
            if (m_GpuEditDeleted != null)
            {
                delWords = new uint[m_GpuEditDeleted.count];
                m_GpuEditDeleted.GetData(delWords);
            }

            Matrix4x4 o2w = transform.localToWorldMatrix;
            int stride = m_GpuPosData.stride;
            float hitR2 = brushRadius * brushRadius;
            Vector3 rd = worldRay.direction;
            float minT = brushRadius * 2f;

            for (int i = 0; i < m_SplatCount; i++)
            {
                if (delWords != null && (delWords[i >> 5] & (1u << (i & 31))) != 0) continue;

                float x = System.BitConverter.ToSingle(posBytes, i * stride + 0);
                float y = System.BitConverter.ToSingle(posBytes, i * stride + 4);
                float z = System.BitConverter.ToSingle(posBytes, i * stride + 8);
                Vector3 wpos = o2w.MultiplyPoint3x4(new Vector3(x, y, z));

                Vector3 toPoint = wpos - worldRay.origin;
                float t = Vector3.Dot(toPoint, rd);
                if (t <= minT || t >= stopT) continue;

                Vector3 perp = toPoint - rd * t;
                if (perp.sqrMagnitude < hitR2)
                {
                    stopT      = t;
                    snapCenter = wpos; // snap sphere center to this splat's position
                }
            }
            return stopT < maxT;
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            m_CSSplatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix, asset);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        public void SetMaskDirty() => m_MaskDirty = true;

        void UpdateMaskBuffer()
        {
            if (m_Mask == null || m_SplatCount == 0)
            {
                DisposeBuffer(ref m_GpuMaskWeights);
                return;
            }

            if (m_GpuMaskWeights == null || m_GpuMaskWeights.count != m_SplatCount)
            {
                DisposeBuffer(ref m_GpuMaskWeights);
                m_GpuMaskWeights = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, sizeof(float)) { name = "GaussianMaskWeights" };
            }

            var weights = new float[m_SplatCount];

            // Base case: no entries — uniform global alpha across all splats.
            if (m_Mask.entries == null || m_Mask.entries.Count == 0)
            {
                for (int i = 0; i < m_SplatCount; i++) weights[i] = m_MaskT;
                m_GpuMaskWeights.SetData(weights);
                return;
            }

            // Sort entries by weight ascending (non-destructive, guarded against nulls).
            var sorted = new System.Collections.Generic.List<GaussianSplatMask.Entry>();
            foreach (var e in m_Mask.entries)
                if (e != null && e.splatIndices != null) sorted.Add(e);
            sorted.Sort((a, b) => a.weight.CompareTo(b.weight));

            if (sorted.Count == 0)
            {
                for (int i = 0; i < m_SplatCount; i++) weights[i] = m_MaskT;
                m_GpuMaskWeights.SetData(weights);
                return;
            }

            // Find the two bracketing entries. Build a per-splat float[] for each and lerp.
            // Below first entry: splat is visible iff it's in the first entry (lerp from 0).
            // Above last entry: splat is visible iff it's in the last entry (lerp to 1).
            // Between two entries: lerp between their two bitmaps.

            // Find lower and upper bracket.
            int lo = -1, hi = -1;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].weight <= m_MaskT) lo = i;
                if (hi < 0 && sorted[i].weight >= m_MaskT) hi = i;
            }

            if (lo < 0 && hi >= 0)
            {
                // Before first entry: lerp from fully invisible (all 0) to first entry's selection.
                float t = Mathf.InverseLerp(0f, sorted[hi].weight, m_MaskT);
                foreach (var idx in sorted[hi].splatIndices)
                    if (idx >= 0 && idx < m_SplatCount) weights[idx] = t;
            }
            else if (lo >= 0 && hi < 0)
            {
                // After last entry: lerp from last entry's selection to fully visible (all 1).
                float t = Mathf.InverseLerp(sorted[lo].weight, 1f, m_MaskT);
                var loWeights = new float[m_SplatCount];
                foreach (var idx in sorted[lo].splatIndices)
                    if (idx >= 0 && idx < m_SplatCount) loWeights[idx] = 1f;
                for (int i = 0; i < m_SplatCount; i++)
                    weights[i] = Mathf.Lerp(loWeights[i], 1f, t);
            }
            else if (lo == hi)
            {
                // Exactly on an entry.
                foreach (var idx in sorted[lo].splatIndices)
                    if (idx >= 0 && idx < m_SplatCount) weights[idx] = 1f;
            }
            else
            {
                // Between lo and hi: lerp between the two entry bitmaps.
                float t = Mathf.InverseLerp(sorted[lo].weight, sorted[hi].weight, m_MaskT);
                var loWeights = new float[m_SplatCount];
                var hiWeights = new float[m_SplatCount];
                foreach (var idx in sorted[lo].splatIndices)
                    if (idx >= 0 && idx < m_SplatCount) loWeights[idx] = 1f;
                foreach (var idx in sorted[hi].splatIndices)
                    if (idx >= 0 && idx < m_SplatCount) hiWeights[idx] = 1f;
                for (int i = 0; i < m_SplatCount; i++)
                    weights[i] = Mathf.Lerp(loWeights[i], hiWeights[i], t);
            }

            m_GpuMaskWeights.SetData(weights);
        }

        bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                m_GpuEditOpacityMult = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination, m_SplatCount, sizeof(float)) {name = "GaussianSplatOpacityMult"};
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
                var onesData = new float[m_SplatCount];
                for (int i = 0; i < onesData.Length; i++) onesData[i] = 1f;
                m_GpuEditOpacityMult.SetData(onesData);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixP, matProj);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        // Converts an RGB color to HSL (H, S, L each in [0,1]) — matches the compute shader's RgbToHsl exactly,
        // so C#-side reference/tolerance values compare correctly against GPU-side per-splat HSL.
        static Vector3 RgbToHsl(Color c)
        {
            float maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            float minc = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            float l = (maxc + minc) * 0.5f;
            float d = maxc - minc;

            float h = 0f, s = 0f;
            if (d > 1e-6f)
            {
                s = d / (1f - Mathf.Abs(2f * l - 1f) + 1e-6f);
                if (maxc == c.r)
                    h = ((c.g - c.b) / d) % 6f;
                else if (maxc == c.g)
                    h = (c.b - c.r) / d + 2f;
                else
                    h = (c.r - c.g) / d + 4f;
                h /= 6f;
                if (h < 0f) h += 1f;
            }
            return new Vector3(h, s, l);
        }

        // brushCenter: screen pixel coordinates. brushRadius: pixels. cam: the scene view camera.
        // density [0..1]: fraction of splats inside the brush radius that actually get (de)selected per stroke — 1 = every touched splat, dithered thinning below that.
        // seed: caller-supplied roll salt — should stay FIXED for the duration of one continuous stroke (mouse-down to mouse-up), not regenerated per dispatch, otherwise a held drag re-rolls every frame and converges to selecting everything regardless of density.
        // colorMode: when true, adds an HSL-delta gate (against refColor/tolHsl, Hue/Saturation/Lightness tolerances) that a splat must pass BEFORE density gets a say — density still dithers within the color-matched candidates.
        public void EditBrushSelect(Vector2 brushCenter, float brushRadius, Camera cam, bool subtract, float density = 1f, int seed = 0,
            bool colorMode = false, Color refColor = default, Vector3 tolHsl = default)
        {
            if (!EnsureEditingBuffers()) return;

            var tr = transform;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Vector4 screenPar = new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0);

            using var cmb = new CommandBuffer { name = "SplatBrushSelect" };
            SetAssetDataOnCS(cmb, KernelIndices.BrushSelect);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_BrushCenter", (Vector4)new Vector2(brushCenter.x, brushCenter.y));
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_BrushRadius", brushRadius);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.BrushDensity, density);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BrushSeed, seed);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BrushColorModeActive, colorMode ? 1 : 0);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.BrushRefColorHsl, RgbToHsl(refColor));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.BrushColorTolHsl, tolHsl);
            DispatchUtilsAndExecute(cmb, KernelIndices.BrushSelect, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        // worldCenter: sphere center in world space. worldRadius: metres.
        // density [0..1]: fraction of splats inside the brush sphere that actually get (de)selected per stroke — 1 = every touched splat, dithered thinning below that.
        // seed: caller-supplied roll salt — should stay FIXED for the duration of one continuous stroke (see EditBrushSelect).
        // colorMode: see EditBrushSelect — same semantics, HSL-delta gate applied before density thinning.
        public void EditBrushSelectWorld(Vector3 worldCenter, float worldRadius, Camera cam, bool subtract, float density = 1f, int seed = 0,
            bool colorMode = false, Color refColor = default, Vector3 tolHsl = default)
        {
            if (!EnsureEditingBuffers()) return;

            var tr = transform;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            Matrix4x4 matView = cam != null ? cam.worldToCameraMatrix : Matrix4x4.identity;
            Matrix4x4 matMV   = matView * matO2W;

            using var cmb = new CommandBuffer { name = "SplatBrushSelectWorld" };
            SetAssetDataOnCS(cmb, KernelIndices.BrushSelectWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matMV);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_BrushCenterWorld", worldCenter);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, "_BrushRadiusWorld", worldRadius);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.BrushDensity, density);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BrushSeed, seed);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BrushColorModeActive, colorMode ? 1 : 0);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.BrushRefColorHsl, RgbToHsl(refColor));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.BrushColorTolHsl, tolHsl);
            DispatchUtilsAndExecute(cmb, KernelIndices.BrushSelectWorld, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        // Screen-space color pick: finds the splat nearest to screenPos (within pickRadiusPx) and returns its
        // base color (SH DC term, not full view-dependent SH). Returns false if nothing was hit within radius.
        public bool EditPickSplatColor(Vector2 screenPos, float pickRadiusPx, Camera cam, out Color color)
        {
            color = Color.clear;
            if (!EnsureEditingBuffers()) return false;
            if (cam == null) return false;

            var tr = transform;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            Vector4 screenPar = new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0);

            using var pickResult = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination, 5, sizeof(uint));
            pickResult.SetData(new uint[] { 0xFFFFFFFFu, 0, 0, 0, 0 });

            using var cmb = new CommandBuffer { name = "SplatPickColor" };
            SetAssetDataOnCS(cmb, KernelIndices.PickSplatColor);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixVP, matProj * matView);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.PickCenter, (Vector4)screenPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.PickRadius, pickRadiusPx);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.PickSplatColor, Props.PickResultRW, pickResult);
            DispatchUtilsAndExecute(cmb, KernelIndices.PickSplatColor, m_SplatCount);

            var resultData = new uint[5];
            pickResult.GetData(resultData);
            if (resultData[0] == 0xFFFFFFFFu)
                return false; // nothing hit within pickRadiusPx

            color = new Color(
                System.BitConverter.Int32BitsToSingle((int)resultData[1]),
                System.BitConverter.Int32BitsToSingle((int)resultData[2]),
                System.BitConverter.Int32BitsToSingle((int)resultData[3]),
                System.BitConverter.Int32BitsToSingle((int)resultData[4]));
            return true;
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        // Moves selected splats along the world-space axis from worldViewpointPos toward/away from
        // each splat's own drag-start position, scaled by reprojectAmount, and rescales each splat
        // (assuming a perspective viewpoint) so its apparent size from that viewpoint stays constant.
        // Scale resize only applies when the asset's scale+SH formats are Float32 — see CSReprojectSelection.
        public void EditReprojectSelection(Vector3 worldViewpointPos, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, float reprojectAmount)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatReprojectSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ReprojectSelection);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ReprojectSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ReprojectSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.ViewpointPos, worldViewpointPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.ReprojectAmount, reprojectAmount);

            DispatchUtilsAndExecute(cmb, KernelIndices.ReprojectSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        // Returns a CPU snapshot of the raw position buffer for undo. Call before any destructive op.
        public byte[] SnapshotPosData()
        {
            if (m_GpuPosData == null) return null;
            var snap = new byte[m_GpuPosData.count * m_GpuPosData.stride];
            m_GpuPosData.GetData(snap);
            return snap;
        }

        // Returns a CPU snapshot of the raw other-data buffer (rotation+scale+SH index) for undo.
        public byte[] SnapshotOtherData()
        {
            if (m_GpuOtherData == null) return null;
            var snap = new byte[m_GpuOtherData.count * m_GpuOtherData.stride];
            m_GpuOtherData.GetData(snap);
            return snap;
        }

        // Restores a previously snapshotted other-data buffer (used by undo).
        public void RestoreOtherData(byte[] snapshot)
        {
            if (snapshot == null || m_GpuOtherData == null) return;
            m_GpuOtherData.SetData(snapshot);
        }

        // Restores a previously snapshotted position buffer (used by undo).
        public void RestorePosData(byte[] snapshot)
        {
            if (snapshot == null || m_GpuPosData == null) return;
            m_GpuPosData.SetData(snapshot);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        // Returns a CPU snapshot of the deleted bits buffer for undo. Call before any destructive op.
        public uint[] SnapshotDeletedBits()
        {
            if (!EnsureEditingBuffers()) return null;
            var snap = new uint[m_GpuEditDeleted.count];
            m_GpuEditDeleted.GetData(snap);
            return snap;
        }

        // Returns a CPU snapshot of the selected bits buffer (needed for Separate).
        public uint[] SnapshotSelectedBits()
        {
            if (!EnsureEditingBuffers()) return null;
            var snap = new uint[m_GpuEditSelected.count];
            m_GpuEditSelected.GetData(snap);
            return snap;
        }

        // Restores a previously snapshotted deleted-bits buffer (used by undo).
        public void RestoreDeletedBits(uint[] snapshot)
        {
            if (snapshot == null || m_GpuEditDeleted == null) return;
            m_GpuEditDeleted.SetData(snapshot);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        // Returns a CPU snapshot of the per-splat opacity-multiplier buffer (needed for delete undo).
        public float[] SnapshotOpacityMult()
        {
            if (!EnsureEditingBuffers()) return null;
            var snap = new float[m_GpuEditOpacityMult.count];
            m_GpuEditOpacityMult.GetData(snap);
            return snap;
        }

        // Restores a previously snapshotted opacity-multiplier buffer (used by undo).
        public void RestoreOpacityMult(float[] snapshot)
        {
            if (snapshot == null || m_GpuEditOpacityMult == null) return;
            m_GpuEditOpacityMult.SetData(snapshot);
            editModified = true;
        }

        // Restores a previously snapshotted selected-bits buffer (used by undo to reinstate the selection).
        public void RestoreSelectedBits(uint[] snapshot)
        {
            if (snapshot == null || m_GpuEditSelected == null) return;
            m_GpuEditSelected.SetData(snapshot);
            UpdateEditCountsAndBounds();
        }

        // Reads pos + selected bits back to CPU and computes tight bounds in raw GPU position space.
        bool ComputeRawSelectionBounds(out Vector3 center, out Vector3 extents)
        {
            center = extents = Vector3.zero;
            if (m_GpuPosData == null || m_GpuEditSelected == null) return false;

            var posBytes = new byte[m_GpuPosData.count * m_GpuPosData.stride];
            var selWords = new uint[m_GpuEditSelected.count];
            m_GpuPosData.GetData(posBytes);
            m_GpuEditSelected.GetData(selWords);

            var delWords = new uint[m_GpuEditDeleted.count];
            m_GpuEditDeleted.GetData(delWords);

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            int stride = m_GpuPosData.stride; // 12 for Float32
            for (int i = 0; i < m_SplatCount; i++)
            {
                int w = i >> 5; int b = i & 31;
                if ((selWords[w] & (1u << b)) == 0) continue;
                if ((delWords[w] & (1u << b)) != 0) continue;
                float x = System.BitConverter.ToSingle(posBytes, i * stride + 0);
                float y = System.BitConverter.ToSingle(posBytes, i * stride + 4);
                float z = System.BitConverter.ToSingle(posBytes, i * stride + 8);
                var p = new Vector3(x, y, z);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
            if (!any) return false;
            center  = (min + max) * 0.5f;
            extents = Vector3.Max((max - min) * 0.5f, Vector3.one * 1e-5f);
            return true;
        }

        // Delete selected splats filtered by opacity, or by a position-driven random dither. density = threshold [0..1], hardness = edge sharpness [0..1].
        // usePositionMode: false = gate on opacity (existing behavior), true = gate on a deterministic per-splat random roll (real dithering), ignoring opacity.
        public void EditDeleteSelectedWithParams(float density, float hardness, bool usePositionMode = false)
        {
            if (!EnsureEditingBuffers()) return;

            // Compute raw-position-space bounds by reading back pos + selected bits on CPU.
            // This avoids any matrix transform ambiguity — s.pos in the kernel is the same float3 we read here.
            if (!ComputeRawSelectionBounds(out Vector3 rawCenter, out Vector3 rawExtents))
                return;

            using var cmb = new CommandBuffer { name = "SplatDeleteWithParams" };
            SetAssetDataOnCS(cmb, KernelIndices.DeleteSelectedWithParams);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.DeleteSelectedWithParams, Props.SplatDeletedBitsRW, m_GpuEditDeleted);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.DeleteSelectedWithParams, Props.SplatOpacityMultRW, m_GpuEditOpacityMult);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.DeleteDensity, density);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.DeleteHardness, hardness);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.DeleteSelectionCenter, rawCenter);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.DeleteSelectionExtents, rawExtents);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.DeleteUsePosition, usePositionMode ? 1 : 0);
            DispatchUtilsAndExecute(cmb, KernelIndices.DeleteSelectedWithParams, m_SplatCount);

            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        // Reads GPU buffers back to CPU byte arrays. Only valid for VeryHigh (no chunks) assets.
        // Returns false if this renderer uses chunk compression.
        public bool EditReadbackBuffers(out byte[] posData, out byte[] otherData, out byte[] shData, out byte[] colorData)
        {
            posData = otherData = shData = colorData = null;
            if (asset.chunkDataSize > 0)
                return false;
            if (!EnsureEditingBuffers()) return false;

            posData   = new byte[m_GpuPosData.count   * m_GpuPosData.stride];
            otherData = new byte[m_GpuOtherData.count * m_GpuOtherData.stride];
            shData    = new byte[m_GpuSHData.count    * m_GpuSHData.stride];
            m_GpuPosData.GetData(posData);
            m_GpuOtherData.GetData(otherData);
            m_GpuSHData.GetData(shData);

            // Color is a RenderTexture — blit to a readable Texture2D first
            var rt = m_GpuColorData as RenderTexture;
            if (rt == null)
            {
                // Already a plain Texture2D (shouldn't happen for VeryHigh, but handle it)
                var tex = m_GpuColorData as Texture2D;
                colorData = tex != null ? tex.GetRawTextureData() : null;
            }
            else
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readable = new Texture2D(rt.width, rt.height, rt.graphicsFormat, UnityEngine.Experimental.Rendering.TextureCreationFlags.None);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                readable.Apply();
                RenderTexture.active = prev;
                colorData = readable.GetRawTextureData();
                UnityEngine.Object.DestroyImmediate(readable);
            }

            return colorData != null;
        }

        // Marks all currently selected splats as deleted (for use after Separate exports them).
        public void EditDeleteSelection()
        {
            EditDeleteSelected();
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkDataSize > 0)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            // Derive strides from loaded GPU buffers — format-agnostic, works with WL layer structure
            int posStride   = m_GpuPosData.count   * m_GpuPosData.stride   / m_SplatCount;
            int otherStride = m_GpuOtherData.count * m_GpuOtherData.stride / m_SplatCount;
            int shStride    = m_GpuSHData.count    * m_GpuSHData.stride    / m_SplatCount;

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            m_CSSplatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(m_CSSplatUtilities, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}