// SPDX-License-Identifier: MIT

using System;
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

        [SerializeField] GaussianMorphMap m_SampleMap;
        [SerializeField] int m_SampleCount = 20;
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
            var ct = m_Cts.Token;

            var dispatcher = new MainThreadDispatcher(this);
            var prog = new Progress<float>(t => { m_Progress = t; m_Status = $"{(int)(t * 100)}%…"; });

            m_BuildTask = Task.Run(() =>
            {
                try
                {
                    var result = GaussianMorphMapBuilder.Build(posL, posR, colL, colR, dispatcher, colorWeight, prog, ct);
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
