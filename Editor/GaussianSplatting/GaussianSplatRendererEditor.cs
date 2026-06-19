// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using GaussianSplatRenderer = GaussianSplatting.Runtime.GaussianSplatRenderer;

namespace GaussianSplatting.Editor
{
[CustomEditor(typeof(GaussianSplatRenderer))]
    [CanEditMultipleObjects]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        const string kPrefExportBake = "nesnausk.GaussianSplatting.ExportBakeTransform";
        const string kPrefDeleteDensity = "nesnausk.GaussianSplatting.DeleteDensity";
        const string kPrefDeleteHardness = "nesnausk.GaussianSplatting.DeleteHardness";

        SerializedProperty m_PropAsset;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;
        SerializedProperty m_PropSHOnly;
        SerializedProperty m_CenterEyeOnly;
        SerializedProperty m_PropRenderOrder;
        SerializedProperty m_PropSortNthFrame;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointDisplaySize;
        SerializedProperty m_PropCutouts;
        SerializedProperty m_PropMask;
        SerializedProperty m_PropMaskT;
        SerializedProperty m_PropShaderSplats;
        SerializedProperty m_PropShaderComposite;
        SerializedProperty m_PropShaderDebugPoints;
        SerializedProperty m_PropShaderDebugBoxes;
        SerializedProperty m_PropCSSplatUtilities_deviceRadixSort;
        SerializedProperty m_PropCSSplatUtilities_fidelityFxSort;
        SerializedProperty m_gpuSortType;
        SerializedProperty m_PropOptimizeForQuest;
        SerializedProperty m_PropContributionCullThreshold;
        SerializedProperty m_PropAlphaDiscardThreshold;
        SerializedProperty m_PropOpaqueExperiment;

        bool m_ResourcesExpanded = false;
        int m_CameraIndex = 0;

        bool m_ExportBakeTransform;
        bool m_AnyDotHovered;
        float m_DeleteDensity = 1f;
        float m_DeleteHardness = 1f;

        public static float DeleteHardness { get; private set; } = 1f;

        // Undo stack for GPU state. Each entry is deleted-bits snapshot + selected-bits snapshot to restore after undo.
        static readonly System.Collections.Generic.Stack<(GaussianSplatRenderer gs, uint[] deletedSnap, uint[] selectedSnap)> s_DeleteUndoStack = new();

        static int s_EditStatsUpdateCounter = 0;

        static HashSet<GaussianSplatRendererEditor> s_AllEditors = new();

        public static void BumpGUICounter()
        {
            ++s_EditStatsUpdateCounter;
        }

        public static void RepaintAll()
        {
            foreach (var e in s_AllEditors)
                e.Repaint();
            SceneView.RepaintAll();
        }

        public void OnEnable()
        {
            m_ExportBakeTransform = EditorPrefs.GetBool(kPrefExportBake, false);
            m_DeleteDensity  = EditorPrefs.GetFloat(kPrefDeleteDensity, 1f);
            m_DeleteHardness = EditorPrefs.GetFloat(kPrefDeleteHardness, 1f);

            m_PropAsset = serializedObject.FindProperty("m_Asset");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
            m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
            m_PropSortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_CenterEyeOnly = serializedObject.FindProperty("m_CenterEyeOnly");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropCutouts = serializedObject.FindProperty("m_Cutouts");
            m_PropMask = serializedObject.FindProperty("m_Mask");
            m_PropMaskT = serializedObject.FindProperty("m_MaskT");
            m_PropShaderSplats = serializedObject.FindProperty("m_ShaderSplats");
            m_PropShaderComposite = serializedObject.FindProperty("m_ShaderComposite");
            m_PropShaderDebugPoints = serializedObject.FindProperty("m_ShaderDebugPoints");
            m_PropShaderDebugBoxes = serializedObject.FindProperty("m_ShaderDebugBoxes");
            m_PropCSSplatUtilities_deviceRadixSort = serializedObject.FindProperty("m_CSSplatUtilities_deviceRadixSort");
            m_PropCSSplatUtilities_fidelityFxSort = serializedObject.FindProperty("m_CSSplatUtilities_fidelityFX");
            m_gpuSortType = serializedObject.FindProperty("m_gpuSortType");
            m_PropOptimizeForQuest = serializedObject.FindProperty("m_OptimizeForQuest");
            m_PropContributionCullThreshold = serializedObject.FindProperty("m_ContributionCullThreshold");
            m_PropAlphaDiscardThreshold = serializedObject.FindProperty("m_AlphaDiscardThreshold");
            m_PropOpaqueExperiment = serializedObject.FindProperty("m_OpaqueExperiment");
            s_AllEditors.Add(this);
            
            // Auto-assign resources if not set
            var gs = target as GaussianSplatRenderer;
            if (gs != null && !gs.HasValidRenderSetup)
            {
                AutoAssignResources(gs);
            }
        }

