// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Editor window front-end for <see cref="GaussianMorphMapBuilder"/>.
    /// Decoding runs on a background Task to keep the editor responsive.
    /// GPU dispatch (compute shader) is marshalled back to the main thread via EditorApplication.update.
    /// </summary>
    public class GaussianMorphMapBuilderWindow : EditorWindow
    {
        const string kMenuPath         = "Tools/Gaussian Splats/Build Morph Map";
        const string kPrefOutputFolder = "GaussianSplatting.MorphMapBuilder.OutputFolder";
        const string kShaderPath       = "Packages/com.worldlabs.gaussian-splatting/Shaders/SplatCorrespondence.compute";

        [SerializeField] GaussianSplatAsset m_AssetLeft;
        [SerializeField] GaussianSplatAsset m_AssetRight;
        [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";
        [SerializeField] float  m_ColorWeight  = 0.5f;
        [SerializeField] bool   m_UseSpatialProbes;
        [SerializeField] bool   m_UseRemainderFallback = true;

        [SerializeField] GaussianMorphMap m_SampleMap;
        [SerializeField] int m_SampleCount = 20;
        [SerializeField] float m_ProbeAccuracy = 1f;
        string m_SampleReport;

        string m_Status;
        float  m_Progress;
        bool   m_Building;

        CancellationTokenSource  m_Cts;
        Task                     m_BuildTask;

        // Shared state for background-thread → main-thread GPU dispatch
        volatile bool        m_GpuDispatchPending;
        Vector3[]            m_GpuPosL, m_GpuPosR;
        Vector4[]            m_GpuColL, m_GpuColR;
        float                m_GpuPosWeight, m_GpuColWeight;
        int[]                m_GpuResultIndex;
        float[]              m_GpuResultDist;
        float[]              m_GpuResultRawDist;
        Exception            m_GpuException;
        ManualResetEventSlim m_GpuDone;

        [MenuItem(kMenuPath)]
        public static void Open() => OpenWindow(null, null);

        public static void Open(GaussianSplatAsset left, GaussianSplatAsset right) => OpenWindow(left, right);

        static void OpenWindow(GaussianSplatAsset left, GaussianSplatAsset right)
        {
            var w = GetWindowWithRect<GaussianMorphMapBuilderWindow>(new Rect(50, 50, 480, 640), false, "Morph Map Builder", true);
            w.minSize = new Vector2(360, 480);
            w.m_AssetLeft  = left;
            w.m_AssetRight = right;
            w.Show();
        }

        void Awake() => m_OutputFolder = EditorPrefs.GetString(kPrefOutputFolder, "Assets/GaussianAssets");

        void OnEnable()  => EditorApplication.update += OnEditorUpdate;
        void OnDisable() => EditorApplication.update -= OnEditorUpdate;

        void OnEditorUpdate()
        {
            if (!m_Building) return;

            // Service GPU dispatch requests from the background task
            if (m_GpuDispatchPending)
            {
                m_GpuDispatchPending = false;
                try   { DispatchCorrespondenceShader(m_GpuPosL, m_GpuColL, m_GpuPosR, m_GpuColR, m_GpuPosWeight, m_GpuColWeight, out m_GpuResultIndex, out m_GpuResultDist, out m_GpuResultRawDist); }
                catch (Exception e) { m_GpuException = e; }
                finally { m_GpuDone.Set(); }
            }

            if (m_ProbeAssignPending)
            {
                m_ProbeAssignPending = false;
                try   { m_ProbeAssignResult = DispatchSpatialProbeAssignment(m_ProbeDispatchPos, m_ProbeDispatchCellsPerAxis); }
                catch (Exception e) { m_ProbeDispatchException = e; }
                finally { m_GpuDone.Set(); }
            }

            if (m_ProbeResolvePending)
            {
                m_ProbeResolvePending = false;
                try
                {
                    m_BucketMatchedR = DispatchResolveProbeBuckets(
                        m_BucketPosL, m_BucketColL, m_BucketStartL,
                        m_BucketPosR, m_BucketColR, m_BucketStartR, m_BucketOrigIndexR,
                        m_BucketProbeCount, m_BucketPosWeight, m_BucketColWeight);
                }
                catch (Exception e) { m_ProbeDispatchException = e; }
                finally { m_GpuDone.Set(); }
            }

            bool cancelled = EditorUtility.DisplayCancelableProgressBar("Building Morph Map", m_Status, m_Progress);
            if (cancelled) m_Cts?.Cancel();

            Repaint();

            if (m_BuildTask != null && m_BuildTask.IsCompleted)
            {
                EditorUtility.ClearProgressBar();
                m_Building  = false;
                m_BuildTask = null;
                Repaint();
            }
        }

        void OnGUI()
        {
            EditorGUILayout.Space(6);
            m_AssetLeft  = (GaussianSplatAsset)EditorGUILayout.ObjectField("Asset Left",  m_AssetLeft,  typeof(GaussianSplatAsset), false);
            m_AssetRight = (GaussianSplatAsset)EditorGUILayout.ObjectField("Asset Right", m_AssetRight, typeof(GaussianSplatAsset), false);

            EditorGUILayout.Space(4);
            m_ColorWeight = EditorGUILayout.Slider(
                new GUIContent("Color weight", "0 = position only, 1 = color only"),
                m_ColorWeight, 0f, 1f);

            EditorGUILayout.Space(4);
            m_UseSpatialProbes = EditorGUILayout.Toggle(
                new GUIContent("Use spatial probes (experimental)", "Partitions both clouds into a shared spatial grid before matching, instead of the round-based greedy search."),
                m_UseSpatialProbes);
            using (new EditorGUI.DisabledScope(!m_UseSpatialProbes))
                m_ProbeAccuracy = EditorGUILayout.Slider("Probe Accuracy %", m_ProbeAccuracy, 0.01f, 1f);

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(m_UseSpatialProbes))
                m_UseRemainderFallback = EditorGUILayout.Toggle(
                    new GUIContent("Use remainder fallback (round-based only)", "When off, the round-based build reports its OWN unpadded match quality instead of silently filling gaps with an unconstrained full-cloud nearest-neighbor search — a diagnostic to see ground-truth match quality without the fallback's influence. Always off for spatial probes."),
                    m_UseRemainderFallback);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_OutputFolder = EditorGUILayout.TextField("Output folder", m_OutputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    var folder = EditorUtility.OpenFolderPanel("Output folder", m_OutputFolder, "");
                    if (!string.IsNullOrEmpty(folder))
                    {
                        m_OutputFolder = "Assets" + folder.Substring(Application.dataPath.Length);
                        EditorPrefs.SetString(kPrefOutputFolder, m_OutputFolder);
                    }
                }
            }

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(m_AssetLeft == null || m_AssetRight == null || m_Building))
            {
                if (GUILayout.Button("Build Morph Map"))
                    StartBuild();
            }

            if (!string.IsNullOrEmpty(m_Status))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(m_Status, m_Building ? MessageType.Info : MessageType.None);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Match Quality Sample", EditorStyles.boldLabel);
            m_SampleMap   = (GaussianMorphMap)EditorGUILayout.ObjectField("Map",     m_SampleMap,   typeof(GaussianMorphMap),   false);
            m_SampleCount = EditorGUILayout.IntField("Sample count", m_SampleCount);

            using (new EditorGUI.DisabledScope(m_SampleMap == null || m_AssetLeft == null || m_AssetRight == null))
            {
                if (GUILayout.Button("Sample Match Quality"))
                    RunSampleMatchQuality();
            }

            using (new EditorGUI.DisabledScope(m_SampleMap == null))
            {
                if (GUILayout.Button("Analyze Duplicates"))
                    RunAnalyzeDuplicates();
            }

            using (new EditorGUI.DisabledScope(m_AssetLeft == null || m_AssetRight == null || m_Building))
            {
                if (GUILayout.Button("Analyze Candidate Collisions"))
                    RunAnalyzeCandidateCollisions();
            }

            using (new EditorGUI.DisabledScope(m_SampleMap == null || m_AssetRight == null))
            {
                if (GUILayout.Button("Top Duplicated R Splats"))
                    RunTopDuplicatedRight();
            }

            using (new EditorGUI.DisabledScope(m_AssetLeft == null || m_AssetRight == null || m_Building))
            {
                if (GUILayout.Button("Verify Top-K Matches Kernel"))
                    RunVerifyTopKMatches();
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Spatial Probe Partitioning (experimental)", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(m_AssetLeft == null || m_AssetRight == null || m_Building))
            {
                if (GUILayout.Button("Analyze Spatial Probe Occupancy"))
                    RunAnalyzeSpatialProbeOccupancy();
            }

            if (!string.IsNullOrEmpty(m_SampleReport))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.TextArea(m_SampleReport, GUILayout.MinHeight(120));
            }
        }

        void RunSampleMatchQuality()
        {
            var samples = GaussianMorphMapBuilder.SampleMatchQuality(m_SampleMap, m_AssetLeft, m_AssetRight, m_SampleCount);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{samples.Length} samples (posDelta, colorDelta):");
            foreach (var s in samples)
                sb.AppendLine($"pair {s.pairIndex,6}  L{s.leftIndex} -> R{s.rightIndex}   pos={s.posDelta:F4}   color={s.colorDelta:F4}");
            m_SampleReport = sb.ToString();
            Debug.Log(m_SampleReport);
        }

        void RunAnalyzeDuplicates()
        {
            var report = GaussianMorphMapBuilder.AnalyzeDuplicates(m_SampleMap);
            m_SampleReport = $"Duplicate L indices: {report.duplicateLeftIndices}\n" +
                              $"Duplicate R indices: {report.duplicateRightIndices}\n" +
                              $"Largest fan-out: {report.largestFanOut}\n" +
                              $"Excess pairs (extra output splats): {report.excessPairs}\n" +
                              $"Total matched pairs: {m_SampleMap.MatchedCount}";
            Debug.Log(m_SampleReport);
        }

        void RunAnalyzeCandidateCollisions()
        {
            // Runs synchronously on the main thread — calls DispatchCorrespondenceShader directly
            // rather than going through MainThreadDispatcher, whose FindBestMatches blocks waiting
            // for OnEditorUpdate to service it; OnEditorUpdate can't run while OnGUI is on the stack.
            Vector3[] posL, posR;
            Vector4[] colL, colR;
            try
            {
                posL = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetLeft);
                posR = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetRight);
                colL = GaussianMorphMapBuilder.DecodeSplatColors(m_AssetLeft);
                colR = GaussianMorphMapBuilder.DecodeSplatColors(m_AssetRight);
            }
            catch (Exception e)
            {
                m_SampleReport = $"Decode error: {e.Message}";
                Debug.LogException(e);
                return;
            }

            var dispatcher = new SyncDispatcher();
            var report = GaussianMorphMapBuilder.AnalyzeCandidateCollisions(posL, posR, colL, colR, dispatcher, m_ColorWeight);
            m_SampleReport = $"L count: {report.countL}\n" +
                              $"R count: {report.countR}\n" +
                              $"Distinct R chosen: {report.distinctRChosen}\n" +
                              $"Distinct ratio (distinctR / min(L,R)): {report.distinctRatio:F4}";
            Debug.Log(m_SampleReport);
        }

        void RunVerifyTopKMatches()
        {
            // Sanity check for the new FindTopKMatches kernel (sparse-Sinkhorn foundation): the
            // top-1 candidate it returns per L splat must exactly match FindBestMatch's existing
            // single-argmin output, since both compute the identical min-max blended distance —
            // only the number of retained candidates differs. A mismatch here would mean the top-K
            // insertion logic diverges from the proven single-argmin path.
            Vector3[] posL, posR;
            Vector4[] colL, colR;
            try
            {
                posL = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetLeft);
                posR = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetRight);
                colL = GaussianMorphMapBuilder.DecodeSplatColors(m_AssetLeft);
                colR = GaussianMorphMapBuilder.DecodeSplatColors(m_AssetRight);
            }
            catch (Exception e)
            {
                m_SampleReport = $"Decode error: {e.Message}";
                Debug.LogException(e);
                return;
            }

            DispatchCorrespondenceShader(posL, colL, posR, colR, 1f - m_ColorWeight, m_ColorWeight,
                out var bestIndex, out var bestDist, out _);
            DispatchTopKMatches(posL, colL, posR, colR, 1f - m_ColorWeight, m_ColorWeight,
                out var topKIndex, out var topKDist);

            int nL = posL.Length;
            int mismatches = 0;
            for (int i = 0; i < nL; i++)
            {
                if (topKIndex[i * kTopK] != bestIndex[i])
                    mismatches++;
            }

            m_SampleReport = $"Verified {nL} splats: top-1 of top-{kTopK} vs single-argmin — " +
                              $"{mismatches} mismatches ({(mismatches == 0 ? "PASS" : "FAIL")}).";
            Debug.Log(m_SampleReport);
        }

        void RunTopDuplicatedRight()
        {
            var top = GaussianMorphMapBuilder.TopDuplicatedRight(m_SampleMap, m_AssetRight, 20);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Top {top.Length} most-duplicated R splats (fanOut, pos, color):");
            foreach (var t in top)
                sb.AppendLine($"R{t.rightIndex,7}  fanOut={t.fanOutCount,5}  pos=({t.position.x:F2},{t.position.y:F2},{t.position.z:F2})  color=({t.color.x:F3},{t.color.y:F3},{t.color.z:F3},{t.color.w:F3})");
            m_SampleReport = sb.ToString();
            Debug.Log(m_SampleReport);
        }

        class SyncDispatcher : GaussianMorphMapBuilder.ICorrespondenceDispatcher
        {
            public void FindBestMatches(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                float posWeight, float colWeight, out int[] bestIndex, out float[] bestDist, out float[] bestRawDist)
                => DispatchCorrespondenceShader(posL, colL, posR, colR, posWeight, colWeight, out bestIndex, out bestDist, out bestRawDist);
        }

        void StartBuild()
        {
            m_Cts      = new CancellationTokenSource();
            m_GpuDone  = new ManualResetEventSlim(false);
            m_Building = true;
            m_Progress = 0f;

            // TextAsset.GetData is main-thread only — decode here before handing off
            m_Status = "Decoding splat data…";
            Vector3[] posL, posR;
            Vector4[] colL, colR;
            try
            {
                posL = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetLeft);
                posR = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetRight);
                colL = GaussianMorphMapBuilder.DecodeSplatColors(m_AssetLeft);
                colR = GaussianMorphMapBuilder.DecodeSplatColors(m_AssetRight);
            }
            catch (Exception e)
            {
                m_Status   = $"Decode error: {e.Message}";
                m_Building = false;
                Debug.LogException(e);
                return;
            }

            var assetLeft   = m_AssetLeft;
            var assetRight  = m_AssetRight;
            float colorWeight = m_ColorWeight;
            bool useSpatialProbes = m_UseSpatialProbes;
            float probeAccuracy = m_ProbeAccuracy;
            bool useRemainderFallback = m_UseRemainderFallback;
            var ct = m_Cts.Token;

            var dispatcher = new MainThreadDispatcher(this);
            var probeDispatcher = new MainThreadProbeDispatcher(this);
            var gridStrategy = new GaussianMorphMapBuilder.RegularSpatialGridStrategy(probeDispatcher);
            var resolutionStrategy = new GaussianMorphMapBuilder.GpuGreedyProbeResolutionStrategy(probeDispatcher);
            var prog = new Progress<float>(t => { m_Progress = t; m_Status = $"{(int)(t * 100)}%…"; });

            m_BuildTask = Task.Run(() =>
            {
                try
                {
                    GaussianMorphMapBuilder.Result result;
                    if (useSpatialProbes)
                    {
                        m_Status = "Partitioning into probes and resolving…";
                        result = GaussianMorphMapBuilder.BuildViaProbes(posL, posR, colL, colR,
                            gridStrategy, resolutionStrategy, probeAccuracy, colorWeight, prog);
                    }
                    else
                    {
                        result = GaussianMorphMapBuilder.Build(posL, posR, colL, colR, dispatcher, colorWeight, prog, ct, useRemainderFallback);
                    }

                    m_Status = $"Done — {result.matchedPairs.Length} matched, {result.unmatchedLeft.Length} + {result.unmatchedRight.Length} unmatched.";
                    EditorApplication.delayCall += () => SaveAsset(result, assetLeft, assetRight);
                }
                catch (OperationCanceledException)
                {
                    m_Status = "Cancelled.";
                }
                catch (Exception e)
                {
                    m_Status = $"Error: {e.Message}";
                    Debug.LogException(e);
                }
            }, ct);
        }

        // ── Probe GPU dispatcher ─────────────────────────────────────────────

        class MainThreadProbeDispatcher : GaussianMorphMapBuilder.IProbeDispatcher
        {
            readonly GaussianMorphMapBuilderWindow m_Window;
            public MainThreadProbeDispatcher(GaussianMorphMapBuilderWindow w) => m_Window = w;

            public int[] AssignToSpatialProbes(Vector3[] pos, int cellsPerAxis)
            {
                var w = m_Window;
                w.m_ProbeDispatchPos          = pos;
                w.m_ProbeDispatchCellsPerAxis = cellsPerAxis;
                w.m_ProbeDispatchException    = null;
                w.m_GpuDone.Reset();
                w.m_ProbeAssignPending = true;
                w.m_GpuDone.Wait();
                if (w.m_ProbeDispatchException != null)
                    throw new Exception("Spatial probe dispatch failed", w.m_ProbeDispatchException);
                return w.m_ProbeAssignResult;
            }

            public int[] ResolveProbeBuckets(
                Vector4[] bucketPosL, Vector4[] bucketColL, int[] bucketStartL,
                Vector4[] bucketPosR, Vector4[] bucketColR, int[] bucketStartR, int[] bucketOrigIndexR,
                int probeCount, float posWeight, float colWeight)
            {
                var w = m_Window;
                w.m_BucketPosL = bucketPosL; w.m_BucketColL = bucketColL; w.m_BucketStartL = bucketStartL;
                w.m_BucketPosR = bucketPosR; w.m_BucketColR = bucketColR; w.m_BucketStartR = bucketStartR;
                w.m_BucketOrigIndexR = bucketOrigIndexR;
                w.m_BucketProbeCount = probeCount; w.m_BucketPosWeight = posWeight; w.m_BucketColWeight = colWeight;
                w.m_ProbeDispatchException = null;
                w.m_GpuDone.Reset();
                w.m_ProbeResolvePending = true;
                w.m_GpuDone.Wait();
                if (w.m_ProbeDispatchException != null)
                    throw new Exception("Probe bucket resolution dispatch failed", w.m_ProbeDispatchException);
                return w.m_BucketMatchedR;
            }
        }

        // Marshalling state for probe GPU dispatch — same flag+ManualResetEventSlim pump
        // MainThreadDispatcher uses for FindBestMatches, serviced from OnEditorUpdate.
        volatile bool m_ProbeAssignPending;
        Vector3[]     m_ProbeDispatchPos;
        int           m_ProbeDispatchCellsPerAxis;
        int[]         m_ProbeAssignResult;
        Exception     m_ProbeDispatchException;

        volatile bool m_ProbeResolvePending;
        Vector4[]     m_BucketPosL, m_BucketColL, m_BucketPosR, m_BucketColR;
        int[]         m_BucketStartL, m_BucketStartR, m_BucketOrigIndexR;
        int           m_BucketProbeCount;
        float         m_BucketPosWeight, m_BucketColWeight;
        int[]         m_BucketMatchedR;

        // ── GPU dispatcher ────────────────────────────────────────────────────

        class MainThreadDispatcher : GaussianMorphMapBuilder.ICorrespondenceDispatcher
        {
            readonly GaussianMorphMapBuilderWindow m_Window;
            public MainThreadDispatcher(GaussianMorphMapBuilderWindow w) => m_Window = w;

            public void FindBestMatches(Vector3[] posL, Vector4[] colL, Vector3[] posR, Vector4[] colR,
                float posWeight, float colWeight, out int[] bestIndex, out float[] bestDist, out float[] bestRawDist)
            {
                var w = m_Window;
                w.m_GpuPosL      = posL;
                w.m_GpuColL      = colL;
                w.m_GpuPosR      = posR;
                w.m_GpuColR      = colR;
                w.m_GpuPosWeight = posWeight;
                w.m_GpuColWeight = colWeight;
                w.m_GpuException = null;
                w.m_GpuDone.Reset();
                w.m_GpuDispatchPending = true;  // signal main thread

                w.m_GpuDone.Wait();             // block background thread until serviced

                if (w.m_GpuException != null)
                    throw new Exception("GPU dispatch failed", w.m_GpuException);

                bestIndex   = w.m_GpuResultIndex;
                bestDist    = w.m_GpuResultDist;
                bestRawDist = w.m_GpuResultRawDist;
            }
        }

        static void DispatchCorrespondenceShader(
            Vector3[] posL, Vector4[] colL,
            Vector3[] posR, Vector4[] colR,
            float posWeight, float colWeight,
            out int[] bestIndex, out float[] bestDist, out float[] bestRawDist)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(kShaderPath);
            if (shader == null)
                throw new Exception($"SplatCorrespondence.compute not found at {kShaderPath}");

            int nL = posL.Length;
            int nR = posR.Length;

            // Normalise each cloud into [0,1] against its OWN bounds, independently — see GetBounds'
            // header comment for why a shared/combined box is wrong here.
            GetBounds(posL, out var bMinL, out var bMaxL);
            GetBounds(posR, out var bMinR, out var bMaxR);
            float scaleL = Mathf.Max(Mathf.Max(bMaxL.x - bMinL.x, bMaxL.y - bMinL.y), bMaxL.z - bMinL.z);
            float scaleR = Mathf.Max(Mathf.Max(bMaxR.x - bMinR.x, bMaxR.y - bMinR.y), bMaxR.z - bMinR.z);
            scaleL = Mathf.Max(scaleL, 1e-6f);
            scaleR = Mathf.Max(scaleR, 1e-6f);

            var splatsLData = new Vector4[nL];
            var splatsRData = new Vector4[nR];
            for (int i = 0; i < nL; i++)
                splatsLData[i] = new Vector4((posL[i].x - bMinL.x) / scaleL, (posL[i].y - bMinL.y) / scaleL, (posL[i].z - bMinL.z) / scaleL, 0);
            for (int j = 0; j < nR; j++)
                splatsRData[j] = new Vector4((posR[j].x - bMinR.x) / scaleR, (posR[j].y - bMinR.y) / scaleR, (posR[j].z - bMinR.z) / scaleR, 0);

            var bufSplatsL = new ComputeBuffer(nL, 16);
            var bufSplatsR = new ComputeBuffer(nR, 16);
            var bufColsL   = new ComputeBuffer(nL, 16);
            var bufColsR   = new ComputeBuffer(nR, 16);
            var bufMatch   = new ComputeBuffer(nL, 4);
            var bufDist    = new ComputeBuffer(nL, 4);
            var bufRawDist = new ComputeBuffer(nL, 4);

            try
            {
                bufSplatsL.SetData(splatsLData);
                bufSplatsR.SetData(splatsRData);
                bufColsL.SetData(colL);
                bufColsR.SetData(colR);

                int kernel = shader.FindKernel("FindBestMatch");
                shader.SetBuffer(kernel, "_SplatsL",         bufSplatsL);
                shader.SetBuffer(kernel, "_ColorsL",         bufColsL);
                shader.SetBuffer(kernel, "_SplatsR",         bufSplatsR);
                shader.SetBuffer(kernel, "_ColorsR",         bufColsR);
                shader.SetBuffer(kernel, "_BestMatchIndex",  bufMatch);
                shader.SetBuffer(kernel, "_BestMatchDist",   bufDist);
                shader.SetBuffer(kernel, "_BestMatchRawDist", bufRawDist);
                shader.SetInt  ("_CountL",    nL);
                shader.SetInt  ("_CountR",    nR);
                shader.SetFloat("_PosWeight", posWeight);
                shader.SetFloat("_ColWeight", colWeight);

                shader.Dispatch(kernel, (nL + 63) / 64, 1, 1);

                bestIndex   = new int[nL];
                bestDist    = new float[nL];
                bestRawDist = new float[nL];
                bufMatch.GetData(bestIndex);
                bufDist.GetData(bestDist);
                bufRawDist.GetData(bestRawDist);
            }
            finally
            {
                bufSplatsL.Dispose(); bufSplatsR.Dispose();
                bufColsL.Dispose();   bufColsR.Dispose();
                bufMatch.Dispose();   bufDist.Dispose();
                bufRawDist.Dispose();
            }
        }

        const int kTopK = 32;

        // Sparse candidate-list gather for the Sinkhorn matcher: FindBestMatch's single argmin
        // commits each L splat to one winner before any global consistency pass can run, which is
        // what let independent greedy argmin mass-converge onto a small R subset (9.2% distinct
        // ratio measured on a real 294912-splat asset). This mirrors DispatchCorrespondenceShader's
        // setup exactly, just targeting FindTopKMatches and returning kTopK candidates per L splat
        // instead of one.
        static void DispatchTopKMatches(
            Vector3[] posL, Vector4[] colL,
            Vector3[] posR, Vector4[] colR,
            float posWeight, float colWeight,
            out int[] topKIndex, out float[] topKDist)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(kShaderPath);
            if (shader == null)
                throw new Exception($"SplatCorrespondence.compute not found at {kShaderPath}");

            int nL = posL.Length;
            int nR = posR.Length;

            GetBounds(posL, out var bMinL, out var bMaxL);
            GetBounds(posR, out var bMinR, out var bMaxR);
            float scaleL = Mathf.Max(Mathf.Max(bMaxL.x - bMinL.x, bMaxL.y - bMinL.y), bMaxL.z - bMinL.z);
            float scaleR = Mathf.Max(Mathf.Max(bMaxR.x - bMinR.x, bMaxR.y - bMinR.y), bMaxR.z - bMinR.z);
            scaleL = Mathf.Max(scaleL, 1e-6f);
            scaleR = Mathf.Max(scaleR, 1e-6f);

            var splatsLData = new Vector4[nL];
            var splatsRData = new Vector4[nR];
            for (int i = 0; i < nL; i++)
                splatsLData[i] = new Vector4((posL[i].x - bMinL.x) / scaleL, (posL[i].y - bMinL.y) / scaleL, (posL[i].z - bMinL.z) / scaleL, 0);
            for (int j = 0; j < nR; j++)
                splatsRData[j] = new Vector4((posR[j].x - bMinR.x) / scaleR, (posR[j].y - bMinR.y) / scaleR, (posR[j].z - bMinR.z) / scaleR, 0);

            var bufSplatsL = new ComputeBuffer(nL, 16);
            var bufSplatsR = new ComputeBuffer(nR, 16);
            var bufColsL   = new ComputeBuffer(nL, 16);
            var bufColsR   = new ComputeBuffer(nR, 16);
            var bufTopKIdx  = new ComputeBuffer(nL * kTopK, 4);
            var bufTopKDist = new ComputeBuffer(nL * kTopK, 4);

            try
            {
                bufSplatsL.SetData(splatsLData);
                bufSplatsR.SetData(splatsRData);
                bufColsL.SetData(colL);
                bufColsR.SetData(colR);

                int kernel = shader.FindKernel("FindTopKMatches");
                shader.SetBuffer(kernel, "_SplatsL",   bufSplatsL);
                shader.SetBuffer(kernel, "_ColorsL",   bufColsL);
                shader.SetBuffer(kernel, "_SplatsR",   bufSplatsR);
                shader.SetBuffer(kernel, "_ColorsR",   bufColsR);
                shader.SetBuffer(kernel, "_TopKIndex", bufTopKIdx);
                shader.SetBuffer(kernel, "_TopKDist",  bufTopKDist);
                shader.SetInt  ("_CountL",    nL);
                shader.SetInt  ("_CountR",    nR);
                shader.SetFloat("_PosWeight", posWeight);
                shader.SetFloat("_ColWeight", colWeight);

                shader.Dispatch(kernel, (nL + 63) / 64, 1, 1);

                topKIndex = new int[nL * kTopK];
                topKDist  = new float[nL * kTopK];
                bufTopKIdx.GetData(topKIndex);
                bufTopKDist.GetData(topKDist);
            }
            finally
            {
                bufSplatsL.Dispose(); bufSplatsR.Dispose();
                bufColsL.Dispose();   bufColsR.Dispose();
                bufTopKIdx.Dispose(); bufTopKDist.Dispose();
            }
        }

        // Spatial probe partitioning: assigns every splat in a cloud to the nearest cell of a shared
        // regular position-only grid, splitting the global correspondence search into many small
        // local sub-problems instead of one global search or assignment solve (see
        // SplatCorrespondence.compute's AssignToSpatialProbes header for the full rationale —
        // Sinkhorn was tried for the global-assignment approach and abandoned for real numerical
        // instability at this scale). This dispatcher runs the kernel once per cloud (L and R are
        // independent — same grid resolution, each cloud's OWN normalised positions).
        static int[] DispatchSpatialProbeAssignment(Vector3[] pos, int cellsPerAxis)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(kShaderPath);
            if (shader == null)
                throw new Exception($"SplatCorrespondence.compute not found at {kShaderPath}");

            int n = pos.Length;
            GetBounds(pos, out var bMin, out var bMax);
            float scale = Mathf.Max(Mathf.Max(bMax.x - bMin.x, bMax.y - bMin.y), bMax.z - bMin.z);
            scale = Mathf.Max(scale, 1e-6f);

            var splatsData = new Vector4[n];
            for (int i = 0; i < n; i++)
                splatsData[i] = new Vector4((pos[i].x - bMin.x) / scale, (pos[i].y - bMin.y) / scale, (pos[i].z - bMin.z) / scale, 0);

            var bufSplats = new ComputeBuffer(n, 16);
            var bufProbeIndex = new ComputeBuffer(n, 4);

            try
            {
                bufSplats.SetData(splatsData);

                int kernel = shader.FindKernel("AssignToSpatialProbes");
                shader.SetBuffer(kernel, "_Splats",     bufSplats);
                shader.SetBuffer(kernel, "_ProbeIndex",  bufProbeIndex);
                shader.SetInt("_CountL", n); // reused as "count of the cloud being dispatched" — see kernel comment
                shader.SetInt("_CellsPerAxis", cellsPerAxis);
                shader.Dispatch(kernel, (n + 63) / 64, 1, 1);

                var probeIndex = new int[n];
                bufProbeIndex.GetData(probeIndex);
                return probeIndex;
            }
            finally
            {
                bufSplats.Dispose();
                bufProbeIndex.Dispose();
            }
        }

        // Resolves every probe's L/R bucket in one dispatch (one thread per probe — see
        // SplatCorrespondence.compute's ResolveProbeBuckets header). bucketPos/col arrays are
        // already CSR-reordered by the caller (GpuGreedyProbeResolutionStrategy.BuildCsr) so each
        // probe's splats are contiguous; bucketStart are the probeCount+1 offsets into them.
        static int[] DispatchResolveProbeBuckets(
            Vector4[] bucketPosL, Vector4[] bucketColL, int[] bucketStartL,
            Vector4[] bucketPosR, Vector4[] bucketColR, int[] bucketStartR, int[] bucketOrigIndexR,
            int probeCount, float posWeight, float colWeight)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(kShaderPath);
            if (shader == null)
                throw new Exception($"SplatCorrespondence.compute not found at {kShaderPath}");

            int nL = bucketPosL.Length;
            int nR = bucketPosR.Length;

            var bufPosL = new ComputeBuffer(Math.Max(nL, 1), 16);
            var bufColL = new ComputeBuffer(Math.Max(nL, 1), 16);
            var bufPosR = new ComputeBuffer(Math.Max(nR, 1), 16);
            var bufColR = new ComputeBuffer(Math.Max(nR, 1), 16);
            var bufStartL = new ComputeBuffer(bucketStartL.Length, 4);
            var bufStartR = new ComputeBuffer(bucketStartR.Length, 4);
            var bufOrigIndexR = new ComputeBuffer(Math.Max(nR, 1), 4);
            var bufMatchedR = new ComputeBuffer(Math.Max(nL, 1), 4);
            var bufRClaimed = new ComputeBuffer(Math.Max(nR, 1), 4);

            try
            {
                if (nL > 0) { bufPosL.SetData(bucketPosL); bufColL.SetData(bucketColL); }
                if (nR > 0) { bufPosR.SetData(bucketPosR); bufColR.SetData(bucketColR); bufOrigIndexR.SetData(bucketOrigIndexR); }
                bufStartL.SetData(bucketStartL);
                bufStartR.SetData(bucketStartR);
                bufRClaimed.SetData(new int[Math.Max(nR, 1)]); // zero-initialised: 0 = available

                int kernel = shader.FindKernel("ResolveProbeBuckets");
                shader.SetBuffer(kernel, "_BucketPosL", bufPosL);
                shader.SetBuffer(kernel, "_BucketColL", bufColL);
                shader.SetBuffer(kernel, "_BucketPosR", bufPosR);
                shader.SetBuffer(kernel, "_BucketColR", bufColR);
                shader.SetBuffer(kernel, "_BucketStartL", bufStartL);
                shader.SetBuffer(kernel, "_BucketStartR", bufStartR);
                shader.SetBuffer(kernel, "_BucketOrigIndexR", bufOrigIndexR);
                shader.SetBuffer(kernel, "_BucketMatchedR", bufMatchedR);
                shader.SetBuffer(kernel, "_BucketRClaimed", bufRClaimed);
                shader.SetInt("_ProbeCount", probeCount);
                shader.SetFloat("_PosWeight", posWeight);
                shader.SetFloat("_ColWeight", colWeight);
                shader.Dispatch(kernel, (probeCount + 63) / 64, 1, 1);

                var matchedR = new int[Math.Max(nL, 1)];
                bufMatchedR.GetData(matchedR);
                return nL > 0 ? matchedR : Array.Empty<int>();
            }
            finally
            {
                bufPosL.Dispose(); bufColL.Dispose();
                bufPosR.Dispose(); bufColR.Dispose();
                bufStartL.Dispose(); bufStartR.Dispose();
                bufOrigIndexR.Dispose(); bufMatchedR.Dispose(); bufRClaimed.Dispose();
            }
        }

        void RunAnalyzeSpatialProbeOccupancy()
        {
            Vector3[] posL, posR;
            try
            {
                posL = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetLeft);
                posR = GaussianMorphMapBuilder.DecodeSplatPositions(m_AssetRight);
            }
            catch (Exception e)
            {
                m_SampleReport = $"Decode error: {e.Message}";
                Debug.LogException(e);
                return;
            }

            int nL = posL.Length;
            int nR = posR.Length;
            int probeCount = Mathf.Max(1, Mathf.RoundToInt(m_ProbeAccuracy * Mathf.Max(nL, nR)));
            int cellsPerAxis = Mathf.Max(1, Mathf.RoundToInt(Mathf.Pow(probeCount, 1f / 3f)));
            int totalProbes = cellsPerAxis * cellsPerAxis * cellsPerAxis;

            var probeIndexL = DispatchSpatialProbeAssignment(posL, cellsPerAxis);
            var probeIndexR = DispatchSpatialProbeAssignment(posR, cellsPerAxis);

            var countL = new int[totalProbes];
            var countR = new int[totalProbes];
            foreach (var p in probeIndexL) countL[p]++;
            foreach (var p in probeIndexR) countR[p]++;

            int emptyBothSides = 0, emptyLOnly = 0, emptyROnly = 0, occupiedBoth = 0;
            int maxL = 0, maxR = 0;
            for (int p = 0; p < totalProbes; p++)
            {
                maxL = Mathf.Max(maxL, countL[p]);
                maxR = Mathf.Max(maxR, countR[p]);
                bool hasL = countL[p] > 0, hasR = countR[p] > 0;
                if (!hasL && !hasR) emptyBothSides++;
                else if (hasL && !hasR) emptyLOnly++;
                else if (!hasL && hasR) emptyROnly++;
                else occupiedBoth++;
            }

            m_SampleReport = $"probeAccuracy={m_ProbeAccuracy:F2} -> probeCount target={probeCount}, cellsPerAxis={cellsPerAxis}, totalProbes={totalProbes}\n" +
                              $"L: {nL} splats, max per probe={maxL}\n" +
                              $"R: {nR} splats, max per probe={maxR}\n" +
                              $"Probes occupied by BOTH sides: {occupiedBoth}\n" +
                              $"Probes with ONLY L: {emptyLOnly}\n" +
                              $"Probes with ONLY R: {emptyROnly}\n" +
                              $"Probes empty on BOTH sides: {emptyBothSides}";
            Debug.Log(m_SampleReport);
        }

        // Each cloud is normalised against its OWN bounds, independently — not a shared/combined
        // box. A shared box lets absolute physical scale dominate: a small structure's splats would
        // all compress into one tiny corner of the joint [0,1]3 space relative to a much larger
        // structure, losing each cloud's own "top-left corner of ITS OWN extent" positional meaning.
        // Independent per-cloud normalisation preserves that — a splat at the small cloud's
        // top-left corner and a splat at the big cloud's top-left corner both land near (0,0,0) in
        // their own normalised space, which is the actual correspondence semantics wanted (position
        // relative to the splat's own object, not raw combined-scene scale).
        static void GetBounds(Vector3[] a, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue,  float.MaxValue,  float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in a) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }

        // ── Asset save ────────────────────────────────────────────────────────

        void SaveAsset(GaussianMorphMapBuilder.Result result, GaussianSplatAsset left, GaussianSplatAsset right)
        {
            if (!AssetDatabase.IsValidFolder(m_OutputFolder))
                System.IO.Directory.CreateDirectory(m_OutputFolder);

            string path = $"{m_OutputFolder}/{left.name}_{right.name}_MorphMap.asset";

            var map = CreateInstance<GaussianMorphMap>();
            map.splatCountLeft  = left.splatCount;
            map.splatCountRight = right.splatCount;
            map.leftAssetGuid   = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(left));
            map.rightAssetGuid  = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(right));
            map.matchedPairs    = result.matchedPairs;
            map.unmatchedLeft   = result.unmatchedLeft;
            map.unmatchedRight  = result.unmatchedRight;

            AssetDatabase.CreateAsset(map, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(map);
            Selection.activeObject = map;
        }
    }
}
