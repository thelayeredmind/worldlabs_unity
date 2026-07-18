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

        [Tooltip("Only used when no MorphMap is assigned. Instead of treating every splat as unmatched (pure fade), pairs splat i on the Left with splat i on the Right for i in [0, min(countLeft,countRight)) — a raw, correspondence-free 1-1 index lerp. Any remainder beyond that range still fades.")]
        [SerializeField] bool m_MatchByUnsortedIndex;

        [SerializeField, Range(0f, 1f)] float m_T;

        [SerializeField] ComputeShader m_MorphShader;

        [Tooltip("Tint unmatched splats solid red (fading out from Left) / blue (fading in from Right) instead of their real color, to visually isolate the fade-in/fade-out path from matched-pair interpolation when judging correspondence quality.")]
        [SerializeField] bool m_DebugTintUnmatched;

        [Tooltip("Debug: hide unmatched-Left splats (the fading-out side) by forcing their output alpha to 0 — isolates the unmatched-Right/fading-in side for inspection.")]
        [SerializeField] bool m_DebugDisableUnmatchedA;
        [Tooltip("Debug: hide unmatched-Right splats (the fading-in side) by forcing their output alpha to 0 — isolates the unmatched-Left/fading-out side for inspection.")]
        [SerializeField] bool m_DebugDisableUnmatchedB;

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

        /// <summary>Only used when no MorphMap is assigned — see field tooltip.</summary>
        public bool matchByUnsortedIndex
        {
            get => m_MatchByUnsortedIndex;
            set => m_MatchByUnsortedIndex = value;
        }

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

        /// <summary>
        /// Assigns a new asset pair and MorphMap from outside the component (e.g. a Timeline
        /// mixer driving a morph-pair clip) — assetLeft/assetRight/morphMap have no public
        /// setter otherwise, and SwapAssets() only swaps the pair already in place.
        ///
        /// If the component is currently disabled, only the fields are assigned; OnEnable's
        /// existing null-guard/Setup() path picks them up whenever the component is next
        /// enabled. If already enabled and both left/right are non-null, rebuilds immediately
        /// via the same ReleaseGpuResources()/Setup() sequence SwapAssets() uses. If already
        /// enabled and either is null, tears down to idle exactly like OnEnable's null-guard —
        /// releases GPU resources and hands the captured asset back to the renderer.
        /// </summary>
        public void SetAssets(GaussianSplatAsset left, GaussianSplatAsset right, GaussianMorphMap map)
        {
            m_AssetLeft = left;
            m_AssetRight = right;
            m_MorphMap = map;

            if (m_Renderer == null) return; // disabled — OnEnable will Setup() when next enabled

            if (m_AssetLeft == null || m_AssetRight == null)
            {
                m_Renderer.SetExternalBuffers(null, null, null, null, null, false, 0, 0);
                m_Renderer.m_Asset = m_CapturedAsset;
                m_Renderer.UpdateRessources();

                ReleaseGpuResources();
                return;
            }

            ReleaseGpuResources();
            Setup();
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
        // True only for BuildIndexBufferAllUnmatched's no-MorphMap case, where unmatchedLeft/Right
        // are the identity permutation (uL[i]=i) — lets ComputeOutputBoundsAndChunk compute real
        // LOCAL per-chunk bounds (output chunk c == source's own contiguous [c*256, c*256+256)
        // range) instead of one bounds pair for the whole region. A real MorphMap's unmatchedLeft/
        // Right is an arbitrary subset with no such guarantee, so this stays false in that case.
        bool m_UnmatchedSequential;
        uint m_SplatFormat;
        uint m_SplatFormatB;
        uint m_OutTexWidth;
        uint m_OutTexWidthB;   // B asset tex width — may differ when splat counts differ
        int  m_KernelMorphSplats;
        int  m_KernelCopyUnmatchedA;
        int  m_KernelCopyUnmatchedB;

        // Combined world-space bounds covering both assets — used for the MATCHED-PAIR output
        // range only (MorphSplats lerps worldPosA/worldPosB directly, so both sides genuinely
        // need one shared coordinate space to land in).
        GraphicsBuffer m_BufOutChunk;
        int m_OutChunkCount;
        Vector3 m_WorldBoundsMin;
        Vector3 m_WorldBoundsMax;

        // Each source asset's OWN bounds — used for the unmatched-A / unmatched-B output ranges.
        // Unmatched splats have no correspondence to lerp toward (t only ever scales their
        // opacity), so there is no reason to quantize their position against the OTHER asset's
        // extent at all. Pairing a small asset (e.g. ~1 unit across) with a much larger one
        // (e.g. ~470 units) previously forced both through m_WorldBoundsMin/Max's shared 11-bit
        // EncodeNorm11 grid — the small asset's entire geometry collapsed onto a handful of
        // coarse grid cells (a visible regular lattice), independent of and unrelated to the
        // scale-saturation bug found earlier the same session. Fixed by never sharing bounds for
        // splats that never lerp with anything on the other side.
        Vector3 m_BoundsMinLeft;
        Vector3 m_BoundsMaxLeft;
        Vector3 m_BoundsMinRight;
        Vector3 m_BoundsMaxRight;

        // ── Unity messages ────────────────────────────────────────────────────

        void OnEnable()
        {
            if (m_AssetLeft == null || m_AssetRight == null)
            {
                Debug.LogWarning("GaussianSplatMorpher: AssetLeft/AssetRight not assigned.", this);
                enabled = false;
                return;
            }
            // m_MorphMap == null is valid — BuildIndexBuffer() treats it as "everything unmatched"
            // (pure fade, no lerp), useful as a raw baseline when judging correspondence quality.

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

                // The morph kernels always write position as Norm11 and scale as Norm6-padded
                // (GaussianSplatting.hlsl's VECTOR_FMT_6_PADDED — same bit layout as Norm6, but
                // 4-byte-aligned per splat per SplatMorph.compute's OUT_OTHER_STRIDE, not the
                // tightly-packed 2-byte stride real Norm6 assets use), regardless of the source
                // assets' formats. Declare both explicitly instead of passing through
                // m_SplatFormat, which still carries asset Left's source pos/scale formats and
                // would make the renderer decode the output with the wrong stride.
                const uint kVectorFmtNorm11 = 2;
                const uint kVectorFmtNorm6Padded = 4; // not a GaussianSplatAsset.VectorFormat value — morph-output-only, see VECTOR_FMT_6_PADDED
                uint outSplatFormat = kVectorFmtNorm11
                    | (kVectorFmtNorm6Padded << 8)
                    | (m_SplatFormat & 0xFF0000u); // SH format passthrough from asset Left
                m_Renderer.SetExternalBuffers(
                    m_BufOutPos, m_BufOutOther, m_BufOutSH, m_TexOutColor,
                    m_BufOutChunk, m_BufOutChunk != null,
                    m_TotalMorphCount, outSplatFormat);
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

            // _ChunksA/_ChunksB must always have something bound — the kernel's chunkCount==0
            // guard means an uncompressed asset's dummy binding is never actually read, but
            // Unity's compute shader validation still requires every declared resource bound.
            m_MorphShader.SetBuffer(k, "_ChunksA", m_BufChunksA != null ? m_BufChunksA : m_BufPosA);
            m_MorphShader.SetBuffer(k, "_ChunksB", m_BufChunksB != null ? m_BufChunksB : m_BufPosB);

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

            // Pass 1: matched pairs — skip when there's nothing to dispatch (e.g. no MorphMap,
            // BuildIndexBufferAllUnmatched leaves m_MatchedCount at 0); Dispatch with 0 thread
            // groups throws "Thread group size must be above zero" instead of being a no-op.
            if (m_MatchedCount > 0)
            {
                int groups = (m_MatchedCount + 63) / 64;
                if (Time.frameCount % 60 == 0) Debug.Log($"[Morpher] Dispatch MorphSplats — groups={groups} chunkA={chunkCountA} chunkB={chunkCountB} outWidth={m_OutTexWidth} outWidthB={m_OutTexWidthB}");
                m_MorphShader.Dispatch(k, groups, 1, 1);
            }

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
                m_MorphShader.SetBuffer(kA, "_ChunksA", m_BufChunksA != null ? m_BufChunksA : m_BufPosA);
                m_MorphShader.SetInt("_ChunkCountA",  chunkCountA);
                m_MorphShader.SetInt("_UnmatchedCount",  m_UnmatchedLeftCount);
                m_MorphShader.SetInt("_OutOffset",       m_MatchedCount);
                m_MorphShader.SetFloat("_T",             m_T);
                m_MorphShader.SetInt("_SplatFormatA",    (int)m_SplatFormat);
                m_MorphShader.SetInt("_OutWidth",        (int)m_OutTexWidth);
                // Left's own bounds, not the combined union — unmatched splats never lerp
                // toward anything on the other side, so there is no reason to share a
                // coordinate space (and every reason not to — see field doc comment above).
                m_MorphShader.SetVector("_OutBoundsMin", m_BoundsMinLeft);
                m_MorphShader.SetVector("_OutBoundsMax", m_BoundsMaxLeft);
                // Per-chunk local bounds (sequential case only) — must match what the renderer
                // will later decode against, or encode/decode disagree and positions come out
                // garbled. Non-sequential (real MorphMap) case: _OutChunkCount=0, kernel falls
                // back to the flat _OutBoundsMin/Max above, same as before this fix.
                m_MorphShader.SetBuffer(kA, "_OutChunk", m_BufOutChunk);
                m_MorphShader.SetInt("_OutChunkCount", m_UnmatchedSequential ? m_OutChunkCount : 0);
                m_MorphShader.SetInt("_DebugTintUnmatched", m_DebugTintUnmatched ? 1 : 0);
                m_MorphShader.SetInt("_DebugDisableThisPass", m_DebugDisableUnmatchedA ? 1 : 0);
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
                m_MorphShader.SetBuffer(kB, "_ChunksB", m_BufChunksB != null ? m_BufChunksB : m_BufPosB);
                m_MorphShader.SetInt("_ChunkCountB",     chunkCountB);
                m_MorphShader.SetInt("_UnmatchedCount",  m_UnmatchedRightCount);
                m_MorphShader.SetInt("_OutOffset",       m_MatchedCount + m_UnmatchedLeftCount);
                m_MorphShader.SetFloat("_T",             m_T);
                m_MorphShader.SetInt("_SplatFormatA",    (int)m_SplatFormat);
                m_MorphShader.SetInt("_SplatFormatB",    (int)m_SplatFormatB);
                m_MorphShader.SetInt("_OutWidthB",       (int)m_OutTexWidthB);
                m_MorphShader.SetInt("_OutWidth",        (int)m_OutTexWidth);
                // Right's own bounds, not the combined union — see CopyUnmatchedA's identical
                // reasoning above; Pass 3 is the mirror case for the other side.
                m_MorphShader.SetVector("_OutBoundsMin", m_BoundsMinRight);
                m_MorphShader.SetVector("_OutBoundsMax", m_BoundsMaxRight);
                m_MorphShader.SetBuffer(kB, "_OutChunk", m_BufOutChunk);
                m_MorphShader.SetInt("_OutChunkCount", m_UnmatchedSequential ? m_OutChunkCount : 0);
                m_MorphShader.SetInt("_DebugTintUnmatched", m_DebugTintUnmatched ? 1 : 0);
                m_MorphShader.SetInt("_DebugDisableThisPass", m_DebugDisableUnmatchedB ? 1 : 0);
                int groupsB = (m_UnmatchedRightCount + 63) / 64;
                m_MorphShader.Dispatch(kB, groupsB, 1, 1);
            }
        }

        void ComputeOutputBoundsAndChunk()
        {
            (m_BoundsMinLeft, m_BoundsMaxLeft)   = ComputeAssetBounds(m_AssetLeft);
            (m_BoundsMinRight, m_BoundsMaxRight) = ComputeAssetBounds(m_AssetRight);
            m_WorldBoundsMin = Vector3.Min(m_BoundsMinLeft, m_BoundsMinRight);
            m_WorldBoundsMax = Vector3.Max(m_BoundsMaxLeft, m_BoundsMaxRight);

            // Write one ChunkInfo (64 bytes) per kChunkSize splats. _SplatChunkCount > 0 makes
            // LoadSplatData() run its dechunk path for every output splat (col = lerp(colMin,colMax,col);
            // col.a = InvSquareCentered01(col.a); scale = lerp(sclMin,sclMax,scale)^8), so colMin/colMax
            // and sclMin/sclMax must be identity (0,1) for the morph shader's already-final RGB and
            // scale values to survive — only position is meant to be chunk-relative here.
            //
            // Position bounds are NOT one global value for every chunk — each chunk uses whichever
            // region (matched-pairs / unmatched-A / unmatched-B) its FIRST splat index falls into.
            // Matched pairs genuinely need the shared/combined bounds (MorphSplats lerps worldPosA
            // and worldPosB directly, so both must land in one coordinate space) — but unmatched
            // splats have no correspondence to lerp toward (only opacity depends on t) and were
            // previously forced through this SAME shared range regardless of the other asset's
            // scale. Pairing a small asset with a much larger one collapsed the small asset's whole
            // geometry onto a handful of coarse EncodeNorm11 grid cells (a visible regular lattice).
            // A chunk that straddles two regions (region boundaries aren't generally 256-aligned)
            // is assigned by its first splat's region only — acceptable minor precision loss at a
            // single straddle chunk, not the systemic collapse this fixes.
            // Packed half2(min=0,max=1) = 0x3C000000 (f32tof16(1.0)=0x3C00 in the high 16 bits).
            const uint kIdentityMinMax = 0x3C000000u;

            int matchedEnd = m_MatchedCount;
            int unmatchedLeftEnd = m_MatchedCount + m_UnmatchedLeftCount;

            // Sequential case (no MorphMap): output chunk c's splats are source indices
            // [firstSplat-regionStart, firstSplat-regionStart+kChunkSize) RELATIVE TO THE REGION'S
            // OWN START — not [0,256),[256,512)... as if the region always began 256-aligned.
            // The unmatched-Right region's start (unmatchedLeftEnd) is only 256-aligned when
            // Left's splat count happens to be a multiple of kChunkSize; otherwise every Right
            // chunk after the first reads the WRONG source range if bounds are precomputed
            // assuming index-0 alignment (found live: pairing 02_DUSK, splatCount=294404, a
            // non-multiple of 256, as Left with 03_NIGHT as Right shifted every Right chunk's
            // "local" bounds by a constant ~252-splat offset — visible as blocky/chunky smearing
            // that only appeared with THAT asset in the Right role, never as Left, since Left's
            // region always starts at output chunk 0 and is therefore always aligned by
            // construction). Fixed by scanning each output chunk's REAL region-relative range
            // directly, per chunk, rather than a precomputed 256-aligned-from-zero array.

            int chunkCount = (m_TotalMorphCount + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
            var allChunkBytes = new byte[chunkCount * 64];
            for (int c = 0; c < chunkCount; c++)
            {
                int firstSplat = c * GaussianSplatAsset.kChunkSize;
                Vector3 chunkBoundsMin, chunkBoundsMax;
                if (firstSplat < matchedEnd)
                {
                    chunkBoundsMin = m_WorldBoundsMin;  chunkBoundsMax = m_WorldBoundsMax;
                }
                else if (firstSplat < unmatchedLeftEnd)
                {
                    int regionStart = firstSplat - matchedEnd;      // region-relative, NOT assumed 256-aligned
                    int regionCount = m_UnmatchedLeftCount;
                    if (m_UnmatchedSequential && regionCount > 0)
                        (chunkBoundsMin, chunkBoundsMax) = ComputeLocalChunkBounds(m_AssetLeft, regionStart, regionCount);
                    else
                        { chunkBoundsMin = m_BoundsMinLeft;  chunkBoundsMax = m_BoundsMaxLeft; }
                }
                else
                {
                    int regionStart = firstSplat - unmatchedLeftEnd; // region-relative, NOT assumed 256-aligned
                    int regionCount = m_UnmatchedRightCount;
                    if (m_UnmatchedSequential && regionCount > 0)
                        (chunkBoundsMin, chunkBoundsMax) = ComputeLocalChunkBounds(m_AssetRight, regionStart, regionCount);
                    else
                        { chunkBoundsMin = m_BoundsMinRight; chunkBoundsMax = m_BoundsMaxRight; }
                }

                int b = c * 64;
                WriteUInt(allChunkBytes, b + 0,  kIdentityMinMax); // colR
                WriteUInt(allChunkBytes, b + 4,  kIdentityMinMax); // colG
                WriteUInt(allChunkBytes, b + 8,  kIdentityMinMax); // colB
                WriteUInt(allChunkBytes, b + 12, kIdentityMinMax); // colA
                WriteFloat(allChunkBytes, b + 16, chunkBoundsMin.x); WriteFloat(allChunkBytes, b + 20, chunkBoundsMax.x);
                WriteFloat(allChunkBytes, b + 24, chunkBoundsMin.y); WriteFloat(allChunkBytes, b + 28, chunkBoundsMax.y);
                WriteFloat(allChunkBytes, b + 32, chunkBoundsMin.z); WriteFloat(allChunkBytes, b + 36, chunkBoundsMax.z);
                WriteUInt(allChunkBytes, b + 40, kIdentityMinMax); // sclX
                WriteUInt(allChunkBytes, b + 44, kIdentityMinMax); // sclY
                WriteUInt(allChunkBytes, b + 48, kIdentityMinMax); // sclZ
            }

            m_OutChunkCount = chunkCount;
            m_BufOutChunk = new GraphicsBuffer(GraphicsBuffer.Target.Raw, allChunkBytes.Length / 4, 4) { name = "MorphOutChunk" };
            m_BufOutChunk.SetData(allChunkBytes);
        }

        // Computes real LOCAL bounds per kChunkSize-splat range, for the sequential (no-MorphMap)
        // unmatched case where output chunk c is exactly source splats [c*kChunkSize, ...+kChunkSize).
        // Compressed source: reuse its own per-chunk bounds directly (same 256-splat grouping,
        // already computed at import time). Uncompressed source (native Float32, e.g. 02_DUSK):
        // scan the raw position bytes per range directly — one-time cost at Setup(), not per-frame.
        //
        // regionStart/regionCount are RELATIVE TO THE UNMATCHED REGION'S OWN START, not assumed
        // 256-aligned — the region only starts at a 256-aligned source index when the OTHER
        // side's splat count happens to be a multiple of kChunkSize, so a fixed per-256-from-zero
        // precomputed array (the previous approach) reads the wrong source range for every chunk
        // after the first whenever it isn't. Computed per output chunk instead, directly from the
        // real region-relative range — correct regardless of alignment on either side.
        static (Vector3 min, Vector3 max) ComputeLocalChunkBounds(GaussianSplatAsset asset, int regionStart, int regionCount)
        {
            var layer = asset.LayerData[0];
            int firstSplat = regionStart;
            int lastSplat = Mathf.Min(regionStart + GaussianSplatAsset.kChunkSize, regionCount);

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            if (layer.m_ChunkData != null)
            {
                // Compressed source: real per-splat position isn't directly stored (only
                // per-256-splat chunk bounds are) — union whichever source chunk(s) this
                // region-relative range overlaps. Slightly coarser than a per-splat scan when
                // the range straddles two source chunks, but always correct and cheap.
                var chunkBytes = layer.m_ChunkData.GetData<byte>();
                int firstSrcChunk = firstSplat / GaussianSplatAsset.kChunkSize;
                int lastSrcChunk  = (lastSplat - 1) / GaussianSplatAsset.kChunkSize;
                for (int sc = firstSrcChunk; sc <= lastSrcChunk; sc++)
                {
                    int b = sc * 64;
                    min.x = Mathf.Min(min.x, ReadFloat(chunkBytes, b + 16)); max.x = Mathf.Max(max.x, ReadFloat(chunkBytes, b + 20));
                    min.y = Mathf.Min(min.y, ReadFloat(chunkBytes, b + 24)); max.y = Mathf.Max(max.y, ReadFloat(chunkBytes, b + 28));
                    min.z = Mathf.Min(min.z, ReadFloat(chunkBytes, b + 32)); max.z = Mathf.Max(max.z, ReadFloat(chunkBytes, b + 36));
                }
                return (min, max);
            }

            // Uncompressed — posFormat is Float32 (12 bytes/splat, x/y/z).
            var posBytes = layer.m_PosData.GetData<byte>();
            const int stride = 12;
            for (int i = firstSplat; i < lastSplat; i++)
            {
                int b = i * stride;
                min.x = Mathf.Min(min.x, ReadFloat(posBytes, b));
                max.x = Mathf.Max(max.x, ReadFloat(posBytes, b));
                min.y = Mathf.Min(min.y, ReadFloat(posBytes, b + 4));
                max.y = Mathf.Max(max.y, ReadFloat(posBytes, b + 4));
                min.z = Mathf.Min(min.z, ReadFloat(posBytes, b + 8));
                max.z = Mathf.Max(max.z, ReadFloat(posBytes, b + 8));
            }
            return (min, max);
        }

        // Computes an asset's own real position bounds — chunk-union for compressed assets,
        // asset-level import-time bounds for uncompressed ones.
        static (Vector3 min, Vector3 max) ComputeAssetBounds(GaussianSplatAsset asset)
        {
            var layer = asset.LayerData[0];
            if (layer.m_ChunkData == null)
                return (asset.boundsMin, asset.boundsMax);

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var chunkBytes = layer.m_ChunkData.GetData<byte>();
            int chunkCount = chunkBytes.Length / 64;
            for (int i = 0; i < chunkCount; i++)
            {
                int b = i * 64;
                min.x = Mathf.Min(min.x, ReadFloat(chunkBytes, b + 16));
                max.x = Mathf.Max(max.x, ReadFloat(chunkBytes, b + 20));
                min.y = Mathf.Min(min.y, ReadFloat(chunkBytes, b + 24));
                max.y = Mathf.Max(max.y, ReadFloat(chunkBytes, b + 28));
                min.z = Mathf.Min(min.z, ReadFloat(chunkBytes, b + 32));
                max.z = Mathf.Max(max.z, ReadFloat(chunkBytes, b + 36));
            }
            return (min, max);
        }

        static float ReadFloat(Unity.Collections.NativeArray<byte> b, int offset) =>
            System.BitConverter.ToSingle(new[] { b[offset], b[offset+1], b[offset+2], b[offset+3] }, 0);

        static void WriteFloat(byte[] b, int offset, float v)
        {
            var bytes = System.BitConverter.GetBytes(v);
            b[offset] = bytes[0]; b[offset+1] = bytes[1]; b[offset+2] = bytes[2]; b[offset+3] = bytes[3];
        }

        static void WriteUInt(byte[] b, int offset, uint v)
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
        ///
        /// Compares by asset GUID identity when the map carries one (built after this field was
        /// added) — this is unambiguous even when AssetLeft/AssetRight have equal or coincidentally
        /// matching splat counts. Falls back to the splat-count heuristic only for maps built
        /// before leftAssetGuid/rightAssetGuid existed.
        /// </summary>
        bool MapIsSwapped()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(m_MorphMap.leftAssetGuid) || !string.IsNullOrEmpty(m_MorphMap.rightAssetGuid))
            {
                string leftGuid  = UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(m_AssetLeft));
                string rightGuid = UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(m_AssetRight));

                if (leftGuid == m_MorphMap.leftAssetGuid && rightGuid == m_MorphMap.rightAssetGuid)
                    return false;
                if (leftGuid == m_MorphMap.rightAssetGuid && rightGuid == m_MorphMap.leftAssetGuid)
                    return true;

                Debug.LogWarning($"GaussianSplatMorpher: MorphMap asset GUIDs don't match AssetLeft/AssetRight " +
                    $"by identity; falling back to splat-count comparison.", this);
            }
