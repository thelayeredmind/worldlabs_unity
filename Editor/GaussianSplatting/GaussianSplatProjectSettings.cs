// SPDX-License-Identifier: MIT

using UnityEditor;
using UnityEngine;

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
            },
            keywords = new System.Collections.Generic.HashSet<string> { "gaussian", "splat", "backup" }
        };
    }
}
