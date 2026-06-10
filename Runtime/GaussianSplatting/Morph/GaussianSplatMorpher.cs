// SPDX-License-Identifier: MIT

using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

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

            // GPU buffers, index/unmatched buffers, formats and bounds were derived from the
            // pre-swap Left/Right assignment — tear down and rebuild from the new assignment.
            if (m_Renderer != null)
            {
                ReleaseGpuResources();
                Setup();
            }
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
        int  m_TotalMorphCount; // matchedCount + unmatchedLeft + unmatchedRight
        int  m_UnmatchedLeftCount;
        int  m_UnmatchedRightCount;
        GraphicsBuffer m_BufUnmatchedLeft;  // int[] — indices into AssetLeft
        GraphicsBuffer m_BufUnmatchedRight; // int[] — indices into AssetRight
        uint m_SplatFormat;
        uint m_SplatFormatB;
        uint m_OutTexWidth;
        uint m_OutTexWidthB;   // B asset tex width — may differ when splat counts differ
        int  m_KernelMorphSplats;
        int  m_KernelCopyUnmatchedA;
        int  m_KernelCopyUnmatchedB;

        // Combined world-space bounds covering both assets — output chunk
        GraphicsBuffer m_BufOutChunk;
        Vector3 m_WorldBoundsMin;
        Vector3 m_WorldBoundsMax;

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

            Setup();
        }

        /// <summary>
        /// Uploads both assets, builds the correspondence/unmatched index buffers, allocates
        /// output buffers and binds for the current <see cref="m_T"/>. Called from OnEnable
        /// and again from SwapAssets after ReleaseGpuResources, since every GPU resource here
        /// is derived from the current Left/Right assignment.
        /// </summary>
        void Setup()
        {
            Debug.Log($"[Morpher] Setup — left={m_AssetLeft.name}({m_AssetLeft.splatCount}) right={m_AssetRight.name}({m_AssetRight.splatCount})");

            UploadSourceBuffers();
            Debug.Log($"[Morpher] UploadSourceBuffers — posA={m_BufPosA?.count} posB={m_BufPosB?.count} chunksA={m_BufChunksA?.count} chunksB={m_BufChunksB?.count}");

            BuildIndexBuffer();
            Debug.Log($"[Morpher] BuildIndexBuffer — matched={m_MatchedCount} unmatchedL={m_UnmatchedLeftCount} unmatchedR={m_UnmatchedRightCount} total={m_TotalMorphCount}");

            AllocateOutputBuffers();
            Debug.Log($"[Morpher] AllocateOutputBuffers — outPos={m_BufOutPos?.count} outOther={m_BufOutOther?.count} outTex={m_TexOutColor?.width}x{m_TexOutColor?.height} outWidth={m_OutTexWidth}");

#if UNITY_EDITOR
            if (m_MorphShader == null)
                m_MorphShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.worldlabs.gaussian-splatting/Shaders/SplatMorph.compute");
#endif
            if (m_MorphShader != null)
            {
                m_KernelMorphSplats    = m_MorphShader.FindKernel("MorphSplats");
                m_KernelCopyUnmatchedA = m_MorphShader.HasKernel("CopyUnmatchedA") ? m_MorphShader.FindKernel("CopyUnmatchedA") : -1;
                m_KernelCopyUnmatchedB = m_MorphShader.HasKernel("CopyUnmatchedB") ? m_MorphShader.FindKernel("CopyUnmatchedB") : -1;
            }
            Debug.Log($"[Morpher] Kernels — morphSplats={m_KernelMorphSplats} copyUnmatchedA={m_KernelCopyUnmatchedA} copyUnmatchedB={m_KernelCopyUnmatchedB}");

            ComputeOutputBoundsAndChunk();
            Debug.Log($"[Morpher] OutputBounds — min={m_WorldBoundsMin} max={m_WorldBoundsMax} chunkBuf={m_BufOutChunk?.count}");

            BindBuffersForT();
            Debug.Log($"[Morpher] Setup done — t={m_T:F2} renderer.splatCount={m_Renderer?.splatCount} renderer.hasExt={m_Renderer?.HasExternalBuffers}");
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

            BindBuffersForT();
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

        void BindBuffersForT()
        {
            if (m_T <= 0f)
            {
                // Pure A — bind source buffers directly, no kernel needed
                m_Renderer.SetExternalBuffers(
                    m_BufPosA, m_BufOtherA, m_BufSHA, m_TexColorA,
                    m_BufChunksA, m_BufChunksA != null,
                    m_AssetLeft.splatCount, m_SplatFormat);
            }
            else if (m_T >= 1f)
            {
                // Pure B — bind source buffers directly
                m_Renderer.SetExternalBuffers(
                    m_BufPosB, m_BufOtherB, m_BufSHB, m_TexColorB,
                    m_BufChunksB, m_BufChunksB != null,
                    m_AssetRight.splatCount, m_SplatFormatB);
            }
            else
            {
                // Blend — dispatch kernel, bind output buffers with combined chunk
                if (Time.frameCount % 60 == 0) Debug.Log($"[Morpher] blend t={m_T:F2} matched={m_MatchedCount} total={m_TotalMorphCount} outPos={m_BufOutPos != null} outChunk={m_BufOutChunk != null} shader={m_MorphShader != null}");
                DispatchMorph();
                if (Time.frameCount % 60 == 0) RequestPosReadback();
                m_Renderer.SetExternalBuffers(
                    m_BufOutPos, m_BufOutOther, m_BufOutSH, m_TexOutColor,
                    m_BufOutChunk, m_BufOutChunk != null,
                    m_TotalMorphCount, m_SplatFormat);
            }
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

            // Ceiling division — integer truncation drops the last partial chunk, leaving tail splats undechunked.
            int chunkCountA = m_BufChunksA != null ? (m_AssetLeft.splatCount  + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize : 0;
            int chunkCountB = m_BufChunksB != null ? (m_AssetRight.splatCount + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize : 0;

            m_MorphShader.SetInt(  "_ChunkCountA",   chunkCountA);
            m_MorphShader.SetInt(  "_ChunkCountB",   chunkCountB);
            m_MorphShader.SetVector("_OutBoundsMin", m_WorldBoundsMin);
            m_MorphShader.SetVector("_OutBoundsMax", m_WorldBoundsMax);
            m_MorphShader.SetFloat("_T",             m_T);
            m_MorphShader.SetInt(  "_MorphCount",    m_MatchedCount);
            m_MorphShader.SetInt(  "_SplatFormatA",  (int)m_SplatFormat);
            m_MorphShader.SetInt(  "_SplatFormatB",  (int)m_SplatFormatB);
            m_MorphShader.SetInt(  "_OutWidth",      (int)m_OutTexWidth);
            m_MorphShader.SetInt(  "_OutWidthB",     (int)m_OutTexWidthB);

            // Pass 1: matched pairs
            int groups = (m_MatchedCount + 63) / 64;
            if (Time.frameCount % 60 == 0) Debug.Log($"[Morpher] Dispatch MorphSplats — groups={groups} chunkA={chunkCountA} chunkB={chunkCountB} outWidth={m_OutTexWidth} outWidthB={m_OutTexWidthB}");
            m_MorphShader.Dispatch(k, groups, 1, 1);

            // Pass 2: unmatched A — copy with opacity × (1−t), output at [matchedCount..]
            if (m_UnmatchedLeftCount > 0 && m_BufUnmatchedLeft != null && m_KernelCopyUnmatchedA >= 0)
            {
                int kA = m_KernelCopyUnmatchedA;
                m_MorphShader.SetBuffer(kA, "_UnmatchedIndices", m_BufUnmatchedLeft);
                m_MorphShader.SetBuffer(kA, "_PosA",    m_BufPosA);
                m_MorphShader.SetBuffer(kA, "_OtherA",  m_BufOtherA);
                m_MorphShader.SetBuffer(kA, "_SHA",     m_BufSHA);
                m_MorphShader.SetTexture(kA,"_ColorA",  m_TexColorA);
                m_MorphShader.SetBuffer(kA, "_OutPos",   m_BufOutPos);
                m_MorphShader.SetBuffer(kA, "_OutOther", m_BufOutOther);
                m_MorphShader.SetBuffer(kA, "_OutSH",    m_BufOutSH);
                m_MorphShader.SetTexture(kA,"_OutColor", m_TexOutColor);
                if (m_BufChunksA != null) m_MorphShader.SetBuffer(kA, "_ChunksA", m_BufChunksA);
                m_MorphShader.SetInt("_ChunkCountA",  chunkCountA);
                m_MorphShader.SetInt("_UnmatchedCount",  m_UnmatchedLeftCount);
                m_MorphShader.SetInt("_OutOffset",       m_MatchedCount);
                m_MorphShader.SetFloat("_T",             m_T);
                m_MorphShader.SetInt("_SplatFormatA",    (int)m_SplatFormat);
                m_MorphShader.SetInt("_OutWidth",        (int)m_OutTexWidth);
                m_MorphShader.SetVector("_OutBoundsMin", m_WorldBoundsMin);
                m_MorphShader.SetVector("_OutBoundsMax", m_WorldBoundsMax);
                int groupsA = (m_UnmatchedLeftCount + 63) / 64;
                m_MorphShader.Dispatch(kA, groupsA, 1, 1);
            }

            // Pass 3: unmatched B — copy with opacity × t, output at [matchedCount + unmatchedLeft..]
            if (m_UnmatchedRightCount > 0 && m_BufUnmatchedRight != null && m_KernelCopyUnmatchedB >= 0)
            {
                int kB = m_KernelCopyUnmatchedB;
                m_MorphShader.SetBuffer(kB, "_UnmatchedIndices", m_BufUnmatchedRight);
                m_MorphShader.SetBuffer(kB, "_PosB",    m_BufPosB);
                m_MorphShader.SetBuffer(kB, "_OtherB",  m_BufOtherB);
                m_MorphShader.SetBuffer(kB, "_SHB",     m_BufSHB);
                m_MorphShader.SetTexture(kB,"_ColorB",  m_TexColorB);
                m_MorphShader.SetBuffer(kB, "_OutPos",   m_BufOutPos);
                m_MorphShader.SetBuffer(kB, "_OutOther", m_BufOutOther);
                m_MorphShader.SetBuffer(kB, "_OutSH",    m_BufOutSH);
                m_MorphShader.SetTexture(kB,"_OutColor", m_TexOutColor);
                if (m_BufChunksB != null) m_MorphShader.SetBuffer(kB, "_ChunksB", m_BufChunksB);
                m_MorphShader.SetInt("_ChunkCountB",     chunkCountB);
                m_MorphShader.SetInt("_UnmatchedCount",  m_UnmatchedRightCount);
                m_MorphShader.SetInt("_OutOffset",       m_MatchedCount + m_UnmatchedLeftCount);
                m_MorphShader.SetFloat("_T",             m_T);
                m_MorphShader.SetInt("_SplatFormatB",    (int)m_SplatFormatB);
                m_MorphShader.SetInt("_OutWidthB",       (int)m_OutTexWidthB);
                m_MorphShader.SetInt("_OutWidth",        (int)m_OutTexWidth);
                m_MorphShader.SetVector("_OutBoundsMin", m_WorldBoundsMin);
                m_MorphShader.SetVector("_OutBoundsMax", m_WorldBoundsMax);
                int groupsB = (m_UnmatchedRightCount + 63) / 64;
                m_MorphShader.Dispatch(kB, groupsB, 1, 1);
            }
        }

        void ComputeOutputBoundsAndChunk()
        {
            m_WorldBoundsMin = new Vector3(float.MaxValue,  float.MaxValue,  float.MaxValue);
            m_WorldBoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            AccumulateAssetBounds(m_AssetLeft);
            AccumulateAssetBounds(m_AssetRight);

            // Write one ChunkInfo (64 bytes) per kChunkSize splats, all with the same global bounds.
            // This way every chunkIdx maps to a valid entry — the renderer can dechunk all splats.
            int chunkCount = (m_TotalMorphCount + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
            var allChunkBytes = new byte[chunkCount * 64];
            for (int c = 0; c < chunkCount; c++)
            {
                int b = c * 64;
                WriteFloat(allChunkBytes, b + 16, m_WorldBoundsMin.x); WriteFloat(allChunkBytes, b + 20, m_WorldBoundsMax.x);
                WriteFloat(allChunkBytes, b + 24, m_WorldBoundsMin.y); WriteFloat(allChunkBytes, b + 28, m_WorldBoundsMax.y);
                WriteFloat(allChunkBytes, b + 32, m_WorldBoundsMin.z); WriteFloat(allChunkBytes, b + 36, m_WorldBoundsMax.z);
            }

            m_BufOutChunk = new GraphicsBuffer(GraphicsBuffer.Target.Raw, allChunkBytes.Length / 4, 4) { name = "MorphOutChunk" };
            m_BufOutChunk.SetData(allChunkBytes);
        }

        void AccumulateAssetBounds(GaussianSplatAsset asset)
        {
            var layer = asset.LayerData[0];
            if (layer.m_ChunkData == null) return;
            var chunkBytes = layer.m_ChunkData.GetData<byte>();
            int chunkCount = chunkBytes.Length / 64;
            for (int i = 0; i < chunkCount; i++)
            {
                int b = i * 64;
                m_WorldBoundsMin.x = Mathf.Min(m_WorldBoundsMin.x, ReadFloat(chunkBytes, b + 16));
                m_WorldBoundsMax.x = Mathf.Max(m_WorldBoundsMax.x, ReadFloat(chunkBytes, b + 20));
                m_WorldBoundsMin.y = Mathf.Min(m_WorldBoundsMin.y, ReadFloat(chunkBytes, b + 24));
                m_WorldBoundsMax.y = Mathf.Max(m_WorldBoundsMax.y, ReadFloat(chunkBytes, b + 28));
                m_WorldBoundsMin.z = Mathf.Min(m_WorldBoundsMin.z, ReadFloat(chunkBytes, b + 32));
                m_WorldBoundsMax.z = Mathf.Max(m_WorldBoundsMax.z, ReadFloat(chunkBytes, b + 36));
            }
        }

        static float ReadFloat(Unity.Collections.NativeArray<byte> b, int offset) =>
            System.BitConverter.ToSingle(new[] { b[offset], b[offset+1], b[offset+2], b[offset+3] }, 0);

        static void WriteFloat(byte[] b, int offset, float v)
        {
            var bytes = System.BitConverter.GetBytes(v);
            b[offset] = bytes[0]; b[offset+1] = bytes[1]; b[offset+2] = bytes[2]; b[offset+3] = bytes[3];
        }

        void CopySourceToOutput(GraphicsBuffer pos, GraphicsBuffer other, GraphicsBuffer sh)
        {
            Graphics.CopyBuffer(pos,   m_BufOutPos);
            Graphics.CopyBuffer(other, m_BufOutOther);
            Graphics.CopyBuffer(sh,    m_BufOutSH);
            Graphics.CopyTexture(m_TexColorA, m_TexOutColor);
        }

        /// <summary>
        /// True if the current Left/Right assignment is swapped relative to the orientation
        /// the MorphMap was built with (matchedPairs.x/unmatchedLeft index AssetRight, and
        /// matchedPairs.y/unmatchedRight index AssetLeft).
        /// </summary>
        bool MapIsSwapped()
        {
            if (m_AssetLeft.splatCount == m_MorphMap.splatCountLeft &&
                m_AssetRight.splatCount == m_MorphMap.splatCountRight)
                return false;
            if (m_AssetLeft.splatCount == m_MorphMap.splatCountRight &&
                m_AssetRight.splatCount == m_MorphMap.splatCountLeft)
                return true;

            Debug.LogWarning($"GaussianSplatMorpher: MorphMap splat counts ({m_MorphMap.splatCountLeft}/{m_MorphMap.splatCountRight}) " +
                $"don't match AssetLeft/AssetRight ({m_AssetLeft.splatCount}/{m_AssetRight.splatCount}).", this);
            return false;
        }

        void BuildIndexBuffer()
        {
            bool swapped = MapIsSwapped();
            var pairs = m_MorphMap.matchedPairs;
            m_MatchedCount = pairs.Length;

            // Sort a local copy by dst index (y) ascending — sequential B reads on Adreno.
            // If the map was built with the opposite Left/Right orientation, flip x/y so
            // x always indexes the current AssetLeft and y the current AssetRight.
            var sorted = new int2[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
                sorted[i] = swapped ? new int2(pairs[i].y, pairs[i].x) : pairs[i];
            Array.Sort(sorted, (a, b) => a.y.CompareTo(b.y));

            m_BufIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_MatchedCount, 8)
                { name = "MorphIndices" };
            m_BufIndices.SetData(sorted);

            // Unmatched index buffers — flip left/right if the map orientation is swapped
            var uL = swapped ? m_MorphMap.unmatchedRight : m_MorphMap.unmatchedLeft;
            var uR = swapped ? m_MorphMap.unmatchedLeft  : m_MorphMap.unmatchedRight;
            m_UnmatchedLeftCount  = uL?.Length ?? 0;
            m_UnmatchedRightCount = uR?.Length ?? 0;
            m_TotalMorphCount     = m_MatchedCount + m_UnmatchedLeftCount + m_UnmatchedRightCount;

            if (m_UnmatchedLeftCount > 0)
            {
                m_BufUnmatchedLeft = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_UnmatchedLeftCount, 4)
                    { name = "MorphUnmatchedLeft" };
                m_BufUnmatchedLeft.SetData(uL);
            }
            if (m_UnmatchedRightCount > 0)
            {
                m_BufUnmatchedRight = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_UnmatchedRightCount, 4)
                    { name = "MorphUnmatchedRight" };
                m_BufUnmatchedRight.SetData(uR);
            }
        }

        void AllocateOutputBuffers()
        {
            // Output buffers are sized to the full morph splat count, not just matched pairs.
            // Layout: [0..matchedCount-1] matched | [matchedCount..+uL-1] unmatched A | [..+uR-1] unmatched B
            var layerA = m_AssetLeft.LayerData[0];
            int nA = m_AssetLeft.splatCount;
            // Per-splat strides derived from actual buffer byte lengths
            int posStride   = layerA.m_PosData.GetData<byte>().Length   / nA;
            int otherStride = layerA.m_OtherData.GetData<byte>().Length  / nA;
            int shStride    = layerA.m_SHData != null ? layerA.m_SHData.GetData<byte>().Length / nA : 0;

            int posLen   = (m_TotalMorphCount * posStride   + 3) & ~3;
            int otherLen = (m_TotalMorphCount * otherStride + 3) & ~3;
            int shLen    = shStride > 0 ? (m_TotalMorphCount * shStride + 3) & ~3 : 4;

            var outTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination;
            m_BufOutPos   = new GraphicsBuffer(outTarget, posLen   / 4, 4) { name = "MorphOutPos" };
            m_BufOutOther = new GraphicsBuffer(outTarget, otherLen / 4, 4) { name = "MorphOutOther" };
            m_BufOutSH    = new GraphicsBuffer(outTarget, shLen    / 4, 4) { name = "MorphOutSH" };

            var (tw,  th)  = GaussianSplatAsset.CalcTextureSize(m_TotalMorphCount);
            var (twB, _)   = GaussianSplatAsset.CalcTextureSize(m_AssetRight.splatCount);
            m_OutTexWidth  = (uint)tw;
            m_OutTexWidthB = (uint)twB;
            var fmt        = GaussianSplatAsset.ColorFormatToGraphics(m_AssetLeft.colorFormat);
            m_TexOutColor  = new RenderTexture(tw, th, fmt, UnityEngine.Experimental.Rendering.GraphicsFormat.None)
                { name = "MorphOutColor", enableRandomWrite = true };
            m_TexOutColor.Create();
        }

        void RequestPosReadback()
        {
            AsyncGPUReadback.Request(m_BufOutPos, 48, 0, req =>
            {
                if (req.hasError) { Debug.Log("[Morpher] readback error"); return; }
                var data = req.GetData<uint>();
                uint enc = data[0];
                float x = (enc & 2047u) / 2047f;
                float y = ((enc >> 11) & 1023u) / 1023f;
                float z = ((enc >> 21) & 2047u) / 2047f;
                Debug.Log($"[Morpher] outPos[0] enc={enc} norm=({x:F3},{y:F3},{z:F3}) | outPos[1] enc={data[1]} | outPos[2] enc={data[2]}");
            });
        }

        void ReleaseGpuResources()
        {
            m_BufPosA?.Dispose();   m_BufPosB?.Dispose();
            m_BufOtherA?.Dispose(); m_BufOtherB?.Dispose();
            m_BufSHA?.Dispose();    m_BufSHB?.Dispose();
            m_BufChunksB?.Dispose();
            m_BufIndices?.Dispose();
            m_BufUnmatchedLeft?.Dispose();  m_BufUnmatchedRight?.Dispose();
            m_BufOutPos?.Dispose(); m_BufOutOther?.Dispose(); m_BufOutSH?.Dispose(); m_BufOutChunk?.Dispose();

            if (m_TexColorA != null) UnityEngine.Object.DestroyImmediate(m_TexColorA);
            if (m_TexColorB != null) UnityEngine.Object.DestroyImmediate(m_TexColorB);
            if (m_TexOutColor != null) m_TexOutColor.Release();

            m_BufPosA = m_BufPosB = m_BufOtherA = m_BufOtherB = m_BufSHA = m_BufSHB = null;
            m_BufIndices = m_BufUnmatchedLeft = m_BufUnmatchedRight = null;
            m_BufOutPos = m_BufOutOther = m_BufOutSH = null;
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