#endif

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
            if (m_MorphMap == null)
            {
                if (m_MatchByUnsortedIndex)
                    BuildIndexBufferIdentityPairs();
                else
                    BuildIndexBufferAllUnmatched();
                return;
            }

            m_UnmatchedSequential = false;
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

        /// <summary>
        /// No MorphMap assigned — every splat on both sides is "unmatched": Left fades out
        /// in place, Right fades in in place, nothing lerps. Useful as a raw correspondence-free
        /// baseline when judging how much of a morph's visual quality actually comes from the
        /// matched-pair path versus the fade path.
        /// </summary>
        void BuildIndexBufferAllUnmatched()
        {
            m_UnmatchedSequential = true;
            m_MatchedCount = 0;
            m_BufIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 8)
                { name = "MorphIndices" }; // zero-length buffers aren't valid — kernel dispatch is skipped anyway (m_MatchedCount == 0)

            m_UnmatchedLeftCount  = m_AssetLeft.splatCount;
            m_UnmatchedRightCount = m_AssetRight.splatCount;
            m_TotalMorphCount     = m_UnmatchedLeftCount + m_UnmatchedRightCount;

            var uL = new int[m_UnmatchedLeftCount];
            for (int i = 0; i < uL.Length; i++) uL[i] = i;
            var uR = new int[m_UnmatchedRightCount];
            for (int i = 0; i < uR.Length; i++) uR[i] = i;

            m_BufUnmatchedLeft = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_UnmatchedLeftCount, 4)
                { name = "MorphUnmatchedLeft" };
            m_BufUnmatchedLeft.SetData(uL);
            m_BufUnmatchedRight = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_UnmatchedRightCount, 4)
                { name = "MorphUnmatchedRight" };
            m_BufUnmatchedRight.SetData(uR);
        }

        /// <summary>
        /// No MorphMap assigned, m_MatchByUnsortedIndex is on — pairs splat i on the Left with
        /// splat i on the Right for i in [0, min(countLeft,countRight)), a raw correspondence-free
        /// 1-1 index lerp. Whichever asset is longer has its remainder ([min..count)) fall back to
        /// the fade path, same as BuildIndexBufferAllUnmatched.
        /// </summary>
        void BuildIndexBufferIdentityPairs()
        {
            m_UnmatchedSequential = true;

            int countLeft  = m_AssetLeft.splatCount;
            int countRight = m_AssetRight.splatCount;
            m_MatchedCount = Mathf.Min(countLeft, countRight);

            var pairs = new int2[m_MatchedCount];
            for (int i = 0; i < m_MatchedCount; i++) pairs[i] = new int2(i, i);
            m_BufIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(m_MatchedCount, 1), 8)
                { name = "MorphIndices" };
            if (m_MatchedCount > 0)
                m_BufIndices.SetData(pairs);

            m_UnmatchedLeftCount  = countLeft  - m_MatchedCount;
            m_UnmatchedRightCount = countRight - m_MatchedCount;
            m_TotalMorphCount     = m_MatchedCount + m_UnmatchedLeftCount + m_UnmatchedRightCount;

            if (m_UnmatchedLeftCount > 0)
            {
                var uL = new int[m_UnmatchedLeftCount];
                for (int i = 0; i < uL.Length; i++) uL[i] = m_MatchedCount + i;
                m_BufUnmatchedLeft = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_UnmatchedLeftCount, 4)
                    { name = "MorphUnmatchedLeft" };
                m_BufUnmatchedLeft.SetData(uL);
            }
            if (m_UnmatchedRightCount > 0)
            {
                var uR = new int[m_UnmatchedRightCount];
                for (int i = 0; i < uR.Length; i++) uR[i] = m_MatchedCount + i;
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
            // Position and Other are always written in the morph kernels' own fixed packed
            // layout (Norm11 pos, rot+Norm6-scale Other) regardless of the source assets'
            // formats — see SplatMorph.compute's OUT_OTHER_STRIDE. Only SH is a passthrough
            // copy of A's source data, so its stride still derives from A's actual format.
            const int posStride = 4;   // EncodeNorm11, 1 uint/splat
            const int otherStride = 8; // OUT_OTHER_STRIDE: rot(4) + Norm6 scale(4)
            int shStride = layerA.m_SHData != null ? layerA.m_SHData.GetData<byte>().Length / nA : 0;

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
            m_BufChunksA?.Dispose(); m_BufChunksB?.Dispose();
            m_BufIndices?.Dispose();
            m_BufUnmatchedLeft?.Dispose();  m_BufUnmatchedRight?.Dispose();
            m_BufOutPos?.Dispose(); m_BufOutOther?.Dispose(); m_BufOutSH?.Dispose(); m_BufOutChunk?.Dispose();

            if (m_TexColorA != null) UnityEngine.Object.DestroyImmediate(m_TexColorA);
            if (m_TexColorB != null) UnityEngine.Object.DestroyImmediate(m_TexColorB);
            if (m_TexOutColor != null) m_TexOutColor.Release();

            m_BufPosA = m_BufPosB = m_BufOtherA = m_BufOtherB = m_BufSHA = m_BufSHB = null;
            m_BufChunksA = m_BufChunksB = null;
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
