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
        Exception            m_GpuException;
        ManualResetEventSlim m_GpuDone;

        [MenuItem(kMenuPath)]
        public static void Open() => OpenWindow(null, null);

        public static void Open(GaussianSplatAsset left, GaussianSplatAsset right) => OpenWindow(left, right);

        static void OpenWindow(GaussianSplatAsset left, GaussianSplatAsset right)
        {
            var w = GetWindowWithRect<GaussianMorphMapBuilderWindow>(new Rect(50, 50, 400, 260), false, "Morph Map Builder", true);
            w.minSize = new Vector2(360, 240);
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
                try   { DispatchCorrespondenceShader(m_GpuPosL, m_GpuColL, m_GpuPosR, m_GpuColR, m_GpuPosWeight, m_GpuColWeight, out m_GpuResultIndex, out m_GpuResultDist); }
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
                float posWeight, float colWeight, out int[] bestIndex, out float[] bestDist)
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

                bestIndex = w.m_GpuResultIndex;
                bestDist  = w.m_GpuResultDist;
            }
        }

        static void DispatchCorrespondenceShader(
            Vector3[] posL, Vector4[] colL,
            Vector3[] posR, Vector4[] colR,
            float posWeight, float colWeight,
            out int[] bestIndex, out float[] bestDist)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(kShaderPath);
            if (shader == null)
                throw new Exception($"SplatCorrespondence.compute not found at {kShaderPath}");

            int nL = posL.Length;
            int nR = posR.Length;

            // Normalise positions into [0,1] across both clouds
            GetBounds(posL, posR, out var bMin, out var bMax);
            float scale = Mathf.Max(Mathf.Max(bMax.x - bMin.x, bMax.y - bMin.y), bMax.z - bMin.z);
            scale = Mathf.Max(scale, 1e-6f);

            var splatsLData = new Vector4[nL];
            var splatsRData = new Vector4[nR];
            for (int i = 0; i < nL; i++)
                splatsLData[i] = new Vector4((posL[i].x - bMin.x) / scale, (posL[i].y - bMin.y) / scale, (posL[i].z - bMin.z) / scale, 0);
            for (int j = 0; j < nR; j++)
                splatsRData[j] = new Vector4((posR[j].x - bMin.x) / scale, (posR[j].y - bMin.y) / scale, (posR[j].z - bMin.z) / scale, 0);

            var bufSplatsL = new ComputeBuffer(nL, 16);
            var bufSplatsR = new ComputeBuffer(nR, 16);
            var bufColsL   = new ComputeBuffer(nL, 16);
            var bufColsR   = new ComputeBuffer(nR, 16);
            var bufMatch   = new ComputeBuffer(nL, 4);
            var bufDist    = new ComputeBuffer(nL, 4);

            try
            {
                bufSplatsL.SetData(splatsLData);
                bufSplatsR.SetData(splatsRData);
                bufColsL.SetData(colL);
                bufColsR.SetData(colR);

                int kernel = shader.FindKernel("FindBestMatch");
                shader.SetBuffer(kernel, "_SplatsL",        bufSplatsL);
                shader.SetBuffer(kernel, "_ColorsL",        bufColsL);
                shader.SetBuffer(kernel, "_SplatsR",        bufSplatsR);
                shader.SetBuffer(kernel, "_ColorsR",        bufColsR);
                shader.SetBuffer(kernel, "_BestMatchIndex", bufMatch);
                shader.SetBuffer(kernel, "_BestMatchDist",  bufDist);
                shader.SetInt  ("_CountL",    nL);
                shader.SetInt  ("_CountR",    nR);
                shader.SetFloat("_PosWeight", posWeight);
                shader.SetFloat("_ColWeight", colWeight);

                shader.Dispatch(kernel, (nL + 63) / 64, 1, 1);

                bestIndex = new int[nL];
                bestDist  = new float[nL];
                bufMatch.GetData(bestIndex);
                bufDist.GetData(bestDist);
            }
            finally
            {
                bufSplatsL.Dispose(); bufSplatsR.Dispose();
                bufColsL.Dispose();   bufColsR.Dispose();
                bufMatch.Dispose();   bufDist.Dispose();
            }
        }

        static void GetBounds(Vector3[] a, Vector3[] b, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue,  float.MaxValue,  float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in a) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            foreach (var p in b) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
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
