// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Editor window front-end for <see cref="GaussianMorphMapBuilder"/>.
    /// </summary>
    public class GaussianMorphMapBuilderWindow : EditorWindow
    {
        const string kMenuPath       = "Tools/Gaussian Splats/Build Morph Map";
        const string kPrefOutputFolder = "GaussianSplatting.MorphMapBuilder.OutputFolder";

        [SerializeField] GaussianSplatAsset m_AssetLeft;
        [SerializeField] GaussianSplatAsset m_AssetRight;
        [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";
        [SerializeField] float m_ColorWeight   = 0.5f;

        string m_Status;
        bool   m_Building;

        [MenuItem(kMenuPath)]
        public static void Open()
        {
            var w = GetWindowWithRect<GaussianMorphMapBuilderWindow>(new Rect(50, 50, 380, 240), false, "Morph Map Builder", true);
            w.minSize = new Vector2(340, 220);
            w.Show();
        }

        void Awake() => m_OutputFolder = EditorPrefs.GetString(kPrefOutputFolder, "Assets/GaussianAssets");

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
                    Build();
            }

            if (!string.IsNullOrEmpty(m_Status))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(m_Status, m_Building ? MessageType.Info : MessageType.None);
            }
        }

        void Build()
        {
            m_Building = true;
            m_Status   = "Building…";
            Repaint();

            try
            {
                var result = GaussianMorphMapBuilder.Build(
                    m_AssetLeft, m_AssetRight, m_ColorWeight,
                    t =>
                    {
                        EditorUtility.DisplayProgressBar("Building Morph Map", $"{(int)(t * 100)}%", t);
                        return true;
                    });

                SaveAsset(result);
                m_Status = $"Done — {result.indicesLeft.Length} matched, {result.unmatchedLeft.Length} unmatched left, {result.unmatchedRight.Length} unmatched right.";
            }
            catch (System.Exception e)
            {
                m_Status = $"Error: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                m_Building = false;
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        void SaveAsset(GaussianMorphMapBuilder.Result result)
        {
            if (!AssetDatabase.IsValidFolder(m_OutputFolder))
                System.IO.Directory.CreateDirectory(m_OutputFolder);

            string path = $"{m_OutputFolder}/{m_AssetLeft.name}_{m_AssetRight.name}_MorphMap.asset";

            var map = CreateInstance<GaussianMorphMap>();
            map.splatCountLeft  = m_AssetLeft.splatCount;
            map.splatCountRight = m_AssetRight.splatCount;
            map.indicesLeft     = result.indicesLeft;
            map.indicesRight    = result.indicesRight;
            map.unmatchedLeft   = result.unmatchedLeft;
            map.unmatchedRight  = result.unmatchedRight;

            AssetDatabase.CreateAsset(map, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(map);
            Selection.activeObject = map;
        }
    }
}
