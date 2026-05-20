// SPDX-License-Identifier: MIT

using System;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Drives a morphing blend between two <see cref="GaussianSplatAsset"/>s using a precomputed
    /// <see cref="GaussianMorphMap"/>. Attach alongside a <see cref="GaussianSplatRenderer"/>.
    ///
    /// OnEnable  — captures the renderer, uploads both assets to GPU, binds external buffers.
    /// OnDisable — hands the destination asset back to the renderer, releases GPU buffers.
    /// Both events are Timeline-driveable via the component's enabled state.
    ///
    /// Exposes <see cref="t"/> (0 = AssetLeft, 1 = AssetRight) for Timeline animation or scripting.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [AddComponentMenu("Gaussian Splatting/Gaussian Splat Morpher")]
    public class GaussianSplatMorpher : MonoBehaviour
    {
        [SerializeField] GaussianSplatAsset m_AssetLeft;
        [SerializeField] GaussianSplatAsset m_AssetRight;

        [Tooltip("Precomputed correspondence. Located automatically when both assets are assigned; assign manually to override.")]
        [SerializeField] GaussianMorphMap m_MorphMap;

        [SerializeField, Range(0f, 1f)] float m_T;

        [SerializeField] ComputeShader m_MorphShader;

        [Header("Auto-play (optional)")]
        [Tooltip("Automatically animate t from 0 to 1 at runtime.")]
        [SerializeField] bool m_AutoPlay;
        [SerializeField, Min(0.01f)] float m_Duration = 2f;
        [SerializeField] bool m_Loop = true;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Blend value. 0 = AssetLeft, 1 = AssetRight. Animatable via Timeline.</summary>
        public float t
        {
            get => m_T;
            set => m_T = Mathf.Clamp01(value);
        }

        public GaussianSplatAsset assetLeft  => m_AssetLeft;
        public GaussianSplatAsset assetRight => m_AssetRight;
        public GaussianMorphMap   morphMap   => m_MorphMap;

        /// <summary>Swap Left and Right assets without rebuilding correspondence.</summary>
        public void SwapAssets()
        {
            (m_AssetLeft, m_AssetRight) = (m_AssetRight, m_AssetLeft);
            m_T = 1f - m_T;
        }

        // ── GPU resources ─────────────────────────────────────────────────────

        GaussianSplatRenderer m_Renderer;
        GaussianSplatAsset    m_CapturedAsset; // renderer's asset before we took over

        // Source buffers — uploaded once at OnEnable, never touched per-frame
        GraphicsBuffer m_BufPosA,    m_BufPosB;
        GraphicsBuffer m_BufOtherA,  m_BufOtherB;
        GraphicsBuffer m_BufSHA,     m_BufSHB;
        GraphicsBuffer m_BufChunksA, m_BufChunksB;
        Texture        m_TexColorA,  m_TexColorB;

        // Sorted correspondence indices — dst (B) side is sequential for Adreno cache
        GraphicsBuffer m_BufIndices; // int2[matchedCount]: x=srcIdx, y=dstIdx sorted by y

        // Output buffers — morph kernel writes here every frame, renderer reads from here
        GraphicsBuffer m_BufOutPos;
        GraphicsBuffer m_BufOutOther;
        GraphicsBuffer m_BufOutSH;
        RenderTexture  m_TexOutColor;

        int  m_MatchedCount;
        uint m_SplatFormat;
        uint m_SplatFormatB;
        uint m_OutTexWidth;
        int  m_KernelMorphSplats;

        // ── Unity messages ────────────────────────────────────────────────────

        void OnEnable()
        {
            if (m_AssetLeft == null || m_AssetRight == null || m_MorphMap == null)
            {
                Debug.LogWarning("GaussianSplatMorpher: assets or MorphMap not assigned.", this);
                enabled = false;
                return;
            }

            m_Renderer      = GetComponent<GaussianSplatRenderer>();
            m_CapturedAsset = m_Renderer.m_Asset;

            UploadSourceBuffers();
            BuildIndexBuffer();
            AllocateOutputBuffers();

            if (m_MorphShader != null)
                m_KernelMorphSplats = m_MorphShader.FindKernel("MorphSplats");

            CopySourceToOutput(m_BufPosA, m_BufOtherA, m_BufSHA);

            m_Renderer.SetExternalBuffers(
                m_BufOutPos, m_BufOutOther, m_BufOutSH, m_TexOutColor,
                m_BufChunksA, m_BufChunksA != null,
                m_AssetLeft.splatCount, m_SplatFormat);
        }

        void OnDisable()
        {
            if (m_Renderer == null) return;

            // Hand destination asset back — renderer resumes standalone
            m_Renderer.SetExternalBuffers(null, null, null, null, null, false, 0, 0);
            m_Renderer.m_Asset = m_T >= 0.5f ? m_AssetRight : m_CapturedAsset;
            m_Renderer.UpdateRessources();

            ReleaseGpuResources();
            m_CapturedAsset = null;
        }

        void Update()
        {
            if (m_AutoPlay)
            {
                m_T += Time.deltaTime / m_Duration;
                if (m_T >= 1f)
                {
                    m_T = m_Loop ? 0f : 1f;
                    if (!m_Loop) m_AutoPlay = false;
                }
            }

            // TODO: dispatch morph kernel
        }

        // ── GPU helpers ───────────────────────────────────────────────────────

        void UploadSourceBuffers()
        {
            UploadAsset(m_AssetLeft,
                ref m_BufPosA, ref m_BufOtherA, ref m_BufSHA, ref m_BufChunksA, ref m_TexColorA);
            UploadAsset(m_AssetRight,
                ref m_BufPosB, ref m_BufOtherB, ref m_BufSHB, ref m_BufChunksB, ref m_TexColorB);

            m_SplatFormat  = (uint)m_AssetLeft.posFormat  | ((uint)m_AssetLeft.scaleFormat  << 8) | ((uint)m_AssetLeft.shFormat  << 16);
            m_SplatFormatB = (uint)m_AssetRight.posFormat | ((uint)m_AssetRight.scaleFormat << 8) | ((uint)m_AssetRight.shFormat << 16);
        }

        static void UploadAsset(GaussianSplatAsset asset,
            ref GraphicsBuffer posOut, ref GraphicsBuffer otherOut,
            ref GraphicsBuffer shOut,  ref GraphicsBuffer chunksOut, ref Texture colorOut)
        {
            var layer = asset.LayerData[0];

            var posBytes   = layer.m_PosData.GetData<byte>();
            var otherBytes = layer.m_OtherData.GetData<byte>();
            var shBytes    = layer.m_SHData != null ? layer.m_SHData.GetData<byte>() : default;
            var colBytes   = layer.m_ColorData.GetData<byte>();

            posOut   = UploadRaw(posBytes,   $"MorphPos_{asset.name}");
            otherOut = UploadRaw(otherBytes, $"MorphOther_{asset.name}");
            shOut    = shBytes.IsCreated
                ? UploadRaw(shBytes, $"MorphSH_{asset.name}")
                : new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, 4, 4) { name = $"MorphSH_{asset.name}_dummy" };

            if (layer.m_ChunkData != null)
            {
                var chunkBytes = layer.m_ChunkData.GetData<byte>();
                chunksOut = UploadRaw(chunkBytes, $"MorphChunks_{asset.name}");
            }
            else
            {
                chunksOut = null;
            }

            colorOut = GaussianSplatRenderer.CreateColorTextureForMorph(colBytes, asset.colorFormat, asset.splatCount);
        }

        static GraphicsBuffer UploadRaw(Unity.Collections.NativeArray<byte> src, string name)
        {
            int aligned = (src.Length + 3) & ~3;
            var buf = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, aligned / 4, 4) { name = name };
            buf.SetData(src);
            return buf;
        }

        void DispatchMorph()
        {
            if (m_MorphShader == null || m_BufOutPos == null) return;

            int k = m_KernelMorphSplats;
            m_MorphShader.SetBuffer(k,  "_MorphPairs", m_BufIndices);
            m_MorphShader.SetBuffer(k,  "_PosA",   m_BufPosA);
            m_MorphShader.SetBuffer(k,  "_PosB",   m_BufPosB);
            m_MorphShader.SetBuffer(k,  "_OtherA", m_BufOtherA);
            m_MorphShader.SetBuffer(k,  "_OtherB", m_BufOtherB);
            m_MorphShader.SetBuffer(k,  "_SHA",    m_BufSHA);
            m_MorphShader.SetBuffer(k,  "_SHB",    m_BufSHB);
            m_MorphShader.SetTexture(k, "_ColorA", m_TexColorA);
            m_MorphShader.SetTexture(k, "_ColorB", m_TexColorB);
            m_MorphShader.SetBuffer(k,  "_OutPos",   m_BufOutPos);
            m_MorphShader.SetBuffer(k,  "_OutOther", m_BufOutOther);
            m_MorphShader.SetBuffer(k,  "_OutSH",    m_BufOutSH);
            m_MorphShader.SetTexture(k, "_OutColor", m_TexOutColor);

            if (m_BufChunksA != null) m_MorphShader.SetBuffer(k, "_ChunksA", m_BufChunksA);
            if (m_BufChunksB != null) m_MorphShader.SetBuffer(k, "_ChunksB", m_BufChunksB);

            m_MorphShader.SetInt(  "_ChunkCountA",   m_BufChunksA != null ? m_AssetLeft.splatCount  / GaussianSplatAsset.kChunkSize : 0);
            m_MorphShader.SetInt(  "_ChunkCountB",   m_BufChunksB != null ? m_AssetRight.splatCount / GaussianSplatAsset.kChunkSize : 0);
            m_MorphShader.SetFloat("_T",             m_T);
            m_MorphShader.SetInt(  "_MorphCount",    m_MatchedCount);
            m_MorphShader.SetInt(  "_SplatFormatA",  (int)m_SplatFormat);
            m_MorphShader.SetInt(  "_SplatFormatB",  (int)m_SplatFormatB);
            m_MorphShader.SetInt(  "_OutWidth",      (int)m_OutTexWidth);

            int groups = (m_MatchedCount + 63) / 64;
            m_MorphShader.Dispatch(k, groups, 1, 1);
        }

        void CopySourceToOutput(GraphicsBuffer pos, GraphicsBuffer other, GraphicsBuffer sh)
        {
            Graphics.CopyBuffer(pos,   m_BufOutPos);
            Graphics.CopyBuffer(other, m_BufOutOther);
            Graphics.CopyBuffer(sh,    m_BufOutSH);
            Graphics.CopyTexture(m_TexColorA, m_TexOutColor);
        }

        void BuildIndexBuffer()
        {
            var pairs = m_MorphMap.matchedPairs;
            m_MatchedCount = pairs.Length;

            // Sort a local copy by dst index (y) ascending — sequential B reads on Adreno
            var sorted = (int2[])pairs.Clone();
            Array.Sort(sorted, (a, b) => a.y.CompareTo(b.y));

            m_BufIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_MatchedCount, 8)
                { name = "MorphIndices" };
            m_BufIndices.SetData(sorted);
        }

        void AllocateOutputBuffers()
        {
            var layer    = m_AssetLeft.LayerData[0];
            int posLen   = (layer.m_PosData.GetData<byte>().Length   + 3) & ~3;
            int otherLen = (layer.m_OtherData.GetData<byte>().Length  + 3) & ~3;
            int shLen    = layer.m_SHData != null
                ? (layer.m_SHData.GetData<byte>().Length + 3) & ~3 : 4;

            var outTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination;
            m_BufOutPos   = new GraphicsBuffer(outTarget, posLen   / 4, 4) { name = "MorphOutPos" };
            m_BufOutOther = new GraphicsBuffer(outTarget, otherLen / 4, 4) { name = "MorphOutOther" };
            m_BufOutSH    = new GraphicsBuffer(outTarget, shLen    / 4, 4) { name = "MorphOutSH" };

            var (tw, th) = GaussianSplatAsset.CalcTextureSize(m_AssetLeft.splatCount);
            m_OutTexWidth = (uint)tw;
            var fmt       = GaussianSplatAsset.ColorFormatToGraphics(m_AssetLeft.colorFormat);
            m_TexOutColor = new RenderTexture(tw, th, fmt, UnityEngine.Experimental.Rendering.GraphicsFormat.None)
                { name = "MorphOutColor", enableRandomWrite = true };
            m_TexOutColor.Create();
        }

        void ReleaseGpuResources()
        {
            m_BufPosA?.Dispose();   m_BufPosB?.Dispose();
            m_BufOtherA?.Dispose(); m_BufOtherB?.Dispose();
            m_BufSHA?.Dispose();    m_BufSHB?.Dispose();
            m_BufChunksB?.Dispose(); // A was handed to the renderer, it owns disposal
            m_BufIndices?.Dispose();
            m_BufOutPos?.Dispose(); m_BufOutOther?.Dispose(); m_BufOutSH?.Dispose();

            if (m_TexColorA != null) UnityEngine.Object.DestroyImmediate(m_TexColorA);
            if (m_TexColorB != null) UnityEngine.Object.DestroyImmediate(m_TexColorB);
            if (m_TexOutColor != null) m_TexOutColor.Release();

            m_BufPosA = m_BufPosB = m_BufOtherA = m_BufOtherB = m_BufSHA = m_BufSHB = null;
            m_BufIndices = m_BufOutPos = m_BufOutOther = m_BufOutSH = null;
            m_TexColorA = m_TexColorB = null;
            m_TexOutColor = null;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Searches the AssetDatabase for a MorphMap whose name matches the canonical
        /// "{left}_{right}_MorphMap" pattern produced by GaussianMorphMapBuilderWindow.
        /// </summary>
        public static GaussianMorphMap FindMorphMap(GaussianSplatAsset left, GaussianSplatAsset right)
        {
            if (left == null || right == null) return null;
            string expected = $"{left.name}_{right.name}_MorphMap";
            string[] guids  = UnityEditor.AssetDatabase.FindAssets($"t:GaussianMorphMap {expected}");
            if (guids.Length == 0) return null;
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GaussianMorphMap>(
                UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
        }
#endif
    }
}
