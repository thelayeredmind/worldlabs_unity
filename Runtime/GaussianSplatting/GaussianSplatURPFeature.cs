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
                 "A Z-prepass records the nearest splat depth per pixel. The transparent pass then discards " +
                 "fragments within ProximityLinearRange metres of that surface (same-surface overdraw), " +
                 "keeping only splats that represent distinct depth layers. " +
                 "Requires two render passes (one RT switch on TBDR).")]
        public bool depthProximityTransparency = false;

        [Range(0.01f, 2.0f)]
        [Tooltip("Linear depth shell (metres) around the nearest surface. Fragments landing inside " +
                 "this shell are discarded as redundant same-surface overdraw. " +
                 "0.01 = barely anything culled. 1.0 = aggressive, only distinct surfaces survive.")]
        public float proximityLinearRange = 0.1f;

        [Range(1.0f, 8.0f)]
        [Tooltip("Opacity multiplier applied to surviving fragments to compensate for discarded layers. " +
                 "Increase when surfaces look too transparent after proximity culling.")]
        public float proximityOpacityBoost = 1.0f;

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
            internal float m_ProximityLinearRange = 0.1f;
            internal float m_ProximityOpacityBoost = 1.0f;

            static readonly int s_StencilOverdrawCapId      = Shader.PropertyToID("_StencilOverdrawCap");
            static readonly int s_PrepassDepthId             = Shader.PropertyToID("_GaussianPrepassDepth");
            static readonly int s_ProximityLinearRangeId     = Shader.PropertyToID("_ProximityLinearRange");
            static readonly int s_ProximityOpacityBoostId    = Shader.PropertyToID("_ProximityOpacityBoost");

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
                    // --- Pass 2: Z-prepass → nearest splat linear eye depth per pixel ---
                    // Prepass stores 1/i.vertex.w (linear eye depth in metres, larger = farther).
                    // BlendOp Min keeps the nearest (smallest) value.
                    // Clear to a large sentinel so any real splat depth wins on first write.
                    m_Cmb.SetRenderTarget(m_PrepassDepthTarget);
                    m_Cmb.ClearRenderTarget(false, true, new Color(1e9f, 0, 0, 0));

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
                    m_Cmb.SetGlobalFloat(s_ProximityLinearRangeId,  m_ProximityLinearRange);
                    m_Cmb.SetGlobalFloat(s_ProximityOpacityBoostId, m_ProximityOpacityBoost);

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
                    //
                    // Explicitly bind the render targets in the command buffer here. Relying on
                    // ConfigureTarget / ConfigureClear (set in OnCameraSetup) to implicitly bind
                    // the depth target is not reliable in Unity 6 URP — the depth attachment may
                    // not be connected when the command buffer actually executes, so ClearRenderTarget
                    // and ZWrite both miss the allocated depth surface.
                    //
                    // On Vulkan/Quest (reversed-Z), ConfigureClear always clears depth to 1.0 which
                    // is NEAR — ZTest GEqual rejects every fragment and nothing renders. The explicit
                    // SetRenderTarget + ClearRenderTarget below fixes this by clearing to the correct
                    // far value (0.0 for reversed-Z, 1.0 otherwise) after binding the correct target.
                    if (m_DepthStencilTarget != null)
                    {
                        m_Cmb.SetRenderTarget(m_RenderTarget, m_DepthStencilTarget);
                        if (system.AnyOpaqueExperiment())
                        {
                            float farDepth = SystemInfo.usesReversedZBuffer ? 0f : 1f;
                            m_Cmb.ClearRenderTarget(clearDepth: true, clearColor: true,
                                backgroundColor: Color.black, depth: farDepth);
                        }
                        else
                        {
                            m_Cmb.ClearRenderTarget(clearDepth: true, clearColor: true,
                                backgroundColor: Color.black, depth: 1f);
                        }
                    }
                    else
                    {
                        m_Cmb.SetRenderTarget(m_RenderTarget);
                        m_Cmb.ClearRenderTarget(clearDepth: false, clearColor: true, backgroundColor: Color.black);
                    }

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
            m_Pass.m_UseDepthProximity     = depthProximityTransparency;
            m_Pass.m_ProximityLinearRange  = proximityLinearRange;
            m_Pass.m_ProximityOpacityBoost = proximityOpacityBoost;
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
