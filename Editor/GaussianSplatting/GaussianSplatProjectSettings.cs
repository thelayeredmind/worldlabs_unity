// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Editor
{
    // Project-wide settings for the Gaussian Splat editor tooling.
    // Stored as a ScriptableObject at kAssetPath so it can be committed to git and shared across the team.
    [FilePath("ProjectSettings/GaussianSplatSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class GaussianSplatProjectSettings : ScriptableSingleton<GaussianSplatProjectSettings>
    {
        [Tooltip("Root folder (relative to project root) where Commit-to-Disk backups are stored. The '~' suffix tells Unity to ignore the folder contents (no import, no locking).")]
        public string backupRoot = "Assets/GaussianBackups~";

        internal void Save() => Save(true);
    }

    // Surfaces GaussianSplatProjectSettings under Edit > Project Settings > Gaussian Splat.
    static class GaussianSplatSettingsProvider
    {
        [SettingsProvider]
        static SettingsProvider Create() => new SettingsProvider("Project/Gaussian Splat", SettingsScope.Project)
        {
            label = "Gaussian Splat",
            guiHandler = _ =>
            {
                var s = GaussianSplatProjectSettings.instance;
                EditorGUI.BeginChangeCheck();

                EditorGUILayout.LabelField("Commit to Disk", EditorStyles.boldLabel);
                EditorGUILayout.Space(2);

                EditorGUILayout.BeginHorizontal();
                s.backupRoot = EditorGUILayout.TextField(
                    new GUIContent("Backup Folder",
                        "Root folder (relative to project root) where originals are backed up before each Commit to Disk."),
                    s.backupRoot);
                if (GUILayout.Button("Browse", GUILayout.Width(60)))
                {
                    string absDefault = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(Application.dataPath, "..", s.backupRoot));
                    string chosen = EditorUtility.OpenFolderPanel("Gaussian Splat Backup Folder", absDefault, "");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        string projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                        if (chosen.StartsWith(projectRoot, System.StringComparison.OrdinalIgnoreCase))
                            chosen = chosen.Substring(projectRoot.Length).TrimStart('/', '\\').Replace('\\', '/');
                        s.backupRoot = chosen;
                        GUI.FocusControl(null);
                    }
                }
                EditorGUILayout.EndHorizontal();

                bool missingTilde = s.backupRoot.Replace('\\', '/').StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)
                                 && !s.backupRoot.TrimEnd('/').EndsWith("~");
                if (missingTilde)
                    EditorGUILayout.HelpBox(
                        "Folder is inside Assets/ but doesn't end with '~'. Unity will import the backup files and lock them. Rename it to end with '~' (e.g. \"Assets/GaussianBackups~\").",
                        MessageType.Warning);
                else
                    EditorGUILayout.HelpBox(
                        "Backups are written to <BackupFolder>/<AssetName>/<timestamp>/. The '~' suffix tells Unity to ignore folder contents — files are never imported or locked. Commit this folder to git to version your backup history.",
                        MessageType.None);

                if (EditorGUI.EndChangeCheck())
                    s.Save();

                EditorGUILayout.Space(12);
                EditorGUILayout.LabelField("Budgeted Load Scheduler", EditorStyles.boldLabel);
                EditorGUILayout.Space(2);

                var schedulerSettings = GaussianSplatSchedulerSettingsEditor.GetOrCreateAsset();
                var so = new SerializedObject(schedulerSettings);
                so.Update();
                EditorGUILayout.PropertyField(so.FindProperty(nameof(GaussianSplatSchedulerSettings.budgetedLoadFrameTimeMs)),
                    new GUIContent("Frame Time Budget (ms)", "Time budget per frame (ms) for the deferred/budgeted resource load, used when a mask is assigned and m_MaskT is 0 at OnEnable. Only caps CHEAP non-blocking work between GPU/driver calls — cannot interrupt a single call already in progress (e.g. a GraphicsBuffer sync or a driver shader-support query), which pays its full real cost in one frame regardless of this setting."));
                EditorGUILayout.PropertyField(so.FindProperty(nameof(GaussianSplatSchedulerSettings.budgetedLoadConcurrentMax)),
                    new GUIContent("Max Concurrent Loads", "Maximum number of GaussianSplatRenderers allowed to be mid-budgeted-load simultaneously. At most one new renderer starts per frame, and only while fewer than this many are already loading."));
                if (so.ApplyModifiedProperties())
                    EditorUtility.SetDirty(schedulerSettings);

                EditorGUILayout.HelpBox(
                    "Controls how many masked GaussianSplatRenderers can load their GPU resources simultaneously when several activate in the same frame (e.g. one Timeline Activation Track cascade). Stored at " + GaussianSplatSchedulerSettings.kAssetPath + ".\n\n" +
                    "Frame Time Budget only limits non-blocking work measured between calls — it cannot preempt a single expensive driver/GPU call (e.g. ComputeShader.IsSupported(), a GraphicsBuffer sync wait). Those still take their full cost in one frame. To reduce THAT cost, reduce the calls themselves (e.g. GpuSorting caches DeviceRadixSort/FidelityFxSort construction across renderers sharing a compute shader) rather than expecting this setting to slice them.",
                    MessageType.Info);
            },
            keywords = new System.Collections.Generic.HashSet<string> { "gaussian", "splat", "backup", "scheduler", "budget", "budgeted", "load" }
        };
    }

    // GaussianSplatSchedulerSettings (Runtime/) is a plain Resources-loaded ScriptableObject,
    // not an Editor-only ScriptableSingleton like GaussianSplatProjectSettings above — it
    // needs to exist as a real asset under Assets/Resources/ so Resources.Load finds it in
    // actual builds. This helper creates that asset on first use if it doesn't exist yet.
    static class GaussianSplatSchedulerSettingsEditor
    {
        public static GaussianSplatSchedulerSettings GetOrCreateAsset()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatSchedulerSettings>(GaussianSplatSchedulerSettings.kAssetPath);
            if (asset != null)
                return asset;

            if (!AssetDatabase.IsValidFolder(GaussianSplatSchedulerSettings.kAssetFolder))
                AssetDatabase.CreateFolder("Assets", "Resources");

            asset = ScriptableObject.CreateInstance<GaussianSplatSchedulerSettings>();
            AssetDatabase.CreateAsset(asset, GaussianSplatSchedulerSettings.kAssetPath);
            AssetDatabase.SaveAssets();
            return asset;
        }
    }
}
