// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        [Range(0.1f, 1.0f)]
        [Tooltip("Scale applied to the splat render texture resolution. Lower = faster but softer.")]
        public float resolutionScale = 0.7f;

        [Space]
        [Tooltip("GSP-CULL-02: Maximum splat layers rendered per pixel. 0 = disabled. " +
                 "Splats render back-to-front so the cap discards the nearest (most prominent) layers last. " +
                 "Try 16–32 to limit overdraw without visible holes.")]
        [Range(0, 64)]
        public int stencilOverdrawCap = 0;

        [Space]
        [Tooltip("GSP-CULL-03: Depth proximity transparency. " +
                 "A Z-prepass records the nearest splat depth per pixel; the transparent pass then discards " +
                 "fragments more than ProximityDepthRange behind that surface. " +
                 "Preserves correct alpha blending for visible layers while eliminating deep overdraw. " +
                 "Requires two render passes (one RT switch on TBDR).")]
        public bool depthProximityTransparency = false;

        [Range(0.001f, 0.2f)]
        [Tooltip("NDC depth range behind the front surface that still renders. " +
                 "Smaller = tighter cull (faster, may lose thin background detail). " +
                 "Start at 0.02 and increase if background splats are clipped.")]
        public float proximityDepthRange = 0.02f;

        class GSRenderPass : ScriptableRenderPass
        {
            RTHandle m_RenderTarget;
            RTHandle m_DepthStencilTarget;
            RTHandle m_PrepassDepthTarget;

            internal ScriptableRenderer m_Renderer = null;
            internal CommandBuffer m_Cmb = null;
            internal float m_ResolutionScale = 0.7f;
            internal int m_StencilOverdrawCap = 0;
            internal bool m_UseDepthProximity = false;
            internal float m_ProximityDepthRange = 0.02f;

            static readonly int s_StencilOverdrawCapId   = Shader.PropertyToID("_StencilOverdrawCap");
            static readonly int s_PrepassDepthId          = Shader.PropertyToID("_GaussianPrepassDepth");
            static readonly int s_ProximityDepthRangeId   = Shader.PropertyToID("_ProximityDepthRange");

            public void Dispose()
            {
                m_RenderTarget?.Release();
                m_DepthStencilTarget?.Release();
                m_PrepassDepthTarget?.Release();
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                RenderTextureDescriptor rtDesc = renderingData.cameraData.cameraTargetDescriptor;
                rtDesc.msaaSamples = 1;
                int w = Mathf.Max(1, Mathf.RoundToInt(rtDesc.width * m_ResolutionScale));
                int h = Mathf.Max(1, Mathf.RoundToInt(rtDesc.height * m_ResolutionScale));

                // Color RT — no depth bits on this descriptor; depth comes from m_DepthStencilTarget.
                var colorDesc = rtDesc;
                colorDesc.depthBufferBits = 0;
                colorDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                colorDesc.width = w;
                colorDesc.height = h;
                RenderingUtils.ReAllocateIfNeeded(ref m_RenderTarget, colorDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_GaussianSplatRT");
                cmd.SetGlobalTexture(m_RenderTarget.name, m_RenderTarget.nameID);

                // Depth+stencil RT — only allocated when actually needed:
                //   stencilOverdrawCap > 0: stencil test requires a stencil surface
                //   opaqueExperiment (Pass 1): ZWrite On + ZTest requires a depth surface
                //   depthProximity + stencilOverdrawCap > 0: stencil cap in Pass 3
                // When none of these apply the stencil block in Pass 0/3 has no live surface,
                // making the test a hardware no-op — zero overhead, all fragments pass.
                bool needDepthStencil = m_StencilOverdrawCap > 0
                    || GaussianSplatRenderSystem.instance.AnyOpaqueExperiment()
                    || (m_UseDepthProximity && m_StencilOverdrawCap > 0);

                if (needDepthStencil)
                {
                    var depthDesc = rtDesc;
                    depthDesc.graphicsFormat = GraphicsFormat.None;
                    depthDesc.depthBufferBits = 32;
                    depthDesc.width = w;
                    depthDesc.height = h;
                    RenderingUtils.ReAllocateIfNeeded(ref m_DepthStencilTarget, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianSplatDepthStencil");

                    ConfigureTarget(m_RenderTarget, m_DepthStencilTarget);
                    ConfigureClear(ClearFlag.Color | ClearFlag.Depth | ClearFlag.Stencil, new Color(0, 0, 0, 0));
                }
                else
                {
                    m_DepthStencilTarget?.Release();
                    m_DepthStencilTarget = null;

                    ConfigureTarget(m_RenderTarget);
                    ConfigureClear(ClearFlag.Color, new Color(0, 0, 0, 0));
                }

                if (m_UseDepthProximity)
                {
                    // R32_SFloat prepass RT receives the nearest-splat NDC depth via BlendOp Min/Max.
                    var prepassDesc = rtDesc;
                    prepassDesc.depthBufferBits = 0;
                    prepassDesc.graphicsFormat = GraphicsFormat.R32_SFloat;
                    prepassDesc.width = w;
                    prepassDesc.height = h;
                    RenderingUtils.ReAllocateIfNeeded(ref m_PrepassDepthTarget, prepassDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianPrepassDepth");
                }
                else
                {
                    m_PrepassDepthTarget?.Release();
                    m_PrepassDepthTarget = null;
                }
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (m_Cmb == null)
                    return;

                var cam = renderingData.cameraData.camera;
                var system = GaussianSplatRenderSystem.instance;

                if (m_UseDepthProximity)
                {
                    // --- Pass 2: Z-prepass → nearest splat depth per pixel ---
                    // Clear prepass RT: reversed-Z far=0, conventional-Z far=1.
                    // Since BlendOp Max (reversed) / Min (conventional) is used, initialising to the
                    // opposite extreme ensures the first real depth value wins.
                    Color prepassClear = SystemInfo.usesReversedZBuffer
                        ? new Color(0, 0, 0, 0)   // reversed-Z: near=1, far=0 → clear to 0
                        : new Color(1, 1, 1, 1);   // conventional: near=0, far=1 → clear to 1
                    m_Cmb.SetRenderTarget(m_PrepassDepthTarget);
                    m_Cmb.ClearRenderTarget(false, true, prepassClear);

                    Material matComposite = system.SortAndRenderSplats(cam, m_Cmb, passOverride: 2);

                    // --- Pass 3: Transparent accumulation + proximity cull ---
                    // Bind depth+stencil only when it was allocated (stencilOverdrawCap > 0).
                    // With no D/S surface the stencil block in Pass 3 is a hardware no-op.
                    if (m_DepthStencilTarget != null)
                    {
                        m_Cmb.SetRenderTarget(m_RenderTarget, m_DepthStencilTarget);
                        m_Cmb.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));
                    }
                    else
                    {
                        m_Cmb.SetRenderTarget(m_RenderTarget);
                        m_Cmb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                    }
                    m_Cmb.SetGlobalTexture(s_PrepassDepthId, m_PrepassDepthTarget);
                    m_Cmb.SetGlobalInteger(s_StencilOverdrawCapId, m_StencilOverdrawCap);
                    m_Cmb.SetGlobalFloat(s_ProximityDepthRangeId, m_ProximityDepthRange);

                    system.SortAndRenderSplats(cam, m_Cmb, passOverride: 3);

                    // Composite
                    m_Cmb.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(m_Cmb, m_RenderTarget, m_Renderer.cameraColorTargetHandle,
                        RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, matComposite, 0);
                    m_Cmb.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                }
                else
                {
                    // Standard path: Pass 0 (transparent) or Pass 1 (opaque experiment).
                    m_Cmb.SetGlobalInteger(s_StencilOverdrawCapId, m_StencilOverdrawCap);

                    Material matComposite = system.SortAndRenderSplats(cam, m_Cmb);

                    m_Cmb.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(m_Cmb, m_RenderTarget, m_Renderer.cameraColorTargetHandle,
                        RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, matComposite, 0);
                    m_Cmb.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                }

                context.ExecuteCommandBuffer(m_Cmb);
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            CommandBuffer cmb = system.InitialClearCmdBuffer(cameraData.camera);
            m_Pass.m_Cmb = cmb;
            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;
            m_Pass.m_Renderer            = renderer;
            m_Pass.m_ResolutionScale      = resolutionScale;
            m_Pass.m_StencilOverdrawCap   = stencilOverdrawCap;
            m_Pass.m_UseDepthProximity    = depthProximityTransparency;
            m_Pass.m_ProximityDepthRange  = proximityDepthRange;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