        public void OnDisable()
        {
            s_AllEditors.Remove(this);
        }

        private void AutoAssignResources(GaussianSplatRenderer gs)
        {
            Undo.RecordObject(gs, "Auto-assign Gaussian Splat Resources");
            
            const string packagePath = "Packages/com.worldlabs.gaussian-splatting";
            
            // Load shaders using absolute package paths
            gs.m_ShaderSplats = AssetDatabase.LoadAssetAtPath<Shader>($"{packagePath}/Shaders/RenderGaussianSplats.shader");
            gs.m_ShaderComposite = AssetDatabase.LoadAssetAtPath<Shader>($"{packagePath}/Shaders/GaussianComposite.shader");
            gs.m_ShaderDebugPoints = AssetDatabase.LoadAssetAtPath<Shader>($"{packagePath}/Shaders/GaussianDebugRenderPoints.shader");
            gs.m_ShaderDebugBoxes = AssetDatabase.LoadAssetAtPath<Shader>($"{packagePath}/Shaders/GaussianDebugRenderBoxes.shader");
            
            // Load compute shaders using absolute package paths
            gs.m_CSSplatUtilities_deviceRadixSort = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{packagePath}/Shaders/SplatUtilities_DeviceRadixSort.compute");
            gs.m_CSSplatUtilities_fidelityFX = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{packagePath}/Shaders/SplatUtilities_FidelityFX.compute");
            
            // Fallback: try Shader.Find if AssetDatabase fails (for shaders)
            if (gs.m_ShaderSplats == null) gs.m_ShaderSplats = Shader.Find("Gaussian Splatting/Render Splats");
            if (gs.m_ShaderComposite == null) gs.m_ShaderComposite = Shader.Find("Hidden/Gaussian Splatting/Composite");
            if (gs.m_ShaderDebugPoints == null) gs.m_ShaderDebugPoints = Shader.Find("Gaussian Splatting/Debug/Render Points");
            if (gs.m_ShaderDebugBoxes == null) gs.m_ShaderDebugBoxes = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            
            EditorUtility.SetDirty(gs);
            serializedObject.Update();

            Debug.Log("[Gaussian Splatting] Resources auto-assigned successfully.");
        }

        // Creates a GaussianSplatMask as a sub-asset of the given GaussianSplatAsset.
        static GaussianSplatMask CreateMaskSubAsset(GaussianSplatAsset gs, string maskName)
        {
            string assetPath = AssetDatabase.GetAssetPath(gs);
            if (string.IsNullOrEmpty(assetPath))
                return null;

            var mask = ScriptableObject.CreateInstance<GaussianSplatMask>();
            mask.name = maskName;

            AssetDatabase.AddObjectToAsset(mask, assetPath);
            AssetDatabase.SaveAssets();
            return mask;
        }

        class CreateMaskNamePrompt : EditorWindow
        {
            string m_Name = "Mask";
            Action<string> m_OnCreate;

            public static void Show(Action<string> onCreate)
            {
                var wnd = CreateInstance<CreateMaskNamePrompt>();
                wnd.m_OnCreate = onCreate;
                wnd.titleContent = new GUIContent("Create Mask");
                wnd.minSize = wnd.maxSize = new Vector2(300, 80);
                wnd.ShowUtility();
            }

