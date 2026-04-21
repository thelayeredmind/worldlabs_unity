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

        class GSRenderPass : ScriptableRenderPass
        {
            RTHandle m_RenderTarget;
            RTHandle m_DepthStencilTarget;
            internal ScriptableRenderer m_Renderer = null;
            internal CommandBuffer m_Cmb = null;
            internal float m_ResolutionScale = 0.7f;
            internal int m_StencilOverdrawCap = 0;

            static readonly int s_StencilOverdrawCapId = Shader.PropertyToID("_StencilOverdrawCap");

            public void Dispose()
            {
                m_RenderTarget?.Release();
                m_DepthStencilTarget?.Release();
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                RenderTextureDescriptor rtDesc = renderingData.cameraData.cameraTargetDescriptor;
                rtDesc.msaaSamples = 1;
                int w = Mathf.Max(1, Mathf.RoundToInt(rtDesc.width * m_ResolutionScale));
                int h = Mathf.Max(1, Mathf.RoundToInt(rtDesc.height * m_ResolutionScale));

                // Color RT — no depth bits
                var colorDesc = rtDesc;
                colorDesc.depthBufferBits = 0;
                colorDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                colorDesc.width = w;
                colorDesc.height = h;
                RenderingUtils.ReAllocateIfNeeded(ref m_RenderTarget, colorDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_GaussianSplatRT");
                cmd.SetGlobalTexture(m_RenderTarget.name, m_RenderTarget.nameID);

                if (m_StencilOverdrawCap > 0)
                {
                    // Depth+stencil RT at the same scaled resolution.
                    // depthBufferBits=32 → D24_UNorm_S8_UInt or D32_SFloat_S8_UInt on Vulkan/Adreno.
                    var depthDesc = rtDesc;
                    depthDesc.graphicsFormat = GraphicsFormat.None;
                    depthDesc.depthBufferBits = 32;
                    depthDesc.width = w;
                    depthDesc.height = h;
                    RenderingUtils.ReAllocateIfNeeded(ref m_DepthStencilTarget, depthDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_GaussianSplatDepthStencil");

                    ConfigureTarget(m_RenderTarget, m_DepthStencilTarget);
                    ConfigureClear(ClearFlag.Color | ClearFlag.Stencil, new Color(0, 0, 0, 0));
                }
                else
                {
                    m_DepthStencilTarget?.Release();
                    m_DepthStencilTarget = null;

                    ConfigureTarget(m_RenderTarget);
                    ConfigureClear(ClearFlag.Color, new Color(0, 0, 0, 0));
                }
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (m_Cmb == null)
                    return;

                // Set the per-pixel overdraw cap for the stencil block in RenderGaussianSplats.shader.
                // Value 0 is intentionally kept (Comp Greater with Ref=0 always fails → stencil block
                // is a no-op when cap is 0, but allocating the depth RT is skipped anyway).
                m_Cmb.SetGlobalInteger(s_StencilOverdrawCapId, m_StencilOverdrawCap);

                // add sorting, view calc and drawing commands for each splat object
                Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(renderingData.cameraData.camera, m_Cmb);

                // compose
                m_Cmb.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                Blitter.BlitCameraTexture(m_Cmb, m_RenderTarget, m_Renderer.cameraColorTargetHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, matComposite, 0);
                m_Cmb.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
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
            m_Pass.m_Renderer = renderer;
            m_Pass.m_ResolutionScale = resolutionScale;
            m_Pass.m_StencilOverdrawCap = stencilOverdrawCap;
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