            void OnGUI()
            {
                GUI.SetNextControlName("MaskNameField");
                m_Name = EditorGUILayout.TextField("Name", m_Name);
                GUI.FocusControl("MaskNameField");

                EditorGUILayout.Space();
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Cancel"))
                    Close();
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Name)))
                {
                    if (GUILayout.Button("Create"))
                    {
                        m_OnCreate(m_Name);
                        Close();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        // Writes indices into a GaussianSplatMaskData sub-asset, replacing any existing one for this entry.
        static void WriteEntryData(GaussianSplatMask mask, GaussianSplatMask.Entry entry, int[] indices)
        {
            string maskPath = AssetDatabase.GetAssetPath(mask);

            if (entry.dataAsset != null)
            {
                AssetDatabase.RemoveObjectFromAsset(entry.dataAsset);
                entry.dataAsset = null;
            }

            var dataObj = ScriptableObject.CreateInstance<GaussianSplatMaskData>();
            dataObj.name = $"_{entry.label}_{entry.weight:F3}";
            dataObj.hideFlags = HideFlags.HideInHierarchy;
            dataObj.bytes = GaussianSplatMask.IndicesToBytes(indices);
            AssetDatabase.AddObjectToAsset(dataObj, maskPath);

            entry.dataAsset = dataObj;
            entry.splatIndices = indices;
            entry.legacySplatIndices = null;
        }

        static Texture2D s_DotTexture;

        static Texture2D GetDotTexture()
        {
            if (s_DotTexture != null) return s_DotTexture;
            const int size = 32;
            s_DotTexture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float outerR = center;
            float innerR = outerR - 1.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                byte a = d <= innerR ? (byte)255 : d <= outerR ? (byte)(255 * (outerR - d)) : (byte)0;
                pixels[y * size + x] = new Color32(255, 255, 255, a);
            }
            s_DotTexture.SetPixels32(pixels);
            s_DotTexture.Apply();
            return s_DotTexture;
        }

        void DrawMaskTSliderWithKeyframes(GaussianSplatRenderer gs)
        {
            var mask = gs.m_Mask;

            var sliderLabel = new GUIContent("Mask T", "Reveal position along the mask timeline (0–1).");
            Rect sliderRect = EditorGUILayout.GetControlRect();

            float labelWidth = EditorGUIUtility.labelWidth;
            float trackX = sliderRect.x + labelWidth;
            float trackW = sliderRect.width - labelWidth - EditorGUIUtility.fieldWidth - 5f;
            float trackY = sliderRect.y + sliderRect.height * 0.5f;
            float dotR = 4f;

            // Check dot clicks BEFORE the slider draws so it doesn't consume the event first
            var evt = Event.current;
            if (mask != null && mask.entries != null && evt.type == EventType.MouseDown)
            {
                foreach (var entry in mask.entries)
                {
                    if (entry == null) continue;
                    float cx = trackX + entry.weight * trackW;
                    var hitRect = new Rect(cx - dotR * 2f, trackY - dotR * 2f, dotR * 4f, dotR * 4f);
                    if (!hitRect.Contains(evt.mousePosition)) continue;

                    Undo.RecordObject(gs, "Load Mask Entry");
                    gs.m_MaskT = entry.weight;
                    EditorUtility.SetDirty(gs);

                    var indices = entry.splatIndices;
                    if (indices != null && indices.Length > 0)
                    {
                        int wordCount = (gs.splatCount + 31) / 32;
                        var bits = evt.shift ? (gs.SnapshotSelectedBits() ?? new uint[wordCount]) : new uint[wordCount];
                        if (bits.Length != wordCount) { var b2 = new uint[wordCount]; bits.CopyTo(b2, 0); bits = b2; }
                        foreach (int idx in indices)
                        {
                            if (idx >= 0 && idx < gs.splatCount)
                                bits[idx >> 5] |= 1u << (idx & 31);
                        }
                        gs.EditDeselectAll(); // ensures GPU buffer exists
                        gs.RestoreSelectedBits(bits);
                    }
                    else if (!evt.shift)
                    {
                        gs.EditDeselectAll();
                    }
                    gs.UpdateEditCountsAndBounds();
                    ToolManager.SetActiveContext<GaussianToolContext>();
                    // Update serialized property so the slider draws at the snapped position,
                    // then consume the event so the slider doesn't also start dragging.
                    m_PropMaskT.floatValue = entry.weight;
                    serializedObject.ApplyModifiedProperties();
                    evt.Use();
                    RepaintAll();
                    break;
                }
            }

            EditorGUI.BeginChangeCheck();
            float newT = EditorGUI.Slider(sliderRect, sliderLabel, gs.m_MaskT, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(gs, "Set Mask T");
                gs.m_MaskT = newT;
                EditorUtility.SetDirty(gs);
            }

            if (mask == null || mask.entries == null || mask.entries.Count == 0)
                return;

            if (evt.type == EventType.MouseMove || evt.type == EventType.Repaint)
            {
                bool anyHovered = false;
                var dot = GetDotTexture();
                var prevColor = GUI.color;
                foreach (var entry in mask.entries)
                {
                    if (entry == null) continue;
                    float cx = trackX + entry.weight * trackW;
                    var hitRect = new Rect(cx - dotR * 2f, trackY - dotR * 2f, dotR * 4f, dotR * 4f);
                    bool hovered = hitRect.Contains(evt.mousePosition);
                    if (hovered) anyHovered = true;
                    if (evt.type == EventType.Repaint)
                    {
                        float r = hovered ? dotR * 1.4f : dotR;
                        var dotRect = new Rect(cx - r, trackY - r, r * 2f, r * 2f);
                        GUI.color = hovered ? Color.white : new Color(0.65f, 0.65f, 0.65f);
                        GUI.DrawTexture(dotRect, dot);
                    }
                }
                GUI.color = prevColor;
                // Only repaint when hover state changes to avoid triggering a continuous repaint loop.
                if (evt.type == EventType.MouseMove && anyHovered != m_AnyDotHovered)
                {
                    m_AnyDotHovered = anyHovered;
                    Repaint();
                }
            }
        }

        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;

            serializedObject.Update();

            GUILayout.Label("Data Asset", EditorStyles.boldLabel);

            var morpher = gs.GetComponent<GaussianSplatting.Runtime.GaussianSplatMorpher>();
            bool morpherActive = morpher != null && morpher.isActiveAndEnabled;
            if (morpherActive)
                EditorGUILayout.HelpBox("Asset overridden by GaussianSplatMorpher.", MessageType.Info);

            using (new EditorGUI.DisabledScope(morpherActive))
                EditorGUILayout.PropertyField(m_PropAsset);

            if (!morpherActive && !gs.HasValidAsset)
            {
                var msg = gs.asset != null && gs.asset.formatVersion != GaussianSplatAsset.kCurrentVersion
                    ? "Gaussian Splat asset version is not compatible, please recreate the asset"
                    : "Gaussian Splat asset is not assigned or is empty";
                EditorGUILayout.HelpBox(msg, MessageType.Error);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropSplatScale);
            EditorGUILayout.PropertyField(m_PropOpacityScale);
            EditorGUILayout.PropertyField(m_PropSHOrder);
            EditorGUILayout.PropertyField(m_PropSHOnly);
            EditorGUILayout.PropertyField(m_PropRenderOrder);
            EditorGUILayout.PropertyField(m_gpuSortType);
            if (gs.m_gpuSortType != GpuSorting.SortType.None)
            {
                EditorGUILayout.PropertyField(m_PropSortNthFrame);
                EditorGUILayout.PropertyField(m_CenterEyeOnly);
            }
            EditorGUILayout.PropertyField(m_PropOptimizeForQuest);
            EditorGUILayout.PropertyField(m_PropContributionCullThreshold);
            EditorGUILayout.PropertyField(m_PropAlphaDiscardThreshold);

            if (gs.HasValidAsset && gs.asset != null)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Layer Options", EditorStyles.boldLabel);

                // Initialize layer activation state list if null
                if (gs.m_LayerActivationState == null)
                {
                    gs.m_LayerActivationState = new List<int2>();
                }

                for (int i = 0; i < gs.asset.layerInfo.Count; i++)
                {
                    if (gs.asset.layerInfo.Count > gs.m_LayerActivationState.Count)
                    {
                        var toAdd = Enumerable.Repeat(default(int2), gs.asset.layerInfo.Count - gs.m_LayerActivationState.Count);
                        gs.m_LayerActivationState.AddRange(toAdd);

                        // On initial resize, activate first layer
                        gs.m_LayerActivationState[0] = new int2(0, 1); 
                        gs.UpdateRessources();
                    }

                    var layer = gs.m_LayerActivationState.ElementAtOrDefault(i);
                    var layerActive = layer.y > 0;
                    var check = EditorGUILayout.Toggle($"Show layer {i}", layerActive);
                    if (check != layerActive)
                    {
                        gs.m_LayerActivationState[i] = new int2(i, check ? 1 : 0);
                        gs.UpdateRessources();
                        EditorUtility.SetDirty(gs);
                    }
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Debugging Tweaks", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropOpaqueExperiment);
            if (m_PropOpaqueExperiment.boolValue)
                EditorGUILayout.HelpBox("GSP-CULL-01: Opaque front-to-back mode active. Output looks wrong by design — compare OVR Metrics App GPU Time against normal render for overdraw lower bound.", MessageType.Info);
            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (m_PropRenderMode.intValue is (int)GaussianSplatRenderer.RenderMode.DebugPoints or (int)GaussianSplatRenderer.RenderMode.DebugPointIndices)
                EditorGUILayout.PropertyField(m_PropPointDisplaySize);

            EditorGUILayout.Space();
            m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
            if (m_ResourcesExpanded)
            {
                EditorGUILayout.PropertyField(m_PropShaderSplats);
                EditorGUILayout.PropertyField(m_PropShaderComposite);
                EditorGUILayout.PropertyField(m_PropShaderDebugPoints);
                EditorGUILayout.PropertyField(m_PropShaderDebugBoxes);
                EditorGUILayout.PropertyField(m_PropCSSplatUtilities_deviceRadixSort);
                EditorGUILayout.PropertyField(m_PropCSSplatUtilities_fidelityFxSort);
                
                EditorGUILayout.Space();
                if (GUILayout.Button("Auto-assign Resources"))
                {
                    AutoAssignResources(gs);
                }
            }
            bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && gs.HasValidAsset;
            if (validAndEnabled && !gs.HasValidRenderSetup)
            {
                EditorGUILayout.HelpBox("Shader resources are not set up. Click 'Auto-assign Resources' above.", MessageType.Error);
                validAndEnabled = false;
            }

            if (validAndEnabled && targets.Length == 1)
            {
                EditCameras(gs);
                EditGUI(gs);
            }
            if (validAndEnabled && targets.Length > 1)
            {
                MultiEditGUI();
            }

            serializedObject.ApplyModifiedProperties();
        }

        void EditCameras(GaussianSplatRenderer gs)
        {
            var asset = gs.asset;
            if (asset == null)
                return;
            var cameras = asset.cameras;
            if (cameras != null && cameras.Length != 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Cameras", EditorStyles.boldLabel);
                var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
                camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
                if (camIndex != m_CameraIndex)
                {
                    m_CameraIndex = camIndex;
                    gs.ActivateCamera(camIndex);
                }
            }
        }

        void MultiEditGUI()
        {
            DrawSeparator();
            CountTargetSplats(out var totalSplats, out var totalObjects);
            EditorGUILayout.LabelField("Total Objects", $"{totalObjects}");
            EditorGUILayout.LabelField("Total Splats", $"{totalSplats:N0}");
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
            {
                EditorGUILayout.HelpBox($"Can't merge, too many splats (max. supported {GaussianSplatAsset.kMaxSplats:N0})", MessageType.Warning);
                return;
            }

            var targetGs = (GaussianSplatRenderer) target;
            if (!targetGs || !targetGs.HasValidAsset || !targetGs.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (no asset or disable)", MessageType.Warning);
                return;
            }

            if (targetGs.asset.chunkDataSize > 0)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (needs to use Very High quality preset)", MessageType.Warning);
                return;
            }
            if (GUILayout.Button($"Merge into {target.name}"))
            {
                MergeSplatObjects();
            }
        }

        void CountTargetSplats(out int totalSplats, out int totalObjects)
        {
            totalObjects = 0;
            totalSplats = 0;
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                ++totalObjects;
                totalSplats += gs.splatCount;
            }
        }

        void MergeSplatObjects()
        {
            CountTargetSplats(out var totalSplats, out _);
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
                return;
            var targetGs = (GaussianSplatRenderer) target;

            int copyDstOffset = targetGs.splatCount;
            targetGs.EditSetSplatCount(totalSplats);
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                if (gs == targetGs)
                    continue;
                gs.EditCopySplatsInto(targetGs, 0, copyDstOffset, gs.splatCount);
                copyDstOffset += gs.splatCount;
                gs.gameObject.SetActive(false);
            }
            Debug.Assert(copyDstOffset == totalSplats, $"Merge count mismatch, {copyDstOffset} vs {totalSplats}");
            Selection.activeObject = targetGs;
        }

        // Called by the Undo Delete button. Returns true if something was restored.
        public static bool PopDeleteUndo()
        {
            if (s_DeleteUndoStack.Count == 0) return false;
            var (gs, deletedSnap, selectedSnap) = s_DeleteUndoStack.Pop();
            if (gs == null) return false;
            // Ensure the GameObject and renderer are active before touching GPU buffers.
            gs.gameObject.SetActive(true);
            gs.enabled = true;
            gs.RestoreDeletedBits(deletedSnap);
            gs.RestoreSelectedBits(selectedSnap);
            Selection.activeGameObject = gs.gameObject;
            ToolManager.SetActiveContext<GaussianToolContext>();
            RepaintAll();
            return true;
        }

        void EditGUI(GaussianSplatRenderer gs)
        {
            // Editing tools operate on gs.asset directly — not applicable when a morpher
            // is driving external buffers (gs.asset is null in that case).
            if (gs.asset == null)
                return;

            ++s_EditStatsUpdateCounter;

            DrawSeparator();
            bool wasToolActive = ToolManager.activeContextType == typeof(GaussianToolContext);
            GUILayout.BeginHorizontal();
            bool isToolActive = GUILayout.Toggle(wasToolActive, "Edit", EditorStyles.miniButton);
            using (new EditorGUI.DisabledScope(!gs.editModified))
            {
                if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog("Reset Splat Modifications?",
                            $"This will reset edits of {gs.name} to match the {gs.asset.name} asset. Continue?",
                            "Yes, reset", "Cancel"))
                    {
                        gs.enabled = false;
                        gs.enabled = true;
                    }
                }
            }

            GUILayout.EndHorizontal();
            if (!wasToolActive && isToolActive)
            {
                ToolManager.SetActiveContext<GaussianToolContext>();
                if (Tools.current == Tool.View)
                    Tools.current = Tool.Move;
            }

            if (wasToolActive && !isToolActive)
            {
                ToolManager.SetActiveContext<GameObjectToolContext>();
            }

            if (isToolActive && gs.asset.chunkDataSize > 0)
            {
                EditorGUILayout.HelpBox("Splat move/rotate/scale tools need Very High splat quality preset", MessageType.Warning);
            }

            // Selection mode toggle: Box vs Brush
            if (isToolActive)
            {
                bool isBrush = GaussianBrushSelectTool.BrushModeActive;
                EditorGUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Select:", GUILayout.ExpandWidth(false));
                bool wantBox   = GUILayout.Toggle(!isBrush, "Box",   EditorStyles.miniButtonLeft);
                bool wantBrush = GUILayout.Toggle( isBrush, "Brush", EditorStyles.miniButtonRight);
                GUILayout.EndHorizontal();

                if (wantBrush && !isBrush) GaussianBrushSelectTool.BrushModeActive = true;
                else if (wantBox && isBrush) GaussianBrushSelectTool.BrushModeActive = false;

                if (GaussianBrushSelectTool.BrushModeActive)
                {
                    // Screen / World mode toggle
                    bool wasWorld = GaussianBrushSelectTool.WorldSpaceMode;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Mode:", GUILayout.ExpandWidth(false));
                    bool wantScreen = GUILayout.Toggle(!wasWorld, "Screen", EditorStyles.miniButtonLeft);
                    bool wantWorld  = GUILayout.Toggle( wasWorld, "World",  EditorStyles.miniButtonRight);
                    GUILayout.EndHorizontal();
                    if (wantWorld  && !wasWorld) GaussianBrushSelectTool.WorldSpaceMode = true;
                    if (wantScreen &&  wasWorld) GaussianBrushSelectTool.WorldSpaceMode = false;

                    EditorGUI.BeginChangeCheck();
                    if (GaussianBrushSelectTool.WorldSpaceMode)
                    {
                        float r = EditorGUILayout.Slider(
                            new GUIContent("Brush Radius (m)", "World-space sphere radius in metres. Scroll wheel also resizes."),
                            GaussianBrushSelectTool.BrushRadiusWorld, 0.1f, 20f);
                        if (EditorGUI.EndChangeCheck()) GaussianBrushSelectTool.BrushRadiusWorld = r;
                    }
                    else
                    {
                        float r = EditorGUILayout.Slider(
                            new GUIContent("Brush Radius (px)", "Screen-space brush radius in pixels. Scroll wheel also resizes."),
                            GaussianBrushSelectTool.BrushRadiusPx, 5f, 500f);
                        if (EditorGUI.EndChangeCheck()) GaussianBrushSelectTool.BrushRadiusPx = r;
                    }
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Mask", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropMask);
            using (new EditorGUI.DisabledScope(gs.asset == null))
            {
                if (GUILayout.Button("Create Mask"))
                {
                    var gsRef = gs;
                    CreateMaskNamePrompt.Show(name =>
                    {
                        var mask = CreateMaskSubAsset(gsRef.asset, name);
                        Undo.RecordObject(gsRef, "Create Mask");
                        gsRef.m_Mask = mask;
                        serializedObject.Update();
                    });
                }
            }
            DrawMaskTSliderWithKeyframes(gs);

            using (new EditorGUI.DisabledScope(gs.editSelectedSplats == 0))
            {
                if (GUILayout.Button("Save Selection as Mask Entry"))
                {
                    var mask = gs.m_Mask;
                    if (mask == null && gs.asset != null)
                    {
                        mask = CreateMaskSubAsset(gs.asset, "Mask");
                        gs.m_Mask = mask;
                        serializedObject.Update();
                    }

                    if (mask != null)
                    {
                        var snap = gs.SnapshotSelectedBits();
                        var indices = new System.Collections.Generic.List<int>();
                        int wordCount = snap.Length;
                        for (int w = 0; w < wordCount; w++)
                        {
                            uint word = snap[w];
                            while (word != 0)
                            {
                                uint lsb = word & (uint)(-(int)word);
                                int bit = 0;
                                if ((lsb & 0xFFFF0000u) != 0) bit += 16;
                                if ((lsb & 0xFF00FF00u) != 0) bit += 8;
                                if ((lsb & 0xF0F0F0F0u) != 0) bit += 4;
                                if ((lsb & 0xCCCCCCCCu) != 0) bit += 2;
                                if ((lsb & 0xAAAAAAAAu) != 0) bit += 1;
                                indices.Add(w * 32 + bit);
                                word &= word - 1;
                            }
                        }

                        var newIndices = indices.ToArray();
                        var existing = mask.entries.Find(e => e != null && Mathf.Abs(e.weight - gs.m_MaskT) < 0.001f);
                        if (existing != null)
                        {
                            bool replace = EditorUtility.DisplayDialog(
                                "Replace Mask Entry?",
                                $"An entry \"{existing.label}\" already exists at weight {existing.weight:F3}. Replace its selection?",
                                "Replace", "Cancel");
                            if (replace)
                            {
                                Undo.RecordObject(mask, "Replace Mask Entry");
                                WriteEntryData(mask, existing, newIndices);
                                EditorUtility.SetDirty(mask);
                                AssetDatabase.SaveAssets();
                                var gsRef = gs; EditorApplication.delayCall += () => gsRef?.SetMaskDirty();
                            }
                        }
                        else
                        {
                            var entry = new GaussianSplatMask.Entry
                            {
                                label = $"Selection {mask.entries.Count}",
                                weight = gs.m_MaskT,
                            };
                            Undo.RecordObject(mask, "Add Mask Entry");
                            mask.entries.Add(entry);
                            WriteEntryData(mask, entry, newIndices);
                            EditorUtility.SetDirty(mask);
                            AssetDatabase.SaveAssets();
                            var gsRef = gs; EditorApplication.delayCall += () => gsRef?.SetMaskDirty();
                        }
                    }
                }
            }

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Cutout"))
            {
                GaussianCutout cutout = ObjectFactory.CreateGameObject("GSCutout", typeof(GaussianCutout)).GetComponent<GaussianCutout>();
                Transform cutoutTr = cutout.transform;
                cutoutTr.SetParent(gs.transform, false);
                cutoutTr.localScale = (gs.asset.boundsMax - gs.asset.boundsMin) * 0.25f;
                gs.m_Cutouts ??= Array.Empty<GaussianCutout>();
                ArrayUtility.Add(ref gs.m_Cutouts, cutout);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
                Selection.activeGameObject = cutout.gameObject;
            }
            if (GUILayout.Button("Use All Cutouts"))
            {
                gs.m_Cutouts = FindObjectsByType<GaussianCutout>(FindObjectsSortMode.InstanceID);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }

            if (GUILayout.Button("No Cutouts"))
            {
                gs.m_Cutouts = Array.Empty<GaussianCutout>();
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(m_PropCutouts);

            bool hasCutouts = gs.m_Cutouts != null && gs.m_Cutouts.Length != 0;
            bool modifiedOrHasCutouts = gs.editModified || hasCutouts;

            var asset = gs.asset;
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            m_ExportBakeTransform = EditorGUILayout.Toggle("Export in world space", m_ExportBakeTransform);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(kPrefExportBake, m_ExportBakeTransform);
            }

            // Delete controls
            EditorGUILayout.Space();
            GUILayout.Label("Delete Selected", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            m_DeleteDensity  = EditorGUILayout.Slider(new GUIContent("Density",  "How much of the selection is deleted. 1 = all candidates, 0 = nothing. Transparent splats go first."), m_DeleteDensity,  0f, 1f);
            m_DeleteHardness = EditorGUILayout.Slider(new GUIContent("Hardness", "Edge falloff. 1 = uniform cut across selection. 0 = full density at center, fades to zero at the selection boundary."), m_DeleteHardness, 0f, 1f);
            DeleteHardness = m_DeleteHardness;
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetFloat(kPrefDeleteDensity,  m_DeleteDensity);
                EditorPrefs.SetFloat(kPrefDeleteHardness, m_DeleteHardness);
            }
            using (new EditorGUI.DisabledScope(gs.editSelectedSplats == 0))
            {
                if (GUILayout.Button("Delete Selected"))
                {
                    var deletedSnap  = gs.SnapshotDeletedBits();
                    var selectedSnap = gs.SnapshotSelectedBits();
                    gs.EditDeleteSelectedWithParams(m_DeleteDensity, m_DeleteHardness);
                    if (gs.editDeletedSplats > 0)
                    {
                        s_DeleteUndoStack.Push((gs, deletedSnap, selectedSnap));
                        EditorUtility.SetDirty(gs);
                    }
                    RepaintAll();
                }
            }

            // Delete undo / commit
            int undoCount = s_DeleteUndoStack.Count;
            GUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(undoCount == 0))
            {
                if (GUILayout.Button($"Undo Delete ({undoCount})"))
                    PopDeleteUndo();
            }
            using (new EditorGUI.DisabledScope(!gs.editModified || gs.asset.chunkDataSize > 0))
            {
                if (GUILayout.Button("Commit to Disk"))
                {
                    s_DeleteUndoStack.Clear();
                    GaussianSplatCommitter.Commit(gs);
                }
            }
            GUILayout.EndHorizontal();

            // Separate selection
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(gs.editSelectedSplats == 0 || gs.asset.chunkDataSize > 0))
            {
                if (GUILayout.Button("Separate Selection to New Asset"))
                    GaussianSplatSeparator.Separate(gs);
            }
            if (gs.asset.chunkDataSize > 0)
                EditorGUILayout.HelpBox("Separate requires VeryHigh quality (no chunk compression)", MessageType.None);

            EditorGUILayout.Space();
            if (GUILayout.Button("Export PLY"))
                ExportPlyFile(gs, m_ExportBakeTransform);
            if (asset.posFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.scaleFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.colorFormat > GaussianSplatAsset.ColorFormat.Float16x4 ||
                asset.shFormat > GaussianSplatAsset.SHFormat.Float16)
            {
                EditorGUILayout.HelpBox(
                    "It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy",
                    MessageType.Warning);
            }

            bool displayEditStats = isToolActive || modifiedOrHasCutouts;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");
            if (displayEditStats)
            {
                EditorGUILayout.LabelField("Cut", $"{gs.editCutSplats:N0}");
                EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
                using (new EditorGUI.DisabledScope(gs.editSelectedSplats == 0))
                {
                    if (GUILayout.Button("Deselect", EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    {
                        gs.EditDeselectAll();
                        RepaintAll();
                    }
                }
                GUILayout.EndHorizontal();
                if (hasCutouts)
                {
                    if (s_EditStatsUpdateCounter > 10)
                    {
                        gs.UpdateEditCountsAndBounds();
                        s_EditStatsUpdateCounter = 0;
                    }
                }
            }
        }

        static void DrawSeparator()
        {
            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
        }

        bool HasFrameBounds()
        {
            return true;
        }

        Bounds OnGetFrameBounds()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs || !gs.HasValidRenderSetup)
                return new Bounds(Vector3.zero, Vector3.one);
            Bounds bounds = default;
            bounds.SetMinMax(gs.asset.boundsMin, gs.asset.boundsMax);
            if (gs.editSelectedSplats > 0)
            {
                bounds = gs.editSelectedBounds;
            }
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        public static Bounds TransformBounds(Transform tr, Bounds bounds )
        {
            var center = tr.TransformPoint(bounds.center);

            var ext = bounds.extents;
            var axisX = tr.TransformVector(ext.x, 0, 0);
            var axisY = tr.TransformVector(0, ext.y, 0);
            var axisZ = tr.TransformVector(0, 0, ext.z);

            // sum their absolute value to get the world extents
            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds { center = center, extents = ext };
        }

        static unsafe void ExportPlyFile(GaussianSplatRenderer gs, bool bakeTransform)
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Gaussian Splat PLY file", "", $"{gs.asset.name}-edit.ply", "ply");
            if (string.IsNullOrWhiteSpace(path))
                return;

            int kSplatSize = UnsafeUtility.SizeOf<GaussianSplatAssetCreator.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gs.splatCount, kSplatSize);

            if (!gs.EditExportData(gpuData, bakeTransform))
                return;

            GaussianSplatAssetCreator.InputSplatData[] data = new GaussianSplatAssetCreator.InputSplatData[gpuData.count];
            gpuData.GetData(data);

            var gpuDeleted = gs.GpuEditDeleted;
            uint[] deleted = new uint[gpuDeleted.count];
            gpuDeleted.GetData(deleted);

            // count non-deleted splats
            int aliveCount = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                    ++aliveCount;
            }

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            // note: this is a long string! but we don't use multiline literal because we want guaranteed LF line ending
            var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {aliveCount}\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float f_rest_0\nproperty float f_rest_1\nproperty float f_rest_2\nproperty float f_rest_3\nproperty float f_rest_4\nproperty float f_rest_5\nproperty float f_rest_6\nproperty float f_rest_7\nproperty float f_rest_8\nproperty float f_rest_9\nproperty float f_rest_10\nproperty float f_rest_11\nproperty float f_rest_12\nproperty float f_rest_13\nproperty float f_rest_14\nproperty float f_rest_15\nproperty float f_rest_16\nproperty float f_rest_17\nproperty float f_rest_18\nproperty float f_rest_19\nproperty float f_rest_20\nproperty float f_rest_21\nproperty float f_rest_22\nproperty float f_rest_23\nproperty float f_rest_24\nproperty float f_rest_25\nproperty float f_rest_26\nproperty float f_rest_27\nproperty float f_rest_28\nproperty float f_rest_29\nproperty float f_rest_30\nproperty float f_rest_31\nproperty float f_rest_32\nproperty float f_rest_33\nproperty float f_rest_34\nproperty float f_rest_35\nproperty float f_rest_36\nproperty float f_rest_37\nproperty float f_rest_38\nproperty float f_rest_39\nproperty float f_rest_40\nproperty float f_rest_41\nproperty float f_rest_42\nproperty float f_rest_43\nproperty float f_rest_44\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
            fs.Write(Encoding.UTF8.GetBytes(header));
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                {
                    var splat = data[i];
                    byte* ptr = (byte*)&splat;
                    fs.Write(new ReadOnlySpan<byte>(ptr, kSplatSize));
                }
            }

            Debug.Log($"Exported PLY {path} with {aliveCount:N0} splats");
        }
    }
}